using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 「系统 → 角色管理」的角色列表（带两个计数）。
///
/// <c>superadmin</c> 的 MenuCount 是**全部菜单数**：它的授权由 matcher 短路承担、不落在 menu_roles 里
/// （见 docs/PERMISSIONS.md 第四节），若按绑定行数算会显示成 0，与"它能访问一切"的事实相反。
/// 其余角色的两个计数按绑定行数去重统计——"有几个成员/几个菜单"数的是人/菜单，不是行。
/// </summary>
public sealed class ListAdminRolesUseCase(
    IRoleRepository roles,
    IUserRoleRepository userRoles,
    IMenuRoleRepository menuRoles,
    IMenuRepository menus)
{
    public async Task<IReadOnlyList<AdminRoleDto>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Role> allRoles = await roles.ListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<UserRoleBinding> userBindings = await userRoles.ListAllAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<MenuRoleBinding> menuBindings = await menuRoles.ListAllAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, int> memberCounts = userBindings
            .GroupBy(b => b.RoleCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(b => b.UserId).Distinct().Count(), StringComparer.Ordinal);

        Dictionary<string, int> menuCounts = menuBindings
            .GroupBy(b => b.RoleCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(b => b.MenuId).Distinct().Count(), StringComparer.Ordinal);

        int totalMenus = await menus.CountAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. allRoles.Select(role => new AdminRoleDto
            {
                Code = role.Code,
                Title = role.Title,
                IsSystem = role.IsSystem,
                MemberCount = memberCounts.GetValueOrDefault(role.Code),
                MenuCount = role.Code == PermissionSubjects.SuperadminRoleCode
                    ? totalMenus
                    : menuCounts.GetValueOrDefault(role.Code),
            }),
        ];
    }
}

/// <summary>
/// 修改角色的显示名。**code 不可改**（请求体里没有它）：它是角色的身份，也是
/// <c>role:{code}</c> 那一段，改它等于把全部用户绑定与菜单绑定换到另一个角色上。
/// </summary>
public sealed class RenameRoleUseCase(IRoleRepository roles, IPolicyWriter policies)
{
    public async Task ExecuteAsync(string code, RenameRoleRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Role role = await roles.FindByCodeAsync(code, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"角色「{code}」不存在", nameof(code));

        string title = request.Title?.Trim() ?? string.Empty;

        if (title.Length == 0)
        {
            throw new ArgumentException("角色名称不能为空", nameof(request));
        }

        await roles.UpdateAsync(role with { Title = title }, cancellationToken).ConfigureAwait(false);

        // 改的是显示名，不产生策略变化——但写操作后重建只有一处入口，不在这里开"这次可以跳过"的口子
        await policies.RebuildAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 删除角色。两条守卫：系统角色不可删；还有成员的先解绑用户——删掉角色会让那些账号
/// 悄悄失去权限，而这件事没有任何界面会提醒。
/// </summary>
public sealed class DeleteRoleUseCase(
    IRoleRepository roles,
    IUserRoleRepository userRoles,
    IPolicyWriter policies)
{
    public async Task ExecuteAsync(string code, CancellationToken cancellationToken = default)
    {
        Role role = await roles.FindByCodeAsync(code, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"角色「{code}」不存在", nameof(code));

        if (role.IsSystem)
        {
            throw new ArgumentException($"角色「{code}」是系统角色，不可删除", nameof(code));
        }

        int memberCount = (await userRoles.ListAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(b => b.RoleCode == code)
            .Select(b => b.UserId)
            .Distinct()
            .Count();

        if (memberCount > 0)
        {
            throw new ArgumentException(
                $"角色「{code}」还有 {memberCount} 名成员，先解绑用户再删除",
                nameof(code));
        }

        await roles.DeleteAsync(code, cancellationToken).ConfigureAwait(false);
        await policies.RebuildAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 读某个角色绑定的菜单 code 列表，按 menus.sortOrder 排（与导航顺序一致）。
///
/// <c>superadmin</c> 回**全部菜单**：它的授权由 matcher 短路承担，不落在绑定表里；
/// 回空数组会让界面显示"什么都没有"，与它实际能访问一切相反。
/// </summary>
public sealed class GetRoleMenusUseCase(
    IRoleRepository roles,
    IMenuRepository menus,
    IMenuRoleRepository menuRoles)
{
    public async Task<RoleMenuCodesDto> ExecuteAsync(string code, CancellationToken cancellationToken = default)
    {
        Role role = await roles.FindByCodeAsync(code, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"角色「{code}」不存在", nameof(code));

        IReadOnlyList<Menu> all = await menus.ListAsync(cancellationToken).ConfigureAwait(false);

        if (role.Code == PermissionSubjects.SuperadminRoleCode)
        {
            return new RoleMenuCodesDto { MenuCodes = [.. all.Select(m => m.Code)] };
        }

        IReadOnlyList<MenuRoleBinding> bindings = await menuRoles.ListAllAsync(cancellationToken).ConfigureAwait(false);
        HashSet<long> boundMenuIds = [.. bindings.Where(b => b.RoleCode == code).Select(b => b.MenuId)];

        return new RoleMenuCodesDto
        {
            MenuCodes = [.. all.Where(m => boundMenuIds.Contains(m.Id)).Select(m => m.Code)],
        };
    }
}

/// <summary>
/// 整体替换某个角色的菜单绑定（「角色管理」页写的就是这个方向）。
///
/// 三条守卫：<c>superadmin</c> 不可整体替换（它的授权由 matcher 短路承担，不落在 menu_roles 里）；
/// 菜单 code 必须全部存在，消息里点名是哪一个；角色必须存在。
/// </summary>
public sealed class SetRoleMenusUseCase(
    IRoleRepository roles,
    IMenuRepository menus,
    IMenuRoleRepository menuRoles,
    IPolicyWriter policies)
{
    public async Task ExecuteAsync(string code, RoleMenuCodesDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Role role = await roles.FindByCodeAsync(code, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"角色「{code}」不存在", nameof(code));

        if (role.Code == PermissionSubjects.SuperadminRoleCode)
        {
            throw new ArgumentException(
                "超级管理员的授权由策略投影自动补齐（不落在绑定表里），不能整体替换",
                nameof(code));
        }

        List<string> menuCodes =
        [
            .. request.MenuCodes
                .Where(menuCode => !string.IsNullOrWhiteSpace(menuCode))
                .Select(menuCode => menuCode.Trim())
                .Distinct(StringComparer.Ordinal),
        ];

        IReadOnlyList<Menu> all = await menus.ListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, long> idByCode = new(StringComparer.Ordinal);
        foreach (Menu menu in all)
        {
            idByCode[menu.Code] = menu.Id;
        }

        List<long> menuIds = [];
        foreach (string menuCode in menuCodes)
        {
            if (!idByCode.TryGetValue(menuCode, out long menuId))
            {
                throw new ArgumentException($"菜单「{menuCode}」不存在", nameof(request));
            }

            menuIds.Add(menuId);
        }

        await menuRoles.ReplaceForRoleAsync(code, menuIds, cancellationToken).ConfigureAwait(false);
        await policies.RebuildAsync(cancellationToken).ConfigureAwait(false);
    }
}
