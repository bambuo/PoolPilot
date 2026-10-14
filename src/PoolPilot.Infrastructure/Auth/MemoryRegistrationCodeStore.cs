using Microsoft.Extensions.Caching.Memory;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Infrastructure.Auth;

/// <summary>
/// 注册验证码的内存暂存（<see cref="IMemoryCache"/>）。
///
/// 为什么是内存而不是库：验证码是**几分钟内有效的一次性凭据**，进程重启后它就该失效——
/// 存进库反而要自己写清理与"启动时清空"的逻辑。本系统是单进程单体，内存缓存没有一致性问题。
///
/// 计数（失败次数）刻意不加锁：并发下最坏是少记一次，而它上面还有"整枚作废"与重发冷却兜底。
/// 这与 <c>MarketDataCache</c> 的取舍一致——给一个人用的本机工具，不值得为竞态引入一把锁。
/// </summary>
public sealed class MemoryRegistrationCodeStore(IMemoryCache cache) : IRegistrationCodeStore
{
    private static string CodeKey(EmailAddress email) => $"auth:code:{email.Value}";

    private static string CooldownKey(EmailAddress email) => $"auth:code-cooldown:{email.Value}";

    public Task<TimeSpan> RemainingCooldownAsync(EmailAddress email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        if (!cache.TryGetValue(CooldownKey(email), out DateTimeOffset availableAt))
        {
            return Task.FromResult(TimeSpan.Zero);
        }

        TimeSpan remaining = availableAt - DateTimeOffset.UtcNow;
        return Task.FromResult(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }

    public Task StoreAsync(
        EmailAddress email,
        string code,
        TimeSpan lifetime,
        TimeSpan resendCooldown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        // 覆盖同一邮箱上一枚未用的码：使用者连点两次"发送"，手上那枚必须是最后一次发出去的那枚
        cache.Set(CodeKey(email), new CodeEntry { Code = code }, lifetime);
        cache.Set(CooldownKey(email), DateTimeOffset.UtcNow + resendCooldown, resendCooldown);

        return Task.CompletedTask;
    }

    public Task<bool> TryConsumeAsync(
        EmailAddress email,
        string code,
        int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        string key = CodeKey(email);

        if (!cache.TryGetValue(key, out CodeEntry? entry) || entry is null)
        {
            return Task.FromResult(false);
        }

        if (string.Equals(entry.Code, code, StringComparison.Ordinal))
        {
            Forget(email);
            return Task.FromResult(true);
        }

        entry.Failures++;

        if (entry.Failures >= maxAttempts)
        {
            // 用完就从缓存里拿掉：留着它只会让人继续对同一枚码试下去
            Forget(email);
        }

        return Task.FromResult(false);
    }

    /// <summary>把码与冷却一起清掉。冷却跟着码走——码没了，等它也就没有意义了。</summary>
    private void Forget(EmailAddress email)
    {
        cache.Remove(CodeKey(email));
        cache.Remove(CooldownKey(email));
    }

    private sealed class CodeEntry
    {
        public required string Code { get; init; }

        public int Failures { get; set; }
    }
}
