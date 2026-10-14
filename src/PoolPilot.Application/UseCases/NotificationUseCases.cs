using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>列出全部通知通道。</summary>
public sealed class ListNotificationProvidersUseCase(INotificationProviderRepository providers)
{
    public async Task<IReadOnlyList<NotificationProviderDto>> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NotificationProvider> rows = await providers.ListAsync(cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(ToDto)];
    }

    internal static NotificationProviderDto ToDto(NotificationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return new NotificationProviderDto
        {
            Id = provider.Id,
            Kind = provider.Kind,
            DisplayName = provider.DisplayName,
            IsEnabled = provider.IsEnabled,
            Host = provider.Host,
            Port = provider.Port,
            UseStartTls = provider.UseStartTls,
            UserName = provider.UserName,
            PasswordHint = SecretHint.Of(provider.Password),
            FromAddress = provider.FromAddress,
            FromName = provider.FromName,
            IsUsable = provider.IsUsable,
        };
    }
}

/// <summary>新建一条通知通道。</summary>
public sealed class CreateNotificationProviderUseCase(INotificationProviderRepository providers)
{
    public async Task<NotificationProviderDto> ExecuteAsync(
        NotificationProviderUpsertDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        NotificationProvider created = await providers.CreateAsync(
            NotificationProviderEditor.Apply(request, existing: null, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        return ListNotificationProvidersUseCase.ToDto(created);
    }
}

/// <summary>修改一条通知通道。口令三态与交易所凭据一致（见 <c>NotificationProviderUpsertDto.Password</c>）。</summary>
public sealed class UpdateNotificationProviderUseCase(INotificationProviderRepository providers)
{
    public async Task<NotificationProviderDto> ExecuteAsync(
        long id,
        NotificationProviderUpsertDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        NotificationProvider existing = await providers.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"通知通道 {id} 不存在", nameof(id));

        NotificationProvider updated = NotificationProviderEditor.Apply(request, existing, DateTimeOffset.UtcNow);

        await providers.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);

        return ListNotificationProvidersUseCase.ToDto(updated);
    }
}

/// <summary>
/// 删除一条通知通道。
///
/// 被样板引用时**拒绝删除**：删掉通道而把样板留在原地，会造出"通知看着配好了、
/// 其实发不出去"的状态——那种状态在注册时才会暴露，而那时人正急着注册。
/// </summary>
public sealed class DeleteNotificationProviderUseCase(INotificationProviderRepository providers)
{
    public async Task ExecuteAsync(long id, CancellationToken cancellationToken = default)
    {
        int usedBy = await providers.CountTemplatesUsingAsync(id, cancellationToken).ConfigureAwait(false);

        if (usedBy > 0)
        {
            throw new ArgumentException(
                $"还有 {usedBy} 类通知在用这条通道，先把它们改到别的通道（或取消选择）再删除",
                nameof(id));
        }

        await providers.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 用某条通道发一封测试邮件。
///
/// 存在的理由：SMTP 配错的样子（口令过期、端口被封、发件人未验证）与"配置正确"在界面上长得一样，
/// 只有真发一封才知道。它发到该通道自己的发件人地址——不需要使用者再输一个收件人，
/// 而"能给自己发到"正是接下来注册验证码要依赖的能力。
/// </summary>
public sealed class SendTestNotificationUseCase(
    INotificationProviderRepository providers,
    INotificationMailer mailer,
    INotificationLogRepository logs,
    TimeProvider clock)
{
    public async Task<NotificationDeliveryDto> ExecuteAsync(
        long providerId,
        CancellationToken cancellationToken = default)
    {
        NotificationProvider provider = await providers.GetAsync(providerId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"通知通道 {providerId} 不存在", nameof(providerId));

        if (provider.FromAddress is null)
        {
            throw new ArgumentException("这条通道还没填发件人地址，测试邮件没有收件人", nameof(providerId));
        }

        const string subject = "PoolPilot 通知通道测试";
        const string body =
            """
            这条消息说明这条通道能用。

            如果你收到了它，注册验证码也会发到这个邮箱。
            """;

        EmailAddress recipient = EmailAddress.Parse(provider.FromAddress);

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

        return NotificationReplies.ToDto(delivery, $"测试邮件已发往 {recipient.Value}");
    }
}

/// <summary>列出全部通知样板（库里的与默认的合成后）。</summary>
public sealed class ListNotificationTemplatesUseCase(INotificationTemplateRepository templates)
{
    public async Task<IReadOnlyList<NotificationTemplateDto>> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NotificationTemplate> stored = await templates.ListAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. NotificationDefaults.All.Select(definition =>
                ListNotificationTemplatesUseCase.ToDto(
                    definition,
                    NotificationDefaults.Effective(
                        definition,
                        stored.FirstOrDefault(t => t.Purpose == definition.Purpose)))),
        ];
    }

    internal static NotificationTemplateDto ToDto(NotificationDefaults.Definition definition, NotificationTemplate effective)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(effective);

        return new NotificationTemplateDto
        {
            Purpose = definition.Purpose,
            DisplayName = definition.DisplayName,
            Subject = effective.Subject,
            Body = effective.Body,
            ProviderId = effective.ProviderId,
            IsCustomized = effective.Id != 0,
            DefaultSubject = definition.Subject,
            DefaultBody = definition.Body,
            RequiredPlaceholders = NotificationDefaults.RequiredPlaceholders(definition),
        };
    }
}

/// <summary>
/// 修改一类通知的样板（库里没有就新建一条）。
///
/// 校验里有两条是**冲着"静默失效"去的**：
/// · 正文必须保留这类通知的必备占位符。注册验证码里没有 <c>{code}</c>，收信人就永远看不到码，
///   而系统这边"发送成功"——这种失败在界面上完全看不出来。
/// · 选了提供商就必须真的存在，否则等于把通知指向一个不存在的通道。
/// </summary>
public sealed class UpdateNotificationTemplateUseCase(
    INotificationTemplateRepository templates,
    INotificationProviderRepository providers)
{
    public async Task<NotificationTemplateDto> ExecuteAsync(
        string purpose,
        NotificationTemplateUpsertDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        NotificationDefaults.Definition definition = NotificationDefaults.For(purpose);

        if (string.IsNullOrWhiteSpace(request.Subject))
        {
            throw new ArgumentException("主题不能为空", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            throw new ArgumentException("正文不能为空", nameof(request));
        }

        foreach (string placeholder in NotificationDefaults.RequiredPlaceholders(definition))
        {
            if (!request.Body.Contains(placeholder, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"正文里必须留下 {placeholder} —— 它是收信人唯一能看到那串码的地方。"
                    + "去掉之后，系统这边照样报「发送成功」，而对方永远看不到码。",
                    nameof(request));
            }
        }

        if (request.ProviderId is { } providerId)
        {
            _ = await providers.GetAsync(providerId, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException($"通知通道 {providerId} 不存在", nameof(request));
        }

        NotificationTemplate? existing = await templates
            .FindByPurposeAsync(purpose, cancellationToken)
            .ConfigureAwait(false);

        NotificationTemplate next = (existing ?? new NotificationTemplate
        {
            Purpose = definition.Purpose,
            DisplayName = definition.DisplayName,
            Subject = definition.Subject,
            Body = definition.Body,
            ProviderId = null,
            UpdatedAt = LighterTimestamp.FromMilliseconds(0),
        }) with
        {
            Subject = request.Subject.Trim(),
            Body = request.Body,
            ProviderId = request.ProviderId,
            UpdatedAt = LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        };

        NotificationTemplate saved;

        if (existing is null)
        {
            saved = await templates.CreateAsync(next, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await templates.UpdateAsync(next, cancellationToken).ConfigureAwait(false);
            saved = next;
        }

        return ListNotificationTemplatesUseCase.ToDto(definition, saved);
    }
}

/// <summary>按时间倒序列出投递记录。</summary>
public sealed class ListNotificationLogsUseCase(INotificationLogRepository logs)
{
    /// <summary>一次最多返回多少条。记录是只追加的，界面只需要"最近发生了什么"。</summary>
    public const int PageSize = 200;

    public async Task<IReadOnlyList<NotificationLogDto>> ExecuteAsync(
        string? purpose = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NotificationLog> rows = await logs
            .ListAsync(PageSize, purpose, cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(ToDto)];
    }

    private static NotificationLogDto ToDto(NotificationLog entry) => new()
    {
        Id = entry.Id,
        Purpose = entry.Purpose,
        PurposeName = NotificationDefaults.DisplayNameOf(entry.Purpose),
        ProviderId = entry.ProviderId,
        ProviderName = entry.ProviderName,
        Kind = entry.Kind,
        Recipient = entry.Recipient,
        Subject = entry.Subject,
        Body = entry.Body,
        Delivered = entry.Delivered,
        LogFallback = entry.LogFallback,
        Error = entry.Error,
        SentAt = entry.SentAt.Utc,
    };
}

/// <summary>新建与修改共用的一套字段规则（单拎出来，两条路径不会各写一份）。</summary>
internal static class NotificationProviderEditor
{
    internal static NotificationProvider Apply(
        NotificationProviderUpsertDto request,
        NotificationProvider? existing,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new ArgumentException("显示名不能为空——同类通道可以配多条，名字是区分它们的唯一依据", nameof(request));
        }

        if (request.Kind != NotificationKinds.Email)
        {
            throw new ArgumentException($"暂不支持的通知类型「{request.Kind}」（目前只有 email）", nameof(request));
        }

        if (request.Port is <= 0 or > 65_535)
        {
            throw new ArgumentException($"端口 {request.Port} 不合法", nameof(request));
        }

        return new NotificationProvider
        {
            Id = existing?.Id ?? 0L,
            Kind = request.Kind,
            DisplayName = request.DisplayName.Trim(),
            IsEnabled = request.IsEnabled,
            Host = Blank(request.Host),
            Port = request.Port,
            UseStartTls = request.UseStartTls,
            UserName = Blank(request.UserName),

            // 三态：null = 保持原值，空白 = 清除，有值 = 换新（与交易所凭据同一条规则）
            Password = ExchangeCredentials.Apply(request.Password, existing?.Password),
            FromAddress = Blank(request.FromAddress),
            FromName = string.IsNullOrWhiteSpace(request.FromName) ? "PoolPilot" : request.FromName.Trim(),
            CreatedAt = existing?.CreatedAt
                ?? LighterTimestamp.FromMilliseconds(nowUtc.ToUnixTimeMilliseconds()),
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>投递记录的写入（注册流程与测试发送共用一处）。</summary>
internal static class NotificationLogWriter
{
    internal static Task AppendAsync(
        INotificationLogRepository logs,
        NotificationProvider? provider,
        string purpose,
        string recipient,
        string subject,
        string body,
        NotificationDelivery delivery,
        TimeProvider clock,
        CancellationToken cancellationToken) =>
        logs.AppendAsync(
            new NotificationLog
            {
                Purpose = purpose,
                ProviderId = provider?.Id,

                // 显示名取快照：通道日后改名或被删，这条记录仍要能说清当时用的是谁
                ProviderName = provider?.DisplayName ?? $"（未配置通道：{NotificationDefaults.DisplayNameOf(purpose)}）",
                Kind = provider?.Kind ?? NotificationKinds.Email,
                Recipient = recipient,
                Subject = subject,
                Body = body,
                Delivered = delivery.Delivered,
                LogFallback = delivery.LogFallback,
                Error = delivery.Error,
                SentAt = LighterTimestamp.FromMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds()),
            },
            cancellationToken);
}

/// <summary>投递结果 → 对外答复。三句话的措辞只有这一处写法。</summary>
internal static class NotificationReplies
{
    internal static NotificationDeliveryDto ToDto(NotificationDelivery delivery, string deliveredMessage)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        return new NotificationDeliveryDto
        {
            Delivered = delivery.Delivered,
            LogFallback = delivery.LogFallback,
            Error = delivery.Error,
            Message = delivery switch
            {
                { Delivered: true } => deliveredMessage,
                { LogFallback: true } => "还没有可用的通知通道（未配置或已停用），内容只写进了服务端日志——"
                    + "去「通知 → 提供商」配一条就能真正发出去",
                _ => $"发送失败：{delivery.Error}",
            },
        };
    }
}
