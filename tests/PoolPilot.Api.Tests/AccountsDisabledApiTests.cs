using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;

using PoolPilot.Application.Dtos;

namespace PoolPilot.Api.Tests;

/// <summary>
/// `Auth:AccountsEnabled=false` 的公开-only 模式：登录、注册、MFA 与首次初始化端点
/// 都该像不存在一样回 404；匿名状态读口要如实告诉前端“账户流程已关闭”。
/// </summary>
[TestClass]
public sealed class AccountsDisabledApiTests
{
    [TestMethod]
    public async Task LoginAndRegistrationEndpoints_Return404_WhenAccountsAreDisabled()
    {
        using AccountsDisabledApp app = new();
        using HttpClient client = app.CreateClient();

        foreach ((string path, object body) in new (string, object)[]
        {
            ("/api/auth/login", new { email = "user@example.com", password = "Password123" }),
            ("/api/auth/register/code", new { email = "user@example.com" }),
            ("/api/auth/register", new
            {
                email = "user@example.com",
                password = "Password123",
                confirmPassword = "Password123",
                code = "123456",
            }),
            ("/api/auth/mfa/verify", new { challengeId = "challenge", code = "123456" }),
            ("/api/auth/mfa/bind", new { challengeId = "challenge", code = "123456" }),
        })
        {
            HttpResponseMessage response = await client.PostAsJsonAsync(path, body);

            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, path);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "登录与注册未启用");
        }
    }

    [TestMethod]
    public async Task SetupEndpoints_Return404_WhenAccountsAreDisabled()
    {
        using AccountsDisabledApp app = new();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/setup/status");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "登录与注册未启用");
    }

    [TestMethod]
    public async Task StatusEndpoint_ReportsAccountsDisabled_EvenWhenPublicPagesAreOn()
    {
        using AccountsDisabledApp app = new();
        using HttpClient client = app.CreateClient();

        PublicPageStatusDto status = (await client.GetFromJsonAsync<PublicPageStatusDto>(
            "/api/public/status"))!;

        Assert.IsFalse(status.AccountsEnabled, "公开页头部要靠它隐藏登录/注册入口");
        Assert.IsTrue(status.Leads, "关闭账户流程不应该顺带关掉公开页");
    }

    [TestMethod]
    public async Task ExistingSessionIntrospection_IsNotGatedByTheAccountsSwitch()
    {
        // 只关“新登录/新注册”，不把会话自省也变成 404：已有会话仍按原逻辑工作，
        // 匿名请求则沿 Cookie 认证原有的 401 出口返回。
        using AccountsDisabledApp app = new();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/auth/me");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>把 Auth 开关钉成 false 的测试宿主；其余工装与 AuthTestApp 完全相同。</summary>
    private sealed class AccountsDisabledApp : AuthTestApp
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:AccountsEnabled", "false");
            builder.UseSetting("PublicPages:Leads", "true");
        }
    }
}
