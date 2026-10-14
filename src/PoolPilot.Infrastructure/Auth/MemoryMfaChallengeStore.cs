using System.Buffers.Text;
using System.Security.Cryptography;

using Microsoft.Extensions.Caching.Memory;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Infrastructure.Auth;

/// <summary>
/// 登录挑战的内存暂存（<see cref="IMemoryCache"/>）。
///
/// 挑战 Id 是 32 字节随机（base64url）。这一点不能省：它是"密码已过、码还没验"这段时间里
/// **唯一的凭据**，可猜的 Id（自增序号、时间戳、邮箱的哈希）等于把第二步绕过去。
/// </summary>
public sealed class MemoryMfaChallengeStore(IMemoryCache cache) : IMfaChallengeStore
{
    /// <summary>挑战 Id 的随机字节数。256 位，与"不可猜"的要求相称。</summary>
    private const int IdBytes = 32;

    private static string Key(string challengeId) => $"auth:mfa-challenge:{challengeId}";

    public Task<string> CreateAsync(
        long userId,
        TotpSecret? pendingSecret,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        string challengeId = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(IdBytes));

        cache.Set(
            Key(challengeId),
            new ChallengeEntry { Challenge = new MfaChallenge(userId, pendingSecret) },
            lifetime);

        return Task.FromResult(challengeId);
    }

    public Task<MfaChallenge?> GetAsync(string challengeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(challengeId)
            || !cache.TryGetValue(Key(challengeId), out ChallengeEntry? entry)
            || entry is null)
        {
            return Task.FromResult<MfaChallenge?>(null);
        }

        return Task.FromResult<MfaChallenge?>(entry.Challenge);
    }

    public Task<bool> RegisterFailureAsync(
        string challengeId,
        int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(challengeId)
            || !cache.TryGetValue(Key(challengeId), out ChallengeEntry? entry)
            || entry is null)
        {
            // 挑战已经不在了（过期、已用过、或本来就不存在）：不是"还活着"
            return Task.FromResult(false);
        }

        entry.Failures++;

        return Task.FromResult(entry.Failures < maxAttempts);
    }

    public Task RemoveAsync(string challengeId, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(challengeId))
        {
            cache.Remove(Key(challengeId));
        }

        return Task.CompletedTask;
    }

    private sealed class ChallengeEntry
    {
        public required MfaChallenge Challenge { get; init; }

        public int Failures { get; set; }
    }
}
