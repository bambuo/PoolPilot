namespace PoolPilot.Domain.ValueObjects;

/// <summary>
/// 登录邮箱。**归一化后再比较、再落库**（去空白 + 转小写）。
///
/// 归一化放在类型里而不是散在各个调用点：注册时存 <c>A@x.com</c>、登录时敲 <c>a@x.com</c>
/// 是两个账号还是同一个，取决于这件事有没有统一口径。散着写就一定会漏某一处。
///
/// 这里的校验**刻意不是 RFC 5322 的完备解析**：真正的校验是"能不能收到那封验证码邮件"，
/// 这件事由收信完成。这一层只挡明显不是邮箱的输入（没有 @、域里没有点、长度离谱），
/// 免得把一次必然失败的注册拖到发信那一步才拒绝。
/// </summary>
public sealed record EmailAddress
{
    /// <summary>RFC 5321：一个地址的总长度上限。</summary>
    public const int MaxLength = 254;

    /// <summary>RFC 5321：<c>@</c> 之前那一段的长度上限。</summary>
    private const int MaxLocalLength = 64;

    private EmailAddress(string value) => Value = value;

    public string Value { get; }

    public static EmailAddress Parse(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);

        string normalized = raw.Trim().ToLowerInvariant();

        if (normalized.Length > MaxLength)
        {
            throw new ArgumentException($"邮箱地址过长（上限 {MaxLength} 个字符）", nameof(raw));
        }

        int at = normalized.IndexOf('@', StringComparison.Ordinal);

        if (at < 0 || at != normalized.LastIndexOf('@'))
        {
            throw new ArgumentException($"「{raw.Trim()}」不像一个邮箱地址：应当有且只有一个 @", nameof(raw));
        }

        string local = normalized[..at];
        string domain = normalized[(at + 1)..];

        if (local.Length is 0 or > MaxLocalLength)
        {
            throw new ArgumentException("@ 前那一段不能为空，也不能超过 64 个字符", nameof(raw));
        }

        if (local.StartsWith('.') || local.EndsWith('.') || local.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("@ 前那一段的点号位置不对（不能以点开头/结尾，也不能连着两个点）", nameof(raw));
        }

        if (domain.Length < 3 || !domain.Contains('.', StringComparison.Ordinal)
            || domain.StartsWith('.') || domain.EndsWith('.')
            || domain.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("@ 后面应当是一个域名（例如 example.com）", nameof(raw));
        }

        if (normalized.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("邮箱地址里不能有空白字符", nameof(raw));
        }

        return new EmailAddress(normalized);
    }

    public override string ToString() => Value;
}
