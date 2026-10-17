using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

using PoolPilot.Application.Abstractions;

namespace PoolPilot.Api.Authorization;

/// <summary>
/// 登录/注册总开关：<c>[AccountsGate]</c>。关闭时回 404，而不是 403。
///
/// 面向“只开放公开页”的部署。用 404 与公开页开关一致：没有开放的入口不该让探测者
/// 通过状态码确认它存在。放在 action 级而不是全局：<c>GET /api/auth/me</c> 等自省端点
/// 仍要服务已有的会话；这里只关“新登录/新注册/MFA/首次初始化”这几条入口。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AccountsGateAttribute : Attribute, IAuthorizationFilter
{
    /// <summary>关闭时的统一文案（形状仍是 <c>{"error": string}</c>）。</summary>
    public const string DisabledMessage = "登录与注册未启用。";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        AuthSettings settings = context.HttpContext.RequestServices.GetRequiredService<AuthSettings>();

        if (!settings.AccountsEnabled)
        {
            context.Result = new NotFoundObjectResult(new { error = DisabledMessage });
        }
    }
}
