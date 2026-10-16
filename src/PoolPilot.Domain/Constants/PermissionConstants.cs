namespace PoolPilot.Domain.Constants;

/// <summary>
/// 菜单节点的种类。
///
/// <c>group</c> = 纯分组（不可路由，只承载子节点）；
/// <c>menu</c> = 导航可见的路由；
/// <c>hidden</c> = 可路由但不在导航显示（详情页那类）。
///
/// 用字符串常量而不是枚举：库里的列是文本、接口上的取值也是文本（<c>group|menu|hidden</c>），
/// 多一层枚举只会多一处要同步的地方（与 <see cref="NotificationKinds"/> 同一条理由）。
/// </summary>
public static class MenuKinds
{
    public const string Group = "group";

    public const string Menu = "menu";

    public const string Hidden = "hidden";

    public static readonly IReadOnlyList<string> All = [Group, Menu, Hidden];

    public static bool IsValid(string? kind) => kind is Group or Menu or Hidden;
}

/// <summary>
/// 权限点的动作。权限点粒度 = 菜单 + 动作（见 docs/PERMISSIONS.md）。
///
/// 这三个字符串同时是：库里 <c>menu_actions.action</c> 的取值、接口上 <c>actions[]</c> 的元素、
/// 以及 Casbin 的 <c>act</c>。**三处必须是同一套字面量**，所以只在这一处定义。
/// </summary>
public static class MenuActions
{
    public const string View = "view";

    public const string Edit = "edit";

    public const string Delete = "delete";

    public static readonly IReadOnlyList<string> All = [View, Edit, Delete];

    public static bool IsValid(string? action) => action is View or Edit or Delete;
}

/// <summary>
/// Casbin 里的 subject / object 命名规则。
///
/// 事实源是四张表（menus / menu_actions / roles / user_roles 以及菜单-角色绑定），
/// 每次写操作后由 <see cref="Services.PermissionPolicyProjection"/> 投影成策略——所以命名的
/// 唯一职责是让"表里的行"和"策略里的行"能一一对上，不能各写各的。
/// </summary>
public static class PermissionSubjects
{
    /// <summary>用户主体前缀：<c>user:{userId}</c>（<c>g</c> 策略的左端）。</summary>
    public const string UserPrefix = "user:";

    /// <summary>角色主体前缀：<c>role:{roleCode}</c>（<c>g</c> 的右端、<c>p</c> 的左端）。</summary>
    public const string RolePrefix = "role:";

    /// <summary>菜单权限键前缀：<c>menu:{code}</c>（<c>p</c> 的对象）。</summary>
    public const string MenuPrefix = "menu:";

    /// <summary>
    /// 超级管理员的角色标识。它是**唯一的系统角色**：
    /// 每条菜单的全部动作都会为它自动补齐显式策略（不用通配符 matcher，见 docs/PERMISSIONS.md）。
    /// </summary>
    public const string SuperadminRoleCode = "superadmin";

    public static string ForUser(long userId) => UserPrefix + userId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string ForRole(string roleCode) => RolePrefix + roleCode;

    /// <summary>菜单权限键 = <c>"menu:" + code</c>。服务端按菜单 code 生成，不接受客户端传入。</summary>
    public static string ForMenu(string menuCode) => MenuPrefix + menuCode;
}

/// <summary>
/// 对外固定用到的权限键。放在常量里，属性上的 <c>[RequirePermission]</c> 才能用编译期常量引用它，
/// 而不是散落的裸字符串。
///
/// **常量必须与菜单种子里的 code 对齐**（见 <c>SeedAuthorizationUseCase.SeedMenus</c>）：
/// 权限键 = <c>"menu:" + code</c>，而 code 是种子定的。改这里的字面量 = 换一个权限点，
/// 会让菜单绑定表里的行错位——所以要改就跟种子一起改。
/// </summary>
public static class PermissionKeys
{
    // ── 系统（「系统」分组下的三个管理页）────────────────────
    /// <summary>「系统 / 菜单管理」这个权限点。</summary>
    public const string SystemMenus = PermissionSubjects.MenuPrefix + "system-menus";

    /// <summary>「系统 / 用户管理」这个权限点。</summary>
    public const string SystemUsers = PermissionSubjects.MenuPrefix + "system-users";

    /// <summary>「系统 / 角色管理」这个权限点。</summary>
    public const string SystemRoles = PermissionSubjects.MenuPrefix + "system-roles";

    // ── 业务菜单 ────────────────────────────────────────────
    /// <summary>「总览」这个权限点（监控事件流与运行期连接状态）。</summary>
    public const string Graph = PermissionSubjects.MenuPrefix + "graph";

    /// <summary>「池列表」这个权限点（池列表与池详情共用，见 docs/PERMISSIONS.md）。</summary>
    public const string Pools = PermissionSubjects.MenuPrefix + "pools";

    /// <summary>「带单员」这个权限点。</summary>
    public const string Leads = PermissionSubjects.MenuPrefix + "leads";

    /// <summary>「跟单」这个权限点（跟单列表、详情、实盘账户与实盘流水共用）。</summary>
    public const string CopyList = PermissionSubjects.MenuPrefix + "copy-list";

    /// <summary>「交易所」这个权限点。</summary>
    public const string Exchanges = PermissionSubjects.MenuPrefix + "exchanges";

    /// <summary>「标的映射」这个权限点。</summary>
    public const string SymbolMappings = PermissionSubjects.MenuPrefix + "symbol-mappings";

    /// <summary>「规模门槛」这个权限点（也覆盖市场行情 <c>/api/markets</c>）。</summary>
    public const string Feasibility = PermissionSubjects.MenuPrefix + "feasibility";

    /// <summary>「回测面板」这个权限点。</summary>
    public const string Backtest = PermissionSubjects.MenuPrefix + "backtest";

    /// <summary>「通知」这个权限点。</summary>
    public const string Notifications = PermissionSubjects.MenuPrefix + "notifications";
}
