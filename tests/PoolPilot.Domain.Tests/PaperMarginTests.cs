using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Tests;

/// <summary>
/// 纸面保证金口径：ROI 的分母是保证金，清算价按全仓账户权益 = 维持保证金求解。
/// 这些数值会直接显示给使用者，算错一个口径比少一列更糟。
/// </summary>
[TestClass]
public sealed class PaperMarginTests
{
    [TestMethod]
    public void Position_MarginAndReturnOnMargin_UseNotionalTimesSourceMargin()
    {
        PaperPosition position = new()
        {
            MarketId = 1,
            Symbol = "ENA",
            Size = SignedSize.From(100m),
            EntryVwap = PriceAmount.From(0.25m),
            RealizedPnl = UsdAmount.Zero,
            Fees = UsdAmount.Zero,
            FillCount = 1,
            InitialMarginPercent = 10m,
        };

        PriceAmount mark = PriceAmount.From(0.30m);

        Assert.AreEqual(30m, position.NotionalAt(mark).Value);
        Assert.AreEqual(3m, position.MarginAmountAt(mark)!.Value, "保证金 = 名义价值 × 10%");
        Assert.AreEqual(166.6666666667m, position.UnrealizedPnlPercentAt(mark)!.Percent, 0.0000000001m);
    }

    [TestMethod]
    public void Position_WithoutMarginPercent_ReturnsNullInsteadOfUsingNotional()
    {
        PaperPosition position = new()
        {
            MarketId = 1,
            Symbol = "ENA",
            Size = SignedSize.From(100m),
            EntryVwap = PriceAmount.From(0.25m),
            RealizedPnl = UsdAmount.Zero,
            Fees = UsdAmount.Zero,
            FillCount = 1,
        };

        Assert.IsNull(position.MarginAmountAt(PriceAmount.From(0.30m)));
        Assert.IsNull(position.UnrealizedPnlPercentAt(PriceAmount.From(0.30m)));
    }

    [TestMethod]
    public void Liquidation_ForLong_SolvesEquityEqualsMaintenance()
    {
        PaperMarginPosition position = Position(size: 100m, entry: 100m, mark: 100m, unrealized: 0m, maintenance: 6m);

        decimal? liquidation = PaperMarginAnalyzer.LiquidationPrice(position, [position], equity: 1_000m);

        // (0 − 1000 + 0 + 100×100) / (100 − 6) = 95.74468...
        Assert.AreEqual(95.7446808511m, liquidation!.Value, 0.0000000001m);
        Assert.IsTrue(liquidation < 100m, "多头清算价必须低于现价");
    }

    [TestMethod]
    public void Liquidation_ForShort_IsAboveMark()
    {
        PaperMarginPosition position = Position(size: -100m, entry: 100m, mark: 100m, unrealized: 0m, maintenance: 6m);

        decimal? liquidation = PaperMarginAnalyzer.LiquidationPrice(position, [position], equity: 1_000m);

        Assert.AreEqual(103.7735849057m, liquidation!.Value, 0.0000000001m);
        Assert.IsTrue(liquidation > 100m, "空头清算价必须高于现价");
    }

    [TestMethod]
    public void Liquidation_WithAmpleEquity_ReturnsNullBecauseNoPositiveSolution()
    {
        PaperMarginPosition position = Position(size: 100m, entry: 100m, mark: 100m, unrealized: 0m, maintenance: 6m);

        Assert.IsNull(PaperMarginAnalyzer.LiquidationPrice(position, [position], equity: 100_000m));
    }

    [TestMethod]
    public void Liquidation_WithAnotherPositionCountsItsMaintenanceRequirement()
    {
        PaperMarginPosition target = Position(size: 100m, entry: 100m, mark: 100m, unrealized: 0m, maintenance: 6m);
        PaperMarginPosition other = Position(size: 100m, entry: 100m, mark: 100m, unrealized: 0m, maintenance: 12m, marketId: 2);

        decimal? liquidation = PaperMarginAnalyzer.LiquidationPrice(target, [target, other], equity: 1_000m);

        // 其他仓位的维持保证金 = 10000 × 12% = 1200
        // (1200 − 1000 + 0 + 10000) / 94 = 108.5106...
        // 这个结果高于现价，说明目标多头在当前权益下已经先被其他仓位的占用拖到清算，
        // 而多头正清算价必须在现价之下，因此不可给出。
        Assert.IsNull(liquidation);

        // 权益足够时，其他仓位的占用只把清算价推近现价，仍应低于现价
        decimal? funded = PaperMarginAnalyzer.LiquidationPrice(target, [target, other], equity: 3_000m);
        Assert.AreEqual(87.2340425532m, funded!.Value, 0.0000000001m);
    }

    [TestMethod]
    public void Liquidation_WhenAnotherPositionLacksMaintenanceRate_ReturnsNull()
    {
        PaperMarginPosition target = Position(size: 100m, entry: 100m, mark: 100m, unrealized: 0m, maintenance: 6m);
        PaperMarginPosition other = Position(size: 10m, entry: 100m, mark: 100m, unrealized: 0m, maintenance: 0m, marketId: 2);

        Assert.IsNull(PaperMarginAnalyzer.LiquidationPrice(target, [target, other], equity: 1_000m));
    }

    private static PaperMarginPosition Position(
        decimal size,
        decimal entry,
        decimal mark,
        decimal unrealized,
        decimal maintenance,
        int marketId = 1) => new()
    {
        MarketId = marketId,
        SignedSize = size,
        EntryPrice = entry,
        MarkPrice = mark,
        Notional = Math.Abs(size) * mark,
        UnrealizedPnl = unrealized,
        MaintenanceMarginPercent = maintenance,
    };
}
