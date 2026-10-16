using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 当前用户的菜单树。
///
/// **只含当前用户有权（view）的节点**：菜单是权限的载体，没权的页面不该出现在导航里——
/// 只在前端藏起来不够（地址栏照样进得去），所以过滤发生在服务端。
/// 节点的 <c>actions</c> 是"我在这个菜单上能做什么"，界面据此决定按钮是否可点。
/// </summary>
public sealed class GetMenuTreeUseCase(IMenuRepository menus, IPermissionEvaluator permissions)
{
    public async Task<IReadOnlyList<MenuNodeDto>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Menu> all = await menus.ListAsync(cancellationToken).ConfigureAwait(false);

        // 候选 = 全部菜单 × 各自动作：列举与判定走同一条 matcher，
        // 超管的短路因此会被如实展开成一份完整清单（见 CasbinPermissionEvaluator）。
        IReadOnlyList<PermissionCandidate> candidates =
        [
            .. all.Select(m => new PermissionCandidate(m.PermissionKey, m.Actions)),
        ];

        IReadOnlyList<PermissionGrant> granted = await permissions
            .GetGrantedAsync(candidates, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, IReadOnlyList<string>> byKey = new(StringComparer.Ordinal);
        foreach (PermissionGrant permission in granted)
        {
            byKey[permission.PermissionKey] = permission.Actions;
        }

        List<Menu> visible =
        [
            .. all.Where(m => byKey.TryGetValue(m.PermissionKey, out IReadOnlyList<string>? actions)
                && actions.Contains(MenuActions.View)),
        ];

        return MenuTreeBuilder.Build(visible, m => byKey[m.PermissionKey]);
    }
}

/// <summary>
/// 管理视图的全量菜单树：**含 group / hidden，不按权限过滤**。
/// 它是"这套系统有哪些页面"的清单，编辑菜单时看的就是它。
/// </summary>
public sealed class GetAdminMenuTreeUseCase(IMenuRepository menus)
{
    public async Task<IReadOnlyList<MenuNodeDto>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Menu> all = await menus.ListAsync(cancellationToken).ConfigureAwait(false);

        return MenuTreeBuilder.Build(all, m => m.Actions);
    }
}

/// <summary>
/// 新建菜单。权限键由服务端按 <c>"menu:" + code</c> 生成，不接受客户端传入。
///
/// 建完之后**立刻重建策略**：这个菜单是新的权限点，被绑定到它的角色要在重建后才拿得到
/// （超管本身靠 matcher 短路不受影响，但重建是这类写操作的统一收尾——不为它留特例分支）。
/// </summary>
public sealed class CreateMenuUseCase(
    IMenuRepository menus,
    IPolicyWriter policies,
    TimeProvider clock)
{
    public async Task<MenuNodeDto> ExecuteAsync(
        MenuUpsertRequestDto request,
        CancellationToken cancellationToken = default)
    {
        MenuDefinition definition = MenuDefinition.From(request);

        if (await menus.FindByCodeAsync(definition.Code, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new ArgumentException($"菜单标识「{definition.Code}」已经存在", nameof(request));
        }

        if (request.ParentId is { } parentId
            && await menus.GetAsync(parentId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new ArgumentException($"父菜单 {parentId} 不存在", nameof(request));
        }

        Menu created = await menus.CreateAsync(definition.ToMenu(id: 0, request.ParentId, clock), cancellationToken)
            .ConfigureAwait(false);

        await policies.RebuildAsync(cancellationToken).ConfigureAwait(false);

        return MenuTreeBuilder.Build([created], m => m.Actions)[0];
    }
}

/// <summary>修改菜单。动作集合**整体替换**（提交什么就是什么）。</summary>
public sealed class UpdateMenuUseCase(
    IMenuRepository menus,
    IPolicyWriter policies)
{
    public async Task<MenuNodeDto> ExecuteAsync(
        long id,
        MenuUpsertRequestDto request,
        CancellationToken cancellationToken = default)
    {
        Menu existing = await menus.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"菜单 {id} 不存在", nameof(id));

        MenuDefinition definition = MenuDefinition.From(request);

        // 内置菜单的 code 不动：权限键（menu:{code}）与前端视图注册表都按它匹配，
        // 改名等于让那一页对除超管外的所有人失效。其余字段（标题/图标/排序/层级/可见性/动作）照旧可改。
        if (SeedAuthorizationUseCase.BuiltInMenuCodes.Contains(existing.Code)
            && !string.Equals(definition.Code, existing.Code, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"「{existing.Title}」是内置菜单，编码不可修改——它被权限键与前端页面映射引用，改了会让这一页失效",
                nameof(request));
        }

        if (await menus.FindByCodeAsync(definition.Code, cancellationToken).ConfigureAwait(false) is { } clash
            && clash.Id != id)
        {
            throw new ArgumentException($"菜单标识「{definition.Code}」已经被占用", nameof(request));
        }

        if (request.ParentId is { } parentId)
        {
            if (parentId == id)
            {
                throw new ArgumentException("父菜单不能是自己", nameof(request));
            }

            if (await menus.GetAsync(parentId, cancellationToken).ConfigureAwait(false) is null)
            {
                throw new ArgumentException($"父菜单 {parentId} 不存在", nameof(request));
            }

            await EnsureNotDescendantAsync(menus, id, parentId, cancellationToken).ConfigureAwait(false);
        }

        Menu updated = definition.ToMenu(id, request.ParentId, clock: null) with { CreatedAt = existing.CreatedAt };

        await menus.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
        await policies.RebuildAsync(cancellationToken).ConfigureAwait(false);

        return MenuTreeBuilder.Build([updated], m => m.Actions)[0];
    }

    /// <summary>父菜单不能落在自己的子树里，否则树会成环（前端渲染时会栈溢出）。</summary>
    private static async Task EnsureNotDescendantAsync(
        IMenuRepository menus,
        long selfId,
        long candidateParentId,
        CancellationToken cancellationToken)
    {
        long? cursor = candidateParentId;

        while (cursor is { } current)
        {
            if (current == selfId)
            {
                throw new ArgumentException("父菜单不能是自己的子节点", nameof(candidateParentId));
            }

            Menu? node = await menus.GetAsync(current, cancellationToken).ConfigureAwait(false);
            cursor = node?.ParentId;
        }
    }
}

/// <summary>
/// 删除菜单。**有子节点时拒绝**（回 400，经由现有异常中间件）：
/// 静默连坐删掉一棵子树是数据丢失，而这台工作台里的菜单是人在界面上一点点配出来的。
/// </summary>
public sealed class DeleteMenuUseCase(IMenuRepository menus, IPolicyWriter policies)
{
    public async Task ExecuteAsync(long id, CancellationToken cancellationToken = default)
    {
        if (await menus.GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } menu)
        {
            throw new ArgumentException($"菜单 {id} 不存在", nameof(id));
        }

        // 内置菜单不可删：删掉之后权限键失去目标（那份"看不见的授权"仍在角色勾选里，
        // 而页面映射也就此断开），重启也不会自愈。要"停用"就把它设为不进导航（IsVisible=false）。
        if (SeedAuthorizationUseCase.BuiltInMenuCodes.Contains(menu.Code))
        {
            throw new ArgumentException(
                $"「{menu.Title}」是内置菜单，不可删除——不需要它就把「进导航」关掉，路由与权限键仍要留在库里",
                nameof(id));
        }

        if (await menus.HasChildrenAsync(id, cancellationToken).ConfigureAwait(false))
        {
            throw new ArgumentException("这个菜单下面还有子节点，先把它们删掉或挪走", nameof(id));
        }

        await menus.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        await policies.RebuildAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>读某个菜单绑定了哪些角色。</summary>
public sealed class GetMenuRolesUseCase(IMenuRepository menus, IMenuRoleRepository menuRoles)
{
    public async Task<MenuRolesDto> ExecuteAsync(long id, CancellationToken cancellationToken = default)
    {
        if (await menus.GetAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new ArgumentException($"菜单 {id} 不存在", nameof(id));
        }

        IReadOnlyList<string> assigned = await menuRoles.ListRoleCodesAsync(id, cancellationToken).ConfigureAwait(false);

        return new MenuRolesDto { RoleCodes = Compose(assigned) };
    }

    /// <summary>超级管理员永远排在最前：它的策略是投影常驻补齐的，界面据此把它显示成不可取消。</summary>
    internal static IReadOnlyList<string> Compose(IReadOnlyList<string> assigned) =>
    [
        PermissionSubjects.SuperadminRoleCode,
        .. assigned.Where(code => code != PermissionSubjects.SuperadminRoleCode),
    ];
}

/// <summary>
/// 整体替换某个菜单的角色绑定（也就是它的 <c>p</c> 策略里"除超管以外的那些行"）。
///
/// **超管行不可被移除**：提交里带不带 superadmin 都一样——它不落 <c>menu_roles</c>，
/// 而是由投影常驻补齐。所以"取消超管"这件事在结构上做不到。
/// </summary>
public sealed class SetMenuRolesUseCase(
    IMenuRepository menus,
    IRoleRepository roles,
    IMenuRoleRepository menuRoles,
    IPolicyWriter policies)
{
    public async Task<MenuRolesDto> ExecuteAsync(
        long id,
        MenuRolesDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await menus.GetAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new ArgumentException($"菜单 {id} 不存在", nameof(id));
        }

        List<string> codes =
        [
            .. request.RoleCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Where(code => code != PermissionSubjects.SuperadminRoleCode)
                .Distinct(StringComparer.Ordinal),
        ];

        IReadOnlyList<Role> known = await roles.ListAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> knownCodes = [.. known.Select(r => r.Code)];

        foreach (string code in codes)
        {
            if (!knownCodes.Contains(code))
            {
                throw new ArgumentException($"角色「{code}」不存在", nameof(request));
            }
        }

        await menuRoles.ReplaceAsync(id, codes, cancellationToken).ConfigureAwait(false);
        await policies.RebuildAsync(cancellationToken).ConfigureAwait(false);

        return new MenuRolesDto { RoleCodes = GetMenuRolesUseCase.Compose(codes) };
    }
}

/// <summary>
/// 校验后的菜单定义。把"请求体 → 领域字段"的校验与归一化收在一处：
/// 新建与修改共用同一套判据，免得两条路径慢慢长歪。
/// </summary>
internal sealed record MenuDefinition(
    string Code,
    string Title,
    string? Path,
    string? Icon,
    string Kind,
    int SortOrder,
    bool IsVisible,
    IReadOnlyList<string> Actions)
{
    internal static MenuDefinition From(MenuUpsertRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string code = request.Code?.Trim() ?? string.Empty;
        string title = request.Title?.Trim() ?? string.Empty;

        if (code.Length == 0)
        {
            throw new ArgumentException("菜单标识不能为空", nameof(request));
        }

        if (title.Length == 0)
        {
            throw new ArgumentException("菜单标题不能为空", nameof(request));
        }

        if (!MenuKinds.IsValid(request.Kind))
        {
            throw new ArgumentException($"菜单种类只能是：{string.Join(" / ", MenuKinds.All)}", nameof(request));
        }

        if (request.Actions is null || request.Actions.Count == 0)
        {
            throw new ArgumentException("至少要选择一个动作", nameof(request));
        }

        if (request.Actions.Any(action => !MenuActions.IsValid(action)))
        {
            throw new ArgumentException($"动作只能是：{string.Join(" / ", MenuActions.All)}", nameof(request));
        }

        string? path = string.IsNullOrWhiteSpace(request.Path) ? null : request.Path.Trim();

        if (request.Kind == MenuKinds.Group && path is not null)
        {
            throw new ArgumentException("分组是纯容器，不能有路由路径", nameof(request));
        }

        if (request.Kind != MenuKinds.Group && path is null)
        {
            throw new ArgumentException("可路由的菜单必须有路由路径", nameof(request));
        }

        return new MenuDefinition(
            code,
            title,
            path,
            string.IsNullOrWhiteSpace(request.Icon) ? null : request.Icon.Trim(),
            request.Kind,
            request.SortOrder,
            request.IsVisible,
            [.. request.Actions.Distinct(StringComparer.Ordinal)]);
    }

    internal Menu ToMenu(long id, long? parentId, TimeProvider? clock)
    {
        long now = (clock?.GetUtcNow() ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

        return new Menu
        {
            Id = id,
            ParentId = parentId,
            Code = Code,
            Title = Title,
            Path = Path,
            Icon = Icon,
            Kind = Kind,
            SortOrder = SortOrder,
            IsVisible = IsVisible,
            PermissionKey = PermissionSubjects.ForMenu(Code),
            Actions = Actions,
            CreatedAt = LighterTimestamp.FromMilliseconds(now),
        };
    }
}
