using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Following;
using PoolPilot.Domain.ValueObjects;
using PoolPilot.Infrastructure.Monitoring;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 带单员运行实例的语义测试。
///
/// 这里锁住的是这次重构的**核心承诺**：观察与跟单是两件事，谁也不依赖谁。
/// 所以断言直接冲着那条承诺去——"订阅者归零后还在不在观察"、"信号会不会串到别人的订阅者那里"。
/// 这些语义如果只写在注释里，下一次改动就会悄悄破坏它，而界面上看不出任何异常。
/// </summary>
[TestClass]
public sealed class LeadTraderRuntimeTests
{
    private static readonly AccountIndex PoolA = AccountIndex.From(281474976552443L);
    private static readonly AccountIndex PoolB = AccountIndex.From(281474976552444L);

    [TestMethod]
    public void ApplySnapshot_FirstSnapshot_OnlyEstablishesBaseline()
    {
        // 没有"上一次"，就无从谈变化。把首帧当成"从 0 开始的开仓"会凭空造出一串假信号。
        LeadTraderRuntime runtime = Runtime(PoolA);

        IReadOnlyList<PoolEvent> events = runtime.ApplySnapshot(Snapshot(PoolA, Position(100m, PositionSide.Long)));

        Assert.IsEmpty(events);
        Assert.IsNotNull(runtime.LastSnapshot);
    }

    [TestMethod]
    public void ApplySnapshot_WhenPoolOpensPosition_ProducesSignal()
    {
        LeadTraderRuntime runtime = Runtime(PoolA);
        runtime.ApplySnapshot(Snapshot(PoolA));

        IReadOnlyList<PoolEvent> events = runtime.ApplySnapshot(Snapshot(PoolA, Position(100m, PositionSide.Long)));

        Assert.HasCount(1, events);
        Assert.AreEqual(PoolEventKind.Open, events[0].Kind);
        Assert.IsNotNull(runtime.LastSignalAt);
    }

    [TestMethod]
    public void ApplySnapshot_WithNoFollowers_StillProducesSignals()
    {
        // ★ 这次重构的核心承诺：**观察不依赖订阅者**。
        // 一个跟单员都没有时，池子的动作照样要被差分、被记录、被广播——
        // 否则"先观察一段时间再决定跟不跟"就永远看不到过去发生了什么。
        LeadTraderRuntime runtime = Runtime(PoolA);
        runtime.ApplySnapshot(Snapshot(PoolA));

        Assert.IsEmpty(runtime.Followers, "前提：这个带单员没有任何订阅者");

        IReadOnlyList<PoolEvent> events = runtime.ApplySnapshot(Snapshot(PoolA, Position(100m, PositionSide.Long)));

        Assert.HasCount(1, events, "没有订阅者也必须产出信号");
        Assert.AreEqual(PoolEventKind.Open, events[0].Kind);
    }

    [TestMethod]
    public void Unsubscribe_RemovesSubscriberButKeepsObserving()
    {
        // 取消跟单只是摘掉订阅者——池子照旧被观察。这正是拆分之前做不到的事：
        // 那时订阅集合由活跃跟单账户推出来，取消跟单等于顺手把观察也关了。
        LeadTraderRuntime runtime = Runtime(PoolA);
        RecordingFollower follower = new(1L, PoolA);
        runtime.Subscribe(follower);
        runtime.ApplySnapshot(Snapshot(PoolA));

        runtime.Unsubscribe(1L);

        Assert.IsEmpty(runtime.Followers);
        Assert.HasCount(
            1,
            runtime.ApplySnapshot(Snapshot(PoolA, Position(50m, PositionSide.Long))),
            "订阅者走了，观察还在");
    }

    [TestMethod]
    public async Task NotifyAsync_DeliversOnlyToSubscribersOfThatPool()
    {
        // 一个池一个实例，订阅者列表挂在实例上——所以信号不可能串到别人的跟单员那里。
        LeadTraderRuntime runtimeA = Runtime(PoolA);
        LeadTraderRuntime runtimeB = Runtime(PoolB);
        RecordingFollower followerA = new(1L, PoolA);
        RecordingFollower followerB = new(2L, PoolB);
        runtimeA.Subscribe(followerA);
        runtimeB.Subscribe(followerB);

        PoolSnapshot state = Snapshot(PoolA, Position(100m, PositionSide.Long));
        LeadSignal signal = new(
            new PoolEvent
            {
                AccountIndex = PoolA,
                Kind = PoolEventKind.Open,
                MarketId = 29,
                Symbol = "ENA",
                Side = PositionSide.Long,
                SizeBefore = 0m,
                SizeAfter = 100m,
                DetectedAt = LighterTimestamp.FromSeconds(1_800_000_000),
                ReferencePrice = PriceAmount.From(0.2447m),
            },
            "ENA 池",
            state.TotalAssetValue);

        await runtimeA.NotifyAsync(signal, state, CancellationToken.None);

        Assert.HasCount(1, followerA.Signals, "订阅了 A 的跟单员应收到 A 的信号");
        Assert.IsEmpty(followerB.Signals, "跟 B 的跟单员不该收到 A 的信号");
    }

    [TestMethod]
    public async Task NotifyAsync_DoesNotFilterByFollowerActiveState()
    {
        // 带单员**不知道也不该知道**谁在暂停：一旦它开始关心订阅者，
        // 两条生命周期就又缠在一起了。暂停的判定留在跟单员自己那里。
        LeadTraderRuntime runtime = Runtime(PoolA);
        RecordingFollower paused = new(1L, PoolA) { IsActive = false };
        runtime.Subscribe(paused);

        PoolSnapshot state = Snapshot(PoolA, Position(10m, PositionSide.Long));
        await runtime.NotifyAsync(SignalFor(PoolA, state), state, CancellationToken.None);

        Assert.HasCount(1, paused.Signals, "是否响应由跟单员自己决定，带单员照发");
    }

    [TestMethod]
    public async Task CatchUpSubscribersAsync_DeliversCurrentState()
    {
        LeadTraderRuntime runtime = Runtime(PoolA);
        RecordingFollower follower = new(1L, PoolA);
        runtime.Subscribe(follower);

        PoolSnapshot state = Snapshot(PoolA, Position(42m, PositionSide.Short));
        await runtime.CatchUpSubscribersAsync(state, CancellationToken.None);

        Assert.HasCount(1, follower.CatchUps);
        Assert.AreSame(state, follower.CatchUps[0], "对齐用的是池子**当下**的状态，不是某条历史信号");
        Assert.IsEmpty(follower.Signals, "对齐不是信号，不该记成一条");
    }

    [TestMethod]
    public void ResetBaseline_DiscardsDiffAcrossTheGap()
    {
        // 重连后以当下为新的起点：断线期间的变化无从差分，假装知道那段时间发生了什么才是错的。
        // 真实变化由跟单员按当前状态对齐补上（见 CatchUpSubscribersAsync）。
        LeadTraderRuntime runtime = Runtime(PoolA);
        runtime.ApplySnapshot(Snapshot(PoolA));
        runtime.ResetBaseline(Snapshot(PoolA, Position(500m, PositionSide.Long)));

        Assert.IsEmpty(
            runtime.ApplySnapshot(Snapshot(PoolA, Position(500m, PositionSide.Long))),
            "重置基线后，同一状态不应被判成变化");
    }

    [TestMethod]
    public void Reconfigure_Disable_KeepsInstanceAndBaseline()
    {
        // 暂停保留实例与基线：重开时不该假装池子没动过——
        // 但也不在暂停期间产出信号（是否产出由注册表看 IsEnabled 决定）。
        LeadTraderRuntime runtime = Runtime(PoolA);
        runtime.ApplySnapshot(Snapshot(PoolA, Position(100m, PositionSide.Long)));
        PoolSnapshot before = runtime.LastSnapshot!;

        runtime.Reconfigure(runtime.Lead with { IsEnabled = false });

        Assert.IsFalse(runtime.IsEnabled);
        Assert.AreSame(before, runtime.LastSnapshot, "基线必须保留");
    }

    [TestMethod]
    public void Subscribe_SameIdTwice_ReplacesInsteadOfDuplicating()
    {
        // 心跳每 15 秒同步一次库与内存，重复挂载同一个跟单员是常态操作：
        // 不去重的话同一个跟单员会被通知 N 次，同一个动作会被记 N 笔。
        LeadTraderRuntime runtime = Runtime(PoolA);
        runtime.Subscribe(new RecordingFollower(7L, PoolA));
        runtime.Subscribe(new RecordingFollower(7L, PoolA));

        Assert.HasCount(1, runtime.Followers);
    }

    // ── helpers ───────────────────────────────────────────

    private static LeadTraderRuntime Runtime(AccountIndex pool) => new(new LeadTrader
    {
        AccountIndex = pool,
        Name = $"池 {pool.Value}",
        IsEnabled = true,
        CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000),
    });

    private static LeadSignal SignalFor(AccountIndex pool, PoolSnapshot state) => new(
        new PoolEvent
        {
            AccountIndex = pool,
            Kind = PoolEventKind.Increase,
            MarketId = 29,
            Symbol = "ENA",
            Side = PositionSide.Long,
            SizeBefore = 0m,
            SizeAfter = 10m,
            DetectedAt = LighterTimestamp.FromSeconds(1_800_000_000),
            ReferencePrice = PriceAmount.From(0.2447m),
        },
        $"池 {pool.Value}",
        state.TotalAssetValue);

    private static PoolSnapshot Snapshot(AccountIndex pool, params PoolPosition[] positions) => new()
    {
        AccountIndex = pool,
        TakenAt = LighterTimestamp.FromSeconds(1_800_000_000),
        TotalAssetValue = UsdAmount.From(72_128.117276m),
        Collateral = UsdAmount.From(72_128.117276m),
        AvailableBalance = UsdAmount.From(72_128.117276m),
        InitialMarginRequirement = UsdAmount.Zero,
        MaintenanceMarginRequirement = UsdAmount.Zero,
        Positions = positions,
        Orders = [],
    };

    private static PoolPosition Position(decimal size, PositionSide side) => new()
    {
        MarketId = 29,
        Symbol = "ENA",
        Side = side,
        Size = size,
        AvgEntryPrice = PriceAmount.From(0.2447m),
        Notional = UsdAmount.From(size * 0.2447m),
        UnrealizedPnl = UsdAmount.Zero,
        Margin = MarginFraction.FromPositionPercent("25.00"),
        OpenOrderCount = 0,
        CumulativeFunding = UsdAmount.Zero,
    };

    /// <summary>记录收到了什么。跟单员的真假由"收到几条"来断言，不模拟账本。</summary>
    private sealed class RecordingFollower(long id, AccountIndex pool) : IFollower
    {
        public List<LeadSignal> Signals { get; } = [];

        public List<PoolSnapshot> CatchUps { get; } = [];

        public long Id { get; } = id;

        public AccountIndex LeadAccountIndex { get; } = pool;

        public bool IsActive { get; init; } = true;

        /// <summary>配置由构造参数如实给出——不要编一个"空的"Follower：
        /// 池索引是正数约束，编出来的 0 会在构造替身时就抛异常。</summary>
        public Follower Config => new()
        {
            Id = Id,
            LeadAccountIndex = LeadAccountIndex,
            InitialCapital = UsdAmount.From(1000m),
            Mode = CopyMode.Paper,
            IsActive = IsActive,
            CreatedAt = LighterTimestamp.FromMilliseconds(0),
        };

        public void Reconfigure(Follower updated) => throw new NotSupportedException();

        public Task OnSignalAsync(LeadSignal signal, PoolSnapshot state, CancellationToken cancellationToken = default)
        {
            Signals.Add(signal);

            return Task.CompletedTask;
        }

        public Task CatchUpAsync(PoolSnapshot state, CancellationToken cancellationToken = default)
        {
            CatchUps.Add(state);

            return Task.CompletedTask;
        }

        /// <summary>体检与对齐在这个替身里记到同一张表：测试关心的是"有没有被叫到"。</summary>
        public Task CheckDriftAsync(PoolSnapshot state, CancellationToken cancellationToken = default)
        {
            Drifts.Add(state);

            return Task.CompletedTask;
        }

        public List<PoolSnapshot> Drifts { get; } = [];
    }
}
