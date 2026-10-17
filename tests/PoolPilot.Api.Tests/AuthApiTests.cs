using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using PoolPilot.Api;
using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;
using PoolPilot.Infrastructure.Persistence;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 注册 → 登录 → 绑定 MFA → 用会话访问业务接口，整条链路跑在真实管线里
/// （路由、鉴权、Cookie、中间件、SQLite 全是真的，只有发信与数据库文件是替身）。
///
/// **这是仓库里第一个 <see cref="WebApplicationFactory{TEntryPoint}"/>**：此前 Api.Tests 都是
/// 直接构造被测对象。登录这件事的价值恰恰在"接线"上——全局 RequireAuthorization、
/// Cookie 事件、中间件的状态码映射，只有把请求真的打进去才测得到。
/// </summary>
[TestClass]
public sealed class AuthApiTests
{
    private const string Email = "api-pilot@example.com";
    private const string Password = "correct horse";

    [TestMethod]
    public async Task Health_IsReachableWithoutSigningIn()
    {
        using AuthTestApp app = new();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync(new Uri("/api/health", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// 全局策略是"控制器一律要登录"：这里挑两条业务接口确认它真的生效，
    /// 且**回的是 401 而不是 302**（API 把未登录变成跳转，前端就只能靠猜）。
    /// </summary>
    [TestMethod]
    public async Task BusinessEndpoints_RequireSigningIn()
    {
        using AuthTestApp app = new();
        using HttpClient client = app.CreateClient();

        foreach (string path in new[] { "/api/pools", "/api/notifications/providers", "/api/auth/me" })
        {
            HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, $"{path} 应当要求登录");

            string body = await response.Content.ReadAsStringAsync();
            StringAssert.Contains(body, "error");
        }
    }

    [TestMethod]
    public async Task FullFlow_RegisterBindMfaThenUseTheApp()
    {
        using AuthTestApp app = new();
        using HttpClient client = app.CreateClient();

        // 1. 发验证码：假投递器把邮件收下了，测试从里面把码读出来
        HttpResponseMessage codeResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/register/code", UriKind.Relative),
            new RegistrationCodeRequestDto { Email = Email });

        Assert.AreEqual(HttpStatusCode.OK, codeResponse.StatusCode);
        NotificationDeliveryDto delivery = (await codeResponse.Content.ReadFromJsonAsync<NotificationDeliveryDto>())!;
        Assert.IsTrue(delivery.Delivered, delivery.Message);

        string code = ExtractCode(Assert.ContainsSingle(app.Mailer.Sent).Body);

        // 2. 错码要被拒
        HttpResponseMessage badRegister = await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            new RegisterRequestDto
            {
                Email = Email,
                Code = "000000",
                Password = Password,
                ConfirmPassword = Password,
            });

        Assert.AreEqual(HttpStatusCode.BadRequest, badRegister.StatusCode);

        // 3. 正确注册（204：它没有内容可回，也不签发会话）
        HttpResponseMessage registered = await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            new RegisterRequestDto
            {
                Email = Email,
                Code = code,
                Password = Password,
                ConfirmPassword = Password,
            });

        Assert.AreEqual(HttpStatusCode.NoContent, registered.StatusCode);

        // 4. 登录第一步：还没绑令牌，拿到挑战与密钥
        LoginResultDto challenge = await LoginAsync(client);
        Assert.AreEqual("mfaBindingRequired", challenge.Status);
        Assert.IsNotNull(challenge.Secret);

        // 5. 绑定：用服务端给的密钥算一个当前时间步的码（算法本身由 Domain.Tests 对着 RFC 向量钉住）
        TotpSecret secret = TotpSecret.Parse(challenge.Secret);
        HttpResponseMessage bound = await client.PostAsJsonAsync(
            new Uri("/api/auth/mfa/bind", UriKind.Relative),
            new MfaCodeRequestDto
            {
                ChallengeId = challenge.ChallengeId,
                Code = TotpGenerator.Generate(secret, DateTimeOffset.UtcNow),
            });

        Assert.AreEqual(HttpStatusCode.OK, bound.StatusCode);

        SessionDto session = (await bound.Content.ReadFromJsonAsync<SessionDto>())!;
        Assert.AreEqual(Email, session.Email);

        // 6. 会话生效：/me 与业务接口都通了
        SessionDto me = (await client.GetFromJsonAsync<SessionDto>(new Uri("/api/auth/me", UriKind.Relative)))!;
        Assert.AreEqual(Email, me.Email);

        HttpResponseMessage pools = await client.GetAsync(new Uri("/api/pools", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.OK, pools.StatusCode);

        // 7. 用同一个码再登一次也能过（绑定时用掉的正是当前这一步，见 TotpGenerator.Verify）
        LoginResultDto second = await LoginAsync(client);
        Assert.AreEqual("mfaRequired", second.Status, "已绑定之后再登录不该再交出密钥");
        Assert.IsNull(second.Secret);

        HttpResponseMessage verified = await client.PostAsJsonAsync(
            new Uri("/api/auth/mfa/verify", UriKind.Relative),
            new MfaCodeRequestDto
            {
                ChallengeId = second.ChallengeId,
                Code = TotpGenerator.Generate(secret, DateTimeOffset.UtcNow),
            });

        Assert.AreEqual(HttpStatusCode.OK, verified.StatusCode);

        // 8. 登出即失效
        HttpResponseMessage loggedOut = await client.PostAsync(new Uri("/api/auth/logout", UriKind.Relative), null);
        Assert.AreEqual(HttpStatusCode.NoContent, loggedOut.StatusCode);

        HttpResponseMessage afterLogout = await client.GetAsync(new Uri("/api/pools", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.Unauthorized, afterLogout.StatusCode);

        // 9. 守卫：这一整套确实是在**临时库**上跑的（否则第 3 步会把测试账号建进真实库）
        await using PoolPilotDbContext db = app.OpenDatabase();
        UserRecord? stored = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == Email);

        Assert.IsNotNull(stored, "账号应当落在临时库里");
        Assert.IsNotNull(stored.TotpSecret, "绑定之后密钥要落库");
    }

    [TestMethod]
    public async Task Login_ForUnknownAccount_SaysTheSameAsWrongPassword()
    {
        using AuthTestApp app = new();
        using HttpClient client = app.CreateClient();

        await RegisterAsync(client, Email, Password, app);

        HttpResponseMessage unknown = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new LoginRequestDto { Email = "nobody@example.com", Password = Password });

        HttpResponseMessage wrongPassword = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new LoginRequestDto { Email = Email, Password = "wrong" });

        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
        Assert.AreEqual(
            await unknown.Content.ReadAsStringAsync(),
            await wrongPassword.Content.ReadAsStringAsync(),
            "两种失败必须回同一句话，否则等于告诉对方这个邮箱注册过没有");
    }

    /// <summary>发验证码 → 从假投递器里取码 → 注册。三步在这一组测试里反复要用。</summary>
    internal static async Task RegisterAsync(HttpClient client, string email, string password, AuthTestApp app)
    {
        HttpResponseMessage codeResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/register/code", UriKind.Relative),
            new RegistrationCodeRequestDto { Email = email });

        Assert.AreEqual(HttpStatusCode.OK, codeResponse.StatusCode);

        string code = ExtractCode(app.Mailer.Sent[^1].Body);

        HttpResponseMessage registered = await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            new RegisterRequestDto
            {
                Email = email,
                Code = code,
                Password = password,
                ConfirmPassword = password,
            });

        Assert.AreEqual(HttpStatusCode.NoContent, registered.StatusCode);
    }

    private static async Task<LoginResultDto> LoginAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new LoginRequestDto { Email = Email, Password = Password });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<LoginResultDto>())!;
    }

    /// <summary>从邮件正文里挖出六位码。</summary>
    internal static string ExtractCode(string body)
    {
        Match match = Regex.Match(body, @"\b\d{6}\b");

        Assert.IsTrue(match.Success, $"邮件正文里没有六位验证码：{body}");

        return match.Value;
    }
}

/// <summary>
/// 测试用的应用实例。
///
/// **两处替换**（都在 <see cref="ConfigureWebHost"/> 里）：
/// · 数据库换成临时文件——绝不能让测试写到 <c>data/poolpilot.db</c>（那里面有真实凭据）。
///   注意必须先 <c>RemoveAll</c>：<c>AddDbContext</c> 是"再加一条注册"，不删的话谁生效
///   取决于解析顺序，而这种"赌顺序"的测试早晚会静默地连错库。
/// · 投递换成假实现：测试要拿到那串验证码才能走完注册，同时也不该真发邮件。
///
/// 不是 <c>sealed</c>：公开页那一组用例要在这两处之外再换掉上游（池客户端、盯市价、
/// 运行期同步）并打开功能开关，那些改动写在
/// <see cref="ConfigureTestServices"/> 的覆写里，而不是把整个工装抄一遍——
/// 抄一遍的结局是两份工装对"临时库"这件事开始有不同行为。
/// </summary>
internal class AuthTestApp : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"poolpilot-apitest-{Guid.NewGuid():N}.db");

    public CapturingMailer Mailer { get; } = new();

    /// <summary>直接打开那份临时库（测试用它做"确实落在临时库上"的守卫，以及播种/核对行数据）。</summary>
    public PoolPilotDbContext OpenDatabase() =>
        new(new DbContextOptionsBuilder<PoolPilotDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // 后台监控会去连交易所，在这一组测试里没有意义
        builder.UseSetting("LeadTraders:Enabled", "false");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<PoolPilotDbContext>>();
            services.RemoveAll<PoolPilotDbContext>();
            services.AddDbContext<PoolPilotDbContext>(options => options.UseSqlite($"Data Source={_dbPath}"));

            services.RemoveAll<INotificationMailer>();
            services.AddSingleton<INotificationMailer>(Mailer);

            ConfigureTestServices(services);
        });
    }

    /// <summary>子类追加服务替换的地方（默认什么都不换）。</summary>
    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        foreach (string path in new[] { _dbPath, $"{_dbPath}-wal", $"{_dbPath}-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 删不掉不影响测试结论（临时目录会被系统清理）
            }
        }
    }
}

/// <summary>把邮件收下的假投递器。</summary>
internal sealed class CapturingMailer : INotificationMailer
{
    private readonly List<(string Recipient, string Subject, string Body)> _sent = [];

    public IReadOnlyList<(string Recipient, string Subject, string Body)> Sent => _sent;

    public Task<NotificationDelivery> SendAsync(
        NotificationProvider? provider,
        EmailAddress recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);

        _sent.Add((recipient.Value, subject, body));

        // 默认回"发出去了"：这一组测试测的是接口接线，降级路径由 Application 层的测试覆盖
        return Task.FromResult(NotificationDelivery.Sent());
    }
}
