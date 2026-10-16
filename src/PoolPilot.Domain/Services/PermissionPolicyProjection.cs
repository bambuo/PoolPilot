using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;

namespace PoolPilot.Domain.Services;

/// <summary>一条 <c>p</c> 策略：<c>sub</c>（角色主体）→ <c>obj</c>（菜单权限键）→ <c>act</c>（动作）。</summary>
public sealed record PolicyRule(string Subject, string ObjectKey, string Action);

/// <summary>一条 <c>g</c> 策略：<c>user:{id}</c> → <c>role:{code}</c>。</summary>
public sealed record GroupingRule(string Subject, string Role);

/// <summary>从表投影出来的完整策略集合。</summary>
public sealed record PolicyProjection(
    IReadOnlyList<GroupingRule> Grouping,
    IReadOnlyList<PolicyRule> Policies);

/// <summary>
/// 把"菜单 + 动作 + 角色绑定 + 用户-角色绑定"投影成 Casbin 的 <c>p</c> / <c>g</c> 策略。
///
/// **这是纯计算**：输入是几组不可变记录，输出是两组策略行。它不读库、不碰 Casbin，
/// 所以能逐条断言（见 Domain.Tests）。
///
/// 两条刻意的约束：
/// <list type="number">
///   <item>**超管不落行**。它的特权由模型里一句 <c>g(r.sub, "role:superadmin")</c> 短路承担
///     （见 Infrastructure 的 PermissionModel），投影只为**真实授权**（角色↔菜单）出行——
///     策略集里因此没有"每人都有的一大片"噪声，一眼能看出谁被真的授过权。
///     超管的可读展开由 <c>GET /api/auth/permissions</c> 按同一段 matcher 给出。</item>
///   <item>**只认表**。策略不落单独的 <c>casbin_rule</c> 表：menu_actions / menu_roles / user_roles
///     是唯一事实源，重建即"重新投影一次"，不存在两份要对账的数据。</item>
/// </list>
/// </summary>
public static class PermissionPolicyProjection
{
    /// <summary>投影出全部策略行。</summary>
    public static PolicyProjection Project(
        IReadOnlyList<Menu> menus,
        IReadOnlyList<MenuRoleBinding> menuRoles,
        IReadOnlyList<UserRoleBinding> userRoles)
    {
        ArgumentNullException.ThrowIfNull(menus);
        ArgumentNullException.ThrowIfNull(menuRoles);
        ArgumentNullException.ThrowIfNull(userRoles);

        // 菜单 Id → 绑定的角色 code（去重，避免同一对写两遍 p 行）
        Dictionary<long, HashSet<string>> rolesByMenu = [];
        foreach (MenuRoleBinding binding in menuRoles)
        {
            if (!rolesByMenu.TryGetValue(binding.MenuId, out HashSet<string>? codes))
            {
                codes = new HashSet<string>(StringComparer.Ordinal);
                rolesByMenu[binding.MenuId] = codes;
            }

            codes.Add(binding.RoleCode);
        }

        List<PolicyRule> policies = [];

        foreach (Menu menu in menus)
        {
            if (!rolesByMenu.TryGetValue(menu.Id, out HashSet<string>? codes))
            {
                continue;
            }

            foreach (string action in menu.Actions)
            {
                foreach (string roleCode in codes)
                {
                    // 超管的授权不落行（它由 matcher 短路承担），绑定表里就算出现它也跳过——
                    // 这样"误绑一次超管"不会在策略集里留下与短路重复的噪声行。
                    if (roleCode == PermissionSubjects.SuperadminRoleCode)
                    {
                        continue;
                    }

                    policies.Add(new PolicyRule(PermissionSubjects.ForRole(roleCode), menu.PermissionKey, action));
                }
            }
        }

        List<GroupingRule> grouping =
        [
            .. userRoles.Select(b => new GroupingRule(
                PermissionSubjects.ForUser(b.UserId),
                PermissionSubjects.ForRole(b.RoleCode))),
        ];

        return new PolicyProjection(grouping, policies);
    }
}
