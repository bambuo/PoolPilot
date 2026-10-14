namespace PoolPilot.Application.Dtos;

/// <summary>
/// 一条通知提供商（通道）配置。
///
/// 与交易所配置同一条口径：**口令不回传明文**，界面拿到的只有尾号——
/// 界面需要回答的只是"配的是哪一条、配齐了没有"。
/// </summary>
public sealed record NotificationProviderDto
{
    public required long Id { get; init; }

    /// <summary><c>email</c>。</summary>
    public required string Kind { get; init; }

    public required string DisplayName { get; init; }

    public required bool IsEnabled { get; init; }

    public required string? Host { get; init; }

    public required int Port { get; init; }

    public required bool UseStartTls { get; init; }

    public required string? UserName { get; init; }

    /// <summary>口令尾号；没配过为 <c>null</c>。</summary>
    public required string? PasswordHint { get; init; }

    public required string? FromAddress { get; init; }

    public required string FromName { get; init; }

    /// <summary>这条通道现在能不能真的发信（填齐且启用）。界面上据此标出"还差什么"。</summary>
    public required bool IsUsable { get; init; }
}

/// <summary>新建 / 修改一条通道。口令三态见 <see cref="Password"/>。</summary>
public sealed record NotificationProviderUpsertDto
{
    public required string Kind { get; init; }

    public required string DisplayName { get; init; }

    public required bool IsEnabled { get; init; }

    public required string? Host { get; init; }

    public required int Port { get; init; }

    public required bool UseStartTls { get; init; }

    public required string? UserName { get; init; }

    /// <summary>
    /// 口令。三态与交易所凭据一致：
    /// <c>null</c> = 界面没动它（保持原值）；空白串 = 显式清除；有值 = 换成新的。
    /// </summary>
    public required string? Password { get; init; }

    public required string? FromAddress { get; init; }

    public required string FromName { get; init; }
}

/// <summary>
/// 一类通知的样板。库里没存过时给出的是代码里的默认文本（<c>IsCustomized = false</c>），
/// 所以这个接口永远返回"实际会发出去的那一份"。
/// </summary>
public sealed record NotificationTemplateDto
{
    /// <summary>用途（<c>registration_code</c>）。</summary>
    public required string Purpose { get; init; }

    public required string DisplayName { get; init; }

    public required string Subject { get; init; }

    public required string Body { get; init; }

    /// <summary>用哪条通道发；<c>null</c> = 还没选（发不出去，界面会提示）。</summary>
    public required long? ProviderId { get; init; }

    /// <summary>是否已被界面改过（<c>false</c> = 现在用的是代码里的默认样板）。</summary>
    public required bool IsCustomized { get; init; }

    /// <summary>默认文本，供界面上的「恢复默认」使用。</summary>
    public required string DefaultSubject { get; init; }

    public required string DefaultBody { get; init; }

    /// <summary>正文里必须保留的占位符（这类通知缺了它就没有意义）。</summary>
    public required IReadOnlyList<string> RequiredPlaceholders { get; init; }
}

/// <summary>修改一类通知的样板。</summary>
public sealed record NotificationTemplateUpsertDto
{
    public required string Subject { get; init; }

    public required string Body { get; init; }

    public required long? ProviderId { get; init; }
}

/// <summary>一条投递记录。</summary>
public sealed record NotificationLogDto
{
    public required long Id { get; init; }

    public required string Purpose { get; init; }

    /// <summary>用途的界面名（"注册验证码"）。</summary>
    public required string PurposeName { get; init; }

    /// <summary>当时用的通道 Id（配置可能已被删除，所以只是参考）。</summary>
    public required long? ProviderId { get; init; }

    /// <summary>当时的通道显示名快照。</summary>
    public required string ProviderName { get; init; }

    public required string Kind { get; init; }

    public required string Recipient { get; init; }

    public required string Subject { get; init; }

    public required string Body { get; init; }

    public required bool Delivered { get; init; }

    public required bool LogFallback { get; init; }

    public required string? Error { get; init; }

    public required DateTimeOffset SentAt { get; init; }
}

/// <summary>
/// 一次投递对外的答复。
///
/// 三件事分开说：发了没有、是不是只进了日志、失败原因是什么。<see cref="Message"/>
/// 是给界面直接显示的一句话——把"未配置通道"翻译成使用者能行动的话是服务端的责任。
/// </summary>
public sealed record NotificationDeliveryDto
{
    public required bool Delivered { get; init; }

    public required bool LogFallback { get; init; }

    public required string? Error { get; init; }

    public required string Message { get; init; }
}
