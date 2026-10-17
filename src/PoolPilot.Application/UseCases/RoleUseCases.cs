using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>列出全部角色。</summary>
public sealed class ListRolesUseCase(IRoleRepository roles)
{
    public async Task<IReadOnlyList<RoleDto>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Role> all = await roles.ListAsync(cancellationToken).ConfigureAwait(false);

        return [.. all.Select(ToDto)];
    }

    internal static RoleDto ToDto(Role role) => new()
    {
        Code = role.Code,
        Title = role.Title,
        IsSystem = role.IsSystem,
    };
}

/// <summary>
/// 新建角色。<c>IsSystem</c> 由服务端定（一律 <c>false</c>）——系统角色只有种子建的超级管理员，
/// 让客户端指定这个字段等于允许"造一个不可删的角色"。
/// </summary>
public sealed class CreateRoleUseCase(IRoleRepository roles, IPolicyWriter policies)
{
    public async Task<RoleDto> ExecuteAsync(CreateRoleRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string code = request.Code?.Trim() ?? string.Empty;
        string title = request.Title?.Trim() ?? string.Empty;

        if (code.Length == 0)
        {
            throw new ArgumentException("角色标识不能为空", nameof(request));
        }

        if (title.Length == 0)
        {
            throw new ArgumentException("角色名称不能为空", nameof(request));
        }

        if (await roles.FindByCodeAsync(code, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new ArgumentException($"角色「{code}」已经存在", nameof(request));
        }

        Role created = await roles.CreateAsync(
            new Role { Code = code, Title = title, IsSystem = false },
            cancellationToken).ConfigureAwait(false);

        // 新角色本身不产生策略（还要有人把它绑到菜单上），但仍走一次重建：
        // 写操作后的重建只有一处，不在这里开"这次可以跳过"的口子
        await policies.RebuildAsync(cancellationToken).ConfigureAwait(false);

        return ListRolesUseCase.ToDto(created);
    }
}

/// <summary>
/// 当前用户的角色与逐权限点的动作（<c>GET /api/auth/permissions</c>）。
///
/// 角色来自表，动作来自 <see cref="IPermissionEvaluator"/>（即 Casbin）——两者必须一致，
/// 因为它们是同一份事实（user_roles 投影成 g，menu_roles 投影成 p）的两个视图。
/// </summary>
public sealed class GetPermissionsUseCase(
    IPermissionEvaluator permissions,
    IMenuRepository menus,
    IUserRoleRepository userRoles,
    IRoleRepository roles,
    ICurrentUser currentUser)
{
    public async Task<PermissionsDto> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // 候选 = 全部菜单 × 各自动作（与菜单树那条口径一致）：列举与判定走同一条 matcher，
        // 超管的短路因此被展开成一份完整清单。
        IReadOnlyList<Menu> allMenus = await menus.ListAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<PermissionCandidate> candidates =
        [
            .. allMenus.Select(m => new PermissionCandidate(m.PermissionKey, m.Actions)),
        ];

        IReadOnlyList<PermissionGrant> granted = await permissions
            .GetGrantedAsync(candidates, cancellationToken)
            .ConfigureAwait(false);

        long? userId = currentUser.UserId;
        IReadOnlyList<string> roleCodes = userId is { } id
            ? await userRoles.ListRoleCodesForUserAsync(id, cancellationToken).ConfigureAwait(false)
            : [];

        IReadOnlyList<Role> all = await roles.ListAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> wanted = [.. roleCodes];

        return new PermissionsDto
        {
            Roles = [.. all.Where(r => wanted.Contains(r.Code)).Select(ListRolesUseCase.ToDto)],
            Permissions =
            [
                .. granted.Select(p => new PermissionGrantDto
                {
                    PermissionKey = p.PermissionKey,
                    Actions = p.Actions,
                }),
            ],
        };
    }
}

/// <summary>
/// 启动时把授权数据补齐：确保超级管理员角色存在、内置菜单按 code 缺一补一、唯一账号绑定超管。
///
/// 三件事都在同一个用例里，且**最后统一重建一次策略**：它们是同一批"让系统可用"的动作，
/// 拆成三个用例只会让调用方（组合根）多写三段编排。
///
/// 判据与注册路径共用 <see cref="SuperadminBootstrap"/>——"谁是第一个账号"只有一套判据。
/// </summary>
public sealed class SeedAuthorizationUseCase(
    IRoleRepository roles,
    IMenuRepository menus,
    IUserRepository users,
    IUserRoleRepository userRoles,
    IPolicyWriter policies,
    TimeProvider clock)
{
    public async Task<AuthorizationSeedResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        bool roleCreated = await EnsureSuperadminRoleAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> addedMenus = await EnsureBuiltInMenusAsync(cancellationToken).ConfigureAwait(false);
        bool superadminBound = await SuperadminBootstrap
            .BindSoleExistingAccountAsync(users, userRoles, cancellationToken)
            .ConfigureAwait(false);

        // 空库也要重建一次：让 Enforcer 至少与（可能为空的）表一致
        await policies.RebuildAsync(cancellationToken).ConfigureAwait(false);

        return new AuthorizationSeedResult(roleCreated, addedMenus, superadminBound);
    }

    private async Task<bool> EnsureSuperadminRoleAsync(CancellationToken cancellationToken)
    {
        if (await roles.FindByCodeAsync(PermissionSubjects.SuperadminRoleCode, cancellationToken).ConfigureAwait(false)
            is not null)
        {
            return false;
        }

        await roles.CreateAsync(
            new Role
            {
                Code = PermissionSubjects.SuperadminRoleCode,
                Title = "超级管理员",
                IsSystem = true,
            },
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// 内置菜单按 code **缺一补一**：存在即跳过（库里那份是事实，标题/排序/动作改动都尊重），
    /// 缺失才插入——缺失只有两个原因：这个库第一次建，或它比代码老。
    ///
    /// 为什么不再分"空库播种 / 老库补插"两套：内置菜单现在**不可删**（见 DeleteMenuUseCase 的守卫），
    /// 于是"库里没有这条"再也不可能是"使用者主动删的"——一套"缺就补"就够了，
    /// 新加内置菜单时也不必再维护第二份清单。
    /// </summary>
    private async Task<IReadOnlyList<string>> EnsureBuiltInMenusAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, long> idByCode = new(StringComparer.Ordinal);
        List<string> added = [];

        foreach (SeedMenu seed in SeedMenus)
        {
            long? parentId = seed.ParentCode is { } parent && idByCode.TryGetValue(parent, out long pid) ? pid : null;

            if (parentId is null && seed.ParentCode is { } parentCode)
            {
                // 父节点不在本批新建里：它可能已存在于库中，或还没轮到（清单是父在前、子在后）
                Menu? existingParent = await menus.FindByCodeAsync(parentCode, cancellationToken).ConfigureAwait(false);

                if (existingParent is not null)
                {
                    idByCode[parentCode] = existingParent.Id;
                    parentId = existingParent.Id;
                }
            }

            Menu? existing = await menus.FindByCodeAsync(seed.Code, cancellationToken).ConfigureAwait(false);

            if (existing is not null)
            {
                idByCode[seed.Code] = existing.Id;
                continue;
            }

            Menu created = await InsertMenuAsync(seed, parentId, cancellationToken).ConfigureAwait(false);

            idByCode[seed.Code] = created.Id;
            added.Add(seed.Code);
        }

        return added;
    }

    private Task<Menu> InsertMenuAsync(SeedMenu seed, long? parentId, CancellationToken cancellationToken) =>
        menus.CreateAsync(
            new Menu
            {
                ParentId = parentId,
                Code = seed.Code,
                Title = seed.Title,
                Path = seed.Path,
                Icon = seed.Icon,
                Kind = seed.Kind,
                SortOrder = seed.SortOrder,
                IsVisible = seed.IsVisible,
                PermissionKey = PermissionSubjects.ForMenu(seed.Code),
                Actions = seed.Actions,
                CreatedAt = LighterTimestamp.FromMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds()),
            },
            cancellationToken);

    /// <summary>
    /// 内置菜单清单。父在前、子在后（补插时按顺序解析父 Id）。
    /// 它是"这套系统有哪些页面"的定义；建出来之后，库里的行才是事实
    /// （标题、图标、排序、可见性、动作这些改动都以库为准，这里只管"缺了才补"）。
    /// </summary>
    private static readonly SeedMenu[] SeedMenus =
    [
        new("system", "系统", null, MenuKinds.Group, "IconSettings", [MenuActions.View], null, 0),
        new("system-menus", "菜单管理", "/system/menus", MenuKinds.Menu, "IconMenu",
            [MenuActions.View, MenuActions.Edit, MenuActions.Delete], "system", 10),
        new("system-users", "用户管理", "/system/users", MenuKinds.Menu, "IconUser",
            [MenuActions.View, MenuActions.Edit], "system", 11),
        new("system-roles", "角色管理", "/system/roles", MenuKinds.Menu, "IconUserGroup",
            [MenuActions.View, MenuActions.Edit, MenuActions.Delete], "system", 12),
        new("graph", "总览", "/graph", MenuKinds.Menu, "IconDashboard", [MenuActions.View], null, 20),
        new("pools", "池列表", "/pools", MenuKinds.Menu, "IconList",
            [MenuActions.View, MenuActions.Edit, MenuActions.Delete], null, 30),
        new("pool-detail", "池详情", "/pools/:accountIndex", MenuKinds.Hidden, null, [MenuActions.View], "pools", 40),
        new("leads", "带单管理", "/leads", MenuKinds.Menu, "IconScan",
            [MenuActions.View, MenuActions.Edit, MenuActions.Delete], null, 50),
        new("lead-detail", "带单员详情", "/leads/:accountIndex", MenuKinds.Hidden, null, [MenuActions.View], "leads", 55),
        new("copy-list", "跟单管理", "/copy", MenuKinds.Menu, "IconUserGroup",
            [MenuActions.View, MenuActions.Edit, MenuActions.Delete], null, 60),
        // code 用 copy-* 前缀而不是 copy：它是前端路由名（addRoute 的 name），而全站跨页链接
        // （GraphView/FollowListView 等 15 处）用的就是这两个名字——改了它们才是真的断链。
        new("copy-detail", "跟单详情", "/copy/:id", MenuKinds.Hidden, null, [MenuActions.View], "copy-list", 70),
        new("exchanges", "交易所", "/exchanges", MenuKinds.Menu, "IconStorage",
            [MenuActions.View, MenuActions.Edit, MenuActions.Delete], null, 80),
        new("symbol-mappings", "标的映射", "/symbol-mappings", MenuKinds.Menu, "IconRelation",
            [MenuActions.View, MenuActions.Edit, MenuActions.Delete], null, 90),
        new("feasibility", "规模门槛", "/feasibility", MenuKinds.Menu, "IconFilter", [MenuActions.View], null, 100),
        // 回了 edit 而不只是 view：跑一次回测是一次真实的对外请求，接口门就是 Backtest·edit。
        // 菜单不声明 edit，界面上的「运行」按钮对**所有人**（含超管）都会是禁用的——
        // 候选动作来自菜单声明，超管短路也只在候选范围内被问。
        new("backtest", "回测面板", "/backtest", MenuKinds.Menu, "IconHistory", [MenuActions.View, MenuActions.Edit], null, 110),
        new("notifications", "通知", "/notifications", MenuKinds.Menu, "IconNotification",
            [MenuActions.View, MenuActions.Edit, MenuActions.Delete], null, 120),
    ];

    /// <summary>
    /// 内置菜单的 code 集合（从上面两份清单派生，不另存一份会漂移的名单）。
    ///
    /// 守卫拿它拦住两件会**自锁**的操作（见 UpdateMenuUseCase / DeleteMenuUseCase）：
    /// 权限键是 <c>menu:{code}</c>（代码里有常量引用它）、前端视图注册表也按 code 匹配——
    /// 改掉或删掉一个内置 code，那一页就对除超管外的所有人失效，且重启不会自愈
    /// （播种只在空库跑，补插只看行在不在）。
    /// </summary>
    public static IReadOnlySet<string> BuiltInMenuCodes { get; } =
        SeedMenus.Select(m => m.Code).ToHashSet(StringComparer.Ordinal);

    private sealed record SeedMenu(
        string Code,
        string Title,
        string? Path,
        string Kind,
        string? Icon,
        IReadOnlyList<string> Actions,
        string? ParentCode,
        int SortOrder)
    {
        internal bool IsVisible => Kind != MenuKinds.Hidden;
    }
}

/// <summary>播种结果。调用方（组合根）据此打日志。</summary>
public sealed record AuthorizationSeedResult(
    bool SuperadminRoleCreated,
    IReadOnlyList<string> AddedMenuCodes,
    bool SuperadminBoundToSoleAccount);

/// <summary>
/// "首个账号自动成为超级管理员"的判据。**只有这一处**：注册路径与启动播种都调它，
/// 免得两条路各判一遍、慢慢长歪。
///
/// 判据是"全库只有这一个账号、且它还没有任何角色"——两条件都成立才绑。
/// 达到上限的防护不是重点（那是防锁死的一道底线），重点是**只在真的还没人管过时**才动手。
/// </summary>
internal static class SuperadminBootstrap
{
    /// <summary>注册：刚建好的这个账号若也是全库唯一账号，就绑超管。</summary>
    internal static Task<bool> BindNewAccountAsync(
        long userId,
        IUserRepository users,
        IUserRoleRepository userRoles,
        CancellationToken cancellationToken) =>
        BindIfSoleAndUnboundAsync(userId, users, userRoles, cancellationToken);

    /// <summary>启动：库里恰有一个账号且它没有任何角色时，把它绑成超管（防锁死）。</summary>
    internal static async Task<bool> BindSoleExistingAccountAsync(
        IUserRepository users,
        IUserRoleRepository userRoles,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<long> ids = await users.ListIdsAsync(cancellationToken).ConfigureAwait(false);

        return ids.Count == 1
            && await BindIfSoleAndUnboundAsync(ids[0], users, userRoles, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> BindIfSoleAndUnboundAsync(
        long userId,
        IUserRepository users,
        IUserRoleRepository userRoles,
        CancellationToken cancellationToken)
    {
        if (await users.CountAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            return false;
        }

        if (await userRoles.CountForUserAsync(userId, cancellationToken).ConfigureAwait(false) > 0)
        {
            return false;
        }

        await userRoles.AddAsync(userId, PermissionSubjects.SuperadminRoleCode, cancellationToken).ConfigureAwait(false);

        return true;
    }
}
