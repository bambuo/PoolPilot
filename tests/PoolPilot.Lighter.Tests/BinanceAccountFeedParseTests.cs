using System.Text.Json;

using PoolPilot.Infrastructure.Brokers;

namespace PoolPilot.Lighter.Tests;

/// <summary>
/// 币安用户数据流的事件解析。
///
/// 用的都是**实测载荷的形状**（见币安 U 本位合约的 User Data Streams 文档与真实帧），
/// 因为这段代码的全部价值在于"认得出这是哪一类事件"：
/// 认错类型不会报错，只会让该立刻刷新的强平被当成普通成交，或者反过来。
///
/// 解析刻意不求完整：只取类型与关键字段，账户数据一律由 REST 重读。
/// 所以这里也**只断言类型与线索**，不断言"解析出了一个完整账户"。
/// </summary>
[TestClass]
public sealed class BinanceAccountFeedParseTests
{
    [TestMethod]
    public void Parse_AccountUpdate_ReadsSymbolAndReason()
    {
        const string json = """
        {
          "e": "ACCOUNT_UPDATE",
          "T": 1791281000000,
          "E": 1791281000001,
          "a": {
            "m": "ORDER",
            "B": [{ "a": "USDT", "wb": "181.42", "cw": "181.42", "bc": "-17.74" }],
            "P": [
              { "s": "ZECUSDT", "pa": "0.013", "ep": "1366.28", "cr": "-0.0317", "up": "-0.0317", "mt": "isolated", "iw": "1.77", "ps": "LONG" }
            ]
          }
        }
        """;

        AccountStreamEvent? parsed = BinanceAccountFeed.Parse(json);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(AccountStreamEventKind.AccountUpdate, parsed.Kind);
        Assert.AreEqual("ZECUSDT", parsed.Symbol, "标的要带出来，日志里靠它说清是哪个合约动了");
        Assert.AreEqual("ORDER", parsed.Detail, "变动原因（ORDER/FUNDING_FEE/LIQUIDATION…）是排查的关键线索");
        Assert.IsFalse(parsed.IsNotable, "普通成交不该按预警处理");
    }

    [TestMethod]
    public void Parse_LiquidationOrder_IsMarkedAsLiquidation()
    {
        // 强平走的是 ORDER_TRADE_UPDATE（o.o = LIQUIDATION）
        const string json = """
        {
          "e": "ORDER_TRADE_UPDATE",
          "T": 1791281000000,
          "o": {
            "s": "ZECUSDT", "c": "autoclose-1", "S": "SELL", "o": "LIQUIDATION",
            "f": "GTC", "q": "0.013", "X": "FILLED", "ps": "LONG"
          }
        }
        """;

        AccountStreamEvent? parsed = BinanceAccountFeed.Parse(json);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(AccountStreamEventKind.Liquidation, parsed.Kind);
        Assert.AreEqual("ZECUSDT", parsed.Symbol);
        Assert.IsTrue(parsed.IsNotable, "强平会影响钱，必须显眼");
    }

    [TestMethod]
    public void Parse_MarginCall_IsNotable()
    {
        const string json = """
        { "e": "MARGIN_CALL", "E": 1791281000000, "cw": "12.5", "p": [{ "s": "ZECUSDT", "ps": "LONG", "pa": "0.013", "mt": "ISOLATED" }] }
        """;

        AccountStreamEvent? parsed = BinanceAccountFeed.Parse(json);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(AccountStreamEventKind.MarginCall, parsed.Kind);
        Assert.IsTrue(parsed.IsNotable, "保证金不足是实盘最该被看到的预警");
    }

    [TestMethod]
    public void Parse_ListenKeyExpired_IsRecognizedSoTheStreamCanRenew()
    {
        const string json = """{ "e": "listenKeyExpired", "E": 1791281000000 }""";

        AccountStreamEvent? parsed = BinanceAccountFeed.Parse(json);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(AccountStreamEventKind.ListenKeyExpired, parsed.Kind);
    }

    [TestMethod]
    public void Parse_UnknownEvent_IsIgnoredInsteadOfGuessed()
    {
        // 币安会不打招呼地加新事件类型。忽略比猜好：猜错会去读一次账户，
        // 而更糟的是把未知事件当成强平或保证金预警报出来
        Assert.IsNull(BinanceAccountFeed.Parse("""{ "e": "SOMETHING_NEW", "x": 1 }"""));
    }

    [TestMethod]
    public void Parse_FrameWithoutEventName_IsIgnored()
    {
        // 用户流是纯 JSON，理论上不会出现无事件名的帧；真出现了也不该当事件处理
        Assert.IsNull(BinanceAccountFeed.Parse("""{ "result": null, "id": 1 }"""));
    }

    [TestMethod]
    public void Parse_BrokenJson_IsIgnoredInsteadOfThrowing()
    {
        // 推送通道上为一条脏数据中断连接，代价是丢掉之后所有事件
        Assert.IsNull(BinanceAccountFeed.Parse("{ 这不是 JSON"));
        Assert.IsNull(BinanceAccountFeed.Parse(string.Empty));
    }

    [TestMethod]
    public void Parse_AccountUpdateWithoutPositions_StillReportsTheEvent()
    {
        // 只动余额（比如划转）时 P 可能是空数组：事件本身仍然有效——
        // 上层的动作是"重读账户"，与具体哪个合约无关
        const string json = """
        { "e": "ACCOUNT_UPDATE", "a": { "m": "TRANSFER", "B": [{ "a": "USDT", "wb": "500.00" }], "P": [] } }
        """;

        AccountStreamEvent? parsed = BinanceAccountFeed.Parse(json);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(AccountStreamEventKind.AccountUpdate, parsed.Kind);
        Assert.IsNull(parsed.Symbol, "没有仓位变动时不该编一个标的出来");
        Assert.AreEqual("TRANSFER", parsed.Detail);
    }
}
