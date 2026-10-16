using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

using PoolPilot.Application.Abstractions;

namespace PoolPilot.Api.Authorization;

/// <summary>
/// 声明式权限点：<c>[RequirePermission("menu:system-menus", "view")]</c>。
///
/// 与全局"控制器一律要登录"是两层不同的判断，顺序也有讲究：
/// <list type="number">
///   <item>授权**中间件**先跑：没登录的请求在这里被驳回（401，见 Program.cs 的
///     <c>OnRedirectToLogin</c>）。所以匿名请求永远不会走到本过滤器。</item>
///   <item>认出来了是谁之后，本过滤器再问"这个人能不能"：不通过就 <see cref="ForbidResult"/>。</item>
/// </list>
///
/// 用 <see cref="ForbidResult"/> 而不是直接写 403：它会走到 Cookie 处理器的
/// <c>OnRedirectToAccessDenied</c>，那里产出的正是前端约定的 <c>{ error: "..." }</c> JSON——
/// 与"登录了但没权限"的其余场景保持同一种形状，前端不必为两种 403 各写一套分支。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequirePermissionAttribute : Attribute, IAuthorizationFilter
{
    private readonly string _permissionKey;

    private readonly string _action;

    public RequirePermissionAttribute(string permissionKey, string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        _permissionKey = permissionKey;
        _action = action;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IPermissionEvaluator evaluator = context.HttpContext.RequestServices
            .GetRequiredService<IPermissionEvaluator>();

        if (!evaluator.Has(_permissionKey, _action))
        {
            context.Result = new ForbidResult(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
