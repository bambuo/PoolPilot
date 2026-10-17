using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.Mapping;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 读取一个跟单员的当前状态。
///
/// 一次请求给出完整账户会计：权益、盈亏、持仓、跟踪偏差、流水、权益曲线。
/// 其中**盯市价是现取的**——用当前盘口中间价重算未实现盈亏；
/// 而权益曲线的历史点则是当时记下的（见 <see cref="PaperEquityPoint"/> 的说明）。
/// 两者不可混用：拿当前价格重算历史权益，等价于假设"持仓一直是现在这个价"。
///
/// 池子侧数据（快照、符号表）按**订阅的带单员**取，而不是按跟单员 Id：
/// 跟单员自己的 Id 只是一个身份，池子是谁由订阅关系决定。
/// </summary>
public sealed class GetFollowerUseCase(
    IFollowerRepository followers,
    IFollowerLedgerRepository ledger,
    ILeadTraderRepository leads,
    ILighterPoolsClient client,
    IMarkPrices marks,
    IExchangeConfigRepository exchanges)
{
    /// <summary>读取时最多重放多少条流水。超出部分会让仓位账不完整，故取一个足够大的上限。</summary>
    private const int FillReplayLimit = 5000;

    /// <summary>权益曲线最多返回多少个点。</summary>
    private const int CurveLimit = 2000;

    public async Task<FollowerDto?> ExecuteAsync(
        long followerId,
        CancellationToken cancellationToken = default)
    {
        Follower? config = await followers.GetAsync(followerId, cancellationToken).ConfigureAwait(false);

        if (config is null)
        {
            return null;
        }

        AccountIndex pool = config.LeadAccountIndex;
        LeadTrader? lead = await leads.GetAsync(pool, cancellationToken).ConfigureAwait(false);

        // 池快照、符号表、流水、曲线互不依赖，并行取
        Task<PoolSnapshot> snapshotTask = client.FetchSnapshotAsync(pool, cancellationToken);
        Task<IReadOnlyList<InstrumentSpec>> instrumentsTask = client.FetchInstrumentsAsync(cancellationToken);
        Task<IReadOnlyList<PaperFill>> fillsTask = ledger.GetFillsAsync(followerId, FillReplayLimit, cancellationToken);
        Task<IReadOnlyList<PaperEquityPoint>> curveTask = ledger.GetEquityCurveAsync(followerId, CurveLimit, cancellationToken);

        await Task.WhenAll(snapshotTask, instrumentsTask, fillsTask, curveTask).ConfigureAwait(false);

        PoolSnapshot snapshot = await snapshotTask.ConfigureAwait(false);
        IReadOnlyList<InstrumentSpec> instruments = await instrumentsTask.ConfigureAwait(false);
        IReadOnlyList<PaperFill> fills = await fillsTask.ConfigureAwait(false);
        IReadOnlyList<PaperEquityPoint> curve = await curveTask.ConfigureAwait(false);

        PaperBook book = PaperLedger.Replay(fills);
        decimal scale = PaperLedger.ScaleFor(config.InitialCapital, snapshot.TotalAssetValue);

        // 盯市价走 IMarkPrices 而不是现取盘口：一个跟单员可能持有十几个市场，
        // 逐个现取要十几秒（每个市场都要新建连接等快照），页面在那段时间里是空的。
        // 取不到的市场会出现在 Unavailable 里，界面据此显示「不可得」而不是 0。
        MarkPrices resolvedMarks = await marks
            .ResolveAsync([.. book.OpenPositions.Select(p => p.MarketId)], cancellationToken)
            .ConfigureAwait(false);

        PaperValuation valuation = PaperLedger.Value(book, config.InitialCapital, resolvedMarks.Prices);

        Dictionary<int, InstrumentSpec> instrumentById = instruments.ToDictionary(i => i.MarketId);
        Dictionary<int, PoolPosition> poolByMarket = snapshot.ActivePositions.ToDictionary(p => p.MarketId);

        List<PaperPositionDto> positions = [];

        // 以"池子持仓"与"我方持仓"的**并集**为准。只遍历我方会漏掉"池子刚开仓但我们被跳过"的市场，
        // 而那恰恰是最该被看到的跟踪偏差——本金不足时它是主要形态。
        foreach (int marketId in poolByMarket.Keys.Union(book.Positions.Keys).Order())
        {
            poolByMarket.TryGetValue(marketId, out PoolPosition? poolPosition);
            book.Positions.TryGetValue(marketId, out PaperPosition? ourPosition);

            positions.Add(BuildPosition(marketId, poolPosition, ourPosition, instrumentById, scale, resolvedMarks));
        }

        ApplyLiquidationPrices(positions, instrumentById, valuation.Equity.Value);

        Dictionary<int, decimal> poolMarginByMarket = poolByMarket
            .Where(entry => entry.Value.Margin is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value.Margin!.Percent);

        return new FollowerDto
        {
            Id = config.Id,
            LeadAccountIndex = pool.Value,
            LeadName = lead?.Name ?? $"池 {pool.Value}",
            LeadIsEnabled = lead?.IsEnabled ?? false,
            Mode = FollowerMapper.Mode(config.Mode),
            ExchangeConfigId = config.ExchangeConfigId,
            ExchangeName = config.ExchangeConfigId is { } configId
                ? (await exchanges.GetAsync(configId, cancellationToken).ConfigureAwait(false))?.DisplayName
                : null,
            InitialCapital = config.InitialCapital.Value,
            IsActive = config.IsActive,
            CreatedAt = config.CreatedAt.Utc,
            Equity = valuation.Equity.Value,
            RealizedPnl = valuation.RealizedPnl.Value,
            UnrealizedPnl = valuation.UnrealizedPnl.Value,
            Fees = valuation.Fees.Value,
            ReturnPercent = valuation.ReturnPercent.Percent,
            GrossNotional = valuation.GrossNotional.Value,
            PoolEquity = snapshot.TotalAssetValue.Value,
            Scale = scale,
            FillCount = fills.Count(f => f.IsFilled),
            SkipCount = fills.Count(f => !f.IsFilled),
            MarksUnavailable = resolvedMarks.Unavailable,
            Positions = positions,
            Fills =
            [
                .. fills
                    .OrderByDescending(f => f.ObservedAt.Utc)
                    .Select(fill => ToFillDto(fill, poolMarginByMarket)),
            ],
            Curve = [.. curve.Select(ToCurveDto)],
        };
    }

    private static PaperPositionDto BuildPosition(
        int marketId,
        PoolPosition? poolPosition,
        PaperPosition? ourPosition,
        Dictionary<int, InstrumentSpec> instruments,
        decimal scale,
        MarkPrices resolvedMarks)
    {
        decimal poolSize = poolPosition is null
            ? 0m
            : SignedSize.FromSide(poolPosition.Size, poolPosition.Side).Value;

        SignedSize target = instruments.TryGetValue(marketId, out InstrumentSpec? instrument)
            ? PaperLedger.TargetFor(SignedSize.From(poolSize), scale, instrument.SizeDecimals)
            : PaperLedger.TargetFor(SignedSize.From(poolSize), scale);

        decimal ourSize = ourPosition?.Size.Value ?? 0m;
        bool hasMark = resolvedMarks.Prices.TryGetValue(marketId, out PriceAmount? mark);
        PriceAmount effectiveMark = hasMark && mark is not null ? mark : ourPosition?.EntryVwap ?? PriceAmount.Zero;
        decimal deviation = ourSize - target.Value;
        decimal notional = ourPosition?.NotionalAt(effectiveMark).Value ?? 0m;
        decimal unrealized = ourPosition?.UnrealizedPnlAt(effectiveMark).Value ?? 0m;

        // 新流水记录建仓时源池的保证金率；旧流水没有记录时退回源池当前值。
        decimal? marginPercent = ourPosition?.InitialMarginPercent ?? poolPosition?.Margin?.Percent;
        decimal? marginAmount = marginPercent is > 0m && notional > 0m
            ? notional * marginPercent.Value / 100m
            : null;

        return new PaperPositionDto
        {
            MarketId = marketId,
            Symbol = ourPosition?.Symbol ?? poolPosition?.Symbol ?? $"market-{marketId}",
            Side = (ourPosition?.Size ?? SignedSize.Zero).Side switch
            {
                PositionSide.Long => "long",
                PositionSide.Short => "short",
                _ => null,
            },
            Size = ourSize,
            EntryVwap = ourPosition?.EntryVwap.Value ?? 0m,
            MarkPrice = effectiveMark.Value,
            HasMark = hasMark,
            Notional = notional,
            UnrealizedPnl = unrealized,
            RealizedPnl = ourPosition?.RealizedPnl.Value ?? 0m,
            InitialMarginPercent = marginPercent,
            MarginAmount = marginAmount,
            MaintenanceMarginPercent = instrument is null ? 0m : instrument.MaintenanceMarginPercent,
            UnrealizedPnlPercent = hasMark && marginAmount is > 0m
                ? unrealized / marginAmount.Value * 100m
                : null,
            LiquidationPrice = null,
            LiquidationDistancePercent = null,
            PoolSize = poolSize,
            TargetSize = target.Value,
            TrackingDeviation = deviation,
            TrackingDeviationPercent = target.Value == 0m
                ? 0m
                : deviation / Math.Abs(target.Value) * 100m,
        };
    }

    /// <summary>
    /// 用账户级全仓口径补清算价：只让这一笔价格变动，其他仓位的维持保证金占用不变。
    /// 任一在仓市场缺少维持保证金率时整批不计算——少算占用会把清算价推远，是危险的方向。
    /// </summary>
    private static void ApplyLiquidationPrices(
        List<PaperPositionDto> positions,
        Dictionary<int, InstrumentSpec> instruments,
        decimal equity)
    {
        List<PaperPositionDto> open = [.. positions.Where(p => p.Size != 0m && p.HasMark)];

        if (open.Any(p => !instruments.TryGetValue(p.MarketId, out InstrumentSpec? spec)
            || spec.MaintenanceMarginPercent <= 0m))
        {
            return;
        }

        List<PaperMarginPosition> marginPositions =
        [
            .. open.Select(p => new PaperMarginPosition
            {
                MarketId = p.MarketId,
                SignedSize = p.Size,
                EntryPrice = p.EntryVwap,
                MarkPrice = p.MarkPrice,
                Notional = p.Notional,
                UnrealizedPnl = p.UnrealizedPnl,
                MaintenanceMarginPercent = instruments[p.MarketId].MaintenanceMarginPercent,
            }),
        ];

        for (int i = 0; i < positions.Count; i++)
        {
            PaperPositionDto position = positions[i];

            if (position.Size == 0m || !position.HasMark)
            {
                continue;
            }

            PaperMarginPosition target = marginPositions.First(p => p.MarketId == position.MarketId);
            decimal? liquidation = PaperMarginAnalyzer.LiquidationPrice(target, marginPositions, equity);

            positions[i] = position with
            {
                LiquidationPrice = liquidation,
                LiquidationDistancePercent = liquidation is null || position.MarkPrice <= 0m
                    ? null
                    : (liquidation.Value - position.MarkPrice) / position.MarkPrice * 100m,
            };
        }
    }

    private static PaperFillDto ToFillDto(
        PaperFill fill,
        Dictionary<int, decimal> poolMarginByMarket) => new()
    {
        Id = fill.Id,
        MarketId = fill.MarketId,
        Symbol = fill.Symbol,
        Side = fill.Side switch
        {
            OrderSide.Buy => "buy",
            OrderSide.Sell => "sell",
            _ => null,
        },
        Size = fill.Size,
        Price = fill.Price.Value,
        Notional = fill.Notional.Value,
        Fee = fill.Fee.Value,
        RealizedPnl = fill.RealizedPnl.Value,
        InitialMarginPercent = fill.InitialMarginPercent
            ?? (poolMarginByMarket.TryGetValue(fill.MarketId, out decimal poolMargin) ? poolMargin : null),
        IsReducing = fill.IsReducing,
        IsFilled = fill.IsFilled,
        SkipReason = fill.SkipReason switch
        {
            PaperSkipReason.BelowMinimumSize => "below_minimum_size",
            PaperSkipReason.BelowMinimumNotional => "below_minimum_notional",
            PaperSkipReason.NoLiquidity => "no_liquidity",
            _ => null,
        },
        DepthSufficient = fill.DepthSufficient,
        PoolPositionAfter = fill.PoolPositionAfter.Value,
        PositionBefore = fill.PositionBefore.Value,
        PositionAfter = fill.PositionAfter.Value,
        TargetPosition = fill.TargetPosition.Value,
        ObservedAt = fill.ObservedAt.Utc,
    };

    private static PaperEquityPointDto ToCurveDto(PaperEquityPoint point) => new()
    {
        At = point.TakenAt.Utc,
        Equity = point.Equity.Value,
        RealizedPnl = point.RealizedPnl.Value,
        UnrealizedPnl = point.UnrealizedPnl.Value,
        GrossNotional = point.GrossNotional.Value,
    };
}
