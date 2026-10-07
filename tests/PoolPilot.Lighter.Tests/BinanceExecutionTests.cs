using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Enums;
using PoolPilot.Infrastructure.Brokers;

namespace PoolPilot.Lighter.Tests;

/// <summary>
/// 币安执行侧的纯逻辑测试（不联网）。
///
/// 三件事在这里被钉住，它们都是"错了会真金白银出错"的地方：
/// <list type="number">
///   <item>**签名**：签的是待签串本身，一个字符都不能差。参考值由 Python 的 hmac 独立算出，
///     不是拿自己的实现当标准。</item>
///   <item>**数量量化**：向下取整而不是四舍五入——多下一点就是超出信号承诺的名义额。</item>
///   <item>**意图到参数的映射**：双向持仓模式下"平多"必须是 sell+LONG。
///     从买卖方向推导 positionSide 会把平多写成开空，那是一个会开错方向的错误。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class BinanceExecutionTests
{
    // 与 Python 独立算出的参考值一致：
    // hmac.new(b'test-secret-please-change', b'symbol=ENAUSDT&side=BUY&type=MARKET&quantity=102&timestamp=1791275454000', sha256).hexdigest()
    private const string ReferenceQuery =
        "symbol=ENAUSDT&side=BUY&type=MARKET&quantity=102&timestamp=1791275454000";

    private const string ReferenceSignature =
        "c587055d75280a6585a08006f96d1cda8a354c930544421532d140e50bdd8fb6";

    [TestMethod]
    public void Sign_MatchesAnIndependentlyComputedVector()
    {
        Assert.AreEqual(
            ReferenceSignature,
            BinanceSignature.Sign("test-secret-please-change", ReferenceQuery));
    }

    [TestMethod]
    public void Sign_IsCaseSensitiveOverTheQueryString()
    {
        // 币安的参数是区分大小写的（side=BUY 与 side=buy 是两回事），
        // 签名自然也必须跟着区分——把待签串统一小写化会得到"看起来对但永远被拒"的签名
        Assert.AreNotEqual(
            BinanceSignature.Sign("test-secret-please-change", ReferenceQuery),
            BinanceSignature.Sign("test-secret-please-change", ReferenceQuery.ToLowerInvariant()));
    }

    [TestMethod]
    public void Text_FormatsDecimalsWithInvariantCulture()
    {
        // 用当前文化格式化会把小数点变成逗号（部分区域设置），签名随之失效——
        // 这类错误只在特定区域设置的机器上出现，极难复现
        Assert.AreEqual("0.001", BinanceSignature.Text(0.001m));
        Assert.AreEqual("102", BinanceSignature.Text(102m));
    }

    [TestMethod]
    public void Quantize_TruncatesInsteadOfRounding()
    {
        // 0.0079 ÷ 0.001 = 7.9 → 必须是 7（不是 8）：多下一点就超出了这一笔的名额
        BrokerInstrument instrument = Instrument(stepSize: 0.001m, minQuantity: 0.001m, minNotional: 20m);

        Assert.AreEqual(0.007m, instrument.Quantize(0.0079m));
        Assert.AreEqual(0.007m, instrument.Quantize(0.007m));
    }

    [TestMethod]
    public void Quantize_WithManyDecimals_KeepsExchangePrecision()
    {
        BrokerInstrument instrument = Instrument(stepSize: 0.1m, minQuantity: 0.1m, minNotional: 5m);

        // 102.34 → 102.3：不能因为 decimal 的精度还留着 102.34 就把它发出去
        Assert.AreEqual(102.3m, instrument.Quantize(102.34m));
    }

    [TestMethod]
    public void Quantize_BelowOneStep_BecomesZero()
    {
        // 落不到一个步长就是 0——调用方据此判"下不出去"，而不是发一个 0 数量的单让交易所拒
        BrokerInstrument instrument = Instrument(stepSize: 0.001m, minQuantity: 0.001m, minNotional: 50m);

        Assert.AreEqual(0m, instrument.Quantize(0.0004m));
    }

    [TestMethod]
    public void FromIntent_MapsAllFourCombinationsWithoutDerivingFromSide()
    {
        // 这四行是"平多不能写成开空"的唯一保证。特别是 close_long 与 open_short：
        // 两者都是 sell，只差 positionSide —— 从方向推导的那一版会把它们写成同一个
        Assert.AreEqual((OrderSide.Buy, "LONG"), BrokerOrderRequest.FromIntent(BrokerOrderIntent.OpenLong));
        Assert.AreEqual((OrderSide.Sell, "LONG"), BrokerOrderRequest.FromIntent(BrokerOrderIntent.CloseLong));
        Assert.AreEqual((OrderSide.Sell, "SHORT"), BrokerOrderRequest.FromIntent(BrokerOrderIntent.OpenShort));
        Assert.AreEqual((OrderSide.Buy, "SHORT"), BrokerOrderRequest.FromIntent(BrokerOrderIntent.CloseShort));
    }

    [TestMethod]
    public void BrokerAccount_GrossNotional_SumsAbsoluteValues()
    {
        // 多空不抵消：总持仓名义额衡量的是"暴露规模"，一笔多一笔空不是零暴露
        BrokerAccount account = new(
            WalletBalance: 5m,
            AvailableBalance: 5m,
            UnrealizedPnl: 0m,
            IsHedgeMode: true,
            Positions:
            [
                Position("ENAUSDT", "LONG", 25m),
                Position("BTCUSDT", "SHORT", -30m),
            ]);

        Assert.AreEqual(55m, account.GrossNotional);
    }

    private static BrokerPosition Position(string symbol, string side, decimal notional) => new(
        Symbol: symbol,
        PositionSide: side,
        Size: notional > 0 ? 1m : -1m,
        EntryPrice: Math.Abs(notional),
        MarkPrice: Math.Abs(notional),
        Leverage: 10,
        LiquidationPrice: 0m,
        UnrealizedPnl: 0m,
        Notional: notional);

    private static BrokerInstrument Instrument(decimal stepSize, decimal minQuantity, decimal minNotional) => new(
        Symbol: "ENAUSDT",
        QuantityPrecision: 3,
        PricePrecision: 4,
        StepSize: stepSize,
        MinQuantity: minQuantity,
        MinNotional: minNotional);
}
