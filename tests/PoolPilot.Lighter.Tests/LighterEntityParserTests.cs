using System.Text.Json;

using PoolPilot.Domain.Enums;
using PoolPilot.Infrastructure.Lighter;

namespace PoolPilot.Lighter.Tests;

/// <summary>
/// 解析器行为测试（离线）。
///
/// 这里的每一条都是**上游真实出现过、且曾造成故障**的数据形状，
/// 不是臆造的边界：margin="0.00" 让一个 44 市场池把整个账户列表接口打成 400
/// （守卫把它当非法值抛出，而它实际表示"该仓位正在变动中"）。
/// </summary>
[TestClass]
public sealed class LighterEntityParserTests
{
    [TestMethod]
    public void ParsePosition_WithZeroImf_ReturnsNullMarginNotException()
    {
        // 实测：仓位变动瞬间 initial_margin_fraction 为 "0.00"。
        // 0% 保证金率 = 无穷杠杆，不可能是真实设定——它表达的是"此刻没有有效值"。
        JsonElement element = PositionJson(imf: "0.00");

        PoolPilot.Domain.Entities.PoolPosition position = LighterEntityParser.ParsePosition(element);

        Assert.IsNull(position.Margin, "imf=0 应解析为 null（无有效值），而不是抛异常或编造杠杆");
    }

    [TestMethod]
    public void ParsePosition_WithNormalImf_KeepsMarginAndComputesLeverage()
    {
        // 实测形态：25.00 → 保证金率 25% → 4 倍杠杆
        JsonElement element = PositionJson(imf: "25.00");

        PoolPilot.Domain.Entities.PoolPosition position = LighterEntityParser.ParsePosition(element);

        Assert.IsNotNull(position.Margin);
        Assert.AreEqual(4m, position.Margin!.Leverage);
        Assert.AreEqual(PositionSide.Long, position.Side);
    }

    /// <summary>构造上游持仓行的实测形状。字段名与 docs/LIGHTER-API-NOTES.md 一致。</summary>
    private static JsonElement PositionJson(string imf) => JsonDocument.Parse($$"""
        {
            "market_id": 29,
            "symbol": "ENA",
            "sign": 1,
            "position": "3404.2",
            "avg_entry_price": "0.255170",
            "position_value": "868.66",
            "unrealized_pnl": "-12.54",
            "liquidation_price": "0.00",
            "initial_margin_fraction": "{{imf}}",
            "open_orders": 0,
            "cumulative_funding": "-0.20"
        }
        """).RootElement.Clone();
}
