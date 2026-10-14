using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Exceptions;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 登录第一步：验邮箱与口令，然后**交回一个挑战**。
///
/// 这一步永远不签发会话——MFA 是强制的，所以它的产出只有两种：
/// 让前端去输六位码，或让前端去扫码绑定。把"下一步做什么"作为答复的一部分，
/// 前端就不必自己判断该显示哪个界面。
/// </summary>
public sealed class LoginUseCase(
    IUserRepository users,
    IMfaChallengeStore challenges,
    ILoginThrottle throttle)
{
    public async Task<LoginResultDto> ExecuteAsync(
        LoginRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EmailAddress address = EmailAddress.Parse(request.Email);

        TimeSpan locked = await throttle.RemainingLockoutAsync(address, cancellationToken).ConfigureAwait(false);

        if (locked > TimeSpan.Zero)
        {
            throw new AuthRateLimitedException(
                $"登录失败次数过多，请 {Math.Ceiling(locked.TotalMinutes):F0} 分钟后再试");
        }

        User? user = await users.FindByEmailAsync(address, cancellationToken).ConfigureAwait(false);
        string password = request.Password ?? string.Empty;

        // **账号不存在与口令不对回同一句话**：分开说等于免费回答了"这个邮箱注册过没有"，
        // 而这个问题是后面那套爆破的前置信息。
        if (user is null || !user.PasswordHash.Verify(password))
        {
            await throttle.RecordFailureAsync(
                address,
                AuthPolicy.MaxLoginFailures,
                AuthPolicy.LoginLockout,
                cancellationToken).ConfigureAwait(false);

            throw new ArgumentException("邮箱或密码不正确", nameof(request));
        }

        await throttle.ClearAsync(address, cancellationToken).ConfigureAwait(false);

        if (!user.HasMfa)
        {
            // 还没绑：生成**尚未落库**的密钥随挑战返回。它此刻只是"候选"，
            // 只有下一步真的验过六位码，才会写进账号（见 BindMfaOnLoginUseCase）。
            TotpSecret secret = TotpSecret.Generate();

            string bindingChallenge = await challenges
                .CreateAsync(user.Id, secret, AuthPolicy.ChallengeLifetime, cancellationToken)
                .ConfigureAwait(false);

            return new LoginResultDto
            {
                Status = "mfaBindingRequired",
                ChallengeId = bindingChallenge,
                Secret = secret.ToBase32(),
                OtpauthUri = TotpGenerator.BuildOtpauthUri(secret, AuthPolicy.Issuer, address.Value),
            };
        }

        string challengeId = await challenges
            .CreateAsync(user.Id, pendingSecret: null, AuthPolicy.ChallengeLifetime, cancellationToken)
            .ConfigureAwait(false);

        return new LoginResultDto
        {
            Status = "mfaRequired",
            ChallengeId = challengeId,

            // 已绑定的账号不再把密钥交出去：能拿到密钥就能自己算出码，
            // 那第二个因子就没了意义
            Secret = null,
            OtpauthUri = null,
        };
    }
}

/// <summary>
/// 登录第二步（已绑定）：用账号上的密钥校验六位码。
/// </summary>
public sealed class VerifyMfaLoginUseCase(
    IUserRepository users,
    IMfaChallengeStore challenges,
    TimeProvider clock)
{
    public async Task<SessionDto> ExecuteAsync(
        MfaCodeRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        (MfaChallenge challenge, User user) = await MfaLoginChallenge.ResolveAsync(
            request.ChallengeId,
            challenges,
            users,
            cancellationToken).ConfigureAwait(false);

        if (challenge.PendingSecret is not null)
        {
            throw new ArgumentException("这次登录还没绑定令牌，请先扫码完成绑定", nameof(request));
        }

        long matchedStep = await MfaLoginChallenge
            .VerifyCodeAsync(
                request.ChallengeId,
                challenge,
                user.TotpSecret!,
                user.TotpLastStep,
                request.Code,
                clock,
                challenges,
                cancellationToken)
            .ConfigureAwait(false);

        await users.UpdateAsync(user.RecordMfaStep(matchedStep), cancellationToken).ConfigureAwait(false);

        return new SessionDto { UserId = user.Id, Email = user.Email.Value };
    }
}

/// <summary>
/// 登录第二步（未绑定）：把挑战里的临时密钥绑到账号上。
///
/// 绑定与登录是**同一次交互**：验过码就说明密钥确实进了那台验证器，
/// 这时既落库、也把那一步的时间记下（防重放），然后允许登录。
/// </summary>
public sealed class BindMfaOnLoginUseCase(
    IUserRepository users,
    IMfaChallengeStore challenges,
    TimeProvider clock)
{
    public async Task<SessionDto> ExecuteAsync(
        MfaCodeRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        (MfaChallenge challenge, User user) = await MfaLoginChallenge.ResolveAsync(
            request.ChallengeId,
            challenges,
            users,
            cancellationToken).ConfigureAwait(false);

        TotpSecret? pending = challenge.PendingSecret
            ?? throw new ArgumentException("这个账号已经绑过令牌了，请直接输入六位码", nameof(request));

        // 还没绑过任何密钥，所以没有"上次用过的时间步"可防重放——绑定这一下本身就是第一次使用
        long matchedStep = await MfaLoginChallenge
            .VerifyCodeAsync(
                request.ChallengeId,
                challenge,
                pending,
                lastAcceptedStep: null,
                request.Code,
                clock,
                challenges,
                cancellationToken)
            .ConfigureAwait(false);

        await users.UpdateAsync(user.BindMfa(pending, matchedStep), cancellationToken).ConfigureAwait(false);

        return new SessionDto { UserId = user.Id, Email = user.Email.Value };
    }
}

/// <summary>
/// 会话里的当前账号（<c>GET /api/auth/me</c>）。
///
/// 特意落一次库而不是只读 Cookie 里的声明：账号被删掉之后，那枚 Cookie 在到期前仍然"有效"，
/// 而它引用的东西已经不存在了。这里查得到才叫登录着。
/// </summary>
public sealed class GetSessionUserUseCase(IUserRepository users)
{
    public async Task<SessionDto?> ExecuteAsync(long userId, CancellationToken cancellationToken = default)
    {
        User? user = await users.GetAsync(userId, cancellationToken).ConfigureAwait(false);

        return user is null ? null : new SessionDto { UserId = user.Id, Email = user.Email.Value };
    }
}

/// <summary>
/// 两条完成路径共用的一段：把挑战捞出来、验码、错则记一次、对了就丢掉挑战。
///
/// 单拎出来是因为"错几次作废"这句话必须只有一处写法——两条路径各写一遍，
/// 早晚会有一条把上限写丢，而丢了它的那条就是爆破入口。
/// </summary>
internal static class MfaLoginChallenge
{
    internal static async Task<(MfaChallenge Challenge, User User)> ResolveAsync(
        string challengeId,
        IMfaChallengeStore challenges,
        IUserRepository users,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeId);

        MfaChallenge challenge = await challenges.GetAsync(challengeId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException("登录流程已过期，请重新输入邮箱与密码", nameof(challengeId));

        User user = await users.GetAsync(challenge.UserId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException("这个账号已经不存在了", nameof(challengeId));

        return (challenge, user);
    }

    /// <summary>
    /// 验六位码。返回命中的时间步（调用方拿它去更新账号，防重放）。
    ///
    /// 两条出口都**不留活口**：成功即消费掉挑战（一次性凭据），失败到上限也把它作废。
    /// </summary>
    internal static async Task<long> VerifyCodeAsync(
        string challengeId,
        MfaChallenge challenge,
        TotpSecret secret,
        long? lastAcceptedStep,
        string candidateCode,
        TimeProvider clock,
        IMfaChallengeStore challenges,
        CancellationToken cancellationToken)
    {
        TotpVerification verification = TotpGenerator.Verify(
            secret,
            candidateCode ?? string.Empty,
            clock.GetUtcNow(),
            lastAcceptedStep);

        if (verification.IsValid)
        {
            await challenges.RemoveAsync(challengeId, cancellationToken).ConfigureAwait(false);
            return verification.MatchedStep;
        }

        bool stillAlive = await challenges
            .RegisterFailureAsync(challengeId, AuthPolicy.MaxChallengeAttempts, cancellationToken)
            .ConfigureAwait(false);

        if (!stillAlive)
        {
            await challenges.RemoveAsync(challengeId, cancellationToken).ConfigureAwait(false);
        }

        throw new ArgumentException(
            stillAlive
                ? "验证码不正确"
                : $"验证码连续输错 {AuthPolicy.MaxChallengeAttempts} 次，本次登录已作废，请重新登录");
    }
}
