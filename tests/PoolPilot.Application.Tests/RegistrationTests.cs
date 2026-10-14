using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Exceptions;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 注册：发验证码 + 建账号。
///
/// 断言的重点在**几条会静默变坏的地方**：验证码被白白消耗掉、半份凭据建出账号、
/// 以及"发送成功"其实只进了日志。这些在界面上都长得跟成功一样。
/// </summary>
[TestClass]
public sealed class RegistrationTests
{
    private const string Email = "newcomer@example.com";
    private const string Password = "correct horse";

    private static (RequestRegistrationCodeUseCase Request, RegisterUserUseCase Register, Harness H) Build()
    {
        Harness h = new();

        return (
            new RequestRegistrationCodeUseCase(
                h.Users,
                h.Codes,
                h.Providers,
                h.Templates,
                h.Mailer,
                h.Logs,
                h.Clock),
            new RegisterUserUseCase(h.Users, h.Codes),
            h);
    }

    [TestMethod]
    public async Task RequestCode_StoresCodeSendsMailAndRecordsIt()
    {
        (RequestRegistrationCodeUseCase request, _, Harness h) = Build();

        NotificationDeliveryDto result = await request.ExecuteAsync(Email);

        // 默认样板（库里没配过）也要能发出去：把码放在正文里，主题里也带一份
        Assert.IsTrue(result.Delivered);
        Assert.HasCount(1, h.Mailer.Sent);
        Assert.AreEqual(Email, h.Mailer.Sent[0].Recipient);
        StringAssert.Contains(h.Mailer.Sent[0].Subject, h.Codes.Peek(EmailAddress.Parse(Email))!);
        StringAssert.Contains(h.Mailer.Sent[0].Body, h.Codes.Peek(EmailAddress.Parse(Email))!);

        // 每一次投递都留记录，且记的是**渲染后**的内容
        Assert.HasCount(1, h.Logs.Entries);
        Assert.AreEqual(NotificationPurposes.RegistrationCode, h.Logs.Entries[0].Purpose);
        Assert.IsTrue(h.Logs.Entries[0].Delivered);
        Assert.IsFalse(h.Logs.Entries[0].LogFallback);
        Assert.AreEqual(Email, h.Logs.Entries[0].Recipient);
    }

    [TestMethod]
    public async Task RequestCode_WithinCooldown_IsRejected()
    {
        (RequestRegistrationCodeUseCase request, _, Harness h) = Build();

        await request.ExecuteAsync(Email);
        AuthRateLimitedException error = await Assert.ThrowsExactlyAsync<AuthRateLimitedException>(
            () => request.ExecuteAsync(Email));

        StringAssert.Contains(error.Message, "秒后再试");
        Assert.HasCount(1, h.Mailer.Sent, "冷却期内不该再发一封");
    }

    [TestMethod]
    public async Task RequestCode_ForRegisteredEmail_IsRejectedEarly()
    {
        (RequestRegistrationCodeUseCase request, _, Harness h) = Build();
        h.Users.Seed(Email, Password);

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => request.ExecuteAsync(Email));

        StringAssert.Contains(error.Message, "已经注册过了");
        Assert.IsEmpty(h.Mailer.Sent, "已注册的邮箱连码都不该发");
    }

    /// <summary>没有可用通道时：**不假装成功**——内容进日志，答复里说清"只写进了日志"。</summary>
    [TestMethod]
    public async Task RequestCode_WithoutUsableChannel_DegradesToLogAndSaysSo()
    {
        (RequestRegistrationCodeUseCase request, _, Harness h) = Build();
        h.Mailer.NextDelivery = NotificationDelivery.FallbackToLog;

        NotificationDeliveryDto result = await request.ExecuteAsync(Email);

        Assert.IsFalse(result.Delivered);
        Assert.IsTrue(result.LogFallback);
        StringAssert.Contains(result.Message, "服务端日志");

        Assert.HasCount(1, h.Logs.Entries);
        Assert.IsTrue(h.Logs.Entries[0].LogFallback);
        Assert.IsFalse(h.Logs.Entries[0].Delivered);

        // 通道名取的是快照：没配通道时也要说清"当时没有通道"
        StringAssert.Contains(h.Logs.Entries[0].ProviderName, "未配置通道");
    }

    /// <summary>库里改过的样板为准：自定义的主题与正文要真的用上，占位符要真的替换。</summary>
    [TestMethod]
    public async Task RequestCode_UsesStoredTemplate()
    {
        (RequestRegistrationCodeUseCase request, _, Harness h) = Build();
        NotificationProvider provider = h.Providers.Seed("自定义通道");

        h.Templates.Seed(new NotificationTemplate
        {
            Purpose = NotificationPurposes.RegistrationCode,
            DisplayName = "注册验证码",
            Subject = "【自定义】{code}",
            Body = "码是 {code}，{minutes} 分钟内有效（发给 {email}）",
            ProviderId = provider.Id,
            UpdatedAt = LighterTimestamp.FromMilliseconds(0),
        });

        await request.ExecuteAsync(Email);

        string code = h.Codes.Peek(EmailAddress.Parse(Email))!;

        Assert.HasCount(1, h.Mailer.Sent);
        Assert.AreEqual($"【自定义】{code}", h.Mailer.Sent[0].Subject);
        Assert.AreEqual($"码是 {code}，10 分钟内有效（发给 {Email}）", h.Mailer.Sent[0].Body);
        Assert.AreSame(provider, h.Mailer.Sent[0].Provider, "样板指向哪条通道，就用哪条发");
    }

    [TestMethod]
    public async Task Register_WithWrongCode_IsRejectedAndCreatesNothing()
    {
        (RequestRegistrationCodeUseCase request, RegisterUserUseCase register, Harness h) = Build();
        await request.ExecuteAsync(Email);

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => register.ExecuteAsync(new RegisterRequestDto
            {
                Email = Email,
                Code = "000000",
                Password = Password,
                ConfirmPassword = Password,
            }));

        StringAssert.Contains(error.Message, "验证码不正确");
        Assert.IsEmpty(h.Users.Users);
    }

    /// <summary>
    /// 两次密码不一致必须**在消耗验证码之前**挡住：否则一次手误就要再等 60 秒重发。
    /// 这条断言守的就是那个顺序。
    /// </summary>
    [TestMethod]
    public async Task Register_WithMismatchedPasswords_RejectsWithoutConsumingTheCode()
    {
        (RequestRegistrationCodeUseCase request, RegisterUserUseCase register, Harness h) = Build();
        await request.ExecuteAsync(Email);
        string code = h.Codes.Peek(EmailAddress.Parse(Email))!;

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => register.ExecuteAsync(new RegisterRequestDto
        {
            Email = Email,
            Code = code,
            Password = Password,
            ConfirmPassword = "别的口令",
        }));

        Assert.AreEqual(code, h.Codes.Peek(EmailAddress.Parse(Email)), "码不该被消耗掉");
        Assert.IsEmpty(h.Users.Users);
    }

    [TestMethod]
    public async Task Register_WithShortPassword_IsRejectedBeforeConsumingTheCode()
    {
        (RequestRegistrationCodeUseCase request, RegisterUserUseCase register, Harness h) = Build();
        await request.ExecuteAsync(Email);
        string code = h.Codes.Peek(EmailAddress.Parse(Email))!;

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => register.ExecuteAsync(new RegisterRequestDto
        {
            Email = Email,
            Code = code,
            Password = "short",
            ConfirmPassword = "short",
        }));

        Assert.AreEqual(code, h.Codes.Peek(EmailAddress.Parse(Email)));
    }

    [TestMethod]
    public async Task Register_ConsumesTheCodeOnlyOnce()
    {
        (RequestRegistrationCodeUseCase request, RegisterUserUseCase register, Harness h) = Build();
        await request.ExecuteAsync(Email);
        string code = h.Codes.Peek(EmailAddress.Parse(Email))!;

        await register.ExecuteAsync(new RegisterRequestDto
        {
            Email = Email,
            Code = code,
            Password = Password,
            ConfirmPassword = Password,
        });

        Assert.IsNull(h.Codes.Peek(EmailAddress.Parse(Email)), "码用掉就该没了");

        // 第二次拿同一个码注册另一个邮箱也行不通（码是邮箱维度的）
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => register.ExecuteAsync(new RegisterRequestDto
        {
            Email = Email,
            Code = code,
            Password = Password,
            ConfirmPassword = Password,
        }));
    }

    /// <summary>注册出来的账号**没有绑定 MFA**：绑定只能发生在登录流程里（见 LoginTests）。</summary>
    [TestMethod]
    public async Task Register_CreatesUserWithHashedPasswordAndNoMfaSecret()
    {
        (RequestRegistrationCodeUseCase request, RegisterUserUseCase register, Harness h) = Build();
        await request.ExecuteAsync(Email);
        string code = h.Codes.Peek(EmailAddress.Parse(Email))!;

        await register.ExecuteAsync(new RegisterRequestDto
        {
            Email = Email,
            Code = code,
            Password = Password,
            ConfirmPassword = Password,
        });

        User created = Assert.ContainsSingle(h.Users.Users);
        Assert.AreEqual(Email, created.Email.Value);
        Assert.IsTrue(created.PasswordHash.Verify(Password));
        Assert.IsFalse(created.PasswordHash.Verify(Password + "x"));
        Assert.IsFalse(created.HasMfa);
        Assert.IsNull(created.TotpSecret);
    }

    [TestMethod]
    public async Task Register_NormalizesEmailCase()
    {
        (RequestRegistrationCodeUseCase request, RegisterUserUseCase register, Harness h) = Build();
        await request.ExecuteAsync("NewComer@Example.COM ");
        string code = h.Codes.Peek(EmailAddress.Parse(Email))!;

        await register.ExecuteAsync(new RegisterRequestDto
        {
            Email = " NEWCOMER@example.com ",
            Code = code,
            Password = Password,
            ConfirmPassword = Password,
        });

        Assert.AreEqual(Email, Assert.ContainsSingle(h.Users.Users).Email.Value);
    }

    /// <summary>装配这一组用例需要的全部假依赖。</summary>
    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero));

        public FakeUserRepository Users { get; } = new();

        public FakeRegistrationCodeStore Codes { get; } = new();

        public FakeNotificationProviderRepository Providers { get; } = new();

        public FakeNotificationTemplateRepository Templates { get; } = new();

        public FakeNotificationMailer Mailer { get; } = new();

        public FakeNotificationLogRepository Logs { get; } = new();
    }
}
