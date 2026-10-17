using System.Net;
using System.Net.Http.Json;

using PoolPilot.Api.Tests.Fakes;
using PoolPilot.Application.Dtos;
using PoolPilot.Infrastructure.Persistence;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 工作台私有数据的归属隔离，跑在真实管线里。
///
/// 锁住三件事：
/// <list type="bullet">
///   <item>普通账号的跟单列表只含自己的**私有**行；公开页的行不进入工作台。</item>
///   <item>知道别人的跟单 Id 也读不到、改不了、删不掉；superadmin 保留跨用户视图。</item>
///   <item>交易所配置同理：列表按归属过滤，按 Id 的读改删在归属不符时与不存在同答。</item>
/// </list>
/// 上游（池数据、盯市价、运行期同步）由 <see cref="PublicPagesApiTests.PublicPagesApp"/> 换成替身，
/// 这一组测的是接线与归属，不是网络。
/// </summary>
[TestClass]
public sealed class DataIsolationApiTests
{
    private const string OwnerEmail = "owner@example.com";
    private const string AliceEmail = "alice@example.com";
    private const string BobEmail = "bob@example.com";
    private const string Password = "correct horse";
    private const string ViewerRole = "viewer";

    [TestMethod]
    public async Task Followers_AreVisibleOnlyToTheirOwner_ExceptForSuperadmin()
    {
        using PublicPagesApiTests.PublicPagesApp app = new();
        app.Start();

        using HttpClient owner = await TestSignIn.Async(app, OwnerEmail, Password);
        using HttpClient alice = await TestSignIn.Async(app, AliceEmail, Password);
        using HttpClient bob = await TestSignIn.Async(app, BobEmail, Password);

        await GrantViewerAsync(owner, AliceEmail, BobEmail);

        (long aliceFollowerId, long bobFollowerId, long publicFollowerId, long legacyFollowerId) =
            SeedFollowers(app);

        List<FollowerSummaryDto> aliceList = await GetFollowersAsync(alice);
        Assert.HasCount(1, aliceList, "公开行与别人的私有行都不该出现在工作台列表");
        Assert.AreEqual(aliceFollowerId, aliceList[0].Id);

        List<FollowerSummaryDto> bobList = await GetFollowersAsync(bob);
        Assert.AreEqual(bobFollowerId, Assert.ContainsSingle(bobList).Id);

        List<FollowerSummaryDto> ownerList = await GetFollowersAsync(owner);
        CollectionAssert.AreEquivalent(
            new List<long> { aliceFollowerId, bobFollowerId, legacyFollowerId },
            ownerList.Select(f => f.Id).ToList(),
            "superadmin 跨用户看全部私有行，但公开行仍留给公开广场");

        // 读详情：自己的可以，别人的与公开行一律 404（不暴露存在性）
        Assert.AreEqual(HttpStatusCode.OK, (await alice.GetAsync(FollowerUri(aliceFollowerId))).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await bob.GetAsync(FollowerUri(aliceFollowerId))).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await alice.GetAsync(FollowerUri(publicFollowerId))).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await bob.GetAsync(FollowerUri(legacyFollowerId))).StatusCode);

        // 写：跨用户修改/删除同样 404
        HttpResponseMessage foreignUpdate = await bob.PutAsJsonAsync(
            FollowerUri(aliceFollowerId),
            new UpdateFollowerRequest { InitialCapital = 900m, IsActive = false });
        Assert.AreEqual(HttpStatusCode.NotFound, foreignUpdate.StatusCode);

        HttpResponseMessage foreignDelete = await bob.DeleteAsync(FollowerUri(aliceFollowerId));
        Assert.AreEqual(HttpStatusCode.NotFound, foreignDelete.StatusCode);

        // 共享带单员：普通账号即使有 leads 的 edit/delete，也不能暂停或停止
        Uri leadUri = LeadUri();
        HttpResponseMessage pause = await alice.PutAsJsonAsync(
            leadUri,
            new ConfigureLeadTraderRequest { IsEnabled = false });
        Assert.AreEqual(HttpStatusCode.BadRequest, pause.StatusCode);
        StringAssert.Contains(await pause.Content.ReadAsStringAsync(), "管理员");

        HttpResponseMessage stop = await alice.DeleteAsync(leadUri);
        Assert.AreEqual(HttpStatusCode.BadRequest, stop.StatusCode);
        StringAssert.Contains(await stop.Content.ReadAsStringAsync(), "管理员");

        // 拥有者自己的写口仍然可用
        HttpResponseMessage ownDelete = await alice.DeleteAsync(FollowerUri(aliceFollowerId));
        Assert.AreEqual(HttpStatusCode.NoContent, ownDelete.StatusCode);
    }

    [TestMethod]
    public async Task ExchangeConfigs_AreVisibleAndWritableOnlyToTheirOwner_ExceptForSuperadmin()
    {
        using PublicPagesApiTests.PublicPagesApp app = new();
        app.Start();

        using HttpClient owner = await TestSignIn.Async(app, OwnerEmail, Password);
        using HttpClient alice = await TestSignIn.Async(app, AliceEmail, Password);
        using HttpClient bob = await TestSignIn.Async(app, BobEmail, Password);

        await GrantViewerAsync(owner, AliceEmail, BobEmail);

        HttpResponseMessage created = await alice.PostAsJsonAsync(
            new Uri("/api/exchanges", UriKind.Relative),
            new CreateExchangeRequest
            {
                Code = "binance",
                Kind = "cex",
                DisplayName = "Alice 主账号",
                MakerFeePercent = 0.02m,
                TakerFeePercent = 0.05m,
            });

        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        ExchangeConfigDto config = (await created.Content.ReadFromJsonAsync<ExchangeConfigDto>())!;

        Assert.AreEqual(1, (await GetExchangesAsync(alice)).Count);
        Assert.IsEmpty(await GetExchangesAsync(bob), "别人的交易所配置不该出现在列表里");
        Assert.AreEqual(1, (await GetExchangesAsync(owner)).Count, "superadmin 保留全局视图");

        Uri configUri = new($"/api/exchanges/{config.Id}", UriKind.Relative);

        HttpResponseMessage foreignUpdate = await bob.PutAsJsonAsync(
            configUri,
            new UpdateExchangeRequest
            {
                DisplayName = "改名",
                IsEnabled = true,
                MakerFeePercent = 0m,
                TakerFeePercent = 0m,
            });
        Assert.AreEqual(HttpStatusCode.NotFound, foreignUpdate.StatusCode);

        Assert.AreEqual(HttpStatusCode.NotFound, (await bob.DeleteAsync(configUri)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await alice.DeleteAsync(configUri)).StatusCode);
    }

    // ── 工装 ──────────────────────────────────────────────

    private static Uri FollowerUri(long id) => new($"/api/followers/{id}", UriKind.Relative);

    private static Uri LeadUri() => new(
        $"/api/lead-traders/{PublicPageUpstream.KnownPool.Value}",
        UriKind.Relative);

    private static async Task<List<FollowerSummaryDto>> GetFollowersAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<FollowerSummaryDto>>(
            new Uri("/api/followers", UriKind.Relative)))!;

    private static async Task<List<ExchangeConfigDto>> GetExchangesAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<ExchangeConfigDto>>(
            new Uri("/api/exchanges", UriKind.Relative)))!;

    /// <summary>
    /// 给两个普通账号一个只含「跟单管理」与「交易所」的角色。
    /// 菜单授权的动作集合来自菜单本身（view/edit/delete），所以列表与写口都够用。
    /// </summary>
    private static async Task GrantViewerAsync(
        HttpClient owner,
        string aliceEmail,
        string bobEmail)
    {
        HttpResponseMessage role = await owner.PostAsJsonAsync(
            new Uri("/api/roles", UriKind.Relative),
            new CreateRoleRequestDto { Code = ViewerRole, Title = "查看者" });

        Assert.AreEqual(HttpStatusCode.OK, role.StatusCode);

        HttpResponseMessage menus = await owner.PutAsJsonAsync(
            new Uri($"/api/admin/roles/{ViewerRole}/menus", UriKind.Relative),
            new RoleMenuCodesDto { MenuCodes = ["copy-list", "exchanges", "leads"] });

        Assert.AreEqual(HttpStatusCode.NoContent, menus.StatusCode);

        List<AdminUserDto> users = (await owner.GetFromJsonAsync<List<AdminUserDto>>(
            new Uri("/api/admin/users", UriKind.Relative)))!;

        foreach (string email in new[] { aliceEmail, bobEmail })
        {
            long userId = users.Single(user => user.Email == email).Id;

            HttpResponseMessage assigned = await owner.PutAsJsonAsync(
                new Uri($"/api/admin/users/{userId}/roles", UriKind.Relative),
                new UserRolesDto { RoleCodes = [ViewerRole] });

            Assert.AreEqual(HttpStatusCode.NoContent, assigned.StatusCode);
        }
    }

    private static (long Alice, long Bob, long Public, long Legacy) SeedFollowers(
        PublicPagesApiTests.PublicPagesApp app)
    {
        using PoolPilotDbContext db = app.OpenDatabase();

        long pool = PublicPageUpstream.KnownPool.Value;

        long aliceId = db.Users.Single(user => user.Email == AliceEmail).Id;
        long bobId = db.Users.Single(user => user.Email == BobEmail).Id;

        db.LeadTraders.Add(new LeadTraderRecord
        {
            AccountIndex = pool,
            Name = "隔离测试池",
            IsEnabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        FollowerRecord alice = NewFollower(pool, ownerUserId: aliceId);
        FollowerRecord bob = NewFollower(pool, ownerUserId: bobId);
        FollowerRecord publicRow = NewFollower(pool, ownerUserId: aliceId, isPublic: true);
        FollowerRecord legacy = NewFollower(pool, ownerUserId: null);

        db.Followers.AddRange(alice, bob, publicRow, legacy);
        db.SaveChanges();

        return (alice.Id, bob.Id, publicRow.Id, legacy.Id);
    }

    private static FollowerRecord NewFollower(long pool, long? ownerUserId, bool isPublic = false) => new()
    {
        LeadAccountIndex = pool,
        InitialCapital = 500m,
        Mode = "Paper",
        IsActive = true,
        IsPublic = isPublic,
        OwnerUserId = ownerUserId,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };
}
