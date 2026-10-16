using System.Net;
using System.Net.Http.Json;

using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 注册 + 登录 + 绑定 MFA，返回带会话 Cookie 的客户端。
///
/// 这是 Api.Tests 里所有"登录者视角"用例的共同入口，所以收在一处：登录三步的细节
/// （验证码从假投递器取、挑战与密钥的形状、绑定用当前时间步的码）散在各测试类里时，
/// 任何一步的口径变化都会只改到一部分测试。
///
/// 注意注册顺序：全库第一个账号会自动成为超级管理员（见 <c>SuperadminBootstrap</c>），
/// 因此"先注册谁"决定了谁是超管。
/// </summary>
internal static class TestSignIn
{
    internal static async Task<HttpClient> Async(AuthTestApp app, string email, string password)
    {
        HttpClient client = app.CreateClient();

        await AuthApiTests.RegisterAsync(client, email, password, app);

        HttpResponseMessage login = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new LoginRequestDto { Email = email, Password = password });

        LoginResultDto challenge = (await login.Content.ReadFromJsonAsync<LoginResultDto>())!;
        Assert.AreEqual("mfaBindingRequired", challenge.Status);

        TotpSecret secret = TotpSecret.Parse(challenge.Secret!);

        HttpResponseMessage bound = await client.PostAsJsonAsync(
            new Uri("/api/auth/mfa/bind", UriKind.Relative),
            new MfaCodeRequestDto
            {
                ChallengeId = challenge.ChallengeId,
                Code = TotpGenerator.Generate(secret, DateTimeOffset.UtcNow),
            });

        Assert.AreEqual(HttpStatusCode.OK, bound.StatusCode);

        return client;
    }
}
