using Microsoft.Extensions.Caching.Memory;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Infrastructure.Auth;

/// <summary>
/// 登录失败计数与锁定的内存实现。
///
/// 计数按邮箱键控，且**不跨进程重启保留**：重启即清零是可以接受的——
/// 重启频率远低于爆破所需的尝试频率，而把这套计数落库又会引入"什么时候清"的额外问题。
/// </summary>
public sealed class MemoryLoginThrottle(IMemoryCache cache) : ILoginThrottle
{
    private static string FailuresKey(EmailAddress email) => $"auth:login-failures:{email.Value}";

    private static string LockoutKey(EmailAddress email) => $"auth:login-lockout:{email.Value}";

    public Task<TimeSpan> RemainingLockoutAsync(EmailAddress email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        if (!cache.TryGetValue(LockoutKey(email), out DateTimeOffset until))
        {
            return Task.FromResult(TimeSpan.Zero);
        }

        TimeSpan remaining = until - DateTimeOffset.UtcNow;
        return Task.FromResult(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }

    public Task RecordFailureAsync(
        EmailAddress email,
        int maxFailures,
        TimeSpan lockout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        int failures = cache.TryGetValue(FailuresKey(email), out int recorded) ? recorded + 1 : 1;

        if (failures >= maxFailures)
        {
            // 达到上限：换一把"锁定"的钥匙，并把计数清零——
            // 否则解锁之后第一个错误又会立刻触发下一轮锁定
            cache.Set(LockoutKey(email), DateTimeOffset.UtcNow + lockout, lockout);
            cache.Remove(FailuresKey(email));

            return Task.CompletedTask;
        }

        // 计数的有效期取锁定时长：一段时间不再失败，历史就不该再算在他头上
        cache.Set(FailuresKey(email), failures, lockout);

        return Task.CompletedTask;
    }

    public Task ClearAsync(EmailAddress email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        cache.Remove(FailuresKey(email));
        cache.Remove(LockoutKey(email));

        return Task.CompletedTask;
    }
}
