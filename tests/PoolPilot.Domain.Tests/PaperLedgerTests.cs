using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Tests;

/// <summary>
/// 纸面跟单账簿测试。
///
/// 这是全项目**最容易算错**的一段：仓位方向、开仓均价、已实现盈亏、手续费四件事互相牵制，
/// 多空要对称，加仓/减仓/平仓/反手要各走各的分支。所以这里用实测数值
/// （ENA 最小下单 35 枚 / 10 USDT、池子权益 72100、池仓 7931.8）覆盖全部分支，
/// 并把"本金不足时到底会怎样"当作一等测试对象——那正是使用者最需要的答案。
///
/// 职责划分（据此决定断言写在哪一层，避免自证其明）：
/// <list type="bullet">
///   <item><see cref="PaperLedger.Decide"/> 负责**已实现盈亏的规则**与约束判定 → 在本层断言。</item>
///   <item><see cref="PaperLedger.Replay"/> 负责**累计与均价重建** → 用显式给定的已实现值断言累计。</item>
///   <item><see cref="PaperLedger.Value"/> 负责**盯市合成** → 断言权益公式。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class PaperLedgerTests
{
    /// <summary>跟单员的持久化主键。流水按跟单员归属，不再借池索引当身份。</summary>
    private const long Follower = 1L;

    private const decimal BestBid = 0.24465m;
    private const decimal BestAsk = 0.24470m;

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

    /// <summary>实测形态盘口：价差约 2 bps，深度足够。</summary>
    private static OrderBookSnapshot Book(decimal bestBid = BestBid, decimal bestAsk = BestAsk, decimal sizePerLevel = 100_000m) => new()
    {
        MarketId = 29,
        Bids = [new OrderBookLevel { Price = PriceAmount.From(bestBid), Size = sizePerLevel }],
        Asks = [new OrderBookLevel { Price = PriceAmount.From(bestAsk), Size = sizePerLevel }],
        TakenAt = LighterTimestamp.FromSeconds(1_800_000_000),
    };

    private static PaperDecisionRequest Request(
        decimal capital,
        decimal poolEquity,
        decimal poolPosition,
        decimal ourPosition = 0m,
        decimal ourEntryVwap = 0m,
        OrderBookSnapshot? book = null,
        InstrumentSpec? instrument = null) => new()
    {
        PoolPositionAfter = SignedSize.From(poolPosition),
        OurPositionBefore = SignedSize.From(ourPosition),
        OurEntryVwapBefore = PriceAmount.From(ourEntryVwap),
        PoolEquity = UsdAmount.From(poolEquity),
        Capital = UsdAmount.From(capital),
        Instrument = instrument ?? Ena,
        Book = book ?? Book(),
        FeePercent = 0m,
    };

    // ── 规模比例 ──────────────────────────────────────────

    [TestMethod]
    public void ScaleFor_WithCapitalOverEquity_ReturnsRatio()
    {
        Assert.AreEqual(0.1m, PaperLedger.ScaleFor(UsdAmount.From(1000m), UsdAmount.From(10_000m)));
    }

    [TestMethod]
    public void ScaleFor_WithZeroEquity_ReturnsZeroNotInfinity()
    {
        // 池子权益为 0 时若返回比值会得到无穷大仓位 —— 必须退化成"不跟"
        Assert.AreEqual(0m, PaperLedger.ScaleFor(UsdAmount.From(1000m), UsdAmount.From(0m)));
    }

    [TestMethod]
    public void TargetFor_WithPoolLong_ScalesProportionally()
    {
        SignedSize target = PaperLedger.TargetFor(SignedSize.From(7931.8m), 0.01m);

        Assert.AreEqual(79.318m, target.Value);
        Assert.AreEqual(PositionSide.Long, target.Side);
    }

    [TestMethod]
    public void TargetFor_WithPoolShort_KeepsNegativeSign()
    {
        SignedSize target = PaperLedger.TargetFor(SignedSize.From(-1000m), 0.05m);

        Assert.AreEqual(-50m, target.Value);
        Assert.AreEqual(PositionSide.Short, target.Side);
    }

    // ── 决策：约束（本金不足是首要答案）───────────────────

    [TestMethod]
    public void Decide_WithTwoHundredCapital_SkipsBecauseBelowMinimumNotional()
    {
        // 复现实测结论：200 USDT 本金 / 池子权益 72100 → 比例 0.2774%
        // 池仓 7931.8 枚 → 我方目标 22.0 枚 → 约 5.4 USDT，低于 ENA 的 10 USDT 最小下单额
        PaperDecision decision = PaperLedger.Decide(Request(capital: 200m, poolEquity: 72_100m, poolPosition: 7931.8m));

        Assert.AreEqual(PaperActionKind.Skip, decision.Kind);
        Assert.AreEqual(PaperSkipReason.BelowMinimumNotional, decision.SkipReason);
        Assert.AreEqual(0m, decision.Size);
    }

    [TestMethod]
    public void Decide_WithSufficientCapital_FillsProportionally()
    {
        // 本金 5000 / 72100 → 7931.8 × 5000/72100 = 550.0555 枚，量化后 550.1 → 约 134.6 USDT，远超最小单
        PaperDecision decision = PaperLedger.Decide(Request(capital: 5000m, poolEquity: 72_100m, poolPosition: 7931.8m));

        Assert.AreEqual(PaperActionKind.Fill, decision.Kind);
        Assert.AreEqual(OrderSide.Buy, decision.Side);
        // 目标 550.0555 量化到 ENA 的 0.1 步长 → 550.1
        Assert.AreEqual(550.1m, decision.Size);
        Assert.AreEqual(BestAsk, decision.Price.Value, "买单按卖一价成交");
        Assert.AreEqual(550.1m, decision.PositionAfter.Value);
    }

    [TestMethod]
    public void Decide_WhenTargetEqualsCurrentPosition_DoesNothing()
    {
        PaperDecision decision = PaperLedger.Decide(
            Request(capital: 5000m, poolEquity: 72_100m, poolPosition: 7931.8m, ourPosition: 550.1m));

        Assert.AreEqual(PaperActionKind.None, decision.Kind);
    }

    [TestMethod]
    public void Decide_WhenDeltaBelowMinimumSize_ReportsSizeWhenSizeIsTheBindingLimit()
    {
        // 哪个约束是硬墙取决于价格：仅当 price > 最小金额/最小数量 时最小数量才先卡住。
        // 这里构造一个满足该条件的标的（价格 1.0、最小数量 100、最小金额 10 → 阈值 0.1）。
        InstrumentSpec instrument = Ena with { MinOrderSize = 100m, MinOrderNotional = 10m };
        OrderBookSnapshot book = new()
        {
            MarketId = 29,
            Bids = [new OrderBookLevel { Price = PriceAmount.From(1.0m), Size = 100_000m }],
            Asks = [new OrderBookLevel { Price = PriceAmount.From(1.0m), Size = 100_000m }],
            TakenAt = LighterTimestamp.FromSeconds(1_800_000_000),
        };

        // 池仓 100 × 比例 0.1% = 0.1 枚 → 量化后 0.1，低于最小数量 100 且低于最小金额 10
        PaperDecision decision = PaperLedger.Decide(Request(
            capital: 100m,
            poolEquity: 100_000m,
            poolPosition: 100m,
            book: book,
            instrument: instrument));

        Assert.AreEqual(PaperActionKind.Skip, decision.Kind);
        Assert.AreEqual(PaperSkipReason.BelowMinimumSize, decision.SkipReason);
    }

    [TestMethod]
    public void Decide_ForEnaAtCurrentPrice_ReportsNotionalAsTheBindingLimit()
    {
        // 实测发现：两个约束同时失败时，对现价的 ENA **永远是金额先卡住**。
        // 判据是 price > 最小金额/最小数量，即 0.2447 > 10/35 = 0.2857 —— 不成立。
        // 这条断言把结论固化：若哪天 ENA 涨过 0.2857，这里会失败并提醒重新审视这个口径。
        PaperDecision decision = PaperLedger.Decide(Request(capital: 200m, poolEquity: 72_100m, poolPosition: 7931.8m));

        Assert.AreEqual(PaperSkipReason.BelowMinimumNotional, decision.SkipReason);
        Assert.IsLessThan(
            Ena.MinOrderNotional / Ena.MinOrderSize,
            0.2447m,
            "ENA 现价低于 10/35 的门槛，故最小金额约束先绑定");
    }

    [TestMethod]
    public void Decide_WithEmptyBook_SkipsForNoLiquidity()
    {
        OrderBookSnapshot empty = new()
        {
            MarketId = 29,
            Bids = [],
            Asks = [],
            TakenAt = LighterTimestamp.FromSeconds(1_800_000_000),
        };

        PaperDecision decision = PaperLedger.Decide(
            Request(capital: 5000m, poolEquity: 72_100m, poolPosition: 7931.8m, book: empty));

        Assert.AreEqual(PaperActionKind.Skip, decision.Kind);
        Assert.AreEqual(PaperSkipReason.NoLiquidity, decision.SkipReason);
    }

    [TestMethod]
    public void Decide_WithZeroPoolEquity_DoesNothing()
    {
        PaperDecision decision = PaperLedger.Decide(Request(capital: 1000m, poolEquity: 0m, poolPosition: 500m));

        Assert.AreEqual(PaperActionKind.None, decision.Kind);
    }

    [TestMethod]
    public void Decide_WithPartialDepth_FillsOnlyWhatBookHolds()
    {
        // 盘口只有 50 枚，目标要买 550 枚 → 只能成交 50，并标记深度不足
        PaperDecision decision = PaperLedger.Decide(Request(
            capital: 5000m,
            poolEquity: 72_100m,
            poolPosition: 7931.8m,
            book: Book(sizePerLevel: 50m)));

        Assert.AreEqual(PaperActionKind.Fill, decision.Kind);
        Assert.AreEqual(50m, decision.Size, "只成交盘口吃得下的部分");
        Assert.IsFalse(decision.DepthSufficient);
        Assert.AreEqual(50m, decision.PositionAfter.Value, "仓位只按真实成交推进");
    }

    // ── 决策：已实现盈亏规则（多空对称）───────────────────

    [TestMethod]
    public void Decide_WhenPoolReduces_RealizesProportionallyAndSellsAtBid()
    {
        // 我方持 550.1 @ 0.2447；池子把仓位砍半 → 目标 275.0 → 卖出 275.1
        // 已实现 = (0.24465 − 0.2447) × 275.1 ≈ −0.0138（几乎打平，微亏）
        PaperDecision decision = PaperLedger.Decide(Request(
            capital: 5000m,
            poolEquity: 72_100m,
            poolPosition: 3965.9m,
            ourPosition: 550.1m,
            ourEntryVwap: 0.2447m));

        Assert.AreEqual(PaperActionKind.Fill, decision.Kind);
        Assert.AreEqual(OrderSide.Sell, decision.Side);
        Assert.AreEqual(BestBid, decision.Price.Value, "卖单按买一价成交");
        Assert.AreEqual(-0.0138m, decision.RealizedPnl.Value, 0.001m, "多头卖出价略低于均价 → 微亏");
        Assert.IsTrue(decision.RealizedPnl.Value < 0m);
    }

    [TestMethod]
    public void Decide_WhenPoolClosesLong_RealizesFullProfit()
    {
        // 我方持 550.1 @ 0.24；池子清仓 → 全部卖出 @ 0.24465 → 已实现 (0.24465 − 0.24) × 550.1 ≈ 2.558
        PaperDecision decision = PaperLedger.Decide(Request(
            capital: 5000m,
            poolEquity: 72_100m,
            poolPosition: 0m,
            ourPosition: 550.1m,
            ourEntryVwap: 0.24m));

        Assert.AreEqual(PaperActionKind.Fill, decision.Kind);
        Assert.AreEqual(OrderSide.Sell, decision.Side);
        Assert.AreEqual(2.56m, decision.RealizedPnl.Value, 0.01m);
        Assert.AreEqual(0m, decision.PositionAfter.Value, "池子清仓后我方也应为空仓");
    }

    [TestMethod]
    public void Decide_WhenPoolClosesShort_RealizesProfitWhenPriceFell()
    {
        // 我方空头 −1000 @ 0.26；池子清仓 → 买回 @ 0.2447（价格下跌）→ 已实现 (0.2447 − 0.26) × 1000 × (−1) = 15.3
        PaperDecision decision = PaperLedger.Decide(Request(
            capital: 5000m,
            poolEquity: 72_100m,
            poolPosition: 0m,
            ourPosition: -1000m,
            ourEntryVwap: 0.26m));

        Assert.AreEqual(OrderSide.Buy, decision.Side, "平空头要买回");
        Assert.AreEqual(15.3m, decision.RealizedPnl.Value, 0.01m, "空头在价格下跌时盈利");
        Assert.IsTrue(decision.RealizedPnl.Value > 0m);
    }

    // ── 流水重放：累计与均价重建 ──────────────────────────

    [TestMethod]
    public void Replay_WithSingleOpen_SetsEntryToFillPrice()
    {
        PaperBook book = PaperLedger.Replay([Fill(size: 100m, price: 0.25m, before: 0m, after: 100m)]);

        PaperPosition position = book.Positions[29];
        Assert.AreEqual(100m, position.Size.Value);
        Assert.AreEqual(0.25m, position.EntryVwap.Value);
        Assert.AreEqual(0m, position.RealizedPnl.Value);
    }

    [TestMethod]
    public void Replay_WithTwoAdds_WeightsEntryBySize()
    {
        // 100 @ 0.25 + 300 @ 0.30 → (25 + 90) / 400 = 0.2875
        PaperBook book = PaperLedger.Replay(
        [
            Fill(size: 100m, price: 0.25m, before: 0m, after: 100m, hour: 0),
            Fill(size: 300m, price: 0.30m, before: 100m, after: 400m, hour: 1),
        ]);

        PaperPosition position = book.Positions[29];
        Assert.AreEqual(400m, position.Size.Value);
        Assert.AreEqual(0.2875m, position.EntryVwap.Value);
    }

    [TestMethod]
    public void Replay_WithPartialReduce_KeepsEntryAndAccumulatesRealized()
    {
        PaperBook book = PaperLedger.Replay(
        [
            Fill(size: 400m, price: 0.2875m, before: 0m, after: 400m, hour: 0),
            Fill(size: 100m, price: 0.35m, before: 400m, after: 300m, realized: 6.25m, hour: 1),
        ]);

        PaperPosition position = book.Positions[29];
        Assert.AreEqual(300m, position.Size.Value);
        Assert.AreEqual(0.2875m, position.EntryVwap.Value, "减仓不改开仓均价");
        Assert.AreEqual(6.25m, position.RealizedPnl.Value);
    }

    [TestMethod]
    public void Replay_WithFullClose_ZeroesEntry()
    {
        PaperBook book = PaperLedger.Replay(
        [
            Fill(size: 100m, price: 0.25m, before: 0m, after: 100m, hour: 0),
            Fill(size: 100m, price: 0.40m, before: 100m, after: 0m, realized: 15m, hour: 1),
        ]);

        PaperPosition position = book.Positions[29];
        Assert.IsTrue(position.IsFlat);
        Assert.AreEqual(0m, position.EntryVwap.Value, "平仓后均价必须归零，否则残留值会污染下次开仓");
        Assert.AreEqual(15m, position.RealizedPnl.Value);
    }

    [TestMethod]
    public void Replay_WithShortPosition_KeepsNegativeEntrySemantics()
    {
        // 空头：−100 @ 0.30 开、−200 加 → 均价 (0.30×100 + 0.32×100)/200 = 0.31
        PaperBook book = PaperLedger.Replay(
        [
            Fill(size: 100m, price: 0.30m, before: 0m, after: -100m, side: OrderSide.Sell, hour: 0),
            Fill(size: 100m, price: 0.32m, before: -100m, after: -200m, side: OrderSide.Sell, hour: 1),
        ]);

        PaperPosition position = book.Positions[29];
        Assert.AreEqual(-200m, position.Size.Value);
        Assert.AreEqual(PositionSide.Short, position.Side);
        Assert.AreEqual(0.31m, position.EntryVwap.Value, "空头加仓同样是数量加权");
    }

    [TestMethod]
    public void Replay_WithFlip_OpensNewSideAtFillPrice()
    {
        // +100 @ 0.25 → 卖 250 → 平掉 100 并反手做空 150，新均价 0.30
        PaperBook book = PaperLedger.Replay(
            [Fill(size: 250m, price: 0.30m, before: 100m, after: -150m, realized: 5m, hour: 1)]);

        PaperPosition position = book.Positions[29];
        Assert.AreEqual(-150m, position.Size.Value, "反手后方向必须翻转");
        Assert.AreEqual(0.30m, position.EntryVwap.Value, "新仓均价是本次成交价，不能沿用旧均价");
        Assert.AreEqual(5m, position.RealizedPnl.Value);
    }

    [TestMethod]
    public void Replay_WithSkippedFills_DoesNotChangePositionOrFillCount()
    {
        PaperBook book = PaperLedger.Replay(
        [
            Fill(size: 100m, price: 0.25m, before: 0m, after: 100m, hour: 0),
            Skipped(),
        ]);

        Assert.AreEqual(100m, book.Positions[29].Size.Value, "被跳过的信号不改变仓位");
        Assert.AreEqual(1, book.Positions[29].FillCount, "跳过不计入成交笔数");
    }

    [TestMethod]
    public void Replay_AccumulatesFeesAcrossFills()
    {
        PaperBook book = PaperLedger.Replay(
        [
            Fill(size: 100m, price: 0.25m, before: 0m, after: 100m, fee: 0.6m, hour: 0),
            Fill(size: 100m, price: 0.30m, before: 100m, after: 0m, fee: 0.9m, hour: 1),
        ]);

        Assert.AreEqual(1.5m, book.Fees.Value);
    }

    [TestMethod]
    public void Replay_WithSourceMarginPercent_KeepsItAcrossReducesAndClearsOnClose()
    {
        PaperBook open = PaperLedger.Replay(
        [
            Fill(size: 100m, price: 0.25m, before: 0m, after: 100m, margin: 10m, hour: 0),
            Fill(size: 40m, price: 0.30m, before: 100m, after: 60m, margin: 20m, hour: 1),
        ]);

        Assert.AreEqual(10m, open.Positions[29].InitialMarginPercent, "减仓不改建仓保证金率");

        PaperBook closed = PaperLedger.Replay(
        [
            Fill(size: 100m, price: 0.25m, before: 0m, after: 100m, margin: 10m, hour: 0),
            Fill(size: 100m, price: 0.30m, before: 100m, after: 0m, margin: 20m, hour: 1),
        ]);

        Assert.IsNull(closed.Positions[29].InitialMarginPercent, "平仓后保证金率必须清掉");
    }

    [TestMethod]
    public void Replay_WithDifferentAddMargins_WeightsByNotional()
    {
        // 100 @ 0.25 × 10% + 100 @ 0.30 × 20% → (25×10 + 30×20) / 55 = 15.4545%
        PaperBook book = PaperLedger.Replay(
        [
            Fill(size: 100m, price: 0.25m, before: 0m, after: 100m, margin: 10m, hour: 0),
            Fill(size: 100m, price: 0.30m, before: 100m, after: 200m, margin: 20m, hour: 1),
        ]);

        Assert.AreEqual(15.4545454545m, book.Positions[29].InitialMarginPercent!.Value, 0.0000000001m);
    }

    // ── 盯市估值 ──────────────────────────────────────────

    [TestMethod]
    public void Value_WithLongAndRisingMark_ReportsUnrealizedGain()
    {
        PaperBook book = PaperLedger.Replay([Fill(size: 100m, price: 0.25m, before: 0m, after: 100m)]);
        PaperValuation valuation = PaperLedger.Value(
            book,
            UsdAmount.From(1000m),
            new Dictionary<int, PriceAmount> { [29] = PriceAmount.From(0.30m) });

        Assert.AreEqual(5m, valuation.UnrealizedPnl.Value);
        Assert.AreEqual(1005m, valuation.Equity.Value);
        Assert.AreEqual(0.5m, valuation.ReturnPercent.Percent);
        Assert.AreEqual(30m, valuation.GrossNotional.Value);
    }

    [TestMethod]
    public void Value_WithShortAndFallingMark_ReportsUnrealizedGain()
    {
        PaperBook book = PaperLedger.Replay(
            [Fill(size: 100m, price: 0.30m, before: 0m, after: -100m, side: OrderSide.Sell)]);
        PaperValuation valuation = PaperLedger.Value(
            book,
            UsdAmount.From(1000m),
            new Dictionary<int, PriceAmount> { [29] = PriceAmount.From(0.25m) });

        Assert.AreEqual(5m, valuation.UnrealizedPnl.Value, "空头在价格下跌时未实现盈利");
    }

    [TestMethod]
    public void Value_CombinesRealizedUnrealizedAndFees()
    {
        // 第一轮实现 +15（含手续费 2）；第二轮未实现 +5 → 权益 = 1000 + 15 + 5 − 2
        PaperBook book = PaperLedger.Replay(
        [
            Fill(size: 100m, price: 0.25m, before: 0m, after: 100m, hour: 0),
            Fill(size: 100m, price: 0.40m, before: 100m, after: 0m, realized: 15m, fee: 2m, hour: 1),
            Fill(size: 100m, price: 0.25m, before: 0m, after: 100m, hour: 2),
        ]);

        PaperValuation valuation = PaperLedger.Value(
            book,
            UsdAmount.From(1000m),
            new Dictionary<int, PriceAmount> { [29] = PriceAmount.From(0.30m) });

        Assert.AreEqual(15m, valuation.RealizedPnl.Value);
        Assert.AreEqual(5m, valuation.UnrealizedPnl.Value);
        Assert.AreEqual(2m, valuation.Fees.Value);
        Assert.AreEqual(1018m, valuation.Equity.Value);
    }

    [TestMethod]
    public void Value_WithoutMark_FallsBackToEntrySoUnrealizedIsZero()
    {
        PaperBook book = PaperLedger.Replay([Fill(size: 100m, price: 0.25m, before: 0m, after: 100m)]);
        PaperValuation valuation = PaperLedger.Value(book, UsdAmount.From(1000m), new Dictionary<int, PriceAmount>());

        Assert.AreEqual(0m, valuation.UnrealizedPnl.Value);
        Assert.AreEqual(1000m, valuation.Equity.Value);
    }

    // ── 跳过去重的幂等键 ──────────────────────────────────

    [TestMethod]
    public void SkipKeyOf_ForSkipRecord_MatchesKeyBuiltFromPoolPosition()
    {
        // 锁住一个已经踩过的坑：写入前的去重判定与重启后重建去重表，
        // 曾经各用一套键（一边"我方仓位"、一边"池仓"），于是键永远匹配不上，
        // 每次服务重启都会补写一条重复的跳过记录。这里断言两者必然相等。
        PaperFill skip = Skipped();

        (int MarketId, PaperSkipReason Reason, decimal PoolPosition)? fromFill = PaperLedger.SkipKeyOf(skip);
        var fromPoolPosition = PaperLedger.SkipKey(
            29,
            PaperSkipReason.BelowMinimumNotional,
            skip.PoolPositionAfter.Value);

        Assert.AreEqual(fromPoolPosition, fromFill, "两种构造方式必须得到同一个键");
    }

    [TestMethod]
    public void SkipKeyOf_WhenPoolPositionChanges_ProducesDifferentKey()
    {
        // 池仓变了说明池子真的动了，那条跳过是新的一次"没跟上"，必须能记下来
        var before = PaperLedger.SkipKey(29, PaperSkipReason.BelowMinimumNotional, 1000m);
        var after = PaperLedger.SkipKey(29, PaperSkipReason.BelowMinimumNotional, 2000m);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void SkipKey_IsInsensitiveToDecimalScale()
    {
        // 实测故障：上游给 134481（scale 0），SQLite 的 TEXT 往返把它变成 134481.0（scale 1），
        // 而 decimal.ToString() 保留 scale，于是"实时算的键"与"重启后从库重建的键"不相等，
        // 每次重启都补写一条重复的跳过记录（实测 10 条同键记录）。
        var fromUpstream = PaperLedger.SkipKey(45, PaperSkipReason.BelowMinimumNotional, 134481m);
        var fromDatabase = PaperLedger.SkipKey(45, PaperSkipReason.BelowMinimumNotional, 134481.0m);
        var withExtraZeros = PaperLedger.SkipKey(45, PaperSkipReason.BelowMinimumNotional, 134481.000m);

        Assert.AreEqual(fromUpstream, fromDatabase, "0 位与 1 位小数必须得到同一个键");
        Assert.AreEqual(fromUpstream, withExtraZeros, "多余的尾随零不该影响键");
    }

    [TestMethod]
    public void SkipKey_StillDistinguishesDifferentPositions()
    {
        // 规范化不能把"不同的池仓"误合并成同一个键——那会让真实的第二次没跟上被吞掉
        var first = PaperLedger.SkipKey(45, PaperSkipReason.BelowMinimumNotional, 134481m);
        var second = PaperLedger.SkipKey(45, PaperSkipReason.BelowMinimumNotional, 134482m);

        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public void SkipKeyOf_ForFilledRecord_ReturnsNull()
    {
        Assert.IsNull(PaperLedger.SkipKeyOf(Fill(size: 100m, price: 0.25m, before: 0m, after: 100m)));
    }

    // ── 流水构造 helper ───────────────────────────────────

    private static PaperFill Fill(
        decimal size,
        decimal price,
        decimal before,
        decimal after,
        OrderSide side = OrderSide.Buy,
        decimal realized = 0m,
        decimal fee = 0m,
        int hour = 0,
        decimal? margin = null) => new()
    {
        FollowerId = 1L,
        MarketId = 29,
        Symbol = "ENA",
        Side = side,
        PoolPositionAfter = SignedSize.From(after * 100m),
        PoolEquity = UsdAmount.From(72_100m),
        TargetPosition = SignedSize.From(after),
        PositionBefore = SignedSize.From(before),
        PositionAfter = SignedSize.From(after),
        Size = size,
        Price = PriceAmount.From(price),
        Notional = UsdAmount.From(size * price),
        DepthSufficient = true,
        Fee = UsdAmount.From(fee),
        RealizedPnl = UsdAmount.From(realized),
        InitialMarginPercent = margin,
        SkipReason = null,
        ObservedAt = LighterTimestamp.FromSeconds(1_800_000_000 + (hour * 3600)),
    };

    private static PaperFill Skipped() => new()
    {
        FollowerId = 1L,
        MarketId = 29,
        Symbol = "ENA",
        Side = OrderSide.Buy,
        PoolPositionAfter = SignedSize.From(10_000m),
        PoolEquity = UsdAmount.From(72_100m),
        TargetPosition = SignedSize.From(200m),
        PositionBefore = SignedSize.From(100m),
        PositionAfter = SignedSize.From(100m),
        Size = 0m,
        Price = PriceAmount.Zero,
        Notional = UsdAmount.Zero,
        DepthSufficient = true,
        Fee = UsdAmount.Zero,
        RealizedPnl = UsdAmount.Zero,
        SkipReason = PaperSkipReason.BelowMinimumNotional,
        ObservedAt = LighterTimestamp.FromSeconds(1_800_003_600),
    };
}
