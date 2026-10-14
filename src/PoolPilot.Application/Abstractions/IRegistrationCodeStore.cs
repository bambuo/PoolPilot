using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 注册验证码的暂存（含重发冷却与尝试上限）。
///
/// TTL 与上限由调用方传进来，不由实现写死：这些是**产品策略**（见 <c>AuthPolicy</c>），
/// 而实现只负责"存、取、按规则失效"。测试因此可以传一个 1 秒的有效期去验过期路径，
/// 而不必真的等十分钟。
/// </summary>
public interface IRegistrationCodeStore
{
    /// <summary>同一邮箱距下一次可发送还剩多久；可以立刻发时返回 <see cref="TimeSpan.Zero"/>。</summary>
    Task<TimeSpan> RemainingCooldownAsync(EmailAddress email, CancellationToken cancellationToken = default);

    /// <summary>存入一枚验证码，覆盖同一邮箱上尚未使用的那枚，并开始计算重发冷却。</summary>
    Task StoreAsync(
        EmailAddress email,
        string code,
        TimeSpan lifetime,
        TimeSpan resendCooldown,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 校验并按需消费。
    ///
    /// 命中（且未过期）即**一次性消费掉**并返回 <c>true</c>；未命中则记一次失败，
    /// 失败次数达到 <paramref name="maxAttempts"/> 时该码立即作废——六位数字只有一百万种，
    /// 不封顶的话，一次发送就等于给出了一百万次试错机会。
    /// </summary>
    Task<bool> TryConsumeAsync(
        EmailAddress email,
        string code,
        int maxAttempts,
        CancellationToken cancellationToken = default);
}
