using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 登录失败计数与锁定（按邮箱）。
///
/// 存在的理由很具体：六位数字码有一百万种，而登录接口是可以反复打的。
/// 没有这一层，MFA 只是把爆破的难度从"猜密码"换成"猜六位数字"。
/// 计数按**邮箱**而不是按 IP：本项目只监听本机，IP 的区分度为零。
/// </summary>
public interface ILoginThrottle
{
    /// <summary>该邮箱还要多久解锁；未锁定时返回 <see cref="TimeSpan.Zero"/>。</summary>
    Task<TimeSpan> RemainingLockoutAsync(EmailAddress email, CancellationToken cancellationToken = default);

    /// <summary>记一次失败。达到 <paramref name="maxFailures"/> 次即开始锁定 <paramref name="lockout"/> 时长。</summary>
    Task RecordFailureAsync(
        EmailAddress email,
        int maxFailures,
        TimeSpan lockout,
        CancellationToken cancellationToken = default);

    /// <summary>登录成功即清零——否则"这个人以前输错过几次"会一直挂在他头上。</summary>
    Task ClearAsync(EmailAddress email, CancellationToken cancellationToken = default);
}
