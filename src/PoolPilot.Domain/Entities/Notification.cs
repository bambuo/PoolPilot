using PoolPilot.Domain.Constants;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 通知提供商：真正把消息送出去的那条通道（现在是邮箱 / SMTP）。
///
/// 与 <see cref="NotificationTemplate"/> 分开两张表、而不是把参数与文案揉成一行：
/// 通道参数是"我有什么账号"（换一次 SMTP 账号，所有通知一起换），
/// 样板是"每一类通知要说什么"（改注册验证码的措辞不该动到 SMTP 端口）。
/// 揉在一起的话，加第二类通知就要把 SMTP 参数抄第二遍。
/// </summary>
public sealed record NotificationProvider
{
    /// <summary>持久化主键；未入库时为 0（由数据库分配）。</summary>
    public long Id { get; init; }

    /// <summary>通道类型。目前只有 <c>email</c>（见 <see cref="NotificationKinds"/>）。</summary>
    public required string Kind { get; init; }

    /// <summary>显示名（界面上的"这是哪条通道"）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>停用后不参与发送（配置留着，但不会被任何样板选中）。</summary>
    public required bool IsEnabled { get; init; }

    // ── kind = email 用到的参数 ──────────────────────────────
    // 与 ExchangeConfig 同一种做法：一张表承载一种类型，用 Kind 区分，
    // 该类型用不到的列留空。等真有第二种通道，再决定是加列还是另起一张表——
    // 现在就按"将来可能有"设计列结构，只会设计出一个谁都没用过的抽象。

    /// <summary>SMTP 主机。</summary>
    public string? Host { get; init; }

    /// <summary>SMTP 端口。587 = STARTTLS。</summary>
    public int Port { get; init; } = 587;

    /// <summary>是否启用 TLS（587 端口下是 STARTTLS 升级）。</summary>
    public bool UseStartTls { get; init; } = true;

    /// <summary>SMTP 登录名，通常是完整邮箱地址。</summary>
    public string? UserName { get; init; }

    /// <summary>
    /// SMTP 口令（授权码）。**明文**——与交易所凭据同一条约定：
    /// 库在本机、已 gitignore，而"密钥与密文同库"等于没防（见 ExchangeConfig 的说明）。
    /// 界面上只回显尾号，不提供查看入口。
    /// </summary>
    public string? Password { get; init; }

    /// <summary>发件人地址。</summary>
    public string? FromAddress { get; init; }

    /// <summary>发件人显示名。</summary>
    public string FromName { get; init; } = "PoolPilot";

    public required LighterTimestamp CreatedAt { get; init; }

    /// <summary>这条通道现在能不能真的发信（邮箱：主机与发件人都得填）。</summary>
    public bool IsUsable => IsEnabled && Kind == NotificationKinds.Email
        && !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(FromAddress)
        && Port is > 0 and <= 65_535;
}

/// <summary>
/// 通知样板：某一类通知要说什么（主题 + 正文）。
///
/// <see cref="ProviderId"/> 决定"这类通知由哪条通道发"——多个同类提供商并存时，
/// 选择规则就落在这里，而不是再加一个"默认提供商"之类的全局开关：
/// 那种开关会让"到底是谁发的"变成一个要跨两处看才能回答的问题。
/// </summary>
public sealed record NotificationTemplate
{
    /// <summary>持久化主键；未入库时为 0。</summary>
    public long Id { get; init; }

    /// <summary>通知用途，一类一条（见 <see cref="NotificationPurposes"/>）。</summary>
    public required string Purpose { get; init; }

    /// <summary>界面上的名字（"注册验证码"）。</summary>
    public required string DisplayName { get; init; }

    public required string Subject { get; init; }

    /// <summary>正文。可用占位符由 <c>NotificationTemplateRenderer</c> 定义。</summary>
    public required string Body { get; init; }

    /// <summary>用哪条通道发；<c>null</c> = 还没选（等于发不出去，界面会提示）。</summary>
    public long? ProviderId { get; init; }

    public required LighterTimestamp UpdatedAt { get; init; }
}

/// <summary>
/// 通知记录：**每一次投递都留一条**，成功与否都留。
///
/// 它回答的是"我到底发出去了什么、用的哪条通道、成没成"——这类问题的现场很容易消失：
/// 提供商改名或删掉之后，光看配置已看不出当时用的是它；而验证码本身又只在邮件里出现过一次。
/// 所以这里存的是**快照**（通道显示名、类型、渲染后的主题与正文），不是指向配置的外键。
/// <see cref="ProviderId"/> 只作参考，可能指向一条已经不存在的配置。
/// </summary>
public sealed record NotificationLog
{
    public long Id { get; init; }

    /// <summary>通知用途（见 <see cref="NotificationPurposes"/>）。</summary>
    public required string Purpose { get; init; }

    /// <summary>当时用的提供商 Id（参考值；配置可能已被删除）。</summary>
    public long? ProviderId { get; init; }

    /// <summary>当时的提供商显示名快照。</summary>
    public required string ProviderName { get; init; }

    /// <summary>当时的通道类型快照（<c>email</c>）。</summary>
    public required string Kind { get; init; }

    /// <summary>收件人。</summary>
    public required string Recipient { get; init; }

    /// <summary>渲染后的主题与正文（即真正发出去的内容）。</summary>
    public required string Subject { get; init; }

    public required string Body { get; init; }

    /// <summary>是否真的交给了外部通道。降级到日志（没有可用通道）与发送失败都是 false。</summary>
    public required bool Delivered { get; init; }

    /// <summary>是否只写进了服务端日志（没有配置可用通道时的降级）。</summary>
    public required bool LogFallback { get; init; }

    /// <summary>失败原因（SMTP 拒绝、认证失败等）；成功时为 <c>null</c>。</summary>
    public string? Error { get; init; }

    public required LighterTimestamp SentAt { get; init; }
}
