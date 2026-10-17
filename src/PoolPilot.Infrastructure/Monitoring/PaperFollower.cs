using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Following;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Infrastructure.Monitoring;

/// <summary>
/// 纸面跟单员：把带单员的信号变成我方的纸面成交。
///
/// 它只订阅信号，不认识 WebSocket，也不认识池子的连接——池子被不被观察是带单员的事。
/// 因此它的来去（新建、暂停、删除）都不会影响池子的观察，反之亦然。
///
/// 实现上有一处刻意的不对称：**收到信号后对账的是全部市场**，而不是信号里那一个。
/// 理由是容错——多对几个市场的代价是零（差额为 0 时既不取盘口也不写记录），
/// 而漏掉一个市场的代价是持仓长期与池子不一致，且界面上看不出来。
/// </summary>
public sealed class PaperFollower(
    IServiceScopeFactory scopeFactory,
    MarketDataCache market,
    IOptions<LeadTraderOptions> options,
    ILogger<PaperFollower> logger,
    IFollowerLedgerActivity activity,
    Follower config) : IFollower
{
    /// <summary>读取流水时的上限，需足够大以完整重放仓位账。</summary>
    private const int LedgerReplayLimit = 5000;

    private Follower _config = config;
    private FollowerRuntimeState? _state;

    public long Id => _config.Id;

    public AccountIndex LeadAccountIndex => _config.LeadAccountIndex;

    public bool IsActive => _config.IsActive;

    /// <summary>当前配置。注册表拿它比较"这条配置变了没有"，据此决定要不要立刻重新对齐。</summary>
    public Follower Config => _config;

    /// <summary>配置变了（改本金、启停）。本金变了会让内存状态整体重载，见 <see cref="FollowerRuntimeState"/>。</summary>
    public void Reconfigure(Follower updated) => _config = updated;

    public async Task OnSignalAsync(
        LeadSignal signal,
        PoolSnapshot state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        logger.LogInformation(
            "跟单员 {Id} 收到信号：{Pool} 在 {Symbol} {Kind}（{Before} → {After}）",
            Id,
            signal.PoolName,
            signal.Event.Symbol,
            signal.Kind,
            signal.Event.SizeBefore,
            signal.Event.SizeAfter);

        await ReconcileAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public Task CatchUpAsync(PoolSnapshot state, CancellationToken cancellationToken = default) =>
        ReconcileAsync(state, cancellationToken);

    /// <summary>
    /// 纸面的体检就是照旧对账。**与实盘刻意不同**：
    /// 纸面回答的是"一直跟着它，账会变成多少"，所以它要始终贴着池子（漏跟会让收益率失真）；
    /// 而实盘回答的是"我该在什么时候真的下单"，那里池子没动就不该有单子出来。
    /// 两种语义的差别写在 <see cref="IFollower.CheckDriftAsync"/> 上。
    /// </summary>
    public Task CheckDriftAsync(PoolSnapshot state, CancellationToken cancellationToken = default) =>
        ReconcileAsync(state, cancellationToken);

    /// <summary>
    /// 对账：把"我现在应该持多少"与"我实际持多少"对齐，并把差额做成一笔纸面成交。
    ///
    /// 设计要点：
    /// <list type="bullet">
    ///   <item>**范围是所有市场**（池子持仓 ∪ 我方持仓），不只信号里那一个。原因见类说明。</item>
    ///   <item>首次对账时我方为空仓而池子已有仓位，差额正好等于等比目标——跟单员由此完成建仓，
    ///     且成交价取**当时的真实盘口**。这也是真实跟单服务的行为：它按当前市价跟上
    ///     领单人的现有仓位，而不是假装拿到了领单人的开仓价。</item>
    ///   <item>**跳过记录要按"池仓 + 原因"去重**。否则本金不足时，每条周期性对账都会
    ///     为同一个跟不动的目标再记一条跳过，把台账淹掉。去重后这条记录的含义才准确：
    ///     "池子在这个市场动了，而我这一次没跟上"。</item>
    ///   <item>差额为 0 时不产生任何记录（<see cref="PaperActionKind.None"/>），
    ///     所以正常的周期性对账不会写脏数据。</item>
    /// </list>
    /// </summary>
    private async Task ReconcileAsync(PoolSnapshot current, CancellationToken cancellationToken)
    {
        if (!_config.IsActive)
        {
            return;
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;

        IFollowerLedgerRepository ledgerRepo = provider.GetRequiredService<IFollowerLedgerRepository>();

        if (_state is null || _state.Capital != _config.InitialCapital.Value)
        {
            IReadOnlyList<PaperFill> history = await ledgerRepo
                .GetFillsAsync(Id, LedgerReplayLimit, cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<PaperEquityPoint> curve = await ledgerRepo
                .GetEquityCurveAsync(Id, 1, cancellationToken)
                .ConfigureAwait(false);

            _state = FollowerRuntimeState.Load(
                _config.InitialCapital.Value,
                history,
                curve.Count > 0 ? curve[^1].TakenAt.Utc : null);
        }

        FollowerRuntimeState state = _state;
        PaperBook book = state.Book;
        Dictionary<int, InstrumentSpec> instruments = await market
            .InstrumentsAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, PoolPosition> poolByMarket = current.ActivePositions.ToDictionary(p => p.MarketId);
        List<PaperFill> newFills = [];

        foreach (int marketId in poolByMarket.Keys.Union(book.Positions.Keys).Order())
        {
            if (!instruments.TryGetValue(marketId, out InstrumentSpec? instrument))
            {
                logger.LogDebug("跳过市场 {MarketId} 的纸面对账：取不到合约规格", marketId);
                continue;
            }

            poolByMarket.TryGetValue(marketId, out PoolPosition? poolPosition);
            book.Positions.TryGetValue(marketId, out PaperPosition? ours);

            SignedSize poolSize = poolPosition is null
                ? SignedSize.Zero
                : SignedSize.FromSide(poolPosition.Size, poolPosition.Side);
            SignedSize oursSize = ours?.Size ?? SignedSize.Zero;

            // 先用**不需要网络**的方式判断有没有事要做：目标仓位只依赖池仓、本金与池子权益。
            // 差额为 0 是常态（池子不动、我方已对齐），此时既不该取盘口也不该产生任何记录。
            SignedSize target = PaperLedger.TargetFor(
                poolSize,
                PaperLedger.ScaleFor(_config.InitialCapital, current.TotalAssetValue),
                instrument.SizeDecimals);

            if (target.Value - oursSize.Value == 0m)
            {
                continue;
            }

            // 差额非 0 才取盘口，而且**强制取最新的**：接下来算出的成交价就是我们要记的账，
            // 用缓存里的旧盘口等于按一个过去的市场成交。
            OrderBookSnapshot orderBook = await market
                .BookAsync(marketId, cancellationToken, forceRefresh: true)
                .ConfigureAwait(false);

            PaperDecision decision = PaperLedger.Decide(new PaperDecisionRequest
            {
                PoolPositionAfter = poolSize,
                OurPositionBefore = oursSize,
                OurEntryVwapBefore = ours?.EntryVwap ?? PriceAmount.Zero,
                PoolEquity = current.TotalAssetValue,
                Capital = _config.InitialCapital,
                Instrument = instrument,
                Book = orderBook,
                FeePercent = instrument.TakerFeePercent,
            });

            if (decision.Kind == PaperActionKind.None)
            {
                continue;
            }

            if (decision is { Kind: PaperActionKind.Skip, SkipReason: { } reason }
                && !state.RecordedSkips.Add(PaperLedger.SkipKey(marketId, reason, poolSize.Value)))
            {
                continue;
            }

            newFills.Add(new PaperFill
            {
                FollowerId = Id,
                MarketId = marketId,
                Symbol = instrument.Symbol,
                Side = decision.Kind == PaperActionKind.Fill ? decision.Side : null,
                PoolPositionAfter = poolSize,
                PoolEquity = current.TotalAssetValue,
                TargetPosition = decision.TargetPosition,
                PositionBefore = oursSize,
                PositionAfter = decision.PositionAfter,
                Size = decision.Size,
                Price = decision.Price,
                Notional = decision.Notional,
                DepthSufficient = decision.DepthSufficient,
                Fee = decision.Fee,
                RealizedPnl = decision.RealizedPnl,
                SkipReason = decision.SkipReason,

                // 记"我方动作发生的时刻"，不是池子快照的时刻。这条流水记的是**我方的动作**；
                // 用快照时刻会让多次动作挤在同一个时间戳上（REST 账户数据未变时快照时刻不变），
                // 那样按时间排序就退化成按插入顺序，看不出动作之间的真实间隔。
                ObservedAt = LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            });
        }

        if (newFills.Count > 0)
        {
            await ledgerRepo.AppendFillsAsync(newFills, cancellationToken).ConfigureAwait(false);
            state = state.With(newFills);
            _state = state;

            logger.LogInformation(
                "跟单员 {Id}：成交 {Filled} 笔、跳过 {Skipped} 笔",
                Id,
                newFills.Count(f => f.IsFilled),
                newFills.Count(f => !f.IsFilled));
        }

        // 权益点按"事件驱动"记录：有动作才记。若按心跳记，一天会写几千个点，
        // 而其中绝大多数与前一帧完全相同，曲线没变得更细，只是更难读。
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool hasPosition = state.Book.OpenPositions.Count > 0;
        bool intervalElapsed = state.LastEquityPointAt is null
            || now - state.LastEquityPointAt.Value >= TimeSpan.FromSeconds(options.Value.EquityPointIntervalSeconds);

        if (newFills.Count == 0 && (!hasPosition || !intervalElapsed))
        {
            // 无成交、且（没有持仓 或 还没到记点间隔）→ 不记。
            // 这样曲线既不会因心跳而膨胀，也不会在空仓时留下一串毫无变化的点。
            return;
        }

        IReadOnlyDictionary<int, PriceAmount> marks = await market
            .MarksAsync(state.Book.OpenPositions, cancellationToken)
            .ConfigureAwait(false);

        PaperValuation valuation = PaperLedger.Value(state.Book, _config.InitialCapital, marks);

        await ledgerRepo.AppendEquityPointAsync(
            new PaperEquityPoint
            {
                FollowerId = Id,
                Equity = valuation.Equity,
                RealizedPnl = valuation.RealizedPnl,
                UnrealizedPnl = valuation.UnrealizedPnl,
                Fees = valuation.Fees,
                GrossNotional = valuation.GrossNotional,

                // 墙钟时刻，不是池子快照时刻。用快照时刻会让同一推送窗口内的所有点挤在
                // 同一个时间戳上，曲线变成一根竖线，也看不出点与点的真实间隔。
                TakenAt = LighterTimestamp.FromMilliseconds(now.ToUnixTimeMilliseconds()),
            },
            cancellationToken).ConfigureAwait(false);

        // 流水与权益点都已落库（有成交时上面刚写过流水）：立刻让公开页重装配。
        // 行情变化走 market_stats 的逐帧补丁；"新流水/新权益点出现了"不走那条路，
        // 因为流水与权益曲线只能从库里重放出来。
        activity.NotifyFollowerChanged(Id);

        _state = state with { LastEquityPointAt = now };
    }
}

/// <summary>
/// 跟单员的运行时状态：流水、跳过去重表、以及由流水重放出的仓位账。
///
/// 缓存的理由：对账挂在推送与心跳上（15 秒一次），每次都从库里读全部流水再重放是纯浪费。
/// 失效只有一个条件——**本金变了**。改本金会清空历史（见 UpdateFollowerUseCase），
/// 因此缓存里的本金与配置不一致时就整体重载。其余情况本地追加即可，不必回读。
///
/// 为什么把跳过去重表也放在这里：本金不足时，同一个"跟不动的目标"每 15 秒就会被重新算一次，
/// 不去重的话台账会被同一条跳过淹没。去重的键是"市场 + 原因 + 当时的池仓"——
/// 池仓一变就说明池子真的动了，那条跳过才有记录价值。
/// </summary>
internal sealed record FollowerRuntimeState(
    decimal Capital,
    List<PaperFill> Fills,
    HashSet<(int MarketId, PaperSkipReason Reason, decimal PoolPosition)> RecordedSkips,
    PaperBook Book,
    DateTimeOffset? LastEquityPointAt)
{
    public static FollowerRuntimeState Load(
        decimal capital,
        IReadOnlyList<PaperFill> history,
        DateTimeOffset? lastEquityPointAt)
    {
        HashSet<(int MarketId, PaperSkipReason Reason, decimal PoolPosition)> skips = [];

        foreach (PaperFill fill in history)
        {
            if (PaperLedger.SkipKeyOf(fill) is { } key)
            {
                skips.Add(key);
            }
        }

        return new FollowerRuntimeState(
            capital,
            [.. history],
            skips,
            PaperLedger.Replay(history),
            lastEquityPointAt);
    }

    /// <summary>追加新流水并重放仓位账。只在内存里做，不回读数据库。</summary>
    public FollowerRuntimeState With(IReadOnlyList<PaperFill> appended)
    {
        Fills.AddRange(appended);

        return this with { Book = PaperLedger.Replay(Fills) };
    }
}
