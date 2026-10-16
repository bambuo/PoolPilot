using PoolPilot.Domain.Constants;

namespace PoolPilot.Infrastructure.Authorization;

/// <summary>
/// Casbin 模型。matcher 只有两段，各承担一件事：
///
/// <code>
/// m = g(r.sub, "role:superadmin") || (g(r.sub, p.sub) && r.obj == p.obj && r.act == p.act)
/// </code>
///
/// <list type="number">
///   <item>**第一段是超管短路**：问"这个请求主体是否（经由 g 行）继承了 superadmin 角色"。
///     注意主语必须走 <c>g()</c> 而不是 <c>r.sub == "role:superadmin"</c>——请求里的 sub 是
///     <c>user:{id}</c>，超管身份存在 g 行里，直接比字符串对真实用户永远为假（实测踩过）。
///     写 g() 还有一个附带好处：把超管的角色解绑，短路立即失效，不必再改任何东西。</item>
///   <item>第二段是普通判定，**精确匹配、无通配符**：等价于"策略表里有没有这一行"。</item>
/// </list>
///
/// 超管的特权因此**不落任何策略行**：投影只为真实授权（角色↔菜单）出行，超管的"能做什么"
/// 由 <c>GET /api/auth/permissions</c> 按同一段 matcher 逐项展开给出（见 CasbinPermissionEvaluator）。
/// </summary>
internal static class PermissionModel
{
    internal static readonly string Text =
        $"""
        [request_definition]
        r = sub, obj, act
        [policy_definition]
        p = sub, obj, act
        [role_definition]
        g = _, _
        [policy_effect]
        e = some(where (p.eft == allow))
        [matchers]
        m = g(r.sub, "{PermissionSubjects.ForRole(PermissionSubjects.SuperadminRoleCode)}") || (g(r.sub, p.sub) && r.obj == p.obj && r.act == p.act)
        """;
}
