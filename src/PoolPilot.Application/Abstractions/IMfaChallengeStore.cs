using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 一次登录挑战：**密码已经验过、MFA 还没验完**的那段时间的状态。
///
/// 它是"第二个因子"的载体：第二步要么拿账号上已绑的密钥验码，要么把 <see cref="PendingSecret"/>
/// 这个**还没落库**的临时密钥绑上去。两种情况下挑战都必须带上用户 Id——
/// 此时还没有会话，"当前用户是谁"只能由挑战本身回答。
/// </summary>
/// <param name="UserId">挑战指向的账号（主键）。</param>
/// <param name="PendingSecret">待绑定的密钥；已绑定账号的挑战里为 <c>null</c>。</param>
public sealed record MfaChallenge(long UserId, TotpSecret? PendingSecret);

/// <summary>
/// 登录挑战的暂存。
///
/// 挑战 Id 是不可猜的随机串（实现用 32 字节随机），而不是"自增序号 + 邮箱"那种可枚举的东西——
/// 它是登录第二步唯一的凭据。
/// </summary>
public interface IMfaChallengeStore
{
    /// <summary>开一个挑战，返回它的 Id。</summary>
    Task<string> CreateAsync(
        long userId,
        TotpSecret? pendingSecret,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);

    /// <summary>读取挑战（不消费）；不存在或已过期返回 <c>null</c>。</summary>
    Task<MfaChallenge?> GetAsync(string challengeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 记一次失败的验码尝试。
    /// 返回 <c>false</c> 表示该挑战已作废（尝试次数用尽），调用方据此换一句更准确的话告诉使用者。
    /// </summary>
    Task<bool> RegisterFailureAsync(
        string challengeId,
        int maxAttempts,
        CancellationToken cancellationToken = default);

    /// <summary>丢掉挑战（验码成功或作废时调用）。一次性：用过即不再存在。</summary>
    Task RemoveAsync(string challengeId, CancellationToken cancellationToken = default);
}
