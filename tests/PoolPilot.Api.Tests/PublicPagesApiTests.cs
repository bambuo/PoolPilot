using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using PoolPilot.Api.Streaming;
using PoolPilot.Api.Tests.Fakes;
using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;
using PoolPilot.Infrastructure.Persistence;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 公开面（<c>/api/public/*</c>）跑在真实管线里的集成用例。
///
/// 这一组用例的价值恰恰在**接线**上，而这些是应用层用例测不到的：
/// <list type="bullet">
///   <item>全局 <c>MapControllers().RequireAuthorization()</c> 与逐动作 <c>[AllowAnonymous]</c>
///     怎么合起来——匿名请求真的能进来，而且不带会话也进得来。</item>
///   <item>访客 Cookie 的属性与"只签发一次"：那是 <c>HttpResponse.Cookies</c> 的行为，
///     只有真的把请求打进去才看得见。</item>
///   <item>开关关掉时八个业务端点都回 404（状态读口与 SSE 各有独立用例），而不是只回一个"我以为关掉了"。</item>
///   <item>状态码与 <c>{ error }</c> 形状：400/403/404 各自走的哪条路（模型绑定、用例抛出的
///     <c>ArgumentException</c>、控制器自己的 StatusCode）。</item>
/// </list>
/// 上游（池子数据、盯市价、运行期同步）换成替身，理由见 <see cref="PublicPageUpstream"/>。
/// </summary>
[TestClass]
public sealed class PublicPagesApiTests
{
    private const string LeadPath = "/api/public/lead-traders";
    private const string FollowerPath = "/api/public/followers";
    private const string OwnershipPath = FollowerPath + "/ownership";
    private const string PoolOptionsPath = "/api/public/pool-options";
    private const string StatusPath = "/api/public/status";
    private const string StreamPath = "/api/public/stream";

    private const string VisitorCookieName = "poolpilot_visitor";

    private static readonly long KnownPoolIndex = PublicPageUpstream.KnownPool.Value;

    /// <summary>
    /// G3 的键集合。HTTP 详情与 <c>detail</c> 流必须**逐键一致**，
    /// 所以两处断言共用这一份——抄两份迟早会有一份先漂移。
    /// </summary>
    private static readonly string[] FollowerDetailKeys =
    [
        "id", "label", "leadAccountIndex", "leadName", "leadIsEnabled", "isActive", "createdAt",
        "isOwnedByCaller", "initialCapital", "equity", "returnPercent", "realizedPnl", "unrealizedPnl", "fees",
        "grossNotional", "scale", "poolEquity", "fillCount", "skipCount", "isValuationAvailable", "positions",
        "mode", "marksUnavailable", "fills", "curve",
    ];

    /// <summary>带单员详情只暴露池详情里适合匿名观察的字段。</summary>
    private static readonly string[] LeadDetailKeys =
    [
        "accountIndex", "name", "origin", "isFrozen", "isWatching", "followerCount", "signalCount",
        "lastSignalAt", "observedSince", "poolCreatedAt", "totalAssetValue", "totalShares", "sharePrice",
        "navAt", "operatorFeePercent", "operatorSharePercent", "minOperatorSharePercent", "isDepositLimited",
        "metrics", "exposure", "positions", "orders", "navCurve",
    ];

    // ── G1–G4：匿名可读 ───────────────────────────────────

    [TestMethod]
    public async Task AnonymousReads_G1ThroughG4_AllComeBackWithoutSigningIn()
    {
        using PublicPagesApp app = new();
        using HttpClient client = app.CreateClient();

        await SeedLeadAsync(app, PublicPageUpstream.KnownPool.Value, isEnabled: true);
        await SeedPublicFollowerAsync(app, ownerUserId: 7L, label: "我登录时建的", visitorOwned: false);

        foreach (string path in new[] { LeadPath, FollowerPath, $"{FollowerPath}/1", PoolOptionsPath })
        {
            HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"{path} 应当匿名可读（公开面就是给人看的）");
        }

        // 带单页的一行：形状就是契约上那七个键
        PublicLeadTraderDto[] leads = (await client.GetFromJsonAsync<PublicLeadTraderDto[]>(
            new Uri(LeadPath, UriKind.Relative)))!;

        Assert.AreEqual(1, leads.Length);
        Assert.AreEqual(PublicPageUpstream.KnownPool.Value, leads[0].AccountIndex);
        Assert.AreEqual("ENA 池", leads[0].Name);
    }

    [TestMethod]
    public async Task G1_LeadTraderRow_HasExactlyTheContractedKeys()
    {
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);

        using HttpClient client = app.CreateClient();
        JsonElement row = FirstRow(await client.GetAsync(new Uri(LeadPath, UriKind.Relative)));

        AssertKeys(row, ["accountIndex", "name", "isWatching", "followerCount", "signalCount", "lastSignalAt", "createdAt"]);

        // isEnabled 不在公开契约里：这一页没有开关，输出它只会让人以为自己能点
        Assert.IsFalse(TryGet(row, "isEnabled", out _), "公开带单页不许输出 isEnabled");
    }

    [TestMethod]
    public async Task G2_FollowerCard_HasExactlyTheContractedKeys_AndNoExchangeFields()
    {
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);
        await SeedPublicFollowerAsync(app, ownerUserId: null, label: "我的第一单", visitorOwned: true);

        using HttpClient client = app.CreateClient();
        JsonElement row = FirstRow(await client.GetAsync(new Uri(FollowerPath, UriKind.Relative)));

        AssertKeys(
            row,
            [
                "id", "label", "leadAccountIndex", "leadName", "leadIsEnabled", "isActive", "createdAt",
                "isOwnedByCaller", "initialCapital", "equity", "returnPercent", "realizedPnl", "unrealizedPnl", "fees",
                "grossNotional", "scale", "poolEquity", "fillCount", "skipCount", "isValuationAvailable", "positions",
            ]);

        // 这几个名字是私有卡片上有、公开契约上没有的。逐个钉住而不是"看起来差不多"：
        // 以后有人把 FollowerSummaryDto 直接返回给公开端点，这条会先红
        foreach (string forbidden in new[] { "exchangeName", "exchangeConfigId", "availableBalance", "livePositions" })
        {
            Assert.IsFalse(TryGet(row, forbidden, out _), $"公开卡片不许输出 {forbidden}");
        }

        JsonElement position = row.GetProperty("positions");
        Assert.AreEqual(JsonValueKind.Array, position.ValueKind);
    }

    [TestMethod]
    public async Task G3_FollowerDetail_HasTheCardFieldsPlusModePositionsFillsAndCurve()
    {
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);
        long id = await SeedPublicFollowerAsync(app, ownerUserId: null, label: "详情", visitorOwned: true);

        using HttpClient client = app.CreateClient();
        HttpResponseMessage response = await client.GetAsync(new Uri($"{FollowerPath}/{id}", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        AssertKeys(body, FollowerDetailKeys);

        Assert.AreEqual("paper", body.GetProperty("mode").GetString());

        // 详情的持仓是完整形状（卡片上只有摘要那几项）
        Assert.IsTrue(body.TryGetProperty("positions", out JsonElement positions) && positions.GetArrayLength() == 0,
            "刚建的行没有持仓，但键必须在");
    }

    [TestMethod]
    public async Task G3_PrivateRowAndLiveRow_AndMissingRow_AllReturn404_WithTheSameMessage()
    {
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);

        long privateId = await SeedFollowerAsync(app, isPublic: false, mode: CopyMode.Paper, ownerUserId: 7L);
        long liveId = await SeedFollowerAsync(app, isPublic: true, mode: CopyMode.Live, ownerUserId: 7L);

        using HttpClient client = app.CreateClient();

        List<string> messages = [];

        foreach (long id in new[] { privateId, liveId, 424_242L })
        {
            HttpResponseMessage response = await client.GetAsync(new Uri($"{FollowerPath}/{id}", UriKind.Relative));

            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, $"公开面对 id={id} 应当只说「不存在」");
            messages.Add(await response.Content.ReadAsStringAsync());
        }

        // 数字先抹成同一个占位再比：三种情况的文案必须一模一样（各自的 Id 本来就在消息里，
        // 那是请求者自己报上来的数，不构成信息差）
        int distinctShapes = messages
            .Select(message => System.Text.RegularExpressions.Regex.Replace(message, @"\d+", "#"))
            .Distinct()
            .Count();

        Assert.AreEqual(1, distinctShapes,
            "三种情况的响应必须一模一样：只要文案形状有差别，{id} 逐个试一遍就能把私有行探测出来");
    }

    [TestMethod]
    public async Task G2_ListExcludesPrivateAndLiveRows_Entirely()
    {
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);
        await SeedFollowerAsync(app, isPublic: false, mode: CopyMode.Paper, ownerUserId: 7L);
        await SeedFollowerAsync(app, isPublic: true, mode: CopyMode.Live, ownerUserId: 7L);
        long publicId = await SeedPublicFollowerAsync(app, ownerUserId: null, label: null, visitorOwned: true);

        using HttpClient client = app.CreateClient();
        PublicFollowerSummaryDto[] rows = (await client.GetFromJsonAsync<PublicFollowerSummaryDto[]>(
            new Uri(FollowerPath, UriKind.Relative)))!;

        Assert.AreEqual(1, rows.Length, "列表里只该有「公开 + 纸面」那一行");
        Assert.AreEqual(publicId, rows[0].Id);
    }

    [TestMethod]
    public async Task G4_PoolOptions_ServesTheCatalogAndClampsTheLimit()
    {
        using PublicPagesApp app = new();

        using HttpClient client = app.CreateClient();
        PublicPoolOptionDto[] options = (await client.GetFromJsonAsync<PublicPoolOptionDto[]>(
            new Uri($"{PoolOptionsPath}?q=ena&limit=500", UriKind.Relative)))!;

        Assert.AreEqual(1, options.Length, "q 是大小写不敏感的子串匹配；冻结池不在此列");
        Assert.AreEqual(KnownPoolIndex, options[0].AccountIndex);
        Assert.AreEqual("ENA 池", options[0].Name);
        Assert.AreEqual(PublicPageUpstream.PoolEquity, options[0].TotalAssetValue);
    }

    [TestMethod]
    public async Task G4_PoolOptions_ExcludesFrozenPools_AndFindsByExactAccountIndex()
    {
        using PublicPagesApp app = new();
        using HttpClient client = app.CreateClient();

        // 名字能搜到、索引也能精确命中，但它是冻结池——两种方式都必须返回空
        foreach (string query in new[] { "已冻结", PublicPageUpstream.FrozenPool.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            PublicPoolOptionDto[] frozen = (await client.GetFromJsonAsync<PublicPoolOptionDto[]>(
                new Uri($"{PoolOptionsPath}?q={Uri.EscapeDataString(query)}", UriKind.Relative)))!;

            Assert.AreEqual(0, frozen.Length, $"冻结池不该出现在候选里（q={query}）");
        }

        PublicPoolOptionDto[] byIndex = (await client.GetFromJsonAsync<PublicPoolOptionDto[]>(
            new Uri($"{PoolOptionsPath}?q={KnownPoolIndex}", UriKind.Relative)))!;

        Assert.AreEqual(1, byIndex.Length, "纯数字查询按账户索引精确匹配");
        Assert.AreEqual(KnownPoolIndex, byIndex[0].AccountIndex);
    }

    [TestMethod]
    public async Task G4_PoolOptions_DoesNotPierceTheUpstreamOnEveryKeystroke()
    {
        // 契约上的"服务端 60 秒缓存"必须有钉子：候选框是每敲一个字问一次的交互，
        // 匿名流量若每次穿透，上游那点按权重算的额度会先被输入框吃光
        using PublicPagesApp app = new();
        using HttpClient client = app.CreateClient();

        await client.GetAsync(new Uri($"{PoolOptionsPath}?q=ena", UriKind.Relative));
        await client.GetAsync(new Uri($"{PoolOptionsPath}?q=ena ", UriKind.Relative));

        Assert.AreEqual(1, app.Upstream.PoolListReads, "60 秒窗口里的第二发必须吃缓存，不该再打上游");
    }

    // ── W1：公开侧无法暂停 ────────────────────────────────

    [TestMethod]
    public async Task W1_OnAnExistingPausedLeadTrader_LeavesItPausedAndReturnsItAsIs()
    {
        // 这一发是整个 W1 契约的落点：公开页用的是 Ensure，不是 Configure。
        // 换成 Configure(IsEnabled=true) 的话，陌生人就能把使用者特意暂停的观察者打开
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: false);

        using HttpClient client = app.CreateClient();
        HttpResponseMessage response = await client.PostAsync(
            new Uri($"{LeadPath}/{KnownPoolIndex}", UriKind.Relative),
            content: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        await using (PoolPilotDbContext db = app.OpenDatabase())
        {
            LeadTraderRecord? stored = await db.LeadTraders.AsNoTracking()
                .SingleAsync(l => l.AccountIndex == KnownPoolIndex);

            Assert.IsFalse(stored!.IsEnabled, "IsEnabled 必须原样留着——这一发没有改动它的权力");
        }
    }

    [TestMethod]
    public async Task W1_OnAKnownPoolWithNoLeadTrader_CreatesItWatching()
    {
        using PublicPagesApp app = new();

        using HttpClient client = app.CreateClient();
        HttpResponseMessage response = await client.PostAsync(
            new Uri($"{LeadPath}/{KnownPoolIndex}", UriKind.Relative),
            content: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        PublicLeadTraderDto created = (await response.Content.ReadFromJsonAsync<PublicLeadTraderDto>())!;
        Assert.AreEqual("ENA 池", created.Name, "新建时把池名落一份快照，与工作台同一条路径");

        await using PoolPilotDbContext db = app.OpenDatabase();
        Assert.AreEqual(1, await db.LeadTraders.CountAsync());
    }

    [TestMethod]
    public async Task W1_OnAnUnknownPool_IsBadRequestWithTheExistingMessage()
    {
        using PublicPagesApp app = new();

        using HttpClient client = app.CreateClient();
        HttpResponseMessage response = await client.PostAsync(
            new Uri($"{LeadPath}/{PublicPageUpstream.MissingPool.Value}", UriKind.Relative),
            content: null);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "找不到账户索引");
    }

    // ── W2：匿名建跟单 ────────────────────────────────────

    [TestMethod]
    public async Task W2_IssuesTheVisitorCookieOnce_AndNeverInItTheSecretAnywhereElse()
    {
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);

        using HttpClient client = app.CreateClient();

        HttpResponseMessage first = await CreateAsync(client, label: "第一单");
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);

        string? cookieHeader = GetSetCookie(first, VisitorCookieName);
        Assert.IsNotNull(cookieHeader, "匿名建的行要靠这枚 Cookie 认创建者，没签出去就是永久孤儿行");

        // ASP.NET Core 写 Set-Cookie 时属性名是小写（httponly/samesite/path/max-age），
        // 断言按实际发出的形状来钉：钉 "HttpOnly" 会红在一个排版上，而不是红在行为上
        foreach (string attribute in new[] { "httponly", "samesite=lax", "path=/api/public", "max-age=" })
        {
            Assert.IsTrue(
                cookieHeader.Contains(attribute, StringComparison.OrdinalIgnoreCase),
                $"访客 Cookie 必须带 {attribute}——少一样这枚 Cookie 的暴露面就变大一点");
        }

        // 第二次不再签：已经有了，重复签发会让浏览器里的标识每次刷新都换一枚，
        // 于是第一批行再也删不掉
        HttpResponseMessage second = await CreateAsync(client, label: "第二单");
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        Assert.IsNull(GetSetCookie(second, VisitorCookieName), "带着合法 Cookie 的请求不该再被签一次");

        // 密钥只出现在 Cookie 里：响应体与 Location 里都不许有它
        string secret = ExtractCookieValue(cookieHeader);
        string body = await second.Content.ReadAsStringAsync();
        Assert.IsFalse(body.Contains(secret, StringComparison.Ordinal), "访客密钥不许出现在响应体里");
        Assert.IsFalse(
            (second.Headers.Location?.ToString() ?? string.Empty).Contains(secret, StringComparison.Ordinal),
            "也不许出现在 URL 里");
    }

    [TestMethod]
    public async Task W2_WithLiveMode_IsRejectedAndWritesNothing()
    {
        using PublicPagesApp app = new();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri(FollowerPath, UriKind.Relative),
            new { leadAccountIndex = KnownPoolIndex, initialCapital = 500m, mode = "live" });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "纸面", "要说清公开页只有纸面，而不是只回一个「参数错误」");
        Assert.IsFalse(body.Contains("已按纸面", StringComparison.Ordinal), "**不许静默降级**：降级成功的口气等于默认接受");

        await using PoolPilotDbContext db = app.OpenDatabase();
        Assert.AreEqual(0, await db.Followers.CountAsync());
        Assert.IsNull(GetSetCookie(response, VisitorCookieName), "被拒的请求不该在浏览器里留下一枚 Cookie");
    }

    [TestMethod]
    public async Task W2_WithExchangeConfigOrBadCapitalOrBadLabel_IsRejected()
    {
        using PublicPagesApp app = new();
        using HttpClient client = app.CreateClient();

        var rejected = new
        {
            withExchange = new { leadAccountIndex = KnownPoolIndex, initialCapital = 500m, exchangeConfigId = 3L },
            // 贴着区间外沿的两侧：9.99 与 10001 都该被拒（区间 [10, 10000] 是闭的，Application 层已钉住）
            tooSmall = new { leadAccountIndex = KnownPoolIndex, initialCapital = 9.99m },
            tooBig = new { leadAccountIndex = KnownPoolIndex, initialCapital = 10_001m },
            tooLong = new { leadAccountIndex = KnownPoolIndex, initialCapital = 500m, label = new string('x', 25) },
        };

        foreach (object payload in new object[] { rejected.withExchange, rejected.tooSmall, rejected.tooBig, rejected.tooLong })
        {
            HttpResponseMessage response = await client.PostAsJsonAsync(
                new Uri(FollowerPath, UriKind.Relative),
                payload);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, $"{payload} 不该被接受");
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "error");
        }

        await using PoolPilotDbContext db = app.OpenDatabase();
        Assert.AreEqual(0, await db.Followers.CountAsync(), "四个被拒的请求一行都不该写进库");
    }

    [TestMethod]
    public async Task W2_OnAPoolWithNoLeadTrader_IsRejectedAndAddsNothing()
    {
        using PublicPagesApp app = new();

        // 故意不播种带单员：这一发钉住的是"跟单员订阅的是带单员，不是池"
        using HttpClient client = app.CreateClient();
        HttpResponseMessage response = await CreateAsync(client, label: null);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "带单员");

        await using PoolPilotDbContext db = app.OpenDatabase();
        Assert.AreEqual(
            0,
            await db.LeadTraders.CountAsync(),
            "建观察者是公开带单页的独立动作（它占上游订阅与持续落库，有自己的名额与限流），不在跟单这一步发生");
        Assert.AreEqual(0, await db.Followers.CountAsync(), "被拒绝的请求一行都不该写进库");
        Assert.AreEqual(0, app.Sync.Requests, "也不该惊动运行期同步");
        Assert.IsNull(GetSetCookie(response, VisitorCookieName), "没建成就不该留下一枚没有归属的访客 Cookie");
    }

    [TestMethod]
    public async Task W2_SignedIn_BelongsToTheUserAndDoesNotGetACookie()
    {
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);

        using HttpClient client = await TestSignIn.Async(app, "public-owner@example.com", "correct horse");

        HttpResponseMessage response = await CreateAsync(client, label: "登录建的");

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.IsNull(GetSetCookie(response, VisitorCookieName), "归 user 列的行不需要访客 Cookie");

        await using PoolPilotDbContext db = app.OpenDatabase();
        FollowerRecord stored = await db.Followers.AsNoTracking().SingleAsync();

        long userId = (await client.GetFromJsonAsync<SessionDto>(new Uri("/api/auth/me", UriKind.Relative)))!.UserId;
        Assert.AreEqual(userId, stored.OwnerUserId);
        Assert.IsNull(stored.OwnerCredentialHash, "两个 owner 列只写一个");
        Assert.IsTrue(stored.IsPublic);
    }

    // ── W3：只有创建者能删 ────────────────────────────────

    [TestMethod]
    public async Task W3_WithoutAnyCookie_IsForbidden()
    {
        using PublicPagesApp app = new();
        long id = await SeedPublicFollowerAsync(app, ownerUserId: null, label: "别人的", visitorOwned: true);

        using HttpClient stranger = app.CreateClient();
        HttpResponseMessage response = await stranger.DeleteAsync(new Uri($"{FollowerPath}/{id}", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, "公开行存在，但创建者不是你");
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "创建者");

        await using PoolPilotDbContext db = app.OpenDatabase();
        Assert.AreEqual(1, await db.Followers.CountAsync(), "403 那一发不该动库");
    }

    [TestMethod]
    public async Task W3_VisitorACannotDeleteVisitorsBRow_ButCanDeleteItsOwn()
    {
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);

        using HttpClient visitorA = app.CreateClient();
        using HttpClient visitorB = app.CreateClient();

        long aRow = (await CreateReader(await CreateAsync(visitorA, label: "A 的行")))!.Id;
        long bRow = (await CreateReader(await CreateAsync(visitorB, label: "B 的行")))!.Id;

        Assert.AreNotEqual(aRow, bRow);

        await SeedLedgerRowsAsync(app, aRow);

        // 两个浏览器各有自己的 Cookie：A 删 B 的行 → 403
        HttpResponseMessage forbidden = await visitorA.DeleteAsync(new Uri($"{FollowerPath}/{bRow}", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // 而 isOwnedByCaller 在 A 的列表里已经把这一点如实标出来了（按钮不会点了才失败）
        PublicFollowerSummaryDto[] visibleToA = (await visitorA.GetFromJsonAsync<PublicFollowerSummaryDto[]>(
            new Uri(FollowerPath, UriKind.Relative)))!;

        Assert.IsTrue(visibleToA.Single(f => f.Id == aRow).IsOwnedByCaller);
        Assert.IsFalse(visibleToA.Single(f => f.Id == bRow).IsOwnedByCaller);

        // A 删自己的 → 204，并连带清掉它的流水
        HttpResponseMessage deleted = await visitorA.DeleteAsync(new Uri($"{FollowerPath}/{aRow}", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);

        await using (PoolPilotDbContext db = app.OpenDatabase())
        {
            Assert.AreEqual(0, await db.Followers.CountAsync(f => f.Id == aRow));
            Assert.AreEqual(1, await db.Followers.CountAsync(f => f.Id == bRow), "B 的行必须还在");

            // 复用工作台的删除路径就意味着复用它的清理：流水与权益点不许变成孤儿行
            Assert.AreEqual(0, await db.FollowerFills.CountAsync(f => f.FollowerId == aRow));
            Assert.AreEqual(0, await db.FollowerEquityPoints.CountAsync(p => p.FollowerId == aRow));
        }

        // 删完之后 B 的行仍然读得到，A 的那一行不再存在
        Assert.AreEqual(
            HttpStatusCode.NotFound,
            (await visitorA.GetAsync(new Uri($"{FollowerPath}/{aRow}", UriKind.Relative))).StatusCode);
    }

    [TestMethod]
    public async Task W3_SignedInCreator_DeletesItsOwnRow()
    {
        // 登录建的行归 owner_user_id 那一列：删除走的是同一句「只有创建者」，
        // 但判据完全不同一条——这一发同时钉住「带着访客 Cookie 的别人删不掉它」
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);

        using HttpClient owner = await TestSignIn.Async(app, "public-deleter@example.com", "correct horse");

        long id = (await CreateReader(await CreateAsync(owner, label: "登录建的")))!.Id;

        using HttpClient stranger = app.CreateClient();
        Assert.AreEqual(
            HttpStatusCode.Forbidden,
            (await stranger.DeleteAsync(new Uri($"{FollowerPath}/{id}", UriKind.Relative))).StatusCode,
            "陌生浏览器（没有会话也没有对应密钥）不该删得掉");

        HttpResponseMessage deleted = await owner.DeleteAsync(new Uri($"{FollowerPath}/{id}", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);

        await using PoolPilotDbContext db = app.OpenDatabase();
        Assert.AreEqual(0, await db.Followers.CountAsync());
    }

    [TestMethod]
    public async Task W3_OnAPrivateRow_IsNotFoundEvenForTheSignedInUserWhoOwnsIt()
    {
        using PublicPagesApp app = new();
        long privateId = await SeedFollowerAsync(app, isPublic: false, mode: CopyMode.Paper, ownerUserId: 7L);

        using HttpClient client = app.CreateClient();
        HttpResponseMessage response = await client.DeleteAsync(new Uri($"{FollowerPath}/{privateId}", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, "工作台建的行在公开面上不存在");
    }

    // ── G2b：归属读口 ─────────────────────────────────────

    [TestMethod]
    public async Task Ownership_ReturnsOnlyTheCallersPublicPaperRows()
    {
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);

        long mine = await SeedPublicFollowerAsync(app, ownerUserId: null, label: "我的", visitorOwned: true);
        await SeedFollowerAsync(app, isPublic: false, mode: CopyMode.Paper, ownerUserId: 7L);
        await SeedFollowerAsync(app, isPublic: true, mode: CopyMode.Live, ownerUserId: 7L);

        using HttpClient stranger = app.CreateClient();
        long[] none = (await stranger.GetFromJsonAsync<long[]>(new Uri(OwnershipPath, UriKind.Relative)))!;
        Assert.AreEqual(0, none.Length, "没有 Cookie 的调用者不拥有任何公开行");

        using HttpClient owner = app.CreateClient();
        owner.DefaultRequestHeaders.Add("Cookie", $"{VisitorCookieName}={FixedVisitorSecret}");

        long[] ids = (await owner.GetFromJsonAsync<long[]>(new Uri(OwnershipPath, UriKind.Relative)))!;

        CollectionAssert.AreEqual(new[] { mine }, ids, "只回公开+纸面且归属匹配的行；私有行与实盘行不在此列");
    }

    [TestMethod]
    public async Task Ownership_ForASignedInCreator_ComesFromTheSession()
    {
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);

        using HttpClient owner = await TestSignIn.Async(app, "public-owner-ids@example.com", "correct horse");
        PublicFollowerDto created = (await CreateReader(await CreateAsync(owner, label: "登录建的")))!;

        long[] ids = (await owner.GetFromJsonAsync<long[]>(new Uri(OwnershipPath, UriKind.Relative)))!;

        CollectionAssert.AreEqual(new[] { created.Id }, ids);
    }

    // ── 开关 ──────────────────────────────────────────────

    [TestMethod]
    public async Task Status_IsAnonymousAndReportsBothSwitches_EvenWhenBothAreOff()
    {
        // 前端靠它区分"这一页没开放"与"实时流正在连接"；两片都关时它必须仍然 200
        using PublicPagesApp off = new(leads: false, followers: false);
        using HttpClient offClient = off.CreateClient();

        HttpResponseMessage response = await offClient.GetAsync(new Uri(StatusPath, UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        PublicPageStatusDto bothOff =
            (await response.Content.ReadFromJsonAsync<PublicPageStatusDto>())!;
        Assert.IsFalse(bothOff.Leads);
        Assert.IsFalse(bothOff.Followers);
        Assert.IsTrue(bothOff.AccountsEnabled, "默认部署仍允许登录/注册");

        using PublicPagesApp half = new(leads: true, followers: false);
        using HttpClient halfClient = half.CreateClient();

        PublicPageStatusDto halfOn = (await halfClient.GetFromJsonAsync<PublicPageStatusDto>(
            new Uri(StatusPath, UriKind.Relative)))!;
        Assert.IsTrue(halfOn.Leads);
        Assert.IsFalse(halfOn.Followers);
        Assert.IsTrue(halfOn.AccountsEnabled);
    }

    [TestMethod]
    public async Task AllPublicEndpoints_Return404_WhenTheirSwitchIsOff()
    {
        // 开关状态由这一发**自己钉住**，不靠环境里的默认值：集成测试的宿主跑在 Development，
        // 谁在 src/PoolPilot.Api/appsettings.Development.json 里放开过公开页，"默认是关的"
        // 这句断言就会在别人机器上变成 200。默认值本身是配置文件的事，不是这条用例的判据。
        using PublicPagesApp app = new(leads: false, followers: false);
        using HttpClient client = app.CreateClient();

        var requests = new (string Method, string Path)[]
        {
            ("GET", LeadPath),
            ("POST", $"{LeadPath}/{KnownPoolIndex}"),
            ("GET", PoolOptionsPath),
            ("GET", FollowerPath),
            ("GET", OwnershipPath),
            ("GET", $"{FollowerPath}/1"),
            ("POST", FollowerPath),
            ("DELETE", $"{FollowerPath}/1"),
        };

        foreach ((string method, string path) in requests)
        {
            using HttpRequestMessage request = new(
                new HttpMethod(method),
                new Uri(path, UriKind.Relative));

            if (method == "POST")
            {
                request.Content = JsonContent.Create(new { leadAccountIndex = KnownPoolIndex, initialCapital = 500m });
            }

            HttpResponseMessage response = await client.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, $"{method} {path} 在开关关掉时应当「不存在」");

            string body = await response.Content.ReadAsStringAsync();
            StringAssert.Contains(body, "公开页未启用");
        }
    }

    [TestMethod]
    public async Task FollowersSwitchOff_ButLeadsSwitchOn_OnlyTheFollowerPageGoesAway()
    {
        // 两片是各自一刀的：只放开带单页时，跟单页的七个动作里属于它的那三条也要关
        using PublicPagesApp app = new(leads: true, followers: false);
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);

        using HttpClient client = app.CreateClient();

        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync(new Uri(LeadPath, UriKind.Relative))).StatusCode);
        Assert.AreEqual(
            HttpStatusCode.NotFound,
            (await client.GetAsync(new Uri(FollowerPath, UriKind.Relative))).StatusCode);
    }

    // ── 限流 ──────────────────────────────────────────────

    [TestMethod]
    public async Task PublicReads_Return429WithRetryAfter_AfterTheWindowBudget()
    {
        using PublicPagesApp app = new();
        using HttpClient client = app.CreateClient();

        for (int i = 0; i < 60; i++)
        {
            using HttpResponseMessage allowed = await client.GetAsync(new Uri(PoolOptionsPath, UriKind.Relative));
            Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode, $"第 {i + 1} 发仍在读额度内");
        }

        using HttpResponseMessage rejected = await client.GetAsync(new Uri(PoolOptionsPath, UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.IsTrue(rejected.Headers.TryGetValues("Retry-After", out _), "429 必须告诉调用方等多久");
        StringAssert.Contains(await rejected.Content.ReadAsStringAsync(), "太频繁");
    }

    [TestMethod]
    public async Task PublicWrites_Return429_AfterTheWindowBudget()
    {
        using PublicPagesApp app = new();
        using HttpClient client = app.CreateClient();

        for (int i = 0; i < 10; i++)
        {
            using HttpResponseMessage allowed = await client.PostAsync(
                new Uri($"{LeadPath}/{KnownPoolIndex}", UriKind.Relative),
                content: null);

            Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode, $"第 {i + 1} 发仍在写额度内");
        }

        using HttpResponseMessage rejected = await client.PostAsync(
            new Uri($"{LeadPath}/{KnownPoolIndex}", UriKind.Relative),
            content: null);

        Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        StringAssert.Contains(await rejected.Content.ReadAsStringAsync(), "太频繁");
    }

    // ── SSE：公开页实时流 ─────────────────────────────────

    [TestMethod]
    public async Task Stream_SnapshotConnection_StartsWithAFullFrame_AndCarriesTheSseHeaders()
    {
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);
        await SeedPublicFollowerAsync(app, ownerUserId: null, label: "流上的行", visitorOwned: true);

        using HttpClient client = app.CreateClient();
        var connection = await OpenSseAsync(client, StreamPath);
        using HttpResponseMessage response = connection.Response;
        using StreamReader reader = connection.Reader;

        (string eventName, string data) = await ReadEventAsync(reader);
        Assert.AreEqual("snapshot", eventName, "列表连接建连即发 snapshot");

        JsonElement frame = JsonDocument.Parse(data).RootElement;
        AssertKeys(frame, ["leads", "followers", "takenAt"]);
        Assert.AreEqual(1, frame.GetProperty("leads").GetArrayLength());
        Assert.AreEqual(1, frame.GetProperty("followers").GetArrayLength());
        Assert.AreEqual(JsonValueKind.String, frame.GetProperty("takenAt").ValueKind);
    }

    [TestMethod]
    public async Task Stream_Returns404_WhenBothPublicPagesAreSwitchedOff()
    {
        using PublicPagesApp app = new(leads: false, followers: false);
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync(new Uri(StreamPath, UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "公开页未启用");
    }

    [TestMethod]
    public async Task Stream_DetailConnection_SendsTheG3DocumentAsItsFirstFrame()
    {
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);
        long id = await SeedPublicFollowerAsync(app, ownerUserId: null, label: "详情流", visitorOwned: true);

        using HttpClient client = app.CreateClient();
        var connection = await OpenSseAsync(client, $"{StreamPath}?detail={id}");
        using HttpResponseMessage response = connection.Response;
        using StreamReader reader = connection.Reader;

        (string eventName, string data) = await ReadEventAsync(reader);
        Assert.AreEqual("detail", eventName, "详情连接只发 detail 帧");

        JsonElement body = JsonDocument.Parse(data).RootElement;
        AssertKeys(body, FollowerDetailKeys);
        Assert.AreEqual(id, body.GetProperty("id").GetInt64());
        Assert.AreEqual("paper", body.GetProperty("mode").GetString());
    }

    [TestMethod]
    public async Task Stream_LeadConnection_SendsTheCuratedPoolDetailAsItsFirstFrame()
    {
        // 带单员详情弹窗的数据源是池详情，但不是工作台那份原样透传：
        // 只给匿名观察需要的指标、敞口与前几大持仓，键集合由 LeadDetailKeys 钉住。
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);

        using HttpClient client = app.CreateClient();
        var connection = await OpenSseAsync(client, $"{StreamPath}?lead={KnownPoolIndex}");
        using HttpResponseMessage response = connection.Response;
        using StreamReader reader = connection.Reader;

        (string eventName, string data) = await ReadEventAsync(reader);
        Assert.AreEqual("lead", eventName, "带单员详情连接只发 lead 帧");

        JsonElement body = JsonDocument.Parse(data).RootElement;
        AssertKeys(body, LeadDetailKeys);
        Assert.AreEqual(KnownPoolIndex, body.GetProperty("accountIndex").GetInt64());
        Assert.AreEqual("ENA 池", body.GetProperty("name").GetString());
        Assert.AreEqual(1_000m, body.GetProperty("totalAssetValue").GetDecimal());
        Assert.AreEqual(1.1m, body.GetProperty("sharePrice").GetDecimal());
        Assert.AreEqual(3, body.GetProperty("navCurve").GetArrayLength());
        Assert.AreEqual(0, body.GetProperty("positions").GetArrayLength());
        Assert.AreEqual(0, body.GetProperty("orders").GetArrayLength());
        Assert.AreEqual(0, body.GetProperty("followerCount").GetInt32(), "没有跟单员时池详情也要能打开");
        Assert.IsFalse(body.GetProperty("isDepositLimited").GetBoolean());

        JsonElement metrics = body.GetProperty("metrics");
        Assert.AreEqual(10m, metrics.GetProperty("realTotalReturnPercent").GetDecimal());
        Assert.AreEqual(12m, metrics.GetProperty("reportedApyPercent").GetDecimal());
        Assert.IsFalse(metrics.GetProperty("isStatisticallyMeaningful").GetBoolean(), "三个点不足以下结论");

        JsonElement exposure = body.GetProperty("exposure");
        Assert.AreEqual(0m, exposure.GetProperty("grossExposure").GetDecimal());
        Assert.AreEqual(0, exposure.GetProperty("activePositionCount").GetInt32());
    }

    [TestMethod]
    public async Task Stream_LeadDetail_CarriesPositionPnlAndOnlyTheDisplayedMarketOrders()
    {
        // 公开详情与工作台共用展示模型：持仓带开仓价/标记价/清算价/逐仓盈亏与 ROI，
        // 挂单只给当前展示的持仓市场（用于行展开），不把池子的整本挂单摊出去。
        using PublicPagesApp app = new();
        app.Upstream.Positions =
        [
            new PoolPosition
            {
                MarketId = 7,
                Symbol = "WLD",
                Side = PositionSide.Long,
                Size = 1_000m,
                AvgEntryPrice = PriceAmount.From(0.51924m),
                Notional = UsdAmount.From(500m),
                UnrealizedPnl = UsdAmount.From(-19.24m),
                Margin = MarginFraction.FromPositionPercent(10m),
                OpenOrderCount = 1,
                CumulativeFunding = UsdAmount.From(0m),
                LiquidationPrice = null,
            },
        ];
        app.Upstream.Orders =
        [
            new PoolOrder
            {
                OrderIndex = 11,
                ClientOrderIndex = 0,
                MarketIndex = 7,
                Side = OrderSide.Buy,
                Price = PriceAmount.From(0.49m),
                RemainingBaseAmount = 100m,
                InitialBaseAmount = 200m,
                Status = "open",
                CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000),
                ExpiresAt = LighterTimestamp.FromSeconds(1_800_086_400),
            },
            new PoolOrder
            {
                OrderIndex = 12,
                ClientOrderIndex = 0,
                MarketIndex = 8,
                Side = OrderSide.Sell,
                Price = PriceAmount.From(2m),
                RemainingBaseAmount = 10m,
                InitialBaseAmount = 10m,
                Status = "open",
                CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000),
                ExpiresAt = LighterTimestamp.FromSeconds(1_800_086_400),
            },
        ];

        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);

        using HttpClient client = app.CreateClient();
        var connection = await OpenSseAsync(client, $"{StreamPath}?lead={KnownPoolIndex}");
        using HttpResponseMessage response = connection.Response;
        using StreamReader reader = connection.Reader;

        (string eventName, string data) = await ReadEventAsync(reader);
        Assert.AreEqual("lead", eventName);

        JsonElement body = JsonDocument.Parse(data).RootElement;
        JsonElement positions = body.GetProperty("positions");

        Assert.AreEqual(1, positions.GetArrayLength());

        JsonElement position = positions[0];
        Assert.AreEqual("WLD", position.GetProperty("symbol").GetString());
        Assert.AreEqual(0.51924m, position.GetProperty("avgEntryPrice").GetDecimal());
        Assert.AreEqual(0.5m, position.GetProperty("markPrice").GetDecimal());
        Assert.AreEqual(JsonValueKind.Null, position.GetProperty("liquidationPrice").ValueKind);
        Assert.AreEqual(-19.24m, position.GetProperty("unrealizedPnl").GetDecimal());
        Assert.AreEqual(-38.48m, position.GetProperty("unrealizedPnlPercent").GetDecimal());
        Assert.AreEqual(0m, position.GetProperty("realizedPnl").GetDecimal());
        Assert.AreEqual(100m, position.GetProperty("weightPercent").GetDecimal());

        JsonElement orders = body.GetProperty("orders");
        Assert.AreEqual(1, orders.GetArrayLength(), "只带走展示市场的挂单");
        Assert.AreEqual(7, orders[0].GetProperty("marketId").GetInt32());
        Assert.AreEqual("buy", orders[0].GetProperty("side").GetString());
        Assert.AreEqual(0.49m, orders[0].GetProperty("price").GetDecimal());
        Assert.AreEqual(49m, orders[0].GetProperty("remainingNotional").GetDecimal());
        Assert.IsTrue(orders[0].GetProperty("isPartiallyFilled").GetBoolean());
    }

    [TestMethod]
    public async Task Stream_LeadConnection_Returns404_WhenLeadsPageIsOffOrTheLeadIsUnknown()
    {
        // 流本身由两页共享，但带单员详情这一段必须自己看 Leads 开关；
        // 没有观察记录的池也按不存在处理（与 REST 的公开带单列表口径一致）。
        using PublicPagesApp leadsOff = new(leads: false, followers: true);
        await SeedLeadAsync(leadsOff, KnownPoolIndex, isEnabled: true);

        using HttpClient offClient = leadsOff.CreateClient();
        HttpResponseMessage disabled = await offClient.GetAsync(
            new Uri($"{StreamPath}?lead={KnownPoolIndex}", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.NotFound, disabled.StatusCode);
        StringAssert.Contains(await disabled.Content.ReadAsStringAsync(), "公开页未启用");

        using PublicPagesApp app = new();
        using HttpClient client = app.CreateClient();
        HttpResponseMessage unknown = await client.GetAsync(
            new Uri($"{StreamPath}?lead={KnownPoolIndex}", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode, "未观察的池不该被详情流旁路暴露");
        StringAssert.Contains(await unknown.Content.ReadAsStringAsync(), "不存在");
    }

    [TestMethod]
    public async Task Stream_LeadConnection_ReceivesAnImmediateFrame_WhenThePoolChanges()
    {
        // 池事件（LeadTraderRegistry 的观察循环）→ NotifyLeadChanged → 这里立即推帧，
        // 与跟单员的落账通知同一条主流程，不靠定时器。
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);

        using HttpClient client = app.CreateClient();
        var connection = await OpenSseAsync(client, $"{StreamPath}?lead={KnownPoolIndex}");

        using (connection.Response)
        using (connection.Reader)
        {
            (string firstEvent, _) = await ReadEventAsync(connection.Reader);
            Assert.AreEqual("lead", firstEvent);

            app.Services.GetRequiredService<PublicPageFeed>().NotifyLeadChanged(KnownPoolIndex);

            (string pushedEvent, string pushedData) = await ReadEventAsync(connection.Reader);
            Assert.AreEqual("lead", pushedEvent, "池事件必须立即推一帧带单员详情");
            Assert.AreEqual(
                KnownPoolIndex,
                JsonDocument.Parse(pushedData).RootElement.GetProperty("accountIndex").GetInt64());
        }
    }

    [TestMethod]
    public async Task Stream_DetailConnection_ReceivesGone_WhenTheRowIsDeleted()
    {
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);
        long id = await SeedPublicFollowerAsync(app, ownerUserId: null, label: "会被删", visitorOwned: true);

        using HttpClient client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{VisitorCookieName}={FixedVisitorSecret}");

        var connection = await OpenSseAsync(client, $"{StreamPath}?detail={id}");
        using HttpResponseMessage response = connection.Response;
        using StreamReader reader = connection.Reader;

        (string firstEvent, _) = await ReadEventAsync(reader);
        Assert.AreEqual("detail", firstEvent);

        HttpResponseMessage deleted = await client.DeleteAsync(
            new Uri($"{FollowerPath}/{id}", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);

        (string goneEvent, string goneData) = await ReadEventAsync(reader);

        Assert.AreEqual("gone", goneEvent, "行被删除后详情流必须收到终止帧，而不是继续保活旧账");
        Assert.AreEqual(id, JsonDocument.Parse(goneData).RootElement.GetProperty("id").GetInt64());
    }

    [TestMethod]
    public async Task Stream_DetailConnection_ReturnsTheSame404AsG3_ForPrivateLiveAndMissingRows()
    {
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);

        long privateId = await SeedFollowerAsync(app, isPublic: false, mode: CopyMode.Paper, ownerUserId: 7L);
        long liveId = await SeedFollowerAsync(app, isPublic: true, mode: CopyMode.Live, ownerUserId: 7L);

        using HttpClient client = app.CreateClient();

        foreach (long id in new[] { privateId, liveId, 424_242L })
        {
            HttpResponseMessage stream = await client.GetAsync(
                new Uri($"{StreamPath}?detail={id}", UriKind.Relative));
            HttpResponseMessage direct = await client.GetAsync(
                new Uri($"{FollowerPath}/{id}", UriKind.Relative));

            Assert.AreEqual(HttpStatusCode.NotFound, stream.StatusCode, $"id={id} 不该建出详情流");

            // 文案必须与 G3 逐字一致：两条路径只要有一句不同，就能拼出探针
            Assert.AreEqual(
                await direct.Content.ReadAsStringAsync(),
                await stream.Content.ReadAsStringAsync(),
                $"id={id} 的 404 文案必须与 G3 完全相同");
        }
    }

    [TestMethod]
    public async Task Stream_ConcurrentConnections_ShareTheLiveSnapshot_AndAnEventPushesANewFrame()
    {
        CountingFeedAssembler builder = new();
        using PublicPagesApp app = new(feedAssembler: builder);
        using HttpClient client = app.CreateClient();

        (HttpResponseMessage Response, StreamReader Reader)[] connections = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(_ => OpenSseAsync(client, StreamPath)));

        try
        {
            foreach ((_, StreamReader reader) in connections)
            {
                (string eventName, _) = await ReadEventAsync(reader);
                Assert.AreEqual("snapshot", eventName);
            }

            Assert.AreEqual(
                1,
                builder.SnapshotBuilds,
                "三个并发首帧必须被单飞收成一次装配（live 快照不靠 TTL）");

            // 没有 tick：一条落账事件就是新的推帧驱动
            await app.Services.GetRequiredService<PublicPageFeed>()
                .PublishFollowerChangedAsync(123L);

            Assert.AreEqual(2, builder.SnapshotBuilds, "事件必须重装配并推送");

            (string pushedEvent, _) = await ReadEventAsync(connections[0].Reader);
            Assert.AreEqual("snapshot", pushedEvent, "事件必须把新帧推给在线连接");
        }
        finally
        {
            foreach ((HttpResponseMessage response, StreamReader reader) in connections)
            {
                reader.Dispose();
                response.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task Stream_WhenTheConnectionCapIsReached_Returns503BeforeStreaming()
    {
        // 上限降到 2：真起 50 条连接再等第 51 条在测试里既慢又脆
        using PublicPagesApp app = new(streamLimit: 2);
        using HttpClient client = app.CreateClient();

        // 这两条 reader 必须活到第三条请求之后：读一帧就 Dispose 会立刻归还名额，
        // 第三条就会以 200 建出一条永远不结束的流，测试最终死在自己的 HttpClient 超时上
        (HttpResponseMessage Response, StreamReader Reader) first = await OpenSseAsync(client, StreamPath);
        (HttpResponseMessage Response, StreamReader Reader) second = await OpenSseAsync(client, StreamPath);

        try
        {
            await ReadEventAsync(first.Reader);
            await ReadEventAsync(second.Reader);

            using HttpResponseMessage third = await client.GetAsync(new Uri(StreamPath, UriKind.Relative));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, third.StatusCode, "第三条必须在推流前被拒");
            StringAssert.Contains(await third.Content.ReadAsStringAsync(), "error");
            Assert.AreNotEqual(
                "text/event-stream",
                third.Content.Headers.ContentType?.MediaType,
                "503 不该是半截 SSE 流");
        }
        finally
        {
            first.Reader.Dispose();
            first.Response.Dispose();
            second.Reader.Dispose();
            second.Response.Dispose();
        }
    }

    [TestMethod]
    public async Task Stream_DetailFirstFrame_IsNeverServedFromAnOld15SecondCache()
    {
        // 详情首帧必须是此刻的账。曾经首帧吃掉 15 秒缓存，用户会看到"建仓前"的 0 笔，
        // 下一次 tick 才变成 6 笔——看起来就是详情不实时。
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);
        long id = await SeedPublicFollowerAsync(app, ownerUserId: null, label: "实时首帧", visitorOwned: true);

        using HttpClient client = app.CreateClient();

        var first = await OpenSseAsync(client, $"{StreamPath}?detail={id}");
        using (first.Response)
        using (first.Reader)
        {
            (string eventName, string data) = await ReadEventAsync(first.Reader);
            Assert.AreEqual("detail", eventName);
            Assert.AreEqual(0, JsonDocument.Parse(data).RootElement.GetProperty("fillCount").GetInt32());
        }

        // 第一条连接刚读过（旧实现会缓存 15 秒），此时落一笔账，再开新连接。
        await SeedLedgerRowsAsync(app, id);

        var second = await OpenSseAsync(client, $"{StreamPath}?detail={id}");
        using (second.Response)
        using (second.Reader)
        {
            (string eventName, string data) = await ReadEventAsync(second.Reader);
            Assert.AreEqual("detail", eventName);

            JsonElement frame = JsonDocument.Parse(data).RootElement;

            Assert.AreEqual(1, frame.GetProperty("fillCount").GetInt32(), "新连接的首帧必须看到刚落的账");
            Assert.AreEqual(1, frame.GetProperty("fills").GetArrayLength());
        }
    }

    [TestMethod]
    public async Task Stream_DetailConnection_PatchesMarkPriceAndUnrealizedPnl_OnMarketTick()
    {
        // market_stats 事件到达时，在线详情直接按新中间价补标记价/未实现/权益，
        // 不重读库、不重打上游；这正是"数据过来就更新"的路径。
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);
        long id = await SeedPublicFollowerAsync(app, ownerUserId: null, label: "行情事件", visitorOwned: true);
        await SeedOpenPositionAsync(app, id);

        using HttpClient client = app.CreateClient();
        var connection = await OpenSseAsync(client, $"{StreamPath}?detail={id}");

        using (connection.Response)
        using (connection.Reader)
        {
            (string firstEvent, string firstData) = await ReadEventAsync(connection.Reader);
            Assert.AreEqual("detail", firstEvent);

            JsonElement before = JsonDocument.Parse(firstData).RootElement;
            Assert.AreEqual(1, before.GetProperty("fillCount").GetInt32());

            app.Services.GetRequiredService<PublicPageFeed>()
                .NotifyMarketPriceChanged(29, PriceAmount.From(110m));

            (string changedEvent, string changedData) = await ReadEventAsync(connection.Reader);
            Assert.AreEqual("detail", changedEvent);

            JsonElement after = JsonDocument.Parse(changedData).RootElement;
            JsonElement position = after.GetProperty("positions").EnumerateArray()
                .Single(p => p.GetProperty("marketId").GetInt32() == 29);

            Assert.AreEqual(110m, position.GetProperty("markPrice").GetDecimal());
            Assert.IsTrue(position.GetProperty("hasMark").GetBoolean());
            Assert.AreEqual(5m, position.GetProperty("unrealizedPnl").GetDecimal());
            Assert.AreEqual(10m, position.GetProperty("initialMarginPercent").GetDecimal());

            // 0.5 × 110 = 55 名义价值；保证金 10% = 5.5；ROI = 5 / 5.5
            Assert.AreEqual(5.5m, position.GetProperty("marginAmount").GetDecimal());
            Assert.AreEqual(90.9090909091m, position.GetProperty("unrealizedPnlPercent").GetDecimal(), 0.0000000001m);
            Assert.AreEqual(5m, after.GetProperty("unrealizedPnl").GetDecimal());
            Assert.AreEqual(504.95m, after.GetProperty("equity").GetDecimal());
        }
    }

    [TestMethod]
    public async Task Stream_ListSnapshot_PatchesCardValuation_OnMarketTick()
    {
        // 卡片也走同一条行情补丁：摘要里补了 marketId/entryVwap/fees，才能重算
        // 持仓未实现盈亏与账户权益，而不必重装配整页。
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);
        long id = await SeedPublicFollowerAsync(app, ownerUserId: null, label: "卡片行情", visitorOwned: true);
        await SeedOpenPositionAsync(app, id);

        using HttpClient client = app.CreateClient();
        var connection = await OpenSseAsync(client, StreamPath);

        using (connection.Response)
        using (connection.Reader)
        {
            (string firstEvent, string firstData) = await ReadEventAsync(connection.Reader);
            Assert.AreEqual("snapshot", firstEvent);

            JsonElement firstFollower = JsonDocument.Parse(firstData).RootElement
                .GetProperty("followers").EnumerateArray()
                .Single(f => f.GetProperty("id").GetInt64() == id);

            Assert.AreEqual(499.95m, firstFollower.GetProperty("equity").GetDecimal());

            app.Services.GetRequiredService<PublicPageFeed>()
                .NotifyMarketPriceChanged(29, PriceAmount.From(110m));

            (string changedEvent, string changedData) = await ReadEventAsync(connection.Reader);
            Assert.AreEqual("snapshot", changedEvent);

            JsonElement follower = JsonDocument.Parse(changedData).RootElement
                .GetProperty("followers").EnumerateArray()
                .Single(f => f.GetProperty("id").GetInt64() == id);

            JsonElement position = follower.GetProperty("positions").EnumerateArray()
                .Single(p => p.GetProperty("marketId").GetInt32() == 29);

            Assert.AreEqual(5m, position.GetProperty("unrealizedPnl").GetDecimal());
            Assert.AreEqual(10m, position.GetProperty("initialMarginPercent").GetDecimal(),
                "卡片杠杆依赖的保证金率必须随行情补丁保留");
            Assert.AreEqual(5m, follower.GetProperty("unrealizedPnl").GetDecimal());
            Assert.AreEqual(504.95m, follower.GetProperty("equity").GetDecimal());
        }
    }

    [TestMethod]
    public async Task Stream_DetailConnection_ReceivesAnImmediateFrame_WhenTheLedgerChanges()
    {
        // 复制管线落账后走 NotifyFollowerChanged → 这里立即推帧，不再等 15 秒 tick。
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);
        long id = await SeedPublicFollowerAsync(app, ownerUserId: null, label: "落账即时帧", visitorOwned: true);

        using HttpClient client = app.CreateClient();
        var connection = await OpenSseAsync(client, $"{StreamPath}?detail={id}");

        using (connection.Response)
        using (connection.Reader)
        {
            (string firstEvent, string firstData) = await ReadEventAsync(connection.Reader);
            Assert.AreEqual("detail", firstEvent);
            Assert.AreEqual(0, JsonDocument.Parse(firstData).RootElement.GetProperty("fillCount").GetInt32());

            await SeedLedgerRowsAsync(app, id);

            // 模拟 PaperFollower 落账后的通知（生产里由 IFollowerLedgerActivity 调它）
            await app.Services.GetRequiredService<PublicPageFeed>()
                .PublishFollowerChangedAsync(id);

            (string changedEvent, string changedData) = await ReadEventAsync(connection.Reader);
            Assert.AreEqual("detail", changedEvent, "落账通知必须立即推一帧详情");
            Assert.AreEqual(1, JsonDocument.Parse(changedData).RootElement.GetProperty("fillCount").GetInt32());
        }
    }

    [TestMethod]
    public async Task Stream_OnlyTheEnabledPageSurvivesInTheSnapshot_WhenOneSwitchIsOff()
    {
        // 两片各有自己的开关。流是共享的一条，但输出必须按开关裁剪：
        // 关掉跟单页时，快照里的 followers 必须是空数组，详情流也不得给出那一行。
        using PublicPagesApp app = new(leads: true, followers: false);
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);
        long publicId = await SeedPublicFollowerAsync(app, ownerUserId: null, label: "不该被看到", visitorOwned: true);

        using HttpClient client = app.CreateClient();
        var connection = await OpenSseAsync(client, StreamPath);
        using HttpResponseMessage response = connection.Response;
        using StreamReader reader = connection.Reader;

        (string eventName, string data) = await ReadEventAsync(reader);
        Assert.AreEqual("snapshot", eventName);

        JsonElement frame = JsonDocument.Parse(data).RootElement;

        Assert.AreEqual(1, frame.GetProperty("leads").GetArrayLength(), "带单页是开的，leads 必须照常输出");
        Assert.AreEqual(0, frame.GetProperty("followers").GetArrayLength(), "跟单页关着，followers 必须是空数组");

        using HttpResponseMessage detail = await client.GetAsync(
            new Uri($"{StreamPath}?detail={publicId}", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.NotFound, detail.StatusCode, "跟单页关着时，详情流不能成为绕过开关的第二条路");
    }

    [TestMethod]
    public async Task Stream_LeadsSwitchOff_KeepsTheFollowerHalfButDropsLeads()
    {
        // 反向组合：只开跟单页。此时流仍然是开的（跟单页要用），但 leads 必须是空数组；
        // 带单页的 REST 端点则会各自 404，不需要在这里重复断言。
        using PublicPagesApp app = new(leads: false, followers: true);
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);
        await SeedPublicFollowerAsync(app, ownerUserId: null, label: "跟单页的行", visitorOwned: true);

        using HttpClient client = app.CreateClient();
        var connection = await OpenSseAsync(client, StreamPath);
        using HttpResponseMessage response = connection.Response;
        using StreamReader reader = connection.Reader;

        (string eventName, string data) = await ReadEventAsync(reader);
        Assert.AreEqual("snapshot", eventName);

        JsonElement frame = JsonDocument.Parse(data).RootElement;

        Assert.AreEqual(0, frame.GetProperty("leads").GetArrayLength(), "带单页关着，leads 必须是空数组");
        Assert.AreEqual(1, frame.GetProperty("followers").GetArrayLength(), "跟单页是开的，followers 必须照常输出");
    }

    [TestMethod]
    public async Task PublicWrites_RequestTheRuntimeSyncInTheBackground_WithoutBlockingTheResponse()
    {
        // W1（新建观察）→ W2（建纸面跟单）→ W3（删掉）：三次成功写都要通知运行期，
        // 但都**不能 await** 它——测试把同步挂起，三次响应仍然必须立刻回来。
        // 只删库不通知会让已删行在心跳前继续落账，这是反方向的错误。
        using PublicPagesApp app = new();
        app.Sync.Block = true;

        using HttpClient client = app.CreateClient();

        try
        {
            HttpResponseMessage added = await client.PostAsync(
                new Uri($"{LeadPath}/{KnownPoolIndex}", UriKind.Relative),
                content: null);
            Assert.AreEqual(HttpStatusCode.OK, added.StatusCode);

            PublicFollowerDto created = (await CreateReader(await CreateAsync(client, label: "后台同步")))!;

            HttpResponseMessage deleted = await client.DeleteAsync(
                new Uri($"{FollowerPath}/{created.Id}", UriKind.Relative));
            Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);

            for (int i = 0; i < 100 && app.Sync.Requests < 3; i++)
            {
                await Task.Delay(20);
            }

            Assert.AreEqual(3, app.Sync.Requests, "三次公开写都应在后台发起一次运行期同步");
        }
        finally
        {
            app.Sync.Release();
        }
    }

    [TestMethod]
    public async Task Stream_WriteSuccess_PushesTheNewCardToAnOpenListConnection()
    {
        // 页面不再 load()：建完必须由流把新卡片送上来，否则刚建的条目要等下一次 tick
        using PublicPagesApp app = new();
        await SeedLeadAsync(app, KnownPoolIndex, isEnabled: true);

        using HttpClient client = app.CreateClient();
        var connection = await OpenSseAsync(client, StreamPath);
        using HttpResponseMessage response = connection.Response;
        using StreamReader reader = connection.Reader;

        (string firstEvent, string firstData) = await ReadEventAsync(reader);
        Assert.AreEqual("snapshot", firstEvent);
        Assert.AreEqual(0, JsonDocument.Parse(firstData).RootElement.GetProperty("followers").GetArrayLength());

        PublicFollowerDto created = (await CreateReader(await CreateAsync(client, label: "推上来的")))!;

        (string pushedEvent, string pushedData) = await ReadEventAsync(reader);
        Assert.AreEqual("snapshot", pushedEvent, "写成功必须立即推一帧，而不是等下一次 tick");

        JsonElement frame = JsonDocument.Parse(pushedData).RootElement;
        Assert.AreEqual(1, frame.GetProperty("followers").GetArrayLength());

        JsonElement row = frame.GetProperty("followers")[0];
        Assert.AreEqual(created.Id, row.GetProperty("id").GetInt64());
        Assert.AreEqual("推上来的", row.GetProperty("label").GetString());
    }

    // ── 帮手：发请求 ──────────────────────────────────────

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string? label) =>
        client.PostAsJsonAsync(
            new Uri(FollowerPath, UriKind.Relative),
            new { leadAccountIndex = KnownPoolIndex, initialCapital = 500m, label });

    private static async Task<PublicFollowerDto?> CreateReader(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());

        return await response.Content.ReadFromJsonAsync<PublicFollowerDto>();
    }

    // ── 帮手：Cookie ──────────────────────────────────────

    /// <summary>取响应里那条指定名字的 Set-Cookie（没有时返回 null）。</summary>
    private static string? GetSetCookie(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return null;
        }

        return values.FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal));
    }

    private static string ExtractCookieValue(string setCookieHeader)
    {
        string pair = setCookieHeader.Split(';')[0];
        int separator = pair.IndexOf('=');

        return pair[(separator + 1)..];
    }

    // ── 帮手：SSE ─────────────────────────────────────────

    private static void AssertSseHeaders(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("no-cache", string.Join(",", response.Headers.GetValues("Cache-Control")));
        Assert.AreEqual("no", string.Join(",", response.Headers.GetValues("X-Accel-Buffering")));
    }

    /// <summary>
    /// 打开一条 SSE 连接并返回**仍然活着**的 reader 与 response。
    ///
    /// 连接必须由调用方用 `using` 持有到断言结束：读一帧就关流会让服务端的连接名额立刻归还，
    /// 容量上限与"tick 是否重装配"这两类断言就会变成空断言（本文件曾经踩过）。
    /// </summary>
    private static async Task<(HttpResponseMessage Response, StreamReader Reader)> OpenSseAsync(
        HttpClient client,
        string path)
    {
        HttpResponseMessage response = await client.GetAsync(
            new Uri(path, UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead);

        AssertSseHeaders(response);

        return (response, new StreamReader(await response.Content.ReadAsStreamAsync()));
    }

    /// <summary>从一条仍然活着的流读到下一个带事件名的帧（忽略 <c>: ping</c> 注释行）。</summary>
    private static async Task<(string Event, string Data)> ReadEventAsync(StreamReader reader)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        string? eventName = null;
        List<string> data = [];

        while (true)
        {
            string? line = await reader.ReadLineAsync(timeout.Token);

            if (line is null)
            {
                Assert.Fail("流在下一帧之前就结束了");
                return default;
            }

            if (line.Length == 0)
            {
                if (eventName is not null)
                {
                    return (eventName, string.Join("\n", data));
                }

                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Add(line["data:".Length..].Trim());
            }
        }
    }

    // ── 帮手：JSON 键名 ───────────────────────────────────

    private static async Task<JsonElement> FirstRowAsync(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.AreEqual(JsonValueKind.Array, body.ValueKind, "G1/G2/G4 的响应体都是数组");
        Assert.IsTrue(body.GetArrayLength() > 0, "至少要有一行可看，否则键名断言是空的");

        return body[0];
    }

    private static JsonElement FirstRow(HttpResponseMessage response) => FirstRowAsync(response).GetAwaiter().GetResult();

    private static void AssertKeys(JsonElement element, string[] expected)
    {
        string[] actual = [.. element.EnumerateObject().Select(property => property.Name)];

        CollectionAssert.AreEquivalent(expected, actual, "公开契约的键名必须与前端拿到的那份文本逐字一致");
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value) =>
        element.TryGetProperty(name, out value);

    // ── 帮手：种库 ────────────────────────────────────────

    private static async Task SeedLeadAsync(PublicPagesApp app, long accountIndex, bool isEnabled)
    {
        app.Start();

        await using PoolPilotDbContext db = app.OpenDatabase();

        db.LeadTraders.Add(new LeadTraderRecord
        {
            AccountIndex = accountIndex,
            Name = "ENA 池",
            IsEnabled = isEnabled,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 种一行跟单员。
    /// <paramref name="visitorOwned"/> 为真时按口径把创建者凭证现派生一遍——
    /// 于是测试里的"访客"也可以用同一枚密钥把这一行认下来。
    /// </summary>
    private static async Task<long> SeedPublicFollowerAsync(
        PublicPagesApp app,
        long? ownerUserId,
        string? label,
        bool visitorOwned)
    {
        long id = await SeedFollowerAsync(app, isPublic: true, mode: CopyMode.Paper, ownerUserId: ownerUserId, label: label);

        if (!visitorOwned)
        {
            return id;
        }

        await using PoolPilotDbContext db = app.OpenDatabase();

        FollowerRecord row = await db.Followers.SingleAsync(f => f.Id == id);
        row.OwnerCredentialHash = PublicFollowerOwnership.DeriveCredential(FixedVisitorSecret, id);
        await db.SaveChangesAsync();

        return id;
    }

    private const string FixedVisitorSecret =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>
    /// 给某个跟单员种一条流水与一个权益点。
    /// 只为 W3 服务：删除要「连带清账」，而孤儿行只有先存在才谈得上被清掉。
    /// </summary>
    private static async Task SeedLedgerRowsAsync(PublicPagesApp app, long followerId)
    {
        app.Start();

        await using PoolPilotDbContext db = app.OpenDatabase();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        db.FollowerFills.Add(new FollowerFillRecord
        {
            FollowerId = followerId,
            MarketId = 29,
            Symbol = "ENA",
            Side = "Buy",
            Size = 0.5m,
            Price = 100m,
            Notional = 50m,
            Fee = 0.1m,
            ObservedAtUtc = now,
        });

        db.FollowerEquityPoints.Add(new FollowerEquityPointRecord
        {
            FollowerId = followerId,
            Equity = 500m,
            TakenAtUtc = now,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>种一笔开仓成交，让详情里有一个带入场价的持仓（行情事件补丁测试用）。</summary>
    private static async Task SeedOpenPositionAsync(PublicPagesApp app, long followerId)
    {
        app.Start();

        await using PoolPilotDbContext db = app.OpenDatabase();

        db.FollowerFills.Add(new FollowerFillRecord
        {
            FollowerId = followerId,
            MarketId = 29,
            Symbol = "ENA",
            Side = "Buy",
            PoolPositionAfter = 0.5m,
            PoolEquity = 1_000m,
            TargetPosition = 0.5m,
            PositionBefore = 0m,
            PositionAfter = 0.5m,
            Size = 0.5m,
            Price = 100m,
            Notional = 50m,
            DepthSufficient = true,
            Fee = 0.05m,
            InitialMarginPercent = 10m,
            ObservedAtUtc = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    private static async Task<long> SeedFollowerAsync(
        PublicPagesApp app,
        bool isPublic,
        CopyMode mode,
        long? ownerUserId,
        string? label = null)
    {
        app.Start();

        await using PoolPilotDbContext db = app.OpenDatabase();

        FollowerRecord row = new()
        {
            LeadAccountIndex = PublicPageUpstream.KnownPool.Value,
            InitialCapital = 500m,
            Mode = mode.ToString(),
            ExchangeConfigId = mode == CopyMode.Live ? 3L : null,
            IsActive = true,
            IsPublic = isPublic,
            OwnerUserId = ownerUserId,
            Label = label,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        db.Followers.Add(row);
        await db.SaveChangesAsync();

        return row.Id;
    }

    // ── 工装 ──────────────────────────────────────────────

    /// <summary>
    /// 开着两片开关的公开页工装。
    ///
    /// 换掉三样：上游池数据、盯市价、运行期同步（理由见 <see cref="PublicPageUpstream"/>）。
    /// 数据库、认证、权限、异常映射、限流、Cookie 全是真的——这组用例测的就是接线。
    ///
    /// SSE 另有两个旋钮：<paramref name="streamLimit"/> 把并发连接上限降下来（测 503），
    /// <paramref name="feedAssembler"/> 换成只计数的装配替身（测单飞，不数上游 fake）。
    /// </summary>
    internal sealed class PublicPagesApp(
        bool leads = true,
        bool followers = true,
        int? streamLimit = null,
        IPublicPageFeedAssembler? feedAssembler = null) : AuthTestApp
    {
        internal PublicPageUpstream Upstream { get; } = new();

        internal PublicPageSync Sync { get; } = new();

        /// <summary>
        /// 立刻把宿主拉起来。播种用的是 <see cref="AuthTestApp.OpenDatabase"/> 直连临时库文件，
        /// 而**建表发生在 Program 的启动那一趟**里——不先把宿主叫起来，种子插的就是一张不存在的表。
        /// </summary>
        internal void Start() => _ = Services;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            base.ConfigureWebHost(builder);

            // 开关状态一律由构造参数钉住（两条都显式 UseSetting）：环境里可能有一份
            // appsettings.Development.json 把公开页打开过，靠"默认值"写断言的用例会在别人机器上变绿/变红
            builder.UseSetting("PublicPages:Leads", leads.ToString().ToLowerInvariant());
            builder.UseSetting("PublicPages:Followers", followers.ToString().ToLowerInvariant());

            if (streamLimit is int cap)
            {
                builder.UseSetting(
                    "PublicPages:Stream:MaxConnections",
                    cap.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.RemoveAll<ILighterPoolsClient>();
            services.AddSingleton<ILighterPoolsClient>(Upstream);

            services.RemoveAll<IMarkPrices>();
            services.AddSingleton<IMarkPrices>(new PublicPageMarks());

            // 真实现会为新建的带单员订频道、取 REST 基线——那是网络，不是这组用例要测的东西
            services.RemoveAll<ILeadTraderSync>();
            services.AddSingleton<ILeadTraderSync>(Sync);

            // 单飞/并发的断言只关心"装配发生了几次"，换掉整个装配器比数上游 fake 更直接
            if (feedAssembler is not null)
            {
                services.RemoveAll<IPublicPageFeedAssembler>();
                services.AddSingleton(feedAssembler);
            }
        }
    }
}

/// <summary>只数装配次数、不碰库的 feed 装配替身。</summary>
internal sealed class CountingFeedAssembler : IPublicPageFeedAssembler
{
    public int SnapshotBuilds { get; private set; }

    public int DetailBuilds { get; private set; }

    public int LeadDetailBuilds { get; private set; }

    public Task<PublicPageFeedSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken = default)
    {
        SnapshotBuilds++;

        return Task.FromResult(new PublicPageFeedSnapshot([], [], DateTimeOffset.UtcNow));
    }

    public Task<PublicFollowerDto?> BuildDetailAsync(
        long followerId,
        CancellationToken cancellationToken = default)
    {
        DetailBuilds++;

        return Task.FromResult<PublicFollowerDto?>(null);
    }

    public Task<PublicLeadTraderDetailDto?> BuildLeadDetailAsync(
        long accountIndex,
        CancellationToken cancellationToken = default)
    {
        LeadDetailBuilds++;

        return Task.FromResult<PublicLeadTraderDetailDto?>(null);
    }
}
