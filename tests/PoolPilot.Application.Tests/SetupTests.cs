using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 首次初始化：判定"还没初始化"的判据，以及那一次配置要同时做完的三件事
/// （建通道、指样板、发测试）。
/// </summary>
[TestClass]
public sealed class SetupTests
{
    private const string RegistrationPurpose = NotificationPurposes.RegistrationCode;

    [TestMethod]
    public async Task Status_WithoutAnyAccount_NeedsSetup()
    {
        Harness h = new();

        SetupStatusDto status = await h.Status.ExecuteAsync();

        Assert.IsTrue(status.NeedsSetup);

        // 同时给出"注册码现在发不出去"这件事与原因（界面直接显示这一句）
        StringAssert.Contains(status.Reason, "还没有指定发送通道");
    }

    /// <summary>第一个账号一旦建出来，初始化窗口就关上了——此后改通道去「通知 → 提供商」。</summary>
    [TestMethod]
    public async Task Status_WithAnAccount_IsInitializedEvenIfNotifyIsNotReady()
    {
        Harness h = new();
        h.Users.Seed("someone@example.com", "correct horse");

        SetupStatusDto status = await h.Status.ExecuteAsync();

        Assert.IsFalse(status.NeedsSetup);
        StringAssert.Contains(status.Reason, "还没有指定发送通道", "就绪状态仍要如实说出来");
    }

    [TestMethod]
    public async Task Status_WithUsableProviderPointedByTemplate_HasNoReason()
    {
        Harness h = new();
        NotificationProvider provider = h.Providers.Seed("能用的通道");
        h.Templates.Seed(Template(provider.Id));

        SetupStatusDto status = await h.Status.ExecuteAsync();

        Assert.IsTrue(status.NeedsSetup, "还没有账号，仍然算未初始化");
        Assert.IsNull(status.Reason, "通道就绪时不给任何提示语");
    }

    /// <summary>通道存在但参数不全（缺主机）时**不算就绪**：它其实发不出去。</summary>
    [TestMethod]
    public async Task Status_WithUnusableProvider_ExplainsWhatIsMissing()
    {
        Harness h = new();
        h.Providers.Seed("缺主机的通道", isEnabled: true, host: null);
        h.Templates.Seed(Template(providerId: 1));

        SetupStatusDto status = await h.Status.ExecuteAsync();

        StringAssert.Contains(status.Reason, "还没配齐");
    }

    [TestMethod]
    public async Task Initialize_CreatesProviderPointsTemplateAndSendsTest()
    {
        Harness h = new();

        NotificationDeliveryDto delivery = await h.Initialize.ExecuteAsync(Request());

        Assert.IsTrue(delivery.Delivered);

        // 通道建出来了，而且是可用的
        NotificationProvider provider = Assert.ContainsSingle(h.Providers.Providers);

        Assert.IsTrue(provider.IsUsable);
        Assert.AreEqual("smtp.example.com", provider.Host);

        // 样板指向了它 —— 少了这一步，注册码照样发不出去
        NotificationTemplate template = Assert.ContainsSingle(h.Templates.Templates);

        Assert.AreEqual(RegistrationPurpose, template.Purpose);
        Assert.AreEqual(provider.Id, template.ProviderId);

        // 当场发了一封测试（并且留了记录）
        Assert.HasCount(1, h.Mailer.Sent);
        Assert.HasCount(1, h.Logs.Entries);
        Assert.AreEqual(NotificationPurposes.ProviderTest, h.Logs.Entries[0].Purpose);

        // 通道随之就绪（理由为空即"能发出去了"）；但窗口仍开着——
        // 关掉它的不是"通道配好了"，而是"第一个账号建出来了"
        Assert.IsNull((await h.Status.ExecuteAsync()).Reason);
    }

    /// <summary>参数不全时**不建通道**：留一条半残的通道会让下一次尝试以为已经配过了。</summary>
    [TestMethod]
    public async Task Initialize_WithIncompleteParameters_IsRejectedWithoutWriting()
    {
        Harness h = new();

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Initialize.ExecuteAsync(Request() with { Host = null }));

        StringAssert.Contains(error.Message, "还不全");
        Assert.IsEmpty(h.Providers.Providers);
        Assert.IsEmpty(h.Templates.Templates);
    }

    /// <summary>失败重试改的是同一条通道，而不是每试一次多出一行。</summary>
    [TestMethod]
    public async Task Initialize_OnRetry_UpdatesTheSameProvider()
    {
        Harness h = new();

        await h.Initialize.ExecuteAsync(Request());
        await h.Initialize.ExecuteAsync(Request() with { DisplayName = "改过名字的通道" });

        NotificationProvider provider = Assert.ContainsSingle(h.Providers.Providers);

        Assert.AreEqual("改过名字的通道", provider.DisplayName);
        Assert.HasCount(1, h.Templates.Templates, "样板也只有一条");
    }

    /// <summary>已经有账号之后就不该还能从这里改配置——否则它是一条绕过登录的入口。</summary>
    [TestMethod]
    public async Task Initialize_OnInitializedSystem_IsRejected()
    {
        Harness h = new();
        NotificationProvider provider = h.Providers.Seed("已有的通道");
        h.Templates.Seed(Template(provider.Id));
        h.Users.Seed("someone@example.com", "correct horse");

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Initialize.ExecuteAsync(Request()));

        StringAssert.Contains(error.Message, "已经初始化过了");
        Assert.HasCount(1, h.Providers.Providers, "不该多出一条");
    }

    /// <summary>
    /// 口令填错时**不能**把初始化窗口关掉：通道静态看是"配齐"的，但码其实发不出去——
    /// 那时若算作"已初始化"，人就卡在"注册要码、码发不出、初始化页进不去"上。
    /// 所以窗口只看"有没有账号"，填错了可以反复改到通为止。
    /// </summary>
    [TestMethod]
    public async Task Initialize_WithWrongPassword_KeepsTheSetupWindowOpen()
    {
        Harness h = new();
        h.Mailer.NextDelivery = () => NotificationDelivery.Failed("认证失败：535");

        NotificationDeliveryDto first = await h.Initialize.ExecuteAsync(Request());
        Assert.IsFalse(first.Delivered);
        StringAssert.Contains(first.Message, "535");

        // 仍然可以再来一次（改口令），而不是被"已经初始化过了"挡回去
        h.Mailer.NextDelivery = NotificationDelivery.Sent;
        NotificationDeliveryDto second = await h.Initialize.ExecuteAsync(Request() with { Password = "改对的口令" });

        Assert.IsTrue(second.Delivered);
        Assert.HasCount(1, h.Providers.Providers, "两次尝试仍然是同一行");
        Assert.AreEqual("改对的口令", h.Providers.Providers[0].Password);
    }

    private static NotificationProviderUpsertDto Request() => new()
    {
        Kind = NotificationKinds.Email,
        DisplayName = "初始化配的通道",
        IsEnabled = true,
        Host = "smtp.example.com",
        Port = 587,
        UseStartTls = true,
        UserName = "notify@example.com",
        Password = "app-password",
        FromAddress = "notify@example.com",
        FromName = "PoolPilot",
    };

    private static NotificationTemplate Template(long providerId) => new()
    {
        Purpose = RegistrationPurpose,
        DisplayName = "注册验证码",
        Subject = "PoolPilot 注册验证码：{code}",
        Body = "你的注册验证码是：{code}",
        ProviderId = providerId,
        UpdatedAt = LighterTimestamp.FromMilliseconds(0),
    };

    private sealed class Harness
    {
        public Harness()
        {
            Status = new GetSetupStatusUseCase(Users, Templates, Providers);
            Initialize = new InitializeNotificationsUseCase(
                Status,
                Providers,
                Templates,
                Mailer,
                Logs,
                Clock);
        }

        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));

        public FakeUserRepository Users { get; } = new();

        public FakeNotificationProviderRepository Providers { get; } = new();

        public FakeNotificationTemplateRepository Templates { get; } = new();

        public FakeNotificationMailer Mailer { get; } = new();

        public FakeNotificationLogRepository Logs { get; } = new();

        public GetSetupStatusUseCase Status { get; }

        public InitializeNotificationsUseCase Initialize { get; }
    }
}
