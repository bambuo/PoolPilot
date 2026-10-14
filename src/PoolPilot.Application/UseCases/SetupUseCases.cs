using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 系统是否还没完成首次初始化。
///
/// 读接口**不需要登录**：初始化就发生在登录之前（那时还没有任何账号），
/// 所以判定这件事只能由匿名接口回答。它泄露的信息是"这套系统还没配通知通道"——
/// 对本机工具来说这算不上秘密，而它是把初始化界面的进出时机说清楚的唯一办法。
/// </summary>
public sealed class GetSetupStatusUseCase(
    IUserRepository users,
    INotificationTemplateRepository templates,
    INotificationProviderRepository providers)
{
    public async Task<SetupStatusDto> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        bool hasAccounts = await users.CountAsync(cancellationToken).ConfigureAwait(false) > 0;

        return new SetupStatusDto
        {
            NeedsSetup = !hasAccounts,
            Reason = await DescribeNotifyStateAsync(templates, providers, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// 注册验证码现在能不能发出去。没配好时给出一句能照着做的话（界面直接显示它）；
    /// 配好了返回 <c>null</c>。
    /// </summary>
    private static async Task<string?> DescribeNotifyStateAsync(
        INotificationTemplateRepository templates,
        INotificationProviderRepository providers,
        CancellationToken cancellationToken)
    {
        NotificationTemplate? template = await templates
            .FindByPurposeAsync(NotificationPurposes.RegistrationCode, cancellationToken)
            .ConfigureAwait(false);

        if (template?.ProviderId is not { } providerId)
        {
            return "注册验证码还没有指定发送通道，它会写进服务端日志而不是寄到邮箱";
        }

        NotificationProvider? provider = await providers
            .GetAsync(providerId, cancellationToken)
            .ConfigureAwait(false);

        if (provider is null)
        {
            return "注册验证码指定的通道已经不存在了";
        }

        return provider.IsUsable
            ? null
            : $"通道「{provider.DisplayName}」还没配齐（或已停用）";
    }
}

/// <summary>
/// 首次初始化：配一条可用的通知通道，并让注册验证码的样板指向它，然后当场发一封测试邮件。
///
/// 三件事在**一次调用**里做完，因为它们是同一个前提的三半：只建通道不指样板，
/// 注册码依然发不出去（默认样板没有指向任何通道）；不测一下就结束，
/// 使用者要到注册那一刻才知道口令填错了——而那一刻他正急着注册。
/// </summary>
public sealed class InitializeNotificationsUseCase(
    GetSetupStatusUseCase setupStatus,
    INotificationProviderRepository providers,
    INotificationTemplateRepository templates,
    INotificationMailer mailer,
    INotificationLogRepository logs,
    TimeProvider clock)
{
    public async Task<NotificationDeliveryDto> ExecuteAsync(
        NotificationProviderUpsertDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        SetupStatusDto status = await setupStatus.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (!status.NeedsSetup)
        {
            // 已经有账号（系统用起来了）之后，这里就不该还能改通道：
            // 否则它是一条绕过登录的配置入口。改通道请走「通知 → 提供商」。
            throw new ArgumentException("系统已经初始化过了，改通道请去「通知 → 提供商」", nameof(request));
        }

        // 初始化阶段**只维护一条通道**：失败重试时改这一条，而不是每试一次多出一行。
        // （「通知」页允许同类多条是另一回事——那是日常使用，这里的是一次性的第一步。）
        NotificationProvider? existing = (await providers.ListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(p => p.Id)
            .FirstOrDefault();

        NotificationProvider provider = await UpsertAsync(request, existing, cancellationToken).ConfigureAwait(false);

        NotificationDefaults.Definition definition = NotificationDefaults.For(NotificationPurposes.RegistrationCode);
        NotificationTemplate? template = await templates
            .FindByPurposeAsync(definition.Purpose, cancellationToken)
            .ConfigureAwait(false);

        NotificationTemplate next = (template ?? new NotificationTemplate
        {
            Purpose = definition.Purpose,
            DisplayName = definition.DisplayName,
            Subject = definition.Subject,
            Body = definition.Body,
            ProviderId = null,
            UpdatedAt = LighterTimestamp.FromMilliseconds(0),
        }) with
        {
            ProviderId = provider.Id,
            UpdatedAt = LighterTimestamp.FromMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds()),
        };

        if (template is null)
        {
            await templates.CreateAsync(next, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await templates.UpdateAsync(next, cancellationToken).ConfigureAwait(false);
        }

        // 当场发一封：口令错、端口封、发件人未验证，这三件事只有真发一次才知道
        const string subject = "PoolPilot 通知通道测试";
        const string body =
            """
            这条消息说明这条通道能用。

            如果你收到了它，注册验证码也会发到这个邮箱。
            """;

        EmailAddress recipient = EmailAddress.Parse(provider.FromAddress!);

        NotificationDelivery delivery = await mailer
            .SendAsync(provider, recipient, subject, body, cancellationToken)
            .ConfigureAwait(false);

        await NotificationLogWriter
            .AppendAsync(
                logs,
                provider,
                NotificationPurposes.ProviderTest,
                recipient.Value,
                subject,
                body,
                delivery,
                clock,
                cancellationToken)
            .ConfigureAwait(false);

        return NotificationReplies.ToDto(delivery, $"测试邮件已发往 {recipient.Value}，去收件箱确认一下");
    }

    private async Task<NotificationProvider> UpsertAsync(
        NotificationProviderUpsertDto request,
        NotificationProvider? existing,
        CancellationToken cancellationToken)
    {
        // 初始化固定是邮箱、固定启用：这两个字段在界面上没有可选性，由服务端钉住，
        // 免得"初始化出一条停用的 SMS 通道"这种没人能解释的状态
        NotificationProviderUpsertDto normalized = request with
        {
            Kind = NotificationKinds.Email,
            IsEnabled = true,
        };

        NotificationProvider candidate = NotificationProviderEditor.Apply(normalized, existing, clock.GetUtcNow());

        // **校验在写入之前**：参数不全时不落任何行——
        // 留一条半残的通道，下一次尝试会以为"已经配过了"而去找它改
        if (!candidate.IsUsable)
        {
            throw new ArgumentException("通道参数还不全（SMTP 主机与发件人地址都要填）", nameof(request));
        }

        if (existing is null)
        {
            return await providers.CreateAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        await providers.UpdateAsync(candidate, cancellationToken).ConfigureAwait(false);

        return candidate;
    }
}
