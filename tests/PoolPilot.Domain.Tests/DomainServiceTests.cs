using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Tests;

/// <summary>
/// 领域服务测试。
///
/// 断言里刻意使用**实测到的真实数值**（见 docs/LIGHTER-API-NOTES.md），
/// 这样测试同时承担两个职责：验证逻辑正确，以及验证逻辑与观测事实一致。
/// </summary>
[TestClass]
public sealed class NavAnalyzerTests
{
    private static NavSeries Series(params decimal[] prices)
    {
        List<NavPoint> points = [];
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (int i = 0; i < prices.Length; i++)
        {
            points.Add(new NavPoint
            {
                Timestamp = LighterTimestamp.FromSeconds(start.AddDays(i).ToUnixTimeSeconds()),
                SharePrice = prices[i],
            });
        }

        return new NavSeries { AccountIndex = AccountIndex.From(1), Points = points };
    }

    [TestMethod]
    public void Compute_WithMonotonicRise_ReportsZeroDrawdown()
    {
        // Arrange
        NavSeries series = Series(1.0m, 1.1m, 1.2m, 1.3m);

        // Act
        NavMetrics metrics = NavAnalyzer.Compute(series);

        // Assert
        Assert.AreEqual(0m, metrics.MaxDrawdownPercent);
        Assert.AreEqual(30m, decimal.Round(metrics.TotalReturnPercent, 2));
        Assert.AreEqual(0, metrics.LossPeriods);
    }

    [TestMethod]
    public void Compute_WithKnownPeakToTrough_ReportsExactMaxDrawdown()
    {
        // Arrange：峰值 2.00 后跌到 1.50，回撤应为 −25%
        NavSeries series = Series(1.0m, 2.0m, 1.5m, 1.8m);

        // Act
        NavMetrics metrics = NavAnalyzer.Compute(series);

        // Assert：回撤是从历史峰值算起，不是从起点
        Assert.AreEqual(-25m, decimal.Round(metrics.MaxDrawdownPercent, 4));
    }

    [TestMethod]
    public void Compute_WithRealSharePrices_ReproducesMeasuredTotalReturn()
    {
        // Arrange：实测某池 150 天的份额净值首末值
        NavSeries series = Series(0.00100244m, 0.00161564m);

        // Act
        NavMetrics metrics = NavAnalyzer.Compute(series);

        // Assert：实测累计收益 +61.17%
        Assert.AreEqual(61.17m, decimal.Round(metrics.TotalReturnPercent, 2));
    }

    [TestMethod]
    public void Compute_WithRealSharePrices_AnnualizesShortSeriesAggressively()
    {
        // Arrange：同一组首末值，但只跨 1 天 —— 年化会被放大到荒谬量级
        NavSeries series = Series(0.00100244m, 0.00161564m);

        // Act
        NavMetrics metrics = NavAnalyzer.Compute(series);

        // Assert：这正是"点估计不可信"的机制——短窗口年化必然失真，
        // 故本分析器同时输出 SpanDays 与 IsStatisticallyMeaningful 供使用者判断
        Assert.IsFalse(metrics.IsStatisticallyMeaningful, "仅 2 个点的样本不应被当作有统计意义");
        Assert.AreEqual(1, metrics.SpanDays);
    }

    [TestMethod]
    public void Compute_WithSinglePoint_ReturnsZeroMetrics()
    {
        // Arrange
        NavSeries series = Series(1.0m);

        // Act
        NavMetrics metrics = NavAnalyzer.Compute(series);

        // Assert
        Assert.AreEqual(0m, metrics.TotalReturnPercent);
        Assert.AreEqual(0m, metrics.MaxDrawdownPercent);
        Assert.IsFalse(metrics.IsStatisticallyMeaningful);
    }

    [TestMethod]
    public void Compute_WithUnsortedAndDuplicatedPoints_NormalizesBeforeCalculating()
    {
        // Arrange：上游不保证顺序，且可能重复
        DateTimeOffset day0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        List<NavPoint> points =
        [
            new() { Timestamp = LighterTimestamp.FromSeconds(day0.AddDays(2).ToUnixTimeSeconds()), SharePrice = 1.2m },
            new() { Timestamp = LighterTimestamp.FromSeconds(day0.ToUnixTimeSeconds()), SharePrice = 1.0m },
            new() { Timestamp = LighterTimestamp.FromSeconds(day0.AddDays(1).ToUnixTimeSeconds()), SharePrice = 1.1m },
            new() { Timestamp = LighterTimestamp.FromSeconds(day0.ToUnixTimeSeconds()), SharePrice = 1.0m },
        ];

        NavSeries series = new() { AccountIndex = AccountIndex.From(1), Points = points };

        // Act
        NavMetrics metrics = NavAnalyzer.Compute(series);

        // Assert：去重后 3 个点，累计 +20%
        Assert.AreEqual(3, metrics.TotalPeriods + 1);
        Assert.AreEqual(20m, decimal.Round(metrics.TotalReturnPercent, 4));
    }

    [TestMethod]
    public void Compute_WithNegativeReturn_ReportsNegativeAnnualized()
    {
        // Arrange：一个月跌 10%
        List<decimal> prices = [];
        for (int i = 0; i < 31; i++)
        {
            prices.Add(1.0m - (0.10m * i / 30m));
        }

        // Act
        NavMetrics metrics = NavAnalyzer.Compute(Series([.. prices]));

        // Assert：累计为负，年化也为负，且回撤由峰值一路延伸
        Assert.IsLessThan(0m, metrics.TotalReturnPercent);
        Assert.IsLessThan(0m, metrics.AnnualizedReturnPercent);
        Assert.IsLessThan(-9m, metrics.MaxDrawdownPercent);
    }
}

[TestClass]
public sealed class PositionDifferTests
{
    private static PoolSnapshot Snapshot(params PoolPosition[] positions) => new()
    {
        AccountIndex = AccountIndex.From(281474976552443L),
        TakenAt = LighterTimestamp.FromSeconds(1791197314L),
        TotalAssetValue = UsdAmount.From(72128.117276m),
        Collateral = UsdAmount.From(72128.117276m),
        AvailableBalance = UsdAmount.From(72128.117276m),
        InitialMarginRequirement = UsdAmount.Zero,
        MaintenanceMarginRequirement = UsdAmount.Zero,
        Positions = positions,
        Orders = [],
    };

    private static PoolPosition Position(decimal size, PositionSide side, decimal notional, decimal avgEntry) => new()
    {
        MarketId = 29,
        Symbol = "ENA",
        Side = side,
        Size = size,
        AvgEntryPrice = PriceAmount.From(avgEntry),
        Notional = UsdAmount.From(notional),
        UnrealizedPnl = UsdAmount.Zero,
        Margin = MarginFraction.FromPositionPercent("25.00"),
        OpenOrderCount = 0,
        CumulativeFunding = UsdAmount.Zero,
    };

    [TestMethod]
    public void Diff_FromFlatToPosition_ReportsOpen()
    {
        // Arrange：实测场景——池从全现金到持有 ENA 3463.6
        PoolSnapshot before = Snapshot();
        PoolSnapshot after = Snapshot(Position(3463.6m, PositionSide.Long, 862.377308m, 0.24898294m));

        // Act
        IReadOnlyList<PoolEvent> events = PositionDiffer.Diff(before, after);

        // Assert
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(PoolEventKind.Open, events[0].Kind);
        Assert.AreEqual(0m, events[0].SizeBefore);
        Assert.AreEqual(3463.6m, events[0].SizeAfter);
        Assert.IsTrue(events[0].IsExposureIncreasing);
    }

    [TestMethod]
    public void Diff_FromPositionToFlat_ReportsClose()
    {
        // Arrange：实测场景——池把 ENA 3463.6 全部平掉
        PoolSnapshot before = Snapshot(Position(3463.6m, PositionSide.Long, 862.377308m, 0.24898294m));
        PoolSnapshot after = Snapshot();

        // Act
        IReadOnlyList<PoolEvent> events = PositionDiffer.Diff(before, after);

        // Assert
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(PoolEventKind.Close, events[0].Kind);
        Assert.AreEqual(3463.6m, events[0].SizeBefore);
        Assert.AreEqual(0m, events[0].SizeAfter);
        // 平仓后方向沿用变化前的方向，让"平掉的是多单还是空单"可读
        Assert.AreEqual(PositionSide.Long, events[0].Side);
        Assert.IsFalse(events[0].IsExposureIncreasing);
    }

    [TestMethod]
    public void Diff_SameSideLargerSize_ReportsIncrease()
    {
        // Arrange
        PoolSnapshot before = Snapshot(Position(100m, PositionSide.Long, 100m, 1.0m));
        PoolSnapshot after = Snapshot(Position(150m, PositionSide.Long, 150m, 1.0m));

        // Act
        IReadOnlyList<PoolEvent> events = PositionDiffer.Diff(before, after);

        // Assert
        Assert.AreEqual(PoolEventKind.Increase, events[0].Kind);
        Assert.AreEqual(50m, events[0].SizeDelta);
    }

    [TestMethod]
    public void Diff_ShortReducedFromMinusHundredToMinusFifty_ReportsReduce()
    {
        // Arrange：空头从 −100 减到 −50，规模绝对值变小
        PoolSnapshot before = Snapshot(Position(100m, PositionSide.Short, 100m, 1.0m));
        PoolSnapshot after = Snapshot(Position(50m, PositionSide.Short, 50m, 1.0m));

        // Act
        IReadOnlyList<PoolEvent> events = PositionDiffer.Diff(before, after);

        // Assert：方向不变、绝对规模变小 → 减仓
        Assert.AreEqual(PoolEventKind.Reduce, events[0].Kind);
        Assert.AreEqual(PositionSide.Short, events[0].Side);
    }

    [TestMethod]
    public void Diff_LongFlippedToShort_ReportsFlipNotReduce()
    {
        // Arrange：这是差分最容易算错的一处——多头 100 反手成空头 50。
        // 若只看绝对规模（100 → 50）会误判成"减仓"，而实际上是平多 + 开空。
        PoolSnapshot before = Snapshot(Position(100m, PositionSide.Long, 100m, 1.0m));
        PoolSnapshot after = Snapshot(Position(50m, PositionSide.Short, 50m, 1.0m));

        // Act
        IReadOnlyList<PoolEvent> events = PositionDiffer.Diff(before, after);

        // Assert
        Assert.AreEqual(PoolEventKind.Flip, events[0].Kind);
        Assert.AreEqual(-150m, events[0].SizeDelta, "反手的规模变化应为 −100 − 50 = −150");
    }

    [TestMethod]
    public void Diff_WithoutChange_ReturnsNoEvents()
    {
        // Arrange
        PoolSnapshot before = Snapshot(Position(100m, PositionSide.Long, 100m, 1.0m));
        PoolSnapshot after = Snapshot(Position(100m, PositionSide.Long, 105m, 1.0m));

        // Act：只有名义价值随价格变化，仓位规模未变
        IReadOnlyList<PoolEvent> events = PositionDiffer.Diff(before, after);

        // Assert
        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public void Diff_AcrossMultipleMarkets_ReportsEachMarketSeparately()
    {
        // Arrange
        PoolPosition ena = Position(3463.6m, PositionSide.Long, 862m, 0.249m);
        PoolPosition sol = Position(77.773m, PositionSide.Long, 9376m, 120.233m) with
        {
            MarketId = 2,
            Symbol = "SOL",
        };

        // Act
        IReadOnlyList<PoolEvent> events = PositionDiffer.Diff(Snapshot(), Snapshot(ena, sol));

        // Assert：按市场分别产出，顺序按市场 ID 升序（不假设调用方关心的顺序）
        Assert.AreEqual(2, events.Count);
        Dictionary<string, PoolEvent> bySymbol = events.ToDictionary(e => e.Symbol);
        Assert.AreEqual(29, bySymbol["ENA"].MarketId);
        Assert.AreEqual(2, bySymbol["SOL"].MarketId);
        Assert.AreEqual(3463.6m, bySymbol["ENA"].SizeAfter);
        Assert.AreEqual(77.773m, bySymbol["SOL"].SizeAfter);
    }
}

[TestClass]
public sealed class CopySlippageCalculatorTests
{
    private static readonly AccountIndex Pool = AccountIndex.From(281474976552443L);
    private static readonly AccountIndex Counterparty = AccountIndex.From(363200L);

    /// <summary>
    /// 构造实测形态的盘口：ENA 价差 2.67 bps（7 个 tick，tick = 1e-5），深度约百万级。
    /// </summary>
    private static OrderBookSnapshot Book(decimal bestBid, decimal bestAsk, decimal sizePerLevel = 100_000m)
    {
        const decimal tick = 0.00001m;

        return new OrderBookSnapshot
        {
            MarketId = 29,
            Bids =
            [
                new OrderBookLevel { Price = PriceAmount.From(bestBid), Size = sizePerLevel },
                new OrderBookLevel { Price = PriceAmount.From(bestBid - tick), Size = sizePerLevel },
                new OrderBookLevel { Price = PriceAmount.From(bestBid - (2m * tick)), Size = sizePerLevel },
            ],
            Asks =
            [
                new OrderBookLevel { Price = PriceAmount.From(bestAsk), Size = sizePerLevel },
                new OrderBookLevel { Price = PriceAmount.From(bestAsk + tick), Size = sizePerLevel },
                new OrderBookLevel { Price = PriceAmount.From(bestAsk + (2m * tick)), Size = sizePerLevel },
            ],
            TakenAt = LighterTimestamp.FromMilliseconds(1791197314925L),
        };
    }

    [TestMethod]
    public void Compute_WithRealSpreadStructure_ReportsSpreadScaleSlippageForSmallSize()
    {
        // Arrange：实测 ENA 盘口 bid 0.26222 / ask 0.26229，价差 2.67 bps
        OrderBookSnapshot book = Book(0.26222m, 0.26229m);

        // Act：池子挂单卖成 0.26229（收 ask），我方市价卖出只能拿 bid
        CopySlippage slippage = CopySlippageCalculator.Compute(
            book,
            OrderSide.Sell,
            copySize: 3463.6m,
            poolPrice: PriceAmount.From(0.26229m));

        // Assert：漏损量级应与实测价差一致（约 2.7 bps 上下），且为正（我方更差）
        Assert.IsTrue(slippage.DepthSufficient);
        Assert.IsGreaterThan(1m, slippage.SlippageBps);
        Assert.IsLessThan(4m, slippage.SlippageBps);
    }

    [TestMethod]
    public void Compute_BuySideWithPriceRisingAbovePool_ReportsPositiveCost()
    {
        // Arrange
        OrderBookSnapshot book = Book(0.25000m, 0.25010m);

        // Act：我们吃 ask（0.25010），而池子成交在 0.25000
        CopySlippage slippage = CopySlippageCalculator.Compute(
            book,
            OrderSide.Buy,
            100m,
            PriceAmount.From(0.25000m));

        // Assert：买贵了，成本为正
        Assert.IsGreaterThan(0m, slippage.SlippageBps);
    }

    [TestMethod]
    public void Compute_SellSideWithPriceFallingBelowPool_ReportsPositiveCost()
    {
        // Arrange
        OrderBookSnapshot book = Book(0.25000m, 0.25010m);

        // Act：我们市价卖拿 bid（0.25000），而池子成交在 0.25010
        CopySlippage slippage = CopySlippageCalculator.Compute(
            book,
            OrderSide.Sell,
            100m,
            PriceAmount.From(0.25010m));

        // Assert：卖便宜了，成本为正
        Assert.IsGreaterThan(0m, slippage.SlippageBps);
    }

    [TestMethod]
    public void Compute_WhenSizeExceedsDepth_FlagsInsufficientDepthInsteadOfUnderestimating()
    {
        // Arrange：每档只有 100，要卖 1000，深度明显不够
        OrderBookSnapshot book = Book(0.26222m, 0.26229m, sizePerLevel: 100m);

        // Act
        CopySlippage slippage = CopySlippageCalculator.Compute(
            book,
            OrderSide.Sell,
            copySize: 1000m,
            poolPrice: PriceAmount.From(0.26229m));

        // Assert：必须显式报告深度不足，而不是按最后一档价格静默成交
        Assert.IsFalse(slippage.DepthSufficient);
        Assert.IsGreaterThan(0m, slippage.OurVwap.Value);
    }

    [TestMethod]
    public void Walk_ConsumesLevelsInOrder()
    {
        // Arrange
        OrderBookSnapshot book = Book(0.26222m, 0.26229m, sizePerLevel: 100m);

        // Act：买 250 需要吃掉两档（100 + 100，第三档吃半个）
        BookWalkResult walk = CopySlippageCalculator.Walk(book, OrderSide.Buy, 250m);

        // Assert
        Assert.AreEqual(3, walk.LevelsConsumed);
        Assert.IsTrue(walk.FullyFilled);
        Assert.AreEqual(250m, walk.FilledSize);
        // VWAP 必须落在前两档与第三档之间
        Assert.IsGreaterThan(0.26229m, walk.Vwap.Value);
        Assert.IsGreaterThan(walk.Vwap.Value - 0.000001m, 0.26231m);
    }

    [TestMethod]
    public void ComputeSlippageBps_WithIdenticalPrices_ReturnsZero()
    {
        // Arrange
        PriceAmount price = PriceAmount.From(0.25232m);

        // Act / Assert
        Assert.AreEqual(
            0m,
            CopySlippageCalculator.ComputeSlippageBps(price, price, OrderSide.Buy));
        Assert.AreEqual(
            0m,
            CopySlippageCalculator.ComputeSlippageBps(price, price, OrderSide.Sell));
    }

    [TestMethod]
    public void SpreadBps_MatchesMeasuredThinMarket()
    {
        // Arrange：实测 ZRO 盘口 bid 1.93488 / ask 1.93607，价差约 6.15 bps
        OrderBookSnapshot book = Book(1.93488m, 1.93607m);

        // Act / Assert
        Assert.AreEqual(6.15m, decimal.Round(book.SpreadBps, 2));
    }

    [TestMethod]
    public void Summarize_WithRealMakerExit_ReportsHighMakerShare()
    {
        // Arrange：实测该池 50 笔成交中 48 笔为挂单被动成交
        List<PoolTrade> trades = [];
        DateTimeOffset start = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

        for (int i = 0; i < 48; i++)
        {
            trades.Add(MakeTrade(i, isMakerAsk: true, TimeSpan.FromMinutes(i)));
        }

        for (int i = 0; i < 2; i++)
        {
            trades.Add(MakeTrade(100 + i, isMakerAsk: false, TimeSpan.FromMinutes(100 + i)));
        }

        // Act
        TradeComposition composition = TradeClassifier.Summarize(trades, Pool);

        // Assert：挂单占比 96%
        Assert.AreEqual(50, composition.TradeCount);
        Assert.AreEqual(48, composition.MakerCount);
        Assert.AreEqual(96m, decimal.Round(composition.MakerSharePercent.Percent, 0));
    }

    private static PoolTrade MakeTrade(int id, bool isMakerAsk, TimeSpan offset)
    {
        DateTimeOffset at = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero) + offset;

        return new PoolTrade
        {
            TradeId = id,
            MarketId = 29,
            Size = 72.0m,
            Price = PriceAmount.From(0.25232m),
            Usd = UsdAmount.From(18.17m),
            IsMakerAsk = isMakerAsk,
            AskAccountIndex = Pool,
            BidAccountIndex = Counterparty,
            Timestamp = LighterTimestamp.FromMilliseconds(at.ToUnixTimeMilliseconds()),
            TransactionTime = LighterTimestamp.FromMicroseconds(at.ToUnixTimeMilliseconds() * 1000L),
            ExecutionType = "trade",
            MakerPositionSizeBefore = 3463.6m,
            TakerPositionSizeBefore = -675716m,
        };
    }
}

[TestClass]
public sealed class SizeFeasibilityEvaluatorTests
{
    private static readonly InstrumentSpec Ena = new()
    {
        MarketId = 29,
        Symbol = "ENA",
        MinOrderNotional = 10m,
        MinOrderSize = 35m,
        SizeDecimals = 1,
        PriceDecimals = 5,
        TakerFeePercent = 0m,
        MakerFeePercent = 0m,
        MaintenanceMarginPercent = 6m,
    };

    [TestMethod]
    public void Evaluate_WithTwoHundredCapital_ReportsBelowMinimumOrder()
    {
        // Arrange：实测池的敞口占其资产 1.2%，ENA 最小下单 10 USDT
        // 200 × 1.2% = 2.4 USDT，低于 10 USDT 最小单 —— 这是物理约束
        // Act
        SizeFeasibility result = SizeFeasibilityEvaluator.Evaluate(
            capitalUsd: 200m,
            poolExposureRatio: 0.012m,
            instrument: Ena,
            referencePrice: 0.2490m);

        // Assert
        Assert.AreEqual(2.4m, decimal.Round(result.IdealFollowNotional, 4));
        Assert.IsFalse(result.CanPlaceMinimumOrder, "200 USDT 按同比例算不出 10 USDT 的最小单");
        Assert.IsGreaterThan(800m, result.RequiredCapitalForMinOrder);
    }

    [TestMethod]
    public void Evaluate_WithFifteenThousandCapital_ReportsCanPlaceOrder()
    {
        // Arrange：实测结论是需要 ≥ 约 836 USDT 才能复刻这一笔 1.2% 的敞口
        // Act
        SizeFeasibility result = SizeFeasibilityEvaluator.Evaluate(
            capitalUsd: 15_000m,
            poolExposureRatio: 0.012m,
            instrument: Ena,
            referencePrice: 0.2490m);

        // Assert
        Assert.IsTrue(result.CanPlaceMinimumOrder);
        Assert.AreEqual(180m, decimal.Round(result.IdealFollowNotional, 2));
    }

    [TestMethod]
    public void Evaluate_WhenForcedToMinimumOrder_ReportsAmplificationOverPoolExposure()
    {
        // Arrange：本金不足却硬凑到最小单，等于把敞口放大
        // Act
        SizeFeasibility result = SizeFeasibilityEvaluator.Evaluate(
            capitalUsd: 500m,
            poolExposureRatio: 0.012m,
            instrument: Ena,
            referencePrice: 0.2490m);

        // Assert：500 × 1.2% = 6 USDT，硬下 10 USDT 即放大 1.67 倍
        Assert.IsFalse(result.CanPlaceMinimumOrder);
        Assert.AreEqual(1.6667m, decimal.Round(result.AmplificationFactor, 4));
    }

    [TestMethod]
    public void Evaluate_WithSizeFloorMoreBindingThanNotionalFloor_UsesStricterOne()
    {
        // Arrange：ENA 最小数量 35 个，在 10 USDT 的价位上折算为 350 USDT，远严于金额下限
        // Act
        SizeFeasibility result = SizeFeasibilityEvaluator.Evaluate(
            capitalUsd: 1000m,
            poolExposureRatio: 0.012m,
            instrument: Ena,
            referencePrice: 10m);

        // Assert
        Assert.AreEqual(350m, result.MinOrderNotional);
        Assert.IsFalse(result.CanPlaceMinimumOrder);
    }

    [TestMethod]
    public void EvaluateAll_OrdersByRequiredCapitalAscending()
    {
        // Arrange：三个标的，最小下单额依次递增
        InstrumentSpec cheap = Ena with { MarketId = 1, Symbol = "DOGE", MinOrderNotional = 5m, MinOrderSize = 1m };
        InstrumentSpec mid = Ena with { MarketId = 2, Symbol = "SOL", MinOrderNotional = 5m, MinOrderSize = 0.01m };
        InstrumentSpec pricey = Ena with { MarketId = 3, Symbol = "ETH", MinOrderNotional = 20m, MinOrderSize = 0.001m };

        // Act
        IReadOnlyList<SizeFeasibility> results = SizeFeasibilityEvaluator.EvaluateAll(
            capitalUsd: 1000m,
            poolExposureRatio: 0.012m,
            instruments: [pricey, cheap, mid]);

        // Assert：DOGE / SOL 的最小单折算金额相同（5 USDT），ETH 最贵
        Assert.AreEqual("ETH", results[^1].Symbol);
        Assert.AreEqual(5m, results[0].MinOrderNotional);
    }
}
