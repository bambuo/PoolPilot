using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 首次初始化：只做一件事——把通知通道配好，让注册验证码发得出去。
///
/// **它是唯一一个匿名就能写的接口，这是刻意的**：初始化的定义就是"还没有账号"，
/// 而在此之前没有任何人能登录——不开口子，那个环就永远是闭的。
/// 口子开得很窄：一旦系统初始化完成（注册验证码有可用通道），
/// 这两个接口立刻失效（写接口回 400），改配置请走需要登录的「通知 → 提供商」。
///
/// 取舍写在这里：在**未初始化的那段时间**，能访问到本机端口的人可以配一条通道
/// （于是能收到验证码邮件）。服务只监听本机，这段窗口属于"第一次打开自己工作台"的那一刻。
/// </summary>
[ApiController]
[Route("api/setup")]
public sealed class SetupController(
    GetSetupStatusUseCase setupStatus,
    InitializeNotificationsUseCase initialize,
    SendSetupTestNotificationUseCase sendTest) : ControllerBase
{
    /// <summary>系统是否还没初始化。前端据此决定"进站先给初始化页还是登录页"。</summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<ActionResult<SetupStatusDto>> Status(CancellationToken cancellationToken = default) =>
        Ok(await setupStatus.ExecuteAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// 存下通道 + 让注册验证码的样板指向它。**不发测试邮件**——那是下一步
    /// （<c>POST /api/setup/notifications/test</c>），界面把它做成两个按钮：
    /// 保存只是写库，测试会真的往外发信。
    ///
    /// 已经初始化过的系统调用它会回 400——那条路径没有第二个入口。
    /// </summary>
    [HttpPost("notifications")]
    [AllowAnonymous]
    public async Task<ActionResult<SetupStatusDto>> ConfigureNotifications(
        NotificationProviderUpsertDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await initialize.ExecuteAsync(request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 用**已保存**的通道发一封测试邮件。发失败**不是 5xx**：结果里带着 SMTP 的原话，
    /// 那正是使用者唯一需要看到的东西。通道还没保存过则回 400。
    /// </summary>
    [HttpPost("notifications/test")]
    [AllowAnonymous]
    public async Task<ActionResult<NotificationDeliveryDto>> SendTestNotification(
        CancellationToken cancellationToken = default) =>
        Ok(await sendTest.ExecuteAsync(cancellationToken).ConfigureAwait(false));
}
