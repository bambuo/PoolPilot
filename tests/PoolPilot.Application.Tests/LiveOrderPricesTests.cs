using PoolPilot.Application.Abstractions;
using PoolPilot.Application.UseCases;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 实盘流水一行上"开仓价 / 平仓价"的取值规则。
///
/// 这两个数是复盘一行时最先看的：开仓单看"我买在哪"，平仓行把两个价放一起、
/// 单位盈亏（平仓价 − 开仓价）直接可算。所以取值规则必须钉死——
/// 尤其是回执缺成交均价时的兜底顺序（参考价兜底，而不是留空或写 0）。
/// </summary>
[TestClass]
public sealed class LiveOrderPricesTests
{
    [TestMethod]
    public void Resolve_OpenOrder_ItsFillIsTheEntryPrice()
    {
        (decimal entry, decimal? close) = LiveOrderPrices.Resolve(
            BrokerOrderIntent.OpenLong,
            positionEntryPrice: 0m,
            referencePrice: 1364.28m,
            fillPrice: 1364.55m);

        Assert.AreEqual(1364.55m, entry, "开仓单的开仓价就是它自己的成交均价");
        Assert.IsNull(close, "开仓单没有平仓价");
    }

    [TestMethod]
    public void Resolve_CloseOrder_PairsTheRoundTripInOneRow()
    {
        // 平多：开仓价是被平仓位的成本线（下单前从账户读的），平仓价是本单的成交均价
        (decimal entry, decimal? close) = LiveOrderPrices.Resolve(
            BrokerOrderIntent.CloseLong,
            positionEntryPrice: 1366.28m,
            referencePrice: 1362.90m,
            fillPrice: 1362.74m);

        Assert.AreEqual(1366.28m, entry);
        Assert.AreEqual(1362.74m, close);
        Assert.IsTrue(close < entry, "这一行自己就能看出是亏着平的");
    }

    [TestMethod]
    public void Resolve_WithoutFillPrice_FallsBackToTheReference()
    {
        // 回执缺均价（少见，但市价单回执偶尔不含）：参考价兜底——
        // 市价单的成交价与它相差通常不到一个基点，比留空或写 0 更接近看表人的目的
        (decimal entry, decimal? close) = LiveOrderPrices.Resolve(
            BrokerOrderIntent.OpenShort,
            positionEntryPrice: 0m,
            referencePrice: 88.20m,
            fillPrice: 0m);

        Assert.AreEqual(88.20m, entry);
        Assert.IsNull(close, "开空是开仓：close 必须为空，否则这一行会被读成'既开又平'");
    }

    [TestMethod]
    public void Resolve_CloseOrderWithoutAPositionEntry_FallsBackToo()
    {
        // 账户里读不到被平仓位的成本线（竞态：刚好被平掉了）时，用参考价兜底
        (decimal entry, decimal? close) = LiveOrderPrices.Resolve(
            BrokerOrderIntent.CloseShort,
            positionEntryPrice: 0m,
            referencePrice: 103.5m,
            fillPrice: 103.6m);

        Assert.AreEqual(103.5m, entry, "开仓价缺实据时兜参考价，近似但不空白");
        Assert.AreEqual(103.6m, close);
    }

    [TestMethod]
    public void Resolve_FailedOrder_StillCarriesTheRoundTripContext()
    {
        // 被拒的单没有成交，但平仓行上"被平仓位是多少开的"仍然有复盘价值
        (decimal entry, decimal? close) = LiveOrderPrices.Resolve(
            BrokerOrderIntent.CloseLong,
            positionEntryPrice: 1366.28m,
            referencePrice: 1362.90m,
            fillPrice: 0m);

        Assert.AreEqual(1366.28m, entry);
        Assert.AreEqual(1362.90m, close, "没成交 → 成交价用参考价兜底，状态列会标明这一行没发出去");
    }
}
