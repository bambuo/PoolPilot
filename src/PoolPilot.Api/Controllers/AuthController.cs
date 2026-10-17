using System.Globalization;
using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PoolPilot.Api.Authorization;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 注册、登录与 MFA。
///
/// **本控制器的每个端点都要显式标 <see cref="AllowAnonymousAttribute"/>**：
/// 全局策略是"控制器一律要登录"（Program.cs 里 MapControllers().RequireAuthorization()），
/// 这里逐个开口子，而不是把整个控制器设成匿名——后者会让"哪些接口不需要登录"
/// 变成一个类级属性，日后往这里加接口时很容易默认匿名。
///
/// 签发会话只发生在 MFA 完成之后（<c>mfa/verify</c> 与 <c>mfa/bind</c>）：
/// 登录第一步永远不签发，因为第二个因子是强制的。
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    RequestRegistrationCodeUseCase requestRegistrationCode,
    RegisterUserUseCase registerUser,
    LoginUseCase login,
    VerifyMfaLoginUseCase verifyMfaLogin,
    BindMfaOnLoginUseCase bindMfaOnLogin,
    GetSessionUserUseCase getSessionUser,
    GetPermissionsUseCase getPermissions) : ControllerBase
{
    /// <summary>
    /// 发送注册验证码。
    ///
    /// 回 200 而不是 204：结果里带着"到底发出去了没有"——没配通知通道时会降级成写服务端日志，
    /// 那种情况必须在界面上说出来，否则使用者只会看着"发送成功"等一封永远不会到的邮件。
    /// </summary>
    [HttpPost("register/code")]
    [AllowAnonymous]
    [AccountsGate]
    public async Task<ActionResult<NotificationDeliveryDto>> SendRegistrationCode(
        RegistrationCodeRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await requestRegistrationCode.ExecuteAsync(request.Email, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 校验验证码与两次密码，建账号。**不签发会话**——注册完仍要走一次登录（含绑定 MFA）。
    ///
    /// 回 204（而不是 200 + 空体）：它确实没有内容可回，而"200 但空体"会让客户端
    /// 在"要不要解析响应"上二选一，总有一边会踩空。
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [AccountsGate]
    public async Task<IActionResult> Register(
        RegisterRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await registerUser.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>登录第一步：验邮箱与口令，返回"下一步做什么"（输码 / 扫码绑定）。</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [AccountsGate]
    public async Task<ActionResult<LoginResultDto>> Login(
        LoginRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await login.ExecuteAsync(request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>登录第二步（已绑定）：六位码换会话。</summary>
    [HttpPost("mfa/verify")]
    [AllowAnonymous]
    [AccountsGate]
    public async Task<ActionResult<SessionDto>> VerifyMfa(
        MfaCodeRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        SessionDto session = await verifyMfaLogin.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        await SignInAsync(session).ConfigureAwait(false);

        return Ok(session);
    }

    /// <summary>登录第二步（未绑定）：验过码即完成绑定，并换到会话。</summary>
    [HttpPost("mfa/bind")]
    [AllowAnonymous]
    [AccountsGate]
    public async Task<ActionResult<SessionDto>> BindMfa(
        MfaCodeRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        SessionDto session = await bindMfaOnLogin.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        await SignInAsync(session).ConfigureAwait(false);

        return Ok(session);
    }

    /// <summary>
    /// 登出。幂等：没登录时调用也返回 204——"确保现在没登录"这件事本来就做到了。
    /// 服务端不存会话，所以清掉 Cookie 就是全部工作。
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// 当前会话是谁。/ 前端靠它判断"要不要显示登录页"，因此它必须同时做到两件事：
    /// 没登录时回 401（而不是 200 + 空对象），登录着时给出邮箱。
    /// </summary>
    [HttpGet("me")]
    public async Task<ActionResult<SessionDto>> Me(CancellationToken cancellationToken = default)
    {
        long? userId = CurrentUserId();

        if (userId is null)
        {
            return Unauthorized(new { error = "未登录" });
        }

        // 特意落一次库：Cookie 在到期前一直"有效"，而它引用的账号可能已经不在了
        SessionDto? session = await getSessionUser.ExecuteAsync(userId.Value, cancellationToken).ConfigureAwait(false);

        return session is null
            ? Unauthorized(new { error = "这个会话对应的账号已不存在，请重新登录" })
            : Ok(session);
    }

    /// <summary>
    /// 当前用户的角色与逐权限点的动作。前端据此决定显示哪些菜单、藏起哪些按钮。
    ///
    /// **登录即可访问**：它就是用来回答"我能做什么"的。未登录时授权中间件会先回 401，
    /// 所以这里不必再判一次。
    /// </summary>
    [HttpGet("permissions")]
    public async Task<ActionResult<PermissionsDto>> Permissions(CancellationToken cancellationToken = default) =>
        Ok(await getPermissions.ExecuteAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>签发会话 Cookie。</summary>
    private Task SignInAsync(SessionDto session)
    {
        // 断言的是"这人现在是谁"——账号 Id 用 NameIdentifier，邮箱用 Name。
        // **不放权限声明**：权限是动态的（改一次角色绑定就该立刻生效），塞进有 7 天有效期的
        // Cookie 里会留下一条比事实更长寿的陈旧授权。判定一律现查 Casbin（见 RequirePermissionAttribute）。
        ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, session.Email),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        return HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));
    }

    private long? CurrentUserId()
    {
        string? raw = User.FindFirstValue(ClaimTypes.NameIdentifier);

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? id : null;
    }
}
