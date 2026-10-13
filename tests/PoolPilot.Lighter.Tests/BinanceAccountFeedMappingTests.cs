using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures.Socket;

using PoolPilot.Infrastructure.Brokers;

namespace PoolPilot.Lighter.Tests;

/// <summary>
/// 币安用户数据流的事件**映射**：库送的强类型更新 → 本项目的事件。
///
/// 迁移前锁的是"从原始 JSON 认事件类型"（那时解析是自己写的）。现在 JSON 由库解析
/// （认不出的帧、脏数据、新事件类型这些都成了库的事），本项目剩下的判断是**这些事件
/// 对我们意味着什么**——强平要显眼、账户变动只当刷新触发器、缺字段也不许编一个出来。
/// 那才是错一次就会让"该立刻刷新的强平被当成普通成交"的地方。
/// </summary>
[TestClass]
public sealed class BinanceAccountFeedMappingTests
{
    [TestMethod]
    public void MapAccountUpdate_ReadsSymbolAndReason()
    {
        BinanceFuturesStreamAccountUpdate update = new()
        {
            UpdateData = new BinanceFuturesStreamAccountUpdateData
            {
                Reason = AccountUpdateReason.Order,
                Positions = [new BinanceFuturesStreamPosition { Symbol = "ZECUSDT" }],
            },
        };

        AccountStreamEvent mapped = BinanceAccountFeed.MapAccountUpdate(update);

        Assert.AreEqual(AccountStreamEventKind.AccountUpdate, mapped.Kind);
        Assert.AreEqual("ZECUSDT", mapped.Symbol, "标的要带出来，日志里靠它说清是哪个合约动了");
        Assert.AreEqual("ORDER", mapped.Detail, "变动原因（ORDER/FUNDING_FEE…）是排查的关键线索");
        Assert.IsFalse(mapped.IsNotable, "普通成交不该按预警处理");
    }

    [TestMethod]
    public void MapAccountUpdate_WithoutPositions_StillReportsTheEvent()
    {
        // 只动余额（比如划转）时仓位数组可能是空的：事件本身仍然有效——
        // 上层的动作是"重读账户"，与具体哪个合约无关
        BinanceFuturesStreamAccountUpdate update = new()
        {
            UpdateData = new BinanceFuturesStreamAccountUpdateData
            {
                Reason = AccountUpdateReason.MarginTransfer,
                Positions = [],
            },
        };

        AccountStreamEvent mapped = BinanceAccountFeed.MapAccountUpdate(update);

        Assert.AreEqual(AccountStreamEventKind.AccountUpdate, mapped.Kind);
        Assert.IsNull(mapped.Symbol, "没有仓位变动时不该编一个标的出来");
        Assert.AreEqual("MARGINTRANSFER", mapped.Detail);
    }

    [TestMethod]
    public void MapOrderUpdate_LiquidationIsMarkedAsLiquidation()
    {
        // 强平走的是订单事件：**原始类型**是 Liquidation（下单时是 MARKET，被强平时上游把它改掉）。
        // 看 Type 会把它当成一笔普通市价成交——那正是这里要防的
        BinanceFuturesStreamOrderUpdate update = new()
        {
            UpdateData = new BinanceFuturesStreamOrderUpdateData
            {
                Symbol = "ZECUSDT",
                Type = FuturesOrderType.Market,
                OriginalType = FuturesOrderType.Liquidation,
                Status = OrderStatus.Filled,
            },
        };

        AccountStreamEvent mapped = BinanceAccountFeed.MapOrderUpdate(update);

        Assert.AreEqual(AccountStreamEventKind.Liquidation, mapped.Kind);
        Assert.AreEqual("ZECUSDT", mapped.Symbol);
        Assert.IsTrue(mapped.IsNotable, "强平会影响钱，必须显眼");
    }

    [TestMethod]
    public void MapOrderUpdate_ExpiredInMatch_IsAlsoTreatedAsLiquidation()
    {
        // 实测上游还会用 EXPIRED_IN_MATCH 标记"因强平/自成交保护而作废"的那一档
        BinanceFuturesStreamOrderUpdate update = new()
        {
            UpdateData = new BinanceFuturesStreamOrderUpdateData
            {
                Symbol = "ZECUSDT",
                Type = FuturesOrderType.Market,
                OriginalType = FuturesOrderType.Market,
                Status = OrderStatus.ExpiredInMatch,
            },
        };

        Assert.AreEqual(AccountStreamEventKind.Liquidation, BinanceAccountFeed.MapOrderUpdate(update).Kind);
    }

    [TestMethod]
    public void MapOrderUpdate_OrdinaryOrder_IsNotNotable()
    {
        BinanceFuturesStreamOrderUpdate update = new()
        {
            UpdateData = new BinanceFuturesStreamOrderUpdateData
            {
                Symbol = "ZECUSDT",
                Type = FuturesOrderType.Market,
                OriginalType = FuturesOrderType.Market,
                Status = OrderStatus.Filled,
            },
        };

        AccountStreamEvent mapped = BinanceAccountFeed.MapOrderUpdate(update);

        Assert.AreEqual(AccountStreamEventKind.OrderUpdate, mapped.Kind);
        Assert.AreEqual("MARKET FILLED", mapped.Detail);
        Assert.IsFalse(mapped.IsNotable);
    }

    [TestMethod]
    public void MarginCallAndListenKeyExpired_AreNotable()
    {
        Assert.AreEqual(AccountStreamEventKind.MarginCall, BinanceAccountFeed.MarginCall().Kind);
        Assert.IsTrue(BinanceAccountFeed.MarginCall().IsNotable, "保证金不足是实盘最该被看到的预警");

        Assert.AreEqual(AccountStreamEventKind.ListenKeyExpired, BinanceAccountFeed.ListenKeyExpired().Kind);
        Assert.IsTrue(BinanceAccountFeed.ListenKeyExpired().IsNotable, "流断了要显眼");

        Assert.AreEqual(AccountStreamEventKind.ConfigUpdate, BinanceAccountFeed.ConfigUpdate().Kind);
        Assert.IsFalse(BinanceAccountFeed.ConfigUpdate().IsNotable, "改杠杆不是钱的问题，只驱动刷新");
    }
}
