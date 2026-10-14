using System.Net;
using System.Net.Http.Json;

using PoolPilot.Application.Dtos;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 首次初始化的接口：匿名可读、匿名可写，但**只在还没有账号时有效**。
/// 这条边界是这一组测试的重点——它是整个系统里唯一一个"没登录也能改配置"的口子。
/// </summary>
[TestClass]
public sealed class SetupApiTests
{
    private const string Admin = "first-user@example.com";
    private const string Password = "correct horse";

    [TestMethod]
    public async Task Status_IsReachableWithoutSigningIn()
    {
        using AuthTestApp app = new();
        using HttpClient client = app.CreateClient();

        SetupStatusDto status = (await client.GetFromJsonAsync<SetupStatusDto>(
            new Uri("/api/setup/status", UriKind.Relative)))!;

        Assert.IsTrue(status.NeedsSetup, "还没有账号 ⇒ 未初始化");
        Assert.IsNotNull(status.Reason, "同时要说清注册码现在发不出去的原因");
    }

    /// <summary>
    /// 初始化配好通道之后，**注册验证码真的走邮件**——这正是这一页存在的理由：
    /// 在此之前，码只会写进服务端日志，而"从终端里捞码"不该是第一次使用的必经之路。
    /// </summary>
    [TestMethod]
    public async Task Configure_ThenRegistrationCodeGoesOutByMail()
    {
        using AuthTestApp app = new();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage configured = await client.PostAsJsonAsync(
            new Uri("/api/setup/notifications", UriKind.Relative),
            SetupRequest());

        Assert.AreEqual(HttpStatusCode.OK, configured.StatusCode);

        NotificationDeliveryDto delivery = (await configured.Content.ReadFromJsonAsync<NotificationDeliveryDto>())!;
        Assert.IsTrue(delivery.Delivered, delivery.Message);

        // 测试邮件是刚发的那一封
        Assert.HasCount(1, app.Mailer.Sent);
        StringAssert.Contains(app.Mailer.Sent[0].Subject, "通道测试");

        // 通道就绪：状态里的"发不出去"理由消失（但仍未初始化——还没有账号）
        SetupStatusDto status = (await client.GetFromJsonAsync<SetupStatusDto>(
            new Uri("/api/setup/status", UriKind.Relative)))!;
        Assert.IsTrue(status.NeedsSetup);
        Assert.IsNull(status.Reason);

        // 现在发注册验证码：走的是邮件，不再是日志
        HttpResponseMessage codeResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/register/code", UriKind.Relative),
            new RegistrationCodeRequestDto { Email = Admin });

        Assert.AreEqual(HttpStatusCode.OK, codeResponse.StatusCode);

        NotificationDeliveryDto code = (await codeResponse.Content.ReadFromJsonAsync<NotificationDeliveryDto>())!;
        Assert.IsTrue(code.Delivered, "配好通道之后，注册验证码应当真的寄出去");
        Assert.HasCount(2, app.Mailer.Sent);
        StringAssert.Contains(app.Mailer.Sent[1].Subject, "注册验证码");
    }

    /// <summary>建出第一个账号之后，这条口子必须关上——否则它是一条绕过登录的配置入口。</summary>
    [TestMethod]
    public async Task Configure_AfterFirstRegistration_IsRejected()
    {
        using AuthTestApp app = new();
        using HttpClient client = app.CreateClient();

        await AuthApiTests.RegisterAsync(client, Admin, Password, app);

        SetupStatusDto status = (await client.GetFromJsonAsync<SetupStatusDto>(
            new Uri("/api/setup/status", UriKind.Relative)))!;
        Assert.IsFalse(status.NeedsSetup, "已有账号 ⇒ 初始化完成");

        HttpResponseMessage again = await client.PostAsJsonAsync(
            new Uri("/api/setup/notifications", UriKind.Relative),
            SetupRequest() with { DisplayName = "想偷偷再加一条" });

        Assert.AreEqual(HttpStatusCode.BadRequest, again.StatusCode);
        StringAssert.Contains(await again.Content.ReadAsStringAsync(), "已经初始化过了");
    }

    private static NotificationProviderUpsertDto SetupRequest() => new()
    {
        Kind = "email",
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
}
