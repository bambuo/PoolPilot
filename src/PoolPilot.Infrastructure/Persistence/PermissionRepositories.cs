using Microsoft.EntityFrameworkCore;

using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Infrastructure.Persistence;

/// <summary>
/// 菜单的存档（EF Core + SQLite）。
///
/// 动作在另一张表（<c>menu_actions</c>），所以读取时**一次取菜单、一次取动作**再在内存里接起来——
/// 逐菜单查动作会退化成 N+1。写入则把该菜单的动作整表清掉重插：动作数量极少（≤3），
/// 而"整体替换"与用例的语义（提交什么就是什么）完全一致，省掉了逐条 diff 的复杂度。
/// </summary>
public sealed class MenuRepository(PoolPilotDbContext db) : IMenuRepository
{
    public async Task<IReadOnlyList<Menu>> ListAsync(CancellationToken cancellationToken = default)
    {
        List<MenuRecord> rows = await db.Menus
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<long, List<string>> actionsByMenu = await LoadActionsAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. rows
                .OrderBy(r => r.SortOrder)
                .ThenBy(r => r.Id)
                .Select(r => ToDomain(r, actionsByMenu.GetValueOrDefault(r.Id, []))),
        ];
    }

    public async Task<Menu?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        MenuRecord? row = await db.Menus
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        List<string> actions = await db.MenuActions
            .AsNoTracking()
            .Where(a => a.MenuId == id)
            .Select(a => a.Action)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return ToDomain(row, actions);
    }

    public async Task<Menu?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        MenuRecord? row = await db.Menus
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Code == code, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        List<string> actions = await db.MenuActions
            .AsNoTracking()
            .Where(a => a.MenuId == row.Id)
            .Select(a => a.Action)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return ToDomain(row, actions);
    }

    public Task<bool> HasChildrenAsync(long id, CancellationToken cancellationToken = default) =>
        db.Menus.AnyAsync(m => m.ParentId == id, cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        db.Menus.CountAsync(cancellationToken);

    public async Task<Menu> CreateAsync(Menu menu, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menu);

        MenuRecord row = FromDomain(menu with { Id = 0L });
        db.Menus.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (string action in menu.Actions)
        {
            db.MenuActions.Add(new MenuActionRecord { MenuId = row.Id, Action = action });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToDomain(row, menu.Actions);
    }

    public async Task UpdateAsync(Menu menu, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menu);

        MenuRecord row = await db.Menus
            .FirstOrDefaultAsync(m => m.Id == menu.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ArgumentException($"菜单 {menu.Id} 不存在，无法更新", nameof(menu));

        row.ParentId = menu.ParentId;
        row.Code = menu.Code;
        row.Title = menu.Title;
        row.Path = menu.Path;
        row.Icon = menu.Icon;
        row.Kind = menu.Kind;
        row.SortOrder = menu.SortOrder;
        row.IsVisible = menu.IsVisible;
        row.PermissionKey = menu.PermissionKey;

        // 动作整体替换
        await db.MenuActions
            .Where(a => a.MenuId == menu.Id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (string action in menu.Actions)
        {
            db.MenuActions.Add(new MenuActionRecord { MenuId = menu.Id, Action = action });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        // 先清子表再删主行：没有配外键级联，顺序反了会留下悬空的动作/绑定行
        await db.MenuActions.Where(a => a.MenuId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.MenuRoles.Where(a => a.MenuId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Menus.Where(m => m.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<long, List<string>>> LoadActionsAsync(CancellationToken cancellationToken)
    {
        List<MenuActionRecord> actions = await db.MenuActions
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return actions
            .GroupBy(a => a.MenuId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Action).ToList());
    }

    private static MenuRecord FromDomain(Menu menu) => new()
    {
        Id = menu.Id,
        ParentId = menu.ParentId,
        Code = menu.Code,
        Title = menu.Title,
        Path = menu.Path,
        Icon = menu.Icon,
        Kind = menu.Kind,
        SortOrder = menu.SortOrder,
        IsVisible = menu.IsVisible,
        PermissionKey = menu.PermissionKey,
        CreatedAtUtc = menu.CreatedAt.Utc,
    };

    private static Menu ToDomain(MenuRecord row, IEnumerable<string> actions) => new()
    {
        Id = row.Id,
        ParentId = row.ParentId,
        Code = row.Code,
        Title = row.Title,
        Path = row.Path,
        Icon = row.Icon,
        Kind = row.Kind,
        SortOrder = row.SortOrder,
        IsVisible = row.IsVisible,
        PermissionKey = row.PermissionKey,
        Actions = OrderActions(actions),
        CreatedAt = LighterTimestamp.FromMilliseconds(row.CreatedAtUtc.ToUnixTimeMilliseconds()),
    };

    /// <summary>动作按固定次序返回（view → edit → delete），让界面与断言看到稳定的顺序。</summary>
    private static IReadOnlyList<string> OrderActions(IEnumerable<string> actions)
    {
        HashSet<string> set = [.. actions];

        return [.. MenuActions.All.Where(set.Contains)];
    }
}

/// <summary>角色的存档（EF Core + SQLite）。</summary>
public sealed class RoleRepository(PoolPilotDbContext db) : IRoleRepository
{
    public async Task<IReadOnlyList<Role>> ListAsync(CancellationToken cancellationToken = default)
    {
        List<RoleRecord> rows = await db.Roles
            .AsNoTracking()
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(ToDomain)];
    }

    public async Task<Role?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        RoleRecord? row = await db.Roles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Code == code, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToDomain(row);
    }

    public async Task<Role?> FindRegistrationDefaultAsync(CancellationToken cancellationToken = default)
    {
        RoleRecord? row = await db.Roles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.IsRegistrationDefault, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToDomain(row);
    }

    public async Task<Role> CreateAsync(Role role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        RoleRecord row = new()
        {
            Code = role.Code,
            Title = role.Title,
            IsSystem = role.IsSystem,
            IsRegistrationDefault = role.IsRegistrationDefault,
        };

        db.Roles.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToDomain(row);
    }

    public async Task UpdateAsync(Role role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        RoleRecord row = await db.Roles
            .FirstOrDefaultAsync(r => r.Id == role.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ArgumentException($"角色 {role.Id} 不存在，无法更新", nameof(role));

        // 只回写 Title：Code 是身份（改它等于换掉全部绑定），IsSystem 由系统维护，
        // 注册默认标记走 SetRegistrationDefaultAsync（它要同时清掉别的角色）
        row.Title = role.Title;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetRegistrationDefaultAsync(string? code, CancellationToken cancellationToken = default)
    {
        if (code is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(code);

            bool exists = await db.Roles
                .AnyAsync(r => r.Code == code, cancellationToken)
                .ConfigureAwait(false);

            if (!exists)
            {
                throw new ArgumentException($"角色「{code}」不存在，无法设为注册默认", nameof(code));
            }
        }

        // 单条 UPDATE 完成"清掉旧的、设上新的"，库里不会出现两个默认角色；
        // null 时所有行都命中 false，等于取消默认
        await db.Roles
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.IsRegistrationDefault, r => code != null && r.Code == code),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        RoleRecord? row = await db.Roles
            .FirstOrDefaultAsync(r => r.Code == code, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return;
        }

        // 先清子表再删主行（同 MenuRepository.DeleteAsync）：没有配外键级联，
        // 顺序反了会留下悬空的用户绑定/菜单绑定行
        await db.UserRoles.Where(link => link.RoleId == row.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.MenuRoles.Where(link => link.RoleId == row.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Roles.Where(r => r.Id == row.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Role ToDomain(RoleRecord row) => new()
    {
        Id = row.Id,
        Code = row.Code,
        Title = row.Title,
        IsSystem = row.IsSystem,
        IsRegistrationDefault = row.IsRegistrationDefault,
    };
}

/// <summary>
/// 用户-角色绑定的存档（EF Core + SQLite）。
/// 绑定用 Id 落库（<c>user_roles</c> 是 Id 对 Id），读出来的却是角色 code——
/// code 才是 Casbin 认得的东西。
/// </summary>
public sealed class UserRoleRepository(PoolPilotDbContext db) : IUserRoleRepository
{
    public async Task<IReadOnlyList<string>> ListRoleCodesForUserAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        List<string> codes = await (
                from link in db.UserRoles.AsNoTracking()
                join role in db.Roles.AsNoTracking() on link.RoleId equals role.Id
                where link.UserId == userId
                orderby role.Code
                select role.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return codes;
    }

    public Task<int> CountForUserAsync(long userId, CancellationToken cancellationToken = default) =>
        db.UserRoles.CountAsync(link => link.UserId == userId, cancellationToken);

    public async Task AddAsync(long userId, string roleCode, CancellationToken cancellationToken = default)
    {
        RoleRecord role = await db.Roles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Code == roleCode, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ArgumentException($"角色「{roleCode}」不存在，无法绑定", nameof(roleCode));

        bool exists = await db.UserRoles
            .AnyAsync(link => link.UserId == userId && link.RoleId == role.Id, cancellationToken)
            .ConfigureAwait(false);

        if (exists)
        {
            return;
        }

        db.UserRoles.Add(new UserRoleRecord { UserId = userId, RoleId = role.Id });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceAsync(
        long userId,
        IReadOnlyList<string> roleCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleCodes);

        List<string> wanted = [.. roleCodes.Distinct(StringComparer.Ordinal)];

        Dictionary<string, long> roleIds = await db.Roles
            .AsNoTracking()
            .Where(r => wanted.Contains(r.Code))
            .ToDictionaryAsync(r => r.Code, r => r.Id, cancellationToken)
            .ConfigureAwait(false);

        foreach (string code in wanted)
        {
            if (!roleIds.ContainsKey(code))
            {
                throw new ArgumentException($"角色「{code}」不存在，无法绑定到用户", nameof(roleCodes));
            }
        }

        await db.UserRoles.Where(link => link.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        foreach (string code in wanted)
        {
            db.UserRoles.Add(new UserRoleRecord { UserId = userId, RoleId = roleIds[code] });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UserRoleBinding>> ListAllAsync(CancellationToken cancellationToken = default) =>
        await (
                from link in db.UserRoles.AsNoTracking()
                join role in db.Roles.AsNoTracking() on link.RoleId equals role.Id
                select new UserRoleBinding(link.UserId, role.Code))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>菜单-角色绑定的存档（EF Core + SQLite）。写入是"整体替换该菜单的绑定"。</summary>
public sealed class MenuRoleRepository(PoolPilotDbContext db) : IMenuRoleRepository
{
    public async Task<IReadOnlyList<string>> ListRoleCodesAsync(
        long menuId,
        CancellationToken cancellationToken = default)
    {
        List<string> codes = await (
                from link in db.MenuRoles.AsNoTracking()
                join role in db.Roles.AsNoTracking() on link.RoleId equals role.Id
                where link.MenuId == menuId
                orderby role.Code
                select role.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return codes;
    }

    public async Task<IReadOnlyList<MenuRoleBinding>> ListAllAsync(CancellationToken cancellationToken = default) =>
        await (
                from link in db.MenuRoles.AsNoTracking()
                join role in db.Roles.AsNoTracking() on link.RoleId equals role.Id
                select new MenuRoleBinding(link.MenuId, role.Code))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task ReplaceAsync(
        long menuId,
        IReadOnlyList<string> roleCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleCodes);

        List<string> wanted = [.. roleCodes.Distinct(StringComparer.Ordinal)];

        Dictionary<string, long> roleIds = await db.Roles
            .AsNoTracking()
            .Where(r => wanted.Contains(r.Code))
            .ToDictionaryAsync(r => r.Code, r => r.Id, cancellationToken)
            .ConfigureAwait(false);

        foreach (string code in wanted)
        {
            if (!roleIds.ContainsKey(code))
            {
                throw new ArgumentException($"角色「{code}」不存在，无法绑定到菜单", nameof(roleCodes));
            }
        }

        await db.MenuRoles.Where(link => link.MenuId == menuId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        foreach (string code in wanted)
        {
            db.MenuRoles.Add(new MenuRoleRecord { MenuId = menuId, RoleId = roleIds[code] });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceForRoleAsync(
        string roleCode,
        IReadOnlyList<long> menuIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleCode);
        ArgumentNullException.ThrowIfNull(menuIds);

        RoleRecord role = await db.Roles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Code == roleCode, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ArgumentException($"角色「{roleCode}」不存在，无法绑定菜单", nameof(roleCode));

        List<long> wanted = [.. menuIds.Distinct()];

        await db.MenuRoles.Where(link => link.RoleId == role.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        foreach (long menuId in wanted)
        {
            db.MenuRoles.Add(new MenuRoleRecord { MenuId = menuId, RoleId = role.Id });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
