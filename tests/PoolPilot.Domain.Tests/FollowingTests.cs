using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Following;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Tests;

/// <summary>
/// 带单员与跟单员的契约测试。
///
/// 这一层只断言**纯逻辑**：信号如何映射仓位、规模比例怎么算、实体允许什么状态。
/// 运行时语义（"订阅者归零后观察是否继续"、"信号是否只送给订阅者"）属于
/// Infrastructure 的 LeadTraderRuntime，在那边用测试锁住。
/// </summary>
[TestClass]
public sealed class FollowingTests
{
    private static readonly AccountIndex Pool = AccountIndex.From(281474976552443L);

    private static PoolEvent Event(
        PositionSide side,
        decimal sizeBefore,
        decimal sizeAfter,
        PoolEventKind kind = PoolEventKind.Increase) => new()
        {
            AccountIndex = Pool,
            Kind = kind,
            MarketId = 29,
            Symbol = "ENA",
            Side = side,
            SizeBefore = sizeBefore,
            SizeAfter = sizeAfter,
            DetectedAt = LighterTimestamp.FromSeconds(1_800_000_000),
            ReferencePrice = PriceAmount.From(0.2447m),
        };

    // ── 信号 ──────────────────────────────────────────────

    [TestMethod]
    public void LeadSignal_ForIncrease_ExposesSignedPoolPosition()
    {
        // 跟单员的目标仓位 = 池仓 × 比例，所以信号必须直接给出"带符号的池仓"
        LeadSignal signal = new(Event(PositionSide.Long, 100m, 250m), "ENA 池", UsdAmount.From(72_100m));

        Assert.AreEqual(250m, signal.PositionAfter.Value);
        Assert.AreEqual(PositionSide.Long, signal.PositionAfter.Side);
        Assert.AreEqual(PoolEventKind.Increase, signal.Kind);
        Assert.AreEqual(29, signal.MarketId);
        Assert.AreEqual(Pool, signal.Pool);
    }

    [TestMethod]
    public void LeadSignal_ForClose_IsFlatNotShort()
    {
        // 平仓后池仓为 0，且**不能**因为携带的是 Short 方向就变成"做空 0 手"以外的含义。
        // 这条锁住一个容易写错的地方：FromSide(0, Short) 必须是"空仓"而不是"负零"。
        LeadSignal signal = new(
            Event(PositionSide.Short, 400m, 0m, PoolEventKind.Close),
            "ENA 池",
            UsdAmount.From(72_100m));

        Assert.IsTrue(signal.PositionAfter.IsFlat);
        Assert.AreEqual(0m, signal.PositionAfter.Value);
        Assert.IsNull(signal.PositionAfter.Side);
    }

    [TestMethod]
    public void LeadSignal_CarriesPoolEquity()
    {
        // 池子权益是规模比例的分母，信号必须带着它——否则跟单员得自己回取一次池子状态，
        // "订阅信号即可交易"就不成立了
        LeadSignal signal = new(Event(PositionSide.Long, 0m, 100m), "ENA 池", UsdAmount.From(72_100m));

        Assert.AreEqual(72_100m, signal.PoolEquity.Value);
    }

    // ── 跟单员 ────────────────────────────────────────────

    [TestMethod]
    public void Follower_ScaleAgainst_IsCapitalOverPoolEquity()
    {
        Follower follower = Follower(capital: 5_000m);

        decimal scale = follower.ScaleAgainst(Snapshot(equity: 100_000m));

        Assert.AreEqual(0.05m, scale);
    }

    [TestMethod]
    public void Follower_ScaleAgainst_WhenPoolEquityIsZeroOrNegative_ReturnsZero()
    {
        // 权益为 0 或为负（爆仓边缘）时不能除，返回 0 表示"算出多少都不该跟"
        Follower follower = Follower(capital: 5_000m);

        Assert.AreEqual(0m, follower.ScaleAgainst(Snapshot(equity: 0m)));
        Assert.AreEqual(0m, follower.ScaleAgainst(Snapshot(equity: -1_200m)));
    }

    [TestMethod]
    public void Follower_CarriesItsOwnIdentityAndSubscription()
    {
        // 身份与订阅关系是跟单员自己的：一个池可以挂多个跟单员，
        // 所以 Id 不能借用池索引，两者必须能各自独立地不同
        Follower first = Follower(capital: 1_000m) with { Id = 7L };
        Follower second = Follower(capital: 2_000m) with { Id = 8L };

        Assert.AreEqual(7L, first.Id);
        Assert.AreEqual(8L, second.Id);
        Assert.AreEqual(first.LeadAccountIndex, second.LeadAccountIndex, "两个跟单员跟的是同一个池");
        Assert.AreNotEqual(first.Id, second.Id, "同一个池下的跟单员必须能被区分开");
    }

    [TestMethod]
    public void Follower_ModeIsPartOfTheEntity_NotAnUiSwitch()
    {
        // 纸面与实盘可以同时挂在同一个带单员下，各自独立记账 —— 模式必须落在实体上
        Follower paper = Follower(capital: 1_000m);
        Follower live = Follower(capital: 1_000m) with { Mode = CopyMode.Live };

        Assert.AreEqual(CopyMode.Paper, paper.Mode);
        Assert.AreEqual(CopyMode.Live, live.Mode);
        Assert.AreEqual(paper.LeadAccountIndex, live.LeadAccountIndex);
    }

    // ── 带单员 ────────────────────────────────────────────

    [TestMethod]
    public void LeadTrader_IdentityIsThePool_AndOnePerPool()
    {
        LeadTrader lead = new()
        {
            AccountIndex = Pool,
            Name = "ENA 池",
            IsEnabled = true,
            CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000),
        };

        Assert.AreEqual(Pool, lead.AccountIndex);
        Assert.IsTrue(lead.IsEnabled);
    }

    // ── helper ────────────────────────────────────────────

    private static Follower Follower(decimal capital) => new()
    {
        LeadAccountIndex = Pool,
        InitialCapital = UsdAmount.From(capital),
        Mode = CopyMode.Paper,
        IsActive = true,
        CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000),
    };

    private static PoolSnapshot Snapshot(decimal equity) => new()
    {
        AccountIndex = Pool,
        TakenAt = LighterTimestamp.FromSeconds(1_800_000_000),
        TotalAssetValue = UsdAmount.From(equity),
        Collateral = UsdAmount.From(equity),
        AvailableBalance = UsdAmount.From(equity),
        InitialMarginRequirement = UsdAmount.Zero,
        MaintenanceMarginRequirement = UsdAmount.Zero,
        Positions = [],
        Orders = [],
    };
}
