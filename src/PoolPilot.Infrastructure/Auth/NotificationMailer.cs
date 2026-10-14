using System.Net;
using System.Net.Mail;
using System.Text;

using Microsoft.Extensions.Logging;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Infrastructure.Auth;

/// <summary>
/// 投递器：把一封渲染好的邮件交给 SMTP；没有可用通道时**降级为只写服务端日志**。
///
/// 用 .NET 自带的 <see cref="SmtpClient"/>，不引第三方库。两点取舍写在这里，
/// 省得下次有人照着报错去查：
/// · 它只支持"先明文连接、再用 STARTTLS 升级"（587 端口那套）；465 的隐式 SSL 连不上，
///   而各家邮箱都同时提供 587。
/// · 微软标注它"不建议用于新项目"，理由是缺少现代协议支持。本项目只需要发一封纯文本短邮件，
///   为它引一个包不划算（依赖最小化）。
///
/// **失败不抛异常**：口令过期、发件人未验证、端口被拦——这些是配置问题，
/// 使用者需要看到 SMTP 的原话。抛出去只会被中间件换成一句"服务内部错误"，
/// 而那恰好把唯一有用的线索弄丢了。
/// </summary>
public sealed class NotificationMailer(ILogger<NotificationMailer> logger) : INotificationMailer
{
    public async Task<NotificationDelivery> SendAsync(
        NotificationProvider? provider,
        EmailAddress recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);

        if (provider is null || !provider.IsUsable)
        {
            logger.LogWarning(
                "没有可用的通知通道（{Reason}），这封邮件只写进服务端日志——仅限本机调试使用。"
                + "去「通知 → 提供商」配一条就能真正发出去。\n收件人：{Recipient}\n主题：{Subject}\n{Body}",
                provider is null ? "样板还没指向任何通道" : "通道未启用或参数不全",
                recipient.Value,
                subject,
                body);

            return NotificationDelivery.FallbackToLog();
        }

        // IsUsable 已经保证它非空；这里再取一次是为了让类型系统也知道
        string from = provider.FromAddress!;

        try
        {
            using MailMessage message = new(from, recipient.Value, subject, body)
            {
                // 中文主题与正文必须显式声明 UTF-8：默认编码会把它们变成乱码
                BodyEncoding = Encoding.UTF8,
                SubjectEncoding = Encoding.UTF8,
            };

            message.From = new MailAddress(from, provider.FromName, Encoding.UTF8);

            using SmtpClient client = new(provider.Host, provider.Port)
            {
                EnableSsl = provider.UseStartTls,
                Credentials = string.IsNullOrWhiteSpace(provider.UserName)
                    ? null
                    : new NetworkCredential(provider.UserName, provider.Password),
            };

            await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "通知已发往 {Recipient}（通道：{Provider}）",
                recipient.Value,
                provider.DisplayName);

            return NotificationDelivery.Sent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "发送通知失败（通道：{Provider}）", provider.DisplayName);

            return NotificationDelivery.Failed(Describe(ex));
        }
    }

    /// <summary>
    /// 取最内层那句话。SMTP 的报错常常套好几层（外层只说"发送失败"，
    /// 真正的原因——认证不过、发件人未验证——在最里面）。
    /// </summary>
    private static string Describe(Exception exception)
    {
        Exception innermost = exception;

        while (innermost.InnerException is { } inner)
        {
            innermost = inner;
        }

        return innermost.Message;
    }
}
