using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Exceptions;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 登录两步 + MFA 绑定。
///
/// 这一组里"码对不对"由 <see cref="TotpGenerator"/> 自己算（算法本身由 Domain.Tests 对着
/// RFC 向量钉住），这里测的是**用例的编排**：谁能登录、挑战什么时候作废、密钥什么时候落库。
/// </summary>
[TestClass]
public sealed class LoginTests
{
    private const string Email = "pilot@example.com";
    private const string Password = "correct horse";

    [TestMethod]
    public async Task Login_UnknownEmailAndWrongPassword_SayTheSameThing()
    {
        Harness h = new();
        h.Users.Seed(Email, Password);

        ArgumentException unknown = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Login.ExecuteAsync(new LoginRequestDto { Email = "nobody@example.com", Password = Password }));

        ArgumentException wrongPassword = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = "wrong" }));

        // 两句话必须一模一样：分开说等于免费告诉对方"这个邮箱注册过没有"
        Assert.AreEqual(unknown.Message, wrongPassword.Message);
        StringAssert.Contains(unknown.Message, "邮箱或密码不正确");
    }

    [TestMethod]
    public async Task Login_UnboundAccount_ReturnsBindingChallengeWithSecret()
    {
        Harness h = new();
        h.Users.Seed(Email, Password);

        LoginResultDto result = await h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = Password });

        Assert.AreEqual("mfaBindingRequired", result.Status);
        Assert.IsNotNull(result.Secret);
        Assert.IsNotNull(result.OtpauthUri);
        StringAssert.StartsWith(result.OtpauthUri, "otpauth://totp/");
        StringAssert.Contains(result.OtpauthUri, "issuer=PoolPilot");

        // 密钥此刻**还没落库**：只有下一步真的验过码才会绑上
        Assert.IsNull(h.Users.Users[0].TotpSecret);
    }

    [TestMethod]
    public async Task Login_BoundAccount_ReturnsVerifyChallengeWithoutSecret()
    {
        Harness h = new();
        h.Users.Seed(Email, Password, TotpSecret.Generate());

        LoginResultDto result = await h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = Password });

        Assert.AreEqual("mfaRequired", result.Status);

        // 已绑定的账号**不再把密钥交出去**：能拿到密钥就能自己算码，第二个因子就没了
        Assert.IsNull(result.Secret);
        Assert.IsNull(result.OtpauthUri);
    }

    [TestMethod]
    public async Task Login_ClearsFailureCounterOnSuccess()
    {
        Harness h = new();
        h.Users.Seed(Email, Password);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = "wrong" }));

        await h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = Password });

        Assert.AreEqual(TimeSpan.Zero, await h.Throttle.RemainingLockoutAsync(EmailAddress.Parse(Email)));
        Assert.AreEqual("mfaBindingRequired", (await h.Login.ExecuteAsync(
            new LoginRequestDto { Email = Email, Password = Password })).Status);
    }

    [TestMethod]
    public async Task Login_AfterTooManyFailures_IsLockedOutBeforeCheckingPassword()
    {
        Harness h = new();
        h.Users.Seed(Email, Password);

        for (int i = 0; i < AuthPolicy.MaxLoginFailures; i++)
        {
            // 第 MaxLoginFailures 次失败**同时**触发锁定：这一次仍然回"密码不对"，
            // 从下一次开始才换 429——第一次失败就报"已锁定"会让人以为账号出了问题
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = "wrong" }));
        }

        AuthRateLimitedException locked = await Assert.ThrowsExactlyAsync<AuthRateLimitedException>(
            () => h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = Password }));

        StringAssert.Contains(locked.Message, "分钟后再试");
    }

    [TestMethod]
    public async Task VerifyMfa_WithCorrectCode_SignsInAndRecordsStep()
    {
        Harness h = new();
        TotpSecret secret = TotpSecret.Generate();
        User user = h.Users.Seed(Email, Password, secret);
        LoginResultDto challenge = await h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = Password });

        SessionDto session = await h.Verify.ExecuteAsync(new MfaCodeRequestDto
        {
            ChallengeId = challenge.ChallengeId,
            Code = TotpGenerator.Generate(secret, h.Clock.GetUtcNow()),
        });

        Assert.AreEqual(user.Id, session.UserId);
        Assert.AreEqual(Email, session.Email);

        // 用掉的时间步要记下来（防重放用的是它），挑战一次性
        Assert.AreEqual(TotpGenerator.StepAt(h.Clock.GetUtcNow()), h.Users.Users[0].TotpLastStep);
        Assert.IsFalse(h.Challenges.Contains(challenge.ChallengeId));
    }

    [TestMethod]
    public async Task VerifyMfa_WithWrongCode_CountsAttemptsAndKeepsChallengeAlive()
    {
        Harness h = new();
        h.Users.Seed(Email, Password, TotpSecret.Generate());
        LoginResultDto challenge = await h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = Password });

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Verify.ExecuteAsync(new MfaCodeRequestDto { ChallengeId = challenge.ChallengeId, Code = "000000" }));

        StringAssert.Contains(error.Message, "验证码不正确");
        Assert.IsTrue(h.Challenges.Contains(challenge.ChallengeId), "没到上限时挑战还在");
    }

    /// <summary>错到上限：挑战作废，并且**换一句更准确的话**告诉使用者要重新登录。</summary>
    [TestMethod]
    public async Task VerifyMfa_AfterTooManyWrongCodes_DiscardsTheChallenge()
    {
        Harness h = new();
        h.Users.Seed(Email, Password, TotpSecret.Generate());
        LoginResultDto challenge = await h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = Password });

        string message = string.Empty;

        for (int i = 0; i < AuthPolicy.MaxChallengeAttempts; i++)
        {
            ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => h.Verify.ExecuteAsync(new MfaCodeRequestDto { ChallengeId = challenge.ChallengeId, Code = "000000" }));

            message = error.Message;
        }

        StringAssert.Contains(message, "本次登录已作废");
        Assert.IsFalse(h.Challenges.Contains(challenge.ChallengeId));

        // 作废之后再拿正确的码也没用
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Verify.ExecuteAsync(new MfaCodeRequestDto { ChallengeId = challenge.ChallengeId, Code = "123456" }));
    }

    [TestMethod]
    public async Task VerifyMfa_WithUnknownOrExpiredChallenge_IsRejected()
    {
        Harness h = new();
        h.Users.Seed(Email, Password, TotpSecret.Generate());

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Verify.ExecuteAsync(new MfaCodeRequestDto { ChallengeId = "not-a-challenge", Code = "123456" }));

        StringAssert.Contains(error.Message, "登录流程已过期");
    }

    /// <summary>拿"待绑定"的挑战去走"已绑定"那条路，必须被挡下并说清该走哪条路。</summary>
    [TestMethod]
    public async Task VerifyMfa_OnBindingChallenge_AsksToBindFirst()
    {
        Harness h = new();
        h.Users.Seed(Email, Password);
        LoginResultDto challenge = await h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = Password });

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Verify.ExecuteAsync(new MfaCodeRequestDto { ChallengeId = challenge.ChallengeId, Code = "123456" }));

        StringAssert.Contains(error.Message, "还没绑定令牌");
    }

    [TestMethod]
    public async Task BindMfa_WithCorrectCode_PersistsSecretAndSignsIn()
    {
        Harness h = new();
        User user = h.Users.Seed(Email, Password);
        LoginResultDto challenge = await h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = Password });
        TotpSecret pending = TotpSecret.Parse(challenge.Secret!);

        SessionDto session = await h.Bind.ExecuteAsync(new MfaCodeRequestDto
        {
            ChallengeId = challenge.ChallengeId,
            Code = TotpGenerator.Generate(pending, h.Clock.GetUtcNow()),
        });

        Assert.AreEqual(Email, session.Email);

        User stored = h.Users.Users.Single(u => u.Id == user.Id);
        Assert.IsTrue(stored.HasMfa);
        Assert.AreEqual(pending.ToBase32(), stored.TotpSecret!.ToBase32());
        Assert.IsFalse(h.Challenges.Contains(challenge.ChallengeId));
    }

    [TestMethod]
    public async Task BindMfa_WithWrongCode_DoesNotPersistAnything()
    {
        Harness h = new();
        h.Users.Seed(Email, Password);
        LoginResultDto challenge = await h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = Password });

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Bind.ExecuteAsync(new MfaCodeRequestDto { ChallengeId = challenge.ChallengeId, Code = "000000" }));

        Assert.IsFalse(h.Users.Users[0].HasMfa, "码不对就不该把密钥绑上");
    }

    [TestMethod]
    public async Task BindMfa_OnAlreadyBoundAccount_IsRejected()
    {
        Harness h = new();
        h.Users.Seed(Email, Password, TotpSecret.Generate());
        LoginResultDto challenge = await h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = Password });

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Bind.ExecuteAsync(new MfaCodeRequestDto { ChallengeId = challenge.ChallengeId, Code = "123456" }));

        StringAssert.Contains(error.Message, "已经绑过令牌");
    }

    /// <summary>绑定之后立刻用同一个码再登一次（同一个时间步）——会踩到"刚注册完就重新登录"那条路。</summary>
    [TestMethod]
    public async Task BindThenLoginAgain_WithinTheSameStep_StillWorks()
    {
        Harness h = new();
        h.Users.Seed(Email, Password);

        LoginResultDto binding = await h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = Password });
        TotpSecret secret = TotpSecret.Parse(binding.Secret!);
        string code = TotpGenerator.Generate(secret, h.Clock.GetUtcNow());

        await h.Bind.ExecuteAsync(new MfaCodeRequestDto { ChallengeId = binding.ChallengeId, Code = code });

        LoginResultDto next = await h.Login.ExecuteAsync(new LoginRequestDto { Email = Email, Password = Password });
        Assert.AreEqual("mfaRequired", next.Status);

        SessionDto session = await h.Verify.ExecuteAsync(new MfaCodeRequestDto
        {
            ChallengeId = next.ChallengeId,
            Code = code,
        });

        Assert.AreEqual(Email, session.Email);
    }

    [TestMethod]
    public async Task GetSessionUser_ReturnsNullForDeletedAccount()
    {
        Harness h = new();
        User user = h.Users.Seed(Email, Password);

        Assert.IsNull(await h.Session.ExecuteAsync(user.Id + 999));
        SessionDto? session = await h.Session.ExecuteAsync(user.Id);
        Assert.IsNotNull(session);
        Assert.AreEqual(Email, session.Email);
    }

    /// <summary>装配这组用例。</summary>
    private sealed class Harness
    {
        public Harness()
        {
            Login = new LoginUseCase(Users, Challenges, Throttle);
            Verify = new VerifyMfaLoginUseCase(Users, Challenges, Clock);
            Bind = new BindMfaOnLoginUseCase(Users, Challenges, Clock);
            Session = new GetSessionUserUseCase(Users);

            Challenges.Now = Clock.GetUtcNow();
            Throttle.Now = Clock.GetUtcNow();
        }

        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero));

        public FakeUserRepository Users { get; } = new();

        public FakeMfaChallengeStore Challenges { get; } = new();

        public FakeLoginThrottle Throttle { get; } = new();

        public LoginUseCase Login { get; }

        public VerifyMfaLoginUseCase Verify { get; }

        public BindMfaOnLoginUseCase Bind { get; }

        public GetSessionUserUseCase Session { get; }
    }
}
