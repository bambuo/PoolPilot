using System.Net;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 本批接入强制的 42 个业务端点，跑在真实管线里的端到端断言。
///
/// 两条互补的用例：
/// <list type="number">
///   <item>**零角色账号**对全部 42 个端点逐条拿到 403——证明门确实接在每个端点上，
///     而不只是"编译里有那么个属性"。零角色是"登录着但没有任何授权"的最干净判据：
///     任何一条回了非 403，就说明那个端点没接上（或接错了权限点）。</item>
///   <item>**超级管理员**对一批只读业务资源拿到 200——反向证明门在授权存在时放行，
///     而不是"一律 403"这种把所有人都挡在外面的假通过。</item>
/// </list>
///
/// 401（匿名）另有覆盖（见 <c>AuthApiTests.BusinessEndpoints_RequireSigningIn</c>）。
/// 请求刻意不带 body / 不带合法的查询参数：授权过滤器跑在模型绑定之前，
/// 因此"门没放行"时一定是 403，而不是 400——这条顺序本身也被这组用例锁住。
/// </summary>
[TestClass]
public sealed class BusinessEndpointAuthorizationTests
{
    private const string OwnerEmail = "owner@example.com";
    private const string PlainEmail = "plain@example.com";
    private const string Password = "correct horse";

    /// <summary>本批接门的 42 个业务端点（方法 + 相对路径）。行数即"42"这个数本身。</summary>
    private static readonly (string Method, string Path)[] GatedEndpoints =
    [
        // 池列表（详情挂父菜单的 view）
        ("GET", "/api/pools"),
        ("GET", "/api/pools/1"),

        // 带单员
        ("GET", "/api/lead-traders"),
        ("GET", "/api/lead-traders/1"),
        ("PUT", "/api/lead-traders/1"),
        ("DELETE", "/api/lead-traders/1"),

        // 跟单（详情挂父菜单的 view；手动平仓是真实下单，归 edit）
        ("GET", "/api/followers"),
        ("GET", "/api/followers/1"),
        ("GET", "/api/followers/1/live-account"),
        ("GET", "/api/followers/1/live-orders"),
        ("POST", "/api/followers"),
        ("POST", "/api/followers/1/live-positions/close"),
        ("PUT", "/api/followers/1"),
        ("DELETE", "/api/followers/1"),

        // 交易所（自检/下单规划归 edit；删除归 delete）
        ("GET", "/api/exchanges"),
        ("POST", "/api/exchanges"),
        ("PUT", "/api/exchanges/1"),
        ("DELETE", "/api/exchanges/1"),
        ("POST", "/api/exchanges/1/probe"),
        ("GET", "/api/exchanges/1/symbol-map?pool=1"),
        ("POST", "/api/exchanges/live-order/plan"),

        // 标的映射
        ("GET", "/api/symbol-mappings/platforms"),
        ("GET", "/api/symbol-mappings/source-symbols"),
        ("GET", "/api/symbol-mappings/symbols?platform=Binance"),
        ("GET", "/api/symbol-mappings"),
        ("POST", "/api/symbol-mappings/scan?platform=Binance"),
        ("POST", "/api/symbol-mappings"),
        ("DELETE", "/api/symbol-mappings/1"),

        // 规模门槛与市场行情
        ("GET", "/api/feasibility/1"),
        ("GET", "/api/markets/1/quote"),

        // 回测（跑一次回测是对外请求，归 edit）
        ("GET", "/api/backtest/1"),

        // 通知
        ("GET", "/api/notifications/providers"),
        ("POST", "/api/notifications/providers"),
        ("PUT", "/api/notifications/providers/1"),
        ("DELETE", "/api/notifications/providers/1"),
        ("POST", "/api/notifications/providers/1/test"),
        ("GET", "/api/notifications/templates"),
        ("PUT", "/api/notifications/templates/registration"),
        ("GET", "/api/notifications/logs"),

        // 监控（台账挂跟单的 view，事件与连接状态挂总览的 view）
        ("GET", "/api/monitor/events"),
        ("GET", "/api/monitor/watch-state"),
        ("GET", "/api/monitor/ledger/1"),
    ];

    [TestMethod]
    public async Task AllGatedEndpoints_Are403_ForAnAccountWithoutAnyRole()
    {
        // 先注册 owner（全库首个账号，自动成为超管），再注册零角色的 plain
        using AuthTestApp app = new();
        using HttpClient owner = await TestSignIn.Async(app, OwnerEmail, Password);
        using HttpClient plain = await TestSignIn.Async(app, PlainEmail, Password);

        // 证明 plain 是"登录着但没有授权"，而不是没登录：自省端点是 200
        Assert.AreEqual(
            HttpStatusCode.OK,
            (await plain.GetAsync(new Uri("/api/auth/permissions", UriKind.Relative))).StatusCode,
            "plain 应当处于已登录状态");

        Assert.HasCount(42, GatedEndpoints, "本表就是签字映射表里的 42 个端点，改动请一并更新本数与测试说明");

        List<string> leaks = [];

        foreach ((string method, string path) in GatedEndpoints)
        {
            HttpResponseMessage response = await SendAsync(plain, method, path);

            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                leaks.Add($"{method} {path} → {(int)response.StatusCode}");
            }
        }

        Assert.AreEqual(
            0,
            leaks.Count,
            "以下端点对「零角色账号」没有回 403（门没接上，或接错了权限点）：\n"
            + string.Join("\n", leaks));

        // 反向守卫：owner 是超管，至少有一条必须放行，否则"一律 403"也会让上面全绿
        Assert.AreEqual(
            HttpStatusCode.OK,
            (await owner.GetAsync(new Uri("/api/lead-traders", UriKind.Relative))).StatusCode,
            "超管应当能读带单员列表");
    }

    [TestMethod]
    public async Task SuperadminEndpoints_Are200_OnReadableBusinessResources()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await TestSignIn.Async(app, OwnerEmail, Password);

        // 只挑不依赖外部网络、不依赖已有数据的只读端点：它们在空库上也应当是 200
        foreach (string path in new[]
        {
            "/api/lead-traders",
            "/api/followers",
            "/api/symbol-mappings",
            "/api/notifications/providers",
            "/api/notifications/templates",
            "/api/notifications/logs",
            "/api/monitor/events",
            "/api/monitor/watch-state",
        })
        {
            HttpResponseMessage response = await owner.GetAsync(new Uri(path, UriKind.Relative));

            Assert.AreEqual(
                HttpStatusCode.OK,
                response.StatusCode,
                $"{path} 超管应当读得到（门在授权存在时要放行）");
        }
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path) => method switch
    {
        "GET" => client.GetAsync(new Uri(path, UriKind.Relative)),
        "POST" => client.PostAsync(new Uri(path, UriKind.Relative), content: null),
        "PUT" => client.PutAsync(new Uri(path, UriKind.Relative), content: null),
        "DELETE" => client.DeleteAsync(new Uri(path, UriKind.Relative)),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "未支持的 HTTP 方法"),
    };
}
