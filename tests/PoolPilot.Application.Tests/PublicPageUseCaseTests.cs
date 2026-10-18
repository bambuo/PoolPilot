using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 公开面的用例：建行时的归属形状、数量上限、字段口径，与读侧的两道筛子。
///
/// 这一组用例锁的是**「匿名面上能发生什么」**：
/// <list type="number">
///   <item>建不出实盘、建不出没归属的行，也建不出无限的行。</item>
///   <item>公开页读不到工作台的行，也读不到别人的实盘账。</item>
///   <item>删只有创建者删得掉，其余一律 403/404。</item>
/// </list>
/// 这些判定全在应用层，与 Cookie 无关（Cookie 是表现层的事），所以普通用例就能把整套规则
/// 跑完——这正是 <see cref="PublicCaller"/> 这个入参值类型存在的原因。
/// </summary>
[TestClass]
public sealed class PublicPageUseCaseTests
{
    private static readonly AccountIndex Pool = AccountIndex.From(281474976552443L);

    private const string VisitorSecret =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string OtherVisitorSecret =
        "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    /// <summary>今天（UTC）。当日新增的计数以它为准，所以测试里要有一个钉住的「现在」。</summary>
    private static readonly DateTimeOffset Today = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly FakeClock Clock = new(Today);

    private readonly FakeSync sync = new();

    // ── W2：建行时的归属形状 ──────────────────────────────

    [TestMethod]
    public async Task CreatePublic_Anonymous_WritesPublicPaperRowOwnedByDerivedCredential()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        await Create(followers, leads, Request(), Caller(userId: null, VisitorSecret));

        Follower stored = followers.Last!;
        Assert.IsTrue(stored.IsPublic, "公开页建的行要能被公开页读到");
        Assert.AreEqual(CopyMode.Paper, stored.Mode);
        Assert.IsNull(stored.ExchangeConfigId, "公开面没有实盘，交易所配置一律留空");
        Assert.IsNull(stored.OwnerUserId, "匿名行的创建者不落用户 Id");
        Assert.IsTrue(
            PublicFollowerOwnership.IsCreator(stored, userId: null, VisitorSecret),
            "存的那一列必须是本次访客密钥按本行 Id 派生的值——否则创建者删不掉自己刚建的行");
        Assert.AreEqual(0, sync.Requests, "公开写不再等运行期全量同步；实时帧由控制器推");
    }

    [TestMethod]
    public async Task CreatePublic_SignedIn_OwnsTheRowByUserIdAndKeepsTheSecretOutOfIt()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        await Create(followers, leads, Request(), Caller(userId: 7L, VisitorSecret));

        Follower stored = followers.Last!;
        Assert.AreEqual(7L, stored.OwnerUserId, "登录建的行归 user 列");
        Assert.IsNull(stored.OwnerCredentialHash, "两个 owner 列只写一个：都写上就会引出「哪个优先」这种没人答得上的问题");
        Assert.IsTrue(PublicFollowerOwnership.IsCreator(stored, 7L, visitorSecret: null));
        Assert.IsFalse(PublicFollowerOwnership.IsCreator(stored, userId: null, VisitorSecret));
    }

    [TestMethod]
    public async Task CreatePublic_ResponseCarriesOwnershipButNoValuationYet()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        PublicFollowerDto dto = await Create(followers, leads, Request(label: "我的第一单"), Caller(null, VisitorSecret));

        Assert.AreEqual("我的第一单", dto.Label);
        Assert.IsTrue(dto.IsOwnedByCaller, "响应要让前端立刻知道这是自己的（删除按钮与列表上的判据同源）");
        Assert.AreEqual("paper", dto.Mode);
        Assert.IsFalse(dto.IsValuationAvailable, "写入后的即时快照没算过账，这里不能把 InitialCapital 当成结果渲染");
        Assert.AreEqual(0, dto.Fills.Count);
    }

    [TestMethod]
    public async Task CreatePrivate_SetsOwnerAndLeavesPublicColumnsEmpty()
    {
        // 工作台私有的行：is_public=false，owner 记当前用户；公开面的凭证与标签保持 null。
        // 公开页建的行是另一套形状（is_public=true + 两种 owner 之一），两条路不能混淆。
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        CreateFollowerUseCase privateCreate = new(
            followers, leads, new FakeExchanges(), new FakeBrokerFactory(), new LiveTradingLimits(), new FakeSync(), Scope());

        await privateCreate.ExecuteAsync(
            new CreateFollowerRequest { LeadAccountIndex = Pool.Value, InitialCapital = 500m },
            CancellationToken.None);

        Follower stored = followers.Last!;
        Assert.IsFalse(stored.IsPublic);
        Assert.AreEqual(1L, stored.OwnerUserId, "工作台私有行必须记住创建者");
        Assert.IsNull(stored.OwnerCredentialHash, "访客凭证只属于公开面");
        Assert.IsNull(stored.Label, "公开页标签只属于公开面");
    }

    // ── W2：字段口径 ──────────────────────────────────────

    [TestMethod]
    public async Task CreatePublic_LiveMode_IsRejectedAndNotSilentlyDowngraded()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Create(followers, leads, Request(mode: "live"), Caller(null, VisitorSecret)));

        Assert.Contains("纸面", error.Message, "要说清公开页只有纸面，而不是只说一句「不支持」");
        Assert.AreEqual(0, followers.Created, "**不许静默降级成 paper**：以为在开实盘、拿到的是一份模拟账，比建不出来危险");
    }

    [TestMethod]
    public async Task CreatePublic_UnknownMode_IsRejected()
    {
        FakeFollowers followers = new();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Create(followers, SeededLeads(), Request(mode: "deleveraged"), Caller(null, VisitorSecret)));

        Assert.AreEqual(0, followers.Created);
    }

    [TestMethod]
    public async Task CreatePublic_WithExchangeConfig_IsRejected()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Create(followers, leads, Request(exchangeConfigId: 12L), Caller(null, VisitorSecret)));

        Assert.Contains("交易所", error.Message);
        Assert.AreEqual(0, followers.Created);
    }

    [TestMethod]
    public async Task CreatePublic_CapitalOutsideThePublicBand_IsRejected()
    {
        foreach (decimal capital in new[] { 0m, 9.99m, PublicPageLimits.MaxPublicCapital + 0.01m, 1_000_000m })
        {
            FakeFollowers followers = new();
            FakeLeads leads = SeededLeads();

            _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                Create(followers, leads, Request(capital: capital), Caller(null, VisitorSecret)));

            Assert.AreEqual(0, followers.Created, $"本金 {capital} 不该建得出来");
        }
    }

    [TestMethod]
    public async Task CreatePublic_CapitalOnTheBandEdges_IsAccepted()
    {
        foreach (decimal capital in new[] { PublicPageLimits.MinPublicCapital, PublicPageLimits.MaxPublicCapital })
        {
            FakeFollowers followers = new();
            FakeLeads leads = SeededLeads();

            PublicFollowerDto dto = await Create(followers, leads, Request(capital: capital), Caller(null, VisitorSecret));

            Assert.AreEqual(capital, dto.InitialCapital, "区间的两端都是闭区间：卡在边界上不该被拒");
        }
    }

    [TestMethod]
    public async Task CreatePublic_LabelIsTrimmedStrippedOfControlCharacters_EmptyBecomesNull()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        await Create(followers, leads, Request(label: "  我的第一单  "), Caller(null, VisitorSecret));
        Assert.AreEqual("我的第一单", followers.Last!.Label, "trim 之后落库，别把空白存成名字的一部分");

        await Create(followers, leads, Request(label: "a\tb\u0000c"), Caller(null, VisitorSecret));
        Assert.AreEqual("abc", followers.Last!.Label, "控制字符要剥掉：它们会打断界面上一行的排版");

        await Create(followers, leads, Request(label: "   "), Caller(null, VisitorSecret));
        Assert.IsNull(followers.Last!.Label, "空串按「没起名」处理，而不是存一条看不见的空白");
    }

    [TestMethod]
    public async Task CreatePublic_LabelOverTheLimit_IsRejectedAndNotStored()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Create(followers, leads, Request(label: new string('x', PublicPageLimits.LabelMaxLength + 1)), Caller(null, VisitorSecret)));

        Assert.Contains(PublicPageLimits.LabelMaxLength.ToString(), error.Message, "要说清能写多少个字符");
        Assert.AreEqual(0, followers.Created);

        // 正好卡在上限上要能过——否则「最多 24 个字符」这句话其实只实现了 23
        await Create(followers, leads, Request(label: new string('x', PublicPageLimits.LabelMaxLength)), Caller(null, VisitorSecret));
        Assert.AreEqual(PublicPageLimits.LabelMaxLength, followers.Last!.Label!.Length);
    }

    // ── W2：数量上限 ──────────────────────────────────────

    [TestMethod]
    public async Task CreatePublic_WhenThisVisitorAlreadyHasTheLimit_IsRejected()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        for (int index = 0; index < PublicPageLimits.FollowersPerVisitor; index++)
        {
            await Create(followers, leads, Request(), Caller(userId: null, VisitorSecret));
        }

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Create(followers, leads, Request(), Caller(userId: null, VisitorSecret)));

        Assert.Contains(PublicPageLimits.FollowersPerVisitor.ToString(), error.Message);
        Assert.AreEqual(PublicPageLimits.FollowersPerVisitor, followers.Created, "被拒时一行都不该多写");
    }

    [TestMethod]
    public async Task CreatePublic_VisitorQuotaCountsOnlyItsOwnRows()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        for (int index = 0; index < PublicPageLimits.FollowersPerVisitor; index++)
        {
            await Create(followers, leads, Request(), Caller(userId: null, VisitorSecret));
        }

        // 另一枚密钥还能建：上限是「每个创建者」的，不是「总共只能十个人」的
        await Create(followers, leads, Request(), Caller(userId: null, OtherVisitorSecret));

        Assert.AreEqual(PublicPageLimits.FollowersPerVisitor + 1, followers.Created);
    }

    [TestMethod]
    public async Task CreatePublic_WhenTheSiteReachedThePublicTotal_IsRejected()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        // 全站额度直接灌满（走仓储而不是用例：任何单个创建者都先到自己那 10 条的上限，
        // 用 Create 循环永远到不了 300 这一闸）。凭证按各行 Id 派生，与新调用者那一枚无关，
        // 所以最后这一发只会撞全站闸，不会先撞访客闸
        for (int index = 0; index < PublicPageLimits.TotalPublicFollowers; index++)
        {
            long id = index + 1L;

            await followers.CreateAsync(
                PublicRow(id) with { OwnerCredentialHash = PublicFollowerOwnership.DeriveCredential(OtherVisitorSecret, id) },
                CancellationToken.None);
        }

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Create(followers, leads, Request(), Caller(userId: null, VisitorSecret)));

        Assert.Contains(PublicPageLimits.TotalPublicFollowers.ToString(), error.Message);
        Assert.AreEqual(PublicPageLimits.TotalPublicFollowers, followers.Created);
    }

    [TestMethod]
    public async Task CreatePublic_GlobalPublicQuotaIgnoresWorkbenchRows()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        // 工作台的行不占公开页的额度：那 300 条说的是「公开页有多大」，不是「这张表有多大」
        for (int index = 0; index < PublicPageLimits.TotalPublicFollowers; index++)
        {
            await followers.CreateAsync(PrivateRow(index + 1L), CancellationToken.None);
        }

        await Create(followers, leads, Request(), Caller(userId: null, VisitorSecret));

        Assert.AreEqual(PublicPageLimits.TotalPublicFollowers + 1, followers.Created);
    }

    [TestMethod]
    public async Task CreatePublic_WithoutAnyWayToIdentifyTheCreator_IsRejected()
    {
        // 既没登录也没 Cookie（表现层也没给候选密钥）时建出来的行两个 owner 列都是空：
        // 谁都无法删除它。宁可现在拒建，也不往库里留一条没人负责的记录
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Create(followers, leads, Request(), Caller(userId: null, visitorSecret: null)));

        Assert.AreEqual(0, followers.Created);
    }

    // ── W2：跟的是带单员，不是池 ──────────────────────────

    [TestMethod]
    public async Task CreatePublic_RejectsWhenThePoolHasNoLeadTrader()
    {
        FakeFollowers followers = new();
        FakeLeads leads = new();

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Create(followers, leads, Request(), Caller(userId: null, VisitorSecret)));

        Assert.Contains("带单员", error.Message);
        Assert.AreEqual(0, followers.Created, "被拒绝的请求不该留下一行纸面账");
        Assert.AreEqual(
            0,
            leads.Rows.Count,
            "不许因为有人想跟就替全站建一个长期观察者——建观察者是公开带单页的独立动作，"
            + "它占上游订阅与持续落库，有自己的名额与限流");
    }

    [TestMethod]
    public async Task CreatePublic_OnAnExistingPausedLeadTrader_LeavesItPausedAndIgnoresTheLeadQuota()
    {
        FakeFollowers followers = new();
        FakeLeads leads = new();
        await leads.UpsertAsync(LeadAt(Pool.Value, createdToday: true, isEnabled: false), CancellationToken.None);

        // 全站带单员已经满了也不影响这一发：它一个新观察者都不增加
        for (int index = 1; index < PublicPageLimits.TotalLeads; index++)
        {
            await leads.UpsertAsync(LeadAt(Pool.Value + 10_000L + index, createdToday: true), CancellationToken.None);
        }

        await Create(followers, leads, Request(), Caller(userId: null, VisitorSecret));

        Assert.IsFalse(
            leads.Rows.Single(l => l.AccountIndex == Pool).IsEnabled,
            "跟单不许顺手把别人特意暂停的观察打开");
        Assert.AreEqual(1, followers.Created);
        Assert.AreEqual(PublicPageLimits.TotalLeads, leads.Rows.Count, "带单员的行数一行都没多");
    }

    // ── W1：公开侧无法暂停 ────────────────────────────────

    [TestMethod]
    public async Task AddPublicLeadTrader_ExistingPausedLeadTrader_StaysPausedAndIsReturnedAsIs()
    {
        FakeLeads leads = new();
        await leads.UpsertAsync(LeadAt(Pool.Value, createdToday: true, isEnabled: false), CancellationToken.None);
        FakeFollowers followers = new();

        PublicLeadTraderDto dto = await BuildAddUseCase(leads, new FakeFollowers())
            .ExecuteAsync(Pool.Value, CancellationToken.None);

        Assert.IsFalse(
            leads.Rows.Single().IsEnabled,
            "这是「公开侧无法暂停、也无法撤销暂停」的落点：Ensure 而不是 Configure");
        Assert.AreEqual(Pool.Value, dto.AccountIndex, "已存在时原样返回现状，而不是回一个新建的空壳");
        Assert.AreEqual(0, followers.Created, "W1 不建跟单员");
        Assert.AreEqual(1, leads.Rows.Count, "也不该多出一个观察者");
    }

    [TestMethod]
    public async Task AddPublicLeadTrader_DailyCapDoesNotBlockAnAlreadyWatchedPool()
    {
        // 名额用尽时，"看一眼这个池"仍然要说得出它的现状；
        // 把这一发变成 400 会让页面显示"这个池没人观察"，而那是一句假话
        FakeLeads leads = new();
        await leads.UpsertAsync(LeadAt(Pool.Value, createdToday: false, isEnabled: true), CancellationToken.None);

        for (int index = 0; index < PublicPageLimits.LeadsPerDay; index++)
        {
            await leads.UpsertAsync(LeadAt(Pool.Value + 200_000L + index, createdToday: true), CancellationToken.None);
        }

        PublicLeadTraderDto dto = await BuildAddUseCase(leads, new FakeFollowers())
            .ExecuteAsync(Pool.Value, CancellationToken.None);

        Assert.AreEqual(Pool.Value, dto.AccountIndex);
        Assert.AreEqual(PublicPageLimits.LeadsPerDay + 1, leads.Rows.Count, "原样返回时一行都不该多");
    }

    [TestMethod]
    public async Task AddPublicLeadTrader_CreatesOnAKnownPool()
    {
        FakeLeads leads = new();

        PublicLeadTraderDto dto = await BuildAddUseCase(leads, new FakeFollowers())
            .ExecuteAsync(Pool.Value, CancellationToken.None);

        Assert.AreEqual(1, leads.Rows.Count);
        Assert.AreEqual("ENA 池", dto.Name, "池名在建行时落一份快照");
        Assert.AreEqual(Pool.Value, dto.AccountIndex);
    }

    [TestMethod]
    public async Task AddPublicLeadTrader_WhenTotalLeadsReached_IsRejected()
    {
        FakeLeads leads = new();

        for (int index = 0; index < PublicPageLimits.TotalLeads; index++)
        {
            await leads.UpsertAsync(LeadAt(Pool.Value + 100_000L + index, createdToday: false), CancellationToken.None);
        }

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            BuildAddUseCase(leads, new FakeFollowers()).ExecuteAsync(Pool.Value, CancellationToken.None));

        Assert.Contains(PublicPageLimits.TotalLeads.ToString(), error.Message);
        Assert.AreEqual(PublicPageLimits.TotalLeads, leads.Rows.Count, "被拒时不该多出一个观察者");
    }

    [TestMethod]
    public async Task AddPublicLeadTrader_WhenTodaysAddsReached_IsRejected_AndYesterdayDoesNotCount()
    {
        FakeLeads leads = new();

        for (int index = 0; index < PublicPageLimits.LeadsPerDay; index++)
        {
            await leads.UpsertAsync(LeadAt(Pool.Value + 100_000L + index, createdToday: false), CancellationToken.None);
            await leads.UpsertAsync(LeadAt(Pool.Value + 200_000L + index, createdToday: true), CancellationToken.None);
        }

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            BuildAddUseCase(leads, new FakeFollowers()).ExecuteAsync(Pool.Value, CancellationToken.None));

        Assert.Contains(PublicPageLimits.LeadsPerDay.ToString(), error.Message, "要说清是「今天新增」到量了，而不是总数");
        Assert.Contains("明天", error.Message, "上限按 UTC 日算，界面要能说出什么时候再来");
    }

    [TestMethod]
    public async Task AddPublicLeadTrader_UnknownPool_KeepsTheExistingMessage()
    {
        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            BuildAddUseCase(new FakeLeads(), new FakeFollowers()).ExecuteAsync(999_999L, CancellationToken.None));

        Assert.Contains("找不到账户索引", error.Message, "池不存在的文案与工作台的 Ensure 同一条，界面不必为公开页特判");
    }

    // ── G2 / G3：读侧的两道筛子 ───────────────────────────

    [TestMethod]
    public async Task ListPublic_ExcludesWorkbenchRowsAndLiveRows()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        await followers.CreateAsync(PublicRow(1L), CancellationToken.None);
        await followers.CreateAsync(PrivateRow(2L), CancellationToken.None);
        await followers.CreateAsync(LivePublicRow(3L), CancellationToken.None);

        IReadOnlyList<PublicFollowerSummaryDto> list = await ListPublic(followers, leads);

        Assert.AreEqual(1, list.Count, "只有「公开 + 纸面」的行能出现在公开页上");
        Assert.AreEqual(1L, list[0].Id);
    }

    [TestMethod]
    public async Task ListPublic_MarksOwnershipPerCallerAndKeepsTheLabel()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        await followers.CreateAsync(
            PublicRow(1L) with
            {
                OwnerCredentialHash = PublicFollowerOwnership.DeriveCredential(VisitorSecret, 1L),
                Label = "我的",
            },
            CancellationToken.None);

        await followers.CreateAsync(
            PublicRow(2L) with { OwnerCredentialHash = PublicFollowerOwnership.DeriveCredential(OtherVisitorSecret, 2L) },
            CancellationToken.None);

        IReadOnlyList<PublicFollowerSummaryDto> list = await ListPublic(followers, leads);

        Assert.AreEqual(2, list.Count);
        Assert.IsTrue(list.Single(f => f.Id == 1L).IsOwnedByCaller);
        Assert.IsFalse(list.Single(f => f.Id == 2L).IsOwnedByCaller, "别人那一条的删除按钮不该亮着");
        Assert.AreEqual("我的", list.Single(f => f.Id == 1L).Label);
        Assert.IsNull(list.Single(f => f.Id == 2L).Label);
    }

    [TestMethod]
    public async Task ListPublic_GivesTheSameNumbersAsTheWorkbenchCard()
    {
        // 公开卡片上的数必须由同一份装配算出来：两边各算一遍迟早会给出两个不同的收益率
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();
        await followers.CreateAsync(PublicRow(1L), CancellationToken.None);

        // 工作台只列私有行：同一个账户形状另放一条私有行，两边仍走同一份装配比较
        await followers.CreateAsync(
            PrivateRow(2L) with { OwnerUserId = 1L },
            CancellationToken.None);

        IReadOnlyList<PublicFollowerSummaryDto> publicList = await ListPublic(followers, leads);
        IReadOnlyList<FollowerSummaryDto> workbench = await new ListFollowersUseCase(
            followers, leads, BuildReadUseCase(followers, leads, new FakeLedger()),
            BuildLiveAccount(followers), new FakeLiveOrders(), Scope()).ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(workbench[0].Equity, publicList[0].Equity);
        Assert.AreEqual(workbench[0].ReturnPercent, publicList[0].ReturnPercent);
        Assert.AreEqual(workbench[0].Scale, publicList[0].Scale);
        Assert.AreEqual(workbench[0].IsValuationAvailable, publicList[0].IsValuationAvailable);
    }

    /// <summary>
    /// 仓位清单在服务端**不裁剪**。
    ///
    /// 卡片上那个"两行"是**视口**，第三行之后的仓位靠跑马灯滚出来（工作台的 .card-positions 与
    /// 公开卡同一份形制）。一旦在这里 <c>Take(2)</c>，第三笔仓位就永远看不见，而那段滚动
    /// 也再没有触发的可能——这条测试钉住的是"显示上限只许长在界面那一侧"。
    /// </summary>
    [TestMethod]
    public async Task Summary_KeepsEveryNonZeroPosition_SoTheCardCanScroll()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();
        FakeLedger ledger = new();
        await followers.CreateAsync(
            PrivateRow(1L) with { OwnerUserId = 1L },
            CancellationToken.None);

        // 三个市场各一笔建仓 → 三笔非零仓位
        for (int index = 0; index < 3; index++)
        {
            ledger.Fills.Add(PaperFill(10L + index, 1L, index, marketId: 29 + index, symbol: $"T{index}"));
        }

        IReadOnlyList<FollowerSummaryDto> summaries = await new ListFollowersUseCase(
            followers,
            leads,
            BuildReadUseCase(followers, leads, ledger),
            BuildLiveAccount(followers),
            new FakeLiveOrders(),
            Scope()).ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(3, summaries[0].Positions.Count, "一行都不该在服务端被裁掉");
    }

    /// <summary>
    /// 卡片上每个持仓的**标的杠杆**来自这里的保证金率（杠杆 = 100 ÷ 保证金率）。
    ///
    /// 纸面把它一路带到卡片上；取不到保证金率时为 null，界面那一格就不渲染——
    /// 不能拿 1× 冒充"没有杠杆"（那会把"不知道"画成"满仓裸奔"）。
    /// </summary>
    [TestMethod]
    public async Task Summary_CarriesTheMarginPercentBehindTheCardLeverage()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();
        FakeLedger ledger = new();
        await followers.CreateAsync(PrivateRow(1L) with { OwnerUserId = 1L }, CancellationToken.None);

        // 一笔记了保证金率（25% → 4×），一笔没记（旧流水，取不到 → 不显示）
        ledger.Fills.Add(PaperFill(10L, 1L, 0, marketId: 29, symbol: "ENA") with { InitialMarginPercent = 25m });
        ledger.Fills.Add(PaperFill(11L, 1L, 1, marketId: 30, symbol: "BTC"));

        IReadOnlyList<FollowerSummaryDto> summaries = await new ListFollowersUseCase(
            followers,
            leads,
            BuildReadUseCase(followers, leads, ledger),
            BuildLiveAccount(followers),
            new FakeLiveOrders(),
            Scope()).ExecuteAsync(CancellationToken.None);

        FollowerSummaryDto summary = Assert.ContainsSingle(summaries);

        Assert.AreEqual(25m, summary.Positions.Single(p => p.Symbol == "ENA").InitialMarginPercent);
        Assert.IsNull(summary.Positions.Single(p => p.Symbol == "BTC").InitialMarginPercent);
    }

    [TestMethod]
    public async Task Workbench_OnlyShowsOwnedPrivateRows()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        await followers.CreateAsync(PrivateRow(1L) with { OwnerUserId = 1L }, CancellationToken.None);
        await followers.CreateAsync(PrivateRow(2L) with { OwnerUserId = 2L }, CancellationToken.None);
        await followers.CreateAsync(PublicRow(3L) with { OwnerUserId = 1L }, CancellationToken.None);

        IReadOnlyList<FollowerSummaryDto> mine = await Workbench(followers, leads, Scope(userId: 1L));

        Assert.HasCount(1, mine, "公开行不进入工作台；别人的私有行也不该出现");
        Assert.AreEqual(1L, mine[0].Id);
    }

    [TestMethod]
    public async Task Workbench_SuperadminSeesAllPrivateRowsButNotPublicOnes()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();

        await followers.CreateAsync(PrivateRow(1L) with { OwnerUserId = 1L }, CancellationToken.None);
        await followers.CreateAsync(PrivateRow(2L) with { OwnerUserId = 2L }, CancellationToken.None);
        await followers.CreateAsync(PublicRow(3L) with { OwnerUserId = 1L }, CancellationToken.None);

        IReadOnlyList<FollowerSummaryDto> all = await Workbench(followers, leads, Scope(all: true));

        CollectionAssert.AreEquivalent(
            new List<long> { 1L, 2L },
            all.Select(row => row.Id).ToList(),
            "superadmin 跨用户看全部私有行；公开行仍留给公开广场");
    }

    [TestMethod]
    public async Task LeadList_ScopesFollowerCounts_ButPublicUsesAllRows()
    {
        FakeLeads leads = SeededLeads();
        FakeFollowers followers = new();

        await followers.CreateAsync(PrivateRow(1L) with { OwnerUserId = 1L }, CancellationToken.None);
        await followers.CreateAsync(PrivateRow(2L) with { OwnerUserId = 2L }, CancellationToken.None);
        await followers.CreateAsync(PublicRow(3L) with { OwnerUserId = 1L }, CancellationToken.None);

        LeadTraderDto mine = Assert.ContainsSingle(
            await new ListLeadTradersUseCase(leads, followers, new FakeEvents(), new FakeWatchState(), Scope())
                .ExecuteAsync(CancellationToken.None));

        Assert.AreEqual(1, mine.FollowerCount, "普通账号只该看到自己的跟单数");
        Assert.AreEqual(1, mine.ActiveFollowerCount);

        LeadTraderDto admin = Assert.ContainsSingle(
            await new ListLeadTradersUseCase(leads, followers, new FakeEvents(), new FakeWatchState(), Scope(all: true))
                .ExecuteAsync(CancellationToken.None));

        Assert.AreEqual(2, admin.FollowerCount, "superadmin 的计数包含所有私有行，但不含公开行");

        LeadTraderDto publicLead = Assert.ContainsSingle(
            await new ListLeadTradersUseCase(leads, followers, new FakeEvents(), new FakeWatchState(), Scope())
                .ExecutePublicAsync(CancellationToken.None));

        Assert.AreEqual(3, publicLead.FollowerCount, "公开页沿用全量计数（本来就是公开广场的口径）");
    }

    private static Task<IReadOnlyList<FollowerSummaryDto>> Workbench(
        FakeFollowers followers,
        FakeLeads leads,
        FakeUserDataScope scope) =>
        new ListFollowersUseCase(
            followers,
            leads,
            BuildReadUseCase(followers, leads, new FakeLedger()),
            BuildLiveAccount(followers),
            new FakeLiveOrders(),
            scope).ExecuteAsync(CancellationToken.None);

    [TestMethod]
    public async Task GetPublic_PrivateRowLiveRowAndMissingRow_AreAllFortyFour()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();
        await followers.CreateAsync(PrivateRow(2L), CancellationToken.None);
        await followers.CreateAsync(LivePublicRow(3L), CancellationToken.None);

        GetPublicFollowerUseCase useCase = BuildGetUseCase(followers, leads);

        Assert.IsNull(await useCase.ExecuteAsync(2L, Caller(null, VisitorSecret), CancellationToken.None), "工作台的行");
        Assert.IsNull(await useCase.ExecuteAsync(3L, Caller(null, VisitorSecret), CancellationToken.None), "实盘行");
        Assert.IsNull(await useCase.ExecuteAsync(404L, Caller(null, VisitorSecret), CancellationToken.None), "根本不存在的行");

        PublicFollowerDto publicOne = (await useCase.ExecuteAsync(1L, Caller(null, VisitorSecret), CancellationToken.None))!;
        Assert.IsNull(publicOne, "还没建过的 1 号也该是 404——上面两个断言不是因为偶然通过");
    }

    [TestMethod]
    public async Task GetPublic_DetailTrimsFillsToTheShownLimitAndKeepsTheLatest()
    {
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();
        FakeLedger ledger = new();
        await followers.CreateAsync(PublicRow(1L), CancellationToken.None);

        int total = PublicPageLimits.FillsShownOnDetail + 20;

        for (int index = 1; index <= total; index++)
        {
            ledger.Fills.Add(PaperFill(index, 1L, index));
        }

        PublicFollowerDto? detail = await BuildGetUseCase(followers, leads, ledger)
            .ExecuteAsync(1L, Caller(null, VisitorSecret), CancellationToken.None);

        Assert.IsNotNull(detail);
        Assert.AreEqual(PublicPageLimits.FillsShownOnDetail, detail.Fills.Count, "详情页只给最近若干条，整条账留在工作台");
        Assert.AreEqual(total, detail.Fills[0].Id, "给的是**最近**那 50 条，不是最旧的 50 条");
    }

    [TestMethod]
    public async Task GetPublic_DetailNeverCarriesExchangeFields()
    {
        // 这一条是整个窄 DTO 存在理由的可执行形式：公开响应里不许出现任何实盘字段。
        // 用反射而不是逐个手挑，以后往私有 DTO 上加字段时这条会替我们想起来
        FakeFollowers followers = new();
        FakeLeads leads = SeededLeads();
        await followers.CreateAsync(PublicRow(1L), CancellationToken.None);

        PublicFollowerDto? detail = await BuildGetUseCase(followers, leads)
            .ExecuteAsync(1L, Caller(null, VisitorSecret), CancellationToken.None);

        Assert.IsNotNull(detail);

        // Fees 不在此列：它是纸面账重放出来的累计手续费（与工作台纸面详情同一口径），
        // 不是实盘侧信息。实盘那一侧的余额与持仓仍由前三项禁掉。
        string[] forbidden = ["ExchangeName", "ExchangeConfigId", "AvailableBalance", "LivePositions"];
        string[] actual = [.. detail.GetType().GetProperties().Select(p => p.Name)];

        foreach (string name in forbidden)
        {
            Assert.IsFalse(actual.Contains(name), $"公开详情不该输出 {name}（实盘侧信息）");
        }
    }

    // ── W3：删除的四个判据 ────────────────────────────────

    [TestMethod]
    public async Task DeletePublic_MissingRow_IsNotFoundAndNothingIsTouched()
    {
        FakeLedger ledger = new();
        FakeSync deleteSync = new();

        PublicFollowerDeleteOutcome outcome = await new DeletePublicFollowerUseCase(
            new FakeFollowers(),
            new DeleteFollowerUseCase(new FakeFollowers(), ledger, deleteSync))
            .ExecuteAsync(1L, Caller(null, VisitorSecret), CancellationToken.None);

        Assert.AreEqual(PublicFollowerDeleteOutcome.NotFound, outcome);
        Assert.AreEqual(0, ledger.Cleared);
        Assert.AreEqual(0, deleteSync.Requests);
    }

    [TestMethod]
    public async Task DeletePublic_WorkbenchRow_IsNotFoundEvenForItsOwnCreator()
    {
        // 非公开行在公开端点上「不存在」：否则把 {id} 逐个试一遍就能探测出哪些行是私有的
        FakeFollowers followers = new();
        await followers.CreateAsync(PrivateRow(2L) with { OwnerUserId = 7L }, CancellationToken.None);
        (FakeLedger ledger, FakeSync deleteSync) = (new(), new());

        PublicFollowerDeleteOutcome outcome = await new DeletePublicFollowerUseCase(
            followers, new DeleteFollowerUseCase(followers, ledger, deleteSync))
            .ExecuteAsync(2L, Caller(userId: 7L, visitorSecret: null), CancellationToken.None);

        Assert.AreEqual(PublicFollowerDeleteOutcome.NotFound, outcome);
        Assert.AreEqual(0, ledger.Cleared, "探测不该动到别人的账");
    }

    [TestMethod]
    public async Task DeletePublic_AnonRow_IsOpenedOnlyByTheSecretItWasDerivedFrom()
    {
        FakeFollowers own = new();
        await own.CreateAsync(
            PublicRow(1L) with { OwnerCredentialHash = PublicFollowerOwnership.DeriveCredential(VisitorSecret, 1L) },
            CancellationToken.None);

        (FakeLedger ownLedger, FakeSync ownSync) = (new(), new());

        Assert.AreEqual(PublicFollowerDeleteOutcome.Deleted, await new DeletePublicFollowerUseCase(
            own, new DeleteFollowerUseCase(own, ownLedger, ownSync))
            .ExecuteAsync(1L, Caller(userId: null, VisitorSecret), CancellationToken.None));

        Assert.AreEqual(1, ownLedger.Cleared, "删要连带清掉流水与权益点（走的是工作台那条路径）");
        Assert.AreEqual(0, ownSync.Requests, "公开删除不唤醒全量同步；实时帧由控制器推");

        // 别的浏览器：403，且一行都不动
        FakeFollowers stranger = new();
        await stranger.CreateAsync(
            PublicRow(1L) with { OwnerCredentialHash = PublicFollowerOwnership.DeriveCredential(VisitorSecret, 1L) },
            CancellationToken.None);

        (FakeLedger strangerLedger, FakeSync strangerSync) = (new(), new());

        Assert.AreEqual(PublicFollowerDeleteOutcome.Forbidden, await new DeletePublicFollowerUseCase(
            stranger, new DeleteFollowerUseCase(stranger, strangerLedger, strangerSync))
            .ExecuteAsync(1L, Caller(userId: null, OtherVisitorSecret), CancellationToken.None));

        Assert.AreEqual(0, strangerLedger.Cleared + strangerSync.Requests, "403 那一发不该动库");
    }

    [TestMethod]
    public async Task DeletePublic_UserRow_FollowsTheUserCriterion()
    {
        FakeFollowers mine = new();
        await mine.CreateAsync(PublicRow(1L) with { OwnerUserId = 7L }, CancellationToken.None);
        (FakeLedger mineLedger, FakeSync mineSync) = (new(), new());

        Assert.AreEqual(PublicFollowerDeleteOutcome.Deleted, await new DeletePublicFollowerUseCase(
            mine, new DeleteFollowerUseCase(mine, mineLedger, mineSync))
            .ExecuteAsync(1L, Caller(userId: 7L, visitorSecret: null), CancellationToken.None));

        FakeFollowers theirs = new();
        await theirs.CreateAsync(PublicRow(1L) with { OwnerUserId = 7L }, CancellationToken.None);
        (FakeLedger theirsLedger, FakeSync theirsSync) = (new(), new());

        Assert.AreEqual(PublicFollowerDeleteOutcome.Forbidden, await new DeletePublicFollowerUseCase(
            theirs, new DeleteFollowerUseCase(theirs, theirsLedger, theirsSync))
            .ExecuteAsync(1L, Caller(userId: 8L, VisitorSecret), CancellationToken.None));
        Assert.AreEqual(0, theirsLedger.Cleared + theirsSync.Requests);
    }

    [TestMethod]
    public async Task DeletePublic_LiveRow_StaysNotFound()
    {
        FakeFollowers followers = new();
        await followers.CreateAsync(LivePublicRow(3L) with { OwnerUserId = 7L }, CancellationToken.None);
        (FakeLedger ledger, FakeSync deleteSync) = (new(), new());

        PublicFollowerDeleteOutcome outcome = await new DeletePublicFollowerUseCase(
            followers, new DeleteFollowerUseCase(followers, ledger, deleteSync))
            .ExecuteAsync(3L, Caller(userId: 7L, visitorSecret: null), CancellationToken.None);

        Assert.AreEqual(PublicFollowerDeleteOutcome.NotFound, outcome, "实盘行即使在库里是公开的也不给公开面删");
        Assert.AreEqual(0, ledger.Cleared);
    }

    // ── G4：池名候选 ──────────────────────────────────────

    [TestMethod]
    public async Task PoolOptions_FiltersByNameCaseInsensitively()
    {
        FakePoolCatalog catalog = new([
            PoolOf(1L, "ENA 池"),
            PoolOf(2L, "Bitcoin 3x Long"),
            PoolOf(3L, "ethereum 2x"),
        ]);

        PublicPoolOptionsUseCase useCase = new(catalog);

        IReadOnlyList<PublicPoolOptionDto> upper = await useCase.ExecuteAsync("ENA", null, CancellationToken.None);
        IReadOnlyList<PublicPoolOptionDto> lower = await useCase.ExecuteAsync("ena", null, CancellationToken.None);

        Assert.AreEqual(1, upper.Count);
        Assert.AreEqual(1L, upper[0].AccountIndex);
        Assert.AreEqual(upper.Count, lower.Count, "大小写不敏感：只按原样匹配会让输入框看起来像坏了");

        Assert.AreEqual(
            1,
            (await useCase.ExecuteAsync("bitcoin ", null, CancellationToken.None)).Count,
            "查询串两端空白不算进去");

        Assert.AreEqual(
            1,
            (await useCase.ExecuteAsync("ETH", null, CancellationToken.None)).Count,
            "子串匹配：ethereum 里的 eth 也要命中");
    }

    [TestMethod]
    public async Task PoolOptions_ExcludesFrozenPools_FromBothEmptyAndTypedQueries()
    {
        FakePoolCatalog catalog = new([
            PoolOf(1L, "ENA 池"),
            PoolOf(2L, "已冻结的池", (int)PoolLifecycle.Frozen),
        ]);

        PublicPoolOptionsUseCase useCase = new(catalog);

        IReadOnlyList<PublicPoolOptionDto> all = await useCase.ExecuteAsync(null, null, CancellationToken.None);

        Assert.AreEqual(1, all.Count, "冻结池不进候选");
        Assert.AreEqual(1L, all[0].AccountIndex);

        Assert.AreEqual(0, (await useCase.ExecuteAsync("已冻结", null, CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task PoolOptions_FindsByExactAccountIndex_NotByPartialDigits()
    {
        FakePoolCatalog catalog = new([
            PoolOf(123_456_789L, "ENA 池"),
            PoolOf(987_654_321L, "Bitcoin 3x Long"),
        ]);

        PublicPoolOptionsUseCase useCase = new(catalog);

        IReadOnlyList<PublicPoolOptionDto> exact =
            await useCase.ExecuteAsync("123456789", null, CancellationToken.None);

        Assert.AreEqual(1, exact.Count);
        Assert.AreEqual(123_456_789L, exact[0].AccountIndex);

        Assert.AreEqual(
            0,
            (await useCase.ExecuteAsync("1234", null, CancellationToken.None)).Count,
            "索引不做子串匹配：几个数字会命中一大片，那不是可用的筛选");
    }

    [TestMethod]
    public async Task PoolOptions_ClampsTheLimitInsteadOfRefusing()
    {
        List<Pool> many = [.. Enumerable.Range(1, 70).Select(index => PoolOf(index, $"池 {index:00}"))];
        PublicPoolOptionsUseCase useCase = new(new FakePoolCatalog(many));

        Assert.AreEqual(
            PublicPageLimits.PoolOptionLimitDefault,
            (await useCase.ExecuteAsync(null, null, CancellationToken.None)).Count,
            "不传 limit 用默认值");

        Assert.AreEqual(
            PublicPageLimits.PoolOptionLimitMax,
            (await useCase.ExecuteAsync("池", 500, CancellationToken.None)).Count,
            "超出上限要夹回来：候选框多给几条少给几条不是数据正确性问题，为它回 400 只会多一条没信息量的分支");

        Assert.AreEqual(
            PublicPageLimits.PoolOptionLimitDefault,
            (await useCase.ExecuteAsync("池", 0, CancellationToken.None)).Count,
            "非正数的 limit 按默认值处理");
    }

    [TestMethod]
    public async Task PoolOptions_ReadsTheCatalogNotTheClient()
    {
        // 匿名面上的这条读必须走带缓存的读口。直连 ILighterPoolsClient 的用例形状
        // 在这里会被抓住：缓存口被调用一次，而客户端一次都不该被碰
        FakePoolCatalog catalog = new([PoolOf(1L, "ENA 池")]);

        await new PublicPoolOptionsUseCase(catalog).ExecuteAsync("ENA", null, CancellationToken.None);

        Assert.AreEqual(1, catalog.Calls);
    }

    // ── 装配被测对象 ──────────────────────────────────────

    private static PublicCaller Caller(long? userId, string? visitorSecret) => new(userId, visitorSecret);

    private static CreatePublicFollowerRequest Request(
        string? mode = null,
        long? exchangeConfigId = null,
        decimal capital = 500m,
        string? label = null) => new()
        {
            LeadAccountIndex = Pool.Value,
            InitialCapital = capital,
            Mode = mode,
            ExchangeConfigId = exchangeConfigId,
            Label = label,
        };

    private static async Task<PublicFollowerDto> Create(
        FakeFollowers followers,
        FakeLeads leads,
        CreatePublicFollowerRequest request,
        PublicCaller caller)
    {
        CreatePublicFollowerUseCase useCase = new(
            followers,
            leads,
            Clock);

        return await useCase.ExecuteAsync(request, caller, CancellationToken.None);
    }

    private static AddPublicLeadTraderUseCase BuildAddUseCase(FakeLeads leads, FakeFollowers followers) => new(
        leads,
        new EnsureLeadTraderUseCase(leads, new FakePoolsClient()),
        new ListLeadTradersUseCase(leads, followers, new FakeEvents(), new FakeWatchState(), Scope(all: true)),
        new PublicLeadTraderQuota(leads, Clock));

    private static GetPublicFollowerUseCase BuildGetUseCase(FakeFollowers followers, FakeLeads leads, FakeLedger? ledger = null) =>
        new(followers, BuildReadUseCase(followers, leads, ledger ?? new FakeLedger()));

    private static GetFollowerUseCase BuildReadUseCase(FakeFollowers followers, FakeLeads leads, FakeLedger ledger) => new(
        followers,
        ledger,
        leads,
        new FakePoolsClient(),
        new FakeMarks(),
        new FakeExchanges());

    private static GetLiveAccountUseCase BuildLiveAccount(FakeFollowers followers) => new(
        followers,
        new FakeExchanges(),
        new FakeBrokerFactory(),
        new FakeCatalogFactory());

    private static async Task<IReadOnlyList<PublicFollowerSummaryDto>> ListPublic(
        FakeFollowers followers,
        FakeLeads leads,
        PublicCaller? caller = null)
    {
        ListPublicFollowersUseCase useCase = new(
            followers,
            new ListFollowersUseCase(
                followers,
                leads,
                BuildReadUseCase(followers, leads, new FakeLedger()),
                BuildLiveAccount(followers),
                new FakeLiveOrders(),
                Scope()));

        return await useCase.ExecuteAsync(caller ?? Caller(userId: null, VisitorSecret), CancellationToken.None);
    }

    private static FakeUserDataScope Scope(long userId = 1L, bool all = false) =>
        new() { UserId = userId, CanSeeAll = all };

    private static FakeLeads SeededLeads()
    {
        FakeLeads leads = new();
        leads.Rows.Add(LeadAt(Pool.Value, createdToday: true));

        return leads;
    }

    // ── 行形状（三种归属 + 两种模式）───────────────────────

    private static Follower PublicRow(long id) => new()
    {
        Id = id,
        LeadAccountIndex = Pool,
        InitialCapital = UsdAmount.From(500m),
        Mode = CopyMode.Paper,
        IsActive = true,
        CreatedAt = LighterTimestamp.FromMilliseconds(Today.ToUnixTimeMilliseconds()),
        IsPublic = true,
    };

    private static Follower PrivateRow(long id) => PublicRow(id) with { IsPublic = false };

    private static Follower LivePublicRow(long id) => PublicRow(id) with
    {
        Mode = CopyMode.Live,
        ExchangeConfigId = 12L,
    };

    /// <summary>一条成交流水。<paramref name="sequence"/> 决定时刻顺序（越晚越新）。</summary>
    private static PaperFill PaperFill(
        long id,
        long followerId,
        int sequence,
        int marketId = 29,
        string symbol = "ENA") => new()
    {
        Id = id,
        FollowerId = followerId,
        MarketId = marketId,
        Symbol = symbol,
        Side = OrderSide.Buy,
        PoolPositionAfter = SignedSize.From(1m),
        PoolEquity = UsdAmount.From(1_000m),
        TargetPosition = SignedSize.From(0.5m),
        PositionBefore = SignedSize.Zero,
        PositionAfter = SignedSize.From(0.5m),
        Size = 0.5m,
        Price = PriceAmount.From(100m),
        Notional = UsdAmount.From(50m),
        DepthSufficient = true,
        Fee = UsdAmount.Zero,
        RealizedPnl = UsdAmount.Zero,
        SkipReason = null,
        ObservedAt = LighterTimestamp.FromMilliseconds(Today.ToUnixTimeMilliseconds() + sequence),
    };

    private static LeadTrader LeadAt(long accountIndex, bool createdToday, bool isEnabled = true) => new()
    {
        AccountIndex = AccountIndex.From(accountIndex),
        Name = accountIndex == Pool.Value ? "ENA 池" : $"池 {accountIndex}",
        IsEnabled = isEnabled,
        CreatedAt = LighterTimestamp.FromMilliseconds(
            (createdToday ? Today : Today - TimeSpan.FromDays(1)).ToUnixTimeMilliseconds()),
    };

    private static Pool PoolOf(long accountIndex, string name, int stateCode = 0) => new()
    {
        AccountIndex = AccountIndex.From(accountIndex),
        Name = name,
        L1Address = "0x0000000000000000000000000000000000000000",
        AccountTypeCode = 1,
        TotalAssetValue = UsdAmount.From(1_000m),
        ReportedAnnualPercentageYield = PercentValue.FromPercent(0m),
        ReportedSharpe = 0m,
        OperatorFee = PercentValue.FromPercent(10m),
        TotalShares = 1_000L,
        // 秒刻度要落在 DateTimeOffset 可表示范围内：账户索引是 15 位的数，直接加到秒上会
        // 撞上 LighterTimestamp 的刻度守卫（那正是守卫该拦的形状）。取模只用来让不同池的
        // 时刻彼此可区分——候选框的排序断言不依赖这里
        CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000 + accountIndex % 1_000L),
        StateCode = stateCode,
    };

    // ── fakes（不用 mock 框架，按项目约定）─────────────────

    private sealed class FakeFollowers : IFollowerRepository
    {
        private readonly List<Follower> _rows = [];

        public int Created { get; private set; }

        public Follower? Last => _rows.Count > 0 ? _rows[^1] : null;

        public Task<Follower?> GetAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.FirstOrDefault(f => f.Id == id));

        public Task<IReadOnlyList<Follower>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Follower>>([.. _rows]);

        public Task<IReadOnlyList<Follower>> ListByLeadAsync(
            AccountIndex leadAccountIndex,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Follower>>([.. _rows.Where(f => f.LeadAccountIndex == leadAccountIndex)]);

        public Task<Follower> CreateAsync(Follower follower, CancellationToken cancellationToken = default)
        {
            Created++;

            // Id 由调用方给的（种子行）就沿用，否则自增——归属判据要按 Id 派生，
            // 所以这里必须真的分配一个稳定 Id，而不是留着 0
            Follower stored = follower with { Id = follower.Id != 0L ? follower.Id : _rows.Count + 1L };
            _rows.Add(stored);

            return Task.FromResult(stored);
        }

        public Task UpdateAsync(Follower follower, CancellationToken cancellationToken = default)
        {
            int index = _rows.FindIndex(f => f.Id == follower.Id);

            if (index >= 0)
            {
                _rows[index] = follower;
            }

            return Task.CompletedTask;
        }

        public Task SetOwnerCredentialAsync(
            long id,
            string ownerCredentialHash,
            CancellationToken cancellationToken = default)
        {
            int index = _rows.FindIndex(f => f.Id == id);

            if (index >= 0)
            {
                _rows[index] = _rows[index] with { OwnerCredentialHash = ownerCredentialHash };
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
        {
            _rows.RemoveAll(f => f.Id == id);

            return Task.CompletedTask;
        }
    }

    private sealed class FakeLeads : ILeadTraderRepository
    {
        public List<LeadTrader> Rows { get; } = [];

        public Task<LeadTrader?> GetAsync(AccountIndex account, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows.FirstOrDefault(l => l.AccountIndex == account));

        public Task<IReadOnlyList<LeadTrader>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LeadTrader>>([.. Rows]);

        public Task UpsertAsync(LeadTrader lead, CancellationToken cancellationToken = default)
        {
            Rows.RemoveAll(l => l.AccountIndex == lead.AccountIndex);
            Rows.Add(lead);

            return Task.CompletedTask;
        }

        public Task DeleteAsync(AccountIndex account, CancellationToken cancellationToken = default)
        {
            Rows.RemoveAll(l => l.AccountIndex == account);

            return Task.CompletedTask;
        }
    }

    private sealed class FakeSync : ILeadTraderSync
    {
        public int Requests { get; private set; }

        public Task RequestSyncAsync(CancellationToken cancellationToken = default)
        {
            Requests++;

            return Task.CompletedTask;
        }
    }

    private sealed class FakeWatchState : ILeadWatchState
    {
        public bool IsWatching(AccountIndex account) => true;
    }

    private sealed class FakeEvents : IPoolEventRepository
    {
        public Task AppendAsync(IReadOnlyList<PoolEvent> events, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PoolEvent>> GetRecentAsync(
            AccountIndex account, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PoolEvent>>([]);

        public Task<IReadOnlyList<PoolEvent>> GetRecentAcrossPoolsAsync(
            int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PoolEvent>>([]);

        public Task<IReadOnlyList<PoolSignalStats>> GetSignalStatsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PoolSignalStats>>([]);
    }

    private sealed class FakeLedger : IFollowerLedgerRepository
    {
        public List<PaperFill> Fills { get; } = [];

        public int Cleared { get; private set; }

        public Task AppendFillsAsync(IReadOnlyList<PaperFill> fills, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PaperFill>> GetFillsAsync(
            long followerId, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PaperFill>>([.. Fills]);

        public Task AppendEquityPointAsync(PaperEquityPoint point, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PaperEquityPoint>> GetEquityCurveAsync(
            long followerId, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PaperEquityPoint>>([]);

        public Task ClearHistoryAsync(long followerId, CancellationToken cancellationToken = default)
        {
            Cleared++;

            return Task.CompletedTask;
        }
    }

    private sealed class FakeLiveOrders : ILiveOrderRepository
    {
        public Task AddAsync(LiveOrder order, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<LiveOrder>> ListAsync(
            long followerId, int limit = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LiveOrder>>([]);

        public Task<(int Placed, int NotFollowed)> CountAsync(
            long followerId, CancellationToken cancellationToken = default) => Task.FromResult((0, 0));

        public Task<IReadOnlyList<string>> SourcesForAsync(
            long followerId, string targetBase, string targetQuote, int limit = 20,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeExchanges : IExchangeConfigRepository
    {
        public Task<ExchangeConfig?> GetAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExchangeConfig?>(null);

        public Task<IReadOnlyList<ExchangeConfig>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExchangeConfig>>([]);

        public Task<ExchangeConfig> CreateAsync(ExchangeConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(config);

        public Task UpdateAsync(ExchangeConfig config, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>一个盯市价都取不到：那些市场会进 MarksUnavailable，而不是被当成 0。</summary>
    private sealed class FakeMarks : IMarkPrices
    {
        public Task<MarkPrices> ResolveAsync(
            IReadOnlyList<int> marketIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MarkPrices(new Dictionary<int, PriceAmount>(), [.. marketIds]));
    }

    /// <summary>
    /// 上游替身：只给得出公开这条路需要的三件事——池清单、池快照、合约规格。
    /// 其余方法一律抛：<c>NotSupportedException</c> 在测试里就是「这条路不该被走到」的断言。
    /// </summary>
    private sealed class FakePoolsClient : ILighterPoolsClient
    {
        public Task<IReadOnlyList<Pool>> FetchPoolsAsync(
            PoolFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Pool>>([PoolOf(Pool.Value, "ENA 池")]);

        public Task<LighterSystemConfig> FetchSystemConfigAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("配置不该读系统配置");

        public Task<PoolSnapshot> FetchSnapshotAsync(
            AccountIndex account, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PoolSnapshot
            {
                AccountIndex = account,
                TakenAt = LighterTimestamp.FromMilliseconds(Today.ToUnixTimeMilliseconds()),
                TotalAssetValue = UsdAmount.From(1_000m),
                Collateral = UsdAmount.From(1_000m),
                AvailableBalance = UsdAmount.From(500m),
                InitialMarginRequirement = UsdAmount.From(500m),
                MaintenanceMarginRequirement = UsdAmount.From(250m),
                Positions = [],
                Orders = [],
            });

        public Task<PoolInfo> FetchPoolInfoAsync(
            AccountIndex account, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("公开页不读池详情");

        public Task<IReadOnlyList<PoolTrade>> FetchRecentTradesAsync(
            AccountIndex account, int limit = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("公开页不读池成交");

        public Task<IReadOnlyList<PoolOrder>> FetchOrdersAsync(
            AccountIndex account, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("公开页不读挂单");

        public Task<IReadOnlyList<InstrumentSpec>> FetchInstrumentsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstrumentSpec>>([]);

        public Task<OrderBookSnapshot> FetchOrderBookAsync(
            int marketId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("公开页不按盘口定价成交");
    }

    private sealed class FakePoolCatalog(List<Pool> pools) : IPoolCatalog
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<Pool>> ListAllAsync(CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult<IReadOnlyList<Pool>>([.. pools]);
        }
    }

    private sealed class FakeBrokerFactory : IExchangeBrokerFactory
    {
        public IExchangeBroker? Create(ExchangeConfig config) => null;
    }

    private sealed class FakeCatalogFactory : IExchangeSymbolCatalogFactory
    {
        public IExchangeSymbolCatalog? Create(ExchangeCode platform) => null;

        public IReadOnlyList<ExchangeCode> SupportedPlatforms => [];
    }
}
