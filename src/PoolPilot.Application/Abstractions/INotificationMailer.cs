using PoolPilot.Domain.Entities;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 一次投递的结果。
///
/// 三种情形分开表达，而不是用"成/败"两个字盖住：
/// · <see cref="Delivered"/>：真的交给了外部通道（SMTP 收下了）。
/// · <see cref="LogFallback"/>：**没有可用通道**，内容只写进了服务端日志（本机调试时验证码就在那里）。
/// · <see cref="Error"/>：通道可用但发失败了（认证不过、被拒、网络不通），原因原样带回来。
/// </summary>
public sealed record NotificationDelivery
{
    public required bool Delivered { get; init; }

    public required bool LogFallback { get; init; }

    public string? Error { get; init; }

    /// <summary>没有可用通道（未配置或已停用）时的降级结果。</summary>
    public static NotificationDelivery FallbackToLog() => new()
    {
        Delivered = false,
        LogFallback = true,
        Error = null,
    };

    /// <summary>发失败。</summary>
    public static NotificationDelivery Failed(string error) => new()
    {
        Delivered = false,
        LogFallback = false,
        Error = error,
    };

    /// <summary>发成功。</summary>
    public static NotificationDelivery Sent() => new()
    {
        Delivered = true,
        LogFallback = false,
        Error = null,
    };
}

/// <summary>
/// 投递通道：把一封已经渲染好的邮件送出去。
///
/// **不抛异常**：发送失败（口令不对、主机拒绝、超时）是要被记录、也要被展示给使用者的事实，
/// 不是程序异常。抛出去只会被中间件翻译成一句"服务内部错误"，而真正的线索（SMTP 说了什么）
/// 恰好是使用者唯一需要看到的东西。
/// </summary>
public interface INotificationMailer
{
    /// <param name="provider">
    /// 选定通道；<c>null</c> 或不可用时降级为只写服务端日志，并在结果里如实说明。
    /// </param>
    Task<NotificationDelivery> SendAsync(
        NotificationProvider? provider,
        EmailAddress recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default);
}
