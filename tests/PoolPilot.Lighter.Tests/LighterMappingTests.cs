using PoolPilot.Infrastructure.Lighter;

using LibraryPosition = Lighter.Net.Objects.Models.LighterPosition;
using LibraryPerpSymbol = Lighter.Net.Objects.Models.LighterSymbolPerpDetails;

namespace PoolPilot.Lighter.Tests;

/// <summary>
/// 库模型 → 领域实体的**映射测试（离线）**。
///
/// 迁移前这段是"从原始 JSON 解析"，测试用实测载荷的形状；现在 JSON 由库解析，
/// 本项目剩下的判断是**字段挑哪个、刻度怎么算**——那才是错一次就静默算错钱的地方
/// （把最小下单量与最小名义额换位、把"正在变动中"的 0% 保证金当成真实设定）。
/// 这几条曾被上游真实数据打脸过，所以一条都不能只靠 Live 门禁。
/// </summary>
[TestClass]
public sealed class LighterMappingTests
{
    [TestMethod]
    public void MapPosition_WithZeroImf_ReturnsNullMarginInsteadOfInventingLeverage()
    {
        // 实测：仓位变动瞬间 initial_margin_fraction 为 "0.00"。
        // 0% 保证金率 = 无穷杠杆，不可能是真实设定——它表达的是"此刻没有有效值"
        PoolPilot.Domain.Entities.PoolPosition position = LighterClientExtensions.MapPosition(
            new LibraryPosition { MarketId = 29, Symbol = "ENA", InitialMarginFraction = 0m, Position = 3404.2m });

        Assert.IsNull(position.Margin, "imf=0 应映射为 null（无有效值），而不是编造杠杆");
    }

    [TestMethod]
    public void MapPosition_WithNormalImf_KeepsMarginAndComputesLeverage()
    {
        // 实测形态：25.00 → 保证金率 25% → 4 倍杠杆
        PoolPilot.Domain.Entities.PoolPosition position = LighterClientExtensions.MapPosition(
            new LibraryPosition
            {
                MarketId = 29,
                Symbol = "ENA",
                PositionSide = global::Lighter.Net.Enums.PositionSide.Long,
                Position = 3404.2m,
                AverageEntryPrice = 0.255170m,
                PositionValue = 868.66m,
                UnrealizedPnl = -12.54m,
                InitialMarginFraction = 25m,
                LiquidationPrice = 0m,
            });

        Assert.IsNotNull(position.Margin);
        Assert.AreEqual(4m, position.Margin!.Leverage);
        Assert.AreEqual(PoolPilot.Domain.Enums.PositionSide.Long, position.Side);
        Assert.AreEqual(3404.2m, position.Size, "数量取绝对值，方向看 PositionSide");
        Assert.IsNull(position.LiquidationPrice, "强平价 0 表示不适用（全仓多头理论值 ≤ 0），不该当成真实价格");
    }

    [TestMethod]
    public void MapPosition_ShortSide_IsShortAndSizeIsAbsolute()
    {
        // 上游给的 position 是带符号的；方向在 positionSide 里。
        // 混着用会把空头算成多头——平仓方向会反
        PoolPilot.Domain.Entities.PoolPosition position = LighterClientExtensions.MapPosition(
            new LibraryPosition
            {
                MarketId = 10,
                Symbol = "NEAR",
                PositionSide = global::Lighter.Net.Enums.PositionSide.Short,
                Position = -1.7m,
                InitialMarginFraction = 20m,
            });

        Assert.AreEqual(PoolPilot.Domain.Enums.PositionSide.Short, position.Side);
        Assert.AreEqual(1.7m, position.Size);
    }

    [TestMethod]
    public void MapInstrument_KeepsMinimumsFeesAndPrecisionsDistinct()
    {
        // 最小**量**与最小**名义额**是两个字段，换位不会报错、只会让量化与门槛判断都错
        var symbol = new LibraryPerpSymbol
        {
            MarketId = 10,
            Symbol = "NEAR",
            MinBaseQuantity = 2.0m,
            MinQuoteQuantity = 20.0m,
            SupportedQuantityDecimals = 1,
            SupportedPriceDecimals = 3,
            TakerFee = 0.0004m,
            MakerFee = 0.0002m,
            MaintenanceMarginFraction = 600m,
        };

        PoolPilot.Domain.Entities.InstrumentSpec spec = LighterClientExtensions.MapInstrument(symbol);

        Assert.AreEqual(2.0m, spec.MinOrderSize);
        Assert.AreEqual(20.0m, spec.MinOrderNotional);
        Assert.AreEqual(1, spec.SizeDecimals);
        Assert.AreEqual(3, spec.PriceDecimals);

        // 费率是百分数原值（"0.0004" 表示 0.0004%），不做缩放
        Assert.AreEqual(0.0004m, spec.TakerFeePercent);
        Assert.AreEqual(0.0002m, spec.MakerFeePercent);

        // orderBookDetails 的整数刻度 ÷10000 成比例，再 ×100 成百分数：600 → 6%
        Assert.AreEqual(6m, spec.MaintenanceMarginPercent);
    }
}
