using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Enums;

namespace PoolPilot.Application.Tests;

/// <summary>
/// "来晚了要不要补"的判据。
///
/// 钉住的是跟单的核心价值主张：**和领单人站在同一个价位上**。
/// 池子在 P₀ 建仓、现价已经跑到 P₁，此时补上去等于买得比它贵——
/// 之后每一分波动都要先填平这个差价。所以判据是"现价不差于它的开仓价"。
/// </summary>
[TestClass]
public sealed class LiveCatchUpRuleTests
{
    [TestMethod]
    public void Decide_LongWithPriceBelowEntry_AllowsCatchingUp()
    {
        // 池子 100 建的多仓，现价 97：补上去比它还便宜 3%
        (bool align, string? reason) = LiveCatchUpRule.Decide("BTC", entryPrice: 100m, markPrice: 97m, PositionSide.Long);

        Assert.IsTrue(align);
        Assert.IsNull(reason);
        Assert.AreEqual(-3m, LiveCatchUpRule.DisadvantagePercent(100m, 97m, PositionSide.Long));
    }

    [TestMethod]
    public void Decide_LongWithPriceAboveEntry_RefusesAndSaysHowMuchWorse()
    {
        (bool align, string? reason) = LiveCatchUpRule.Decide("BTC", entryPrice: 100m, markPrice: 103m, PositionSide.Long);

        Assert.IsFalse(align, "买在比带单员贵 3% 的位置，那不叫跟单");
        Assert.Contains("3.00%", reason ?? string.Empty, "要说清差多少，人才能判断值不值得等");
        Assert.Contains("接盘", reason ?? string.Empty);
    }

    [TestMethod]
    public void Decide_ShortIsMirrored_TheSameRuleWithTheOppositeSign()
    {
        // 空头是"卖出"：带单员在 100 卖出的，我卖得比它低就是更差（同一个资产卖便宜了）。
        // 所以判断方向与多头相反——价格跌到 97 时**不该**补空，涨到 103 时才该补
        Assert.IsFalse(
            LiveCatchUpRule.Decide("BTC", 100m, 97m, PositionSide.Short).Align,
            "空头跌了：现在补空是卖得比带单员便宜");

        Assert.IsTrue(
            LiveCatchUpRule.Decide("BTC", 100m, 103m, PositionSide.Short).Align,
            "空头涨了：现在补空能卖得比带单员贵");

        Assert.AreEqual(3m, LiveCatchUpRule.DisadvantagePercent(100m, 97m, PositionSide.Short));
        Assert.AreEqual(-3m, LiveCatchUpRule.DisadvantagePercent(100m, 103m, PositionSide.Short));
    }

    [TestMethod]
    public void Decide_AtExactlyTheEntryPrice_AllowsCatchingUp()
    {
        // 打平就是"不差于"：再加手续费才是真正的分界，而费用与滑点无法在挂载那一刻精确预知
        Assert.IsTrue(LiveCatchUpRule.Decide("BTC", 100m, 100m, PositionSide.Long).Align);
    }

    [TestMethod]
    public void Decide_WithoutPrices_RefusesInsteadOfBuyingBlind()
    {
        // 这条规则的全部意义是"别买贵了"；在不知道贵不贵的时候买，正是它要防的事
        (bool align, string? reason) = LiveCatchUpRule.Decide("BTC", entryPrice: 0m, markPrice: 100m, PositionSide.Long);

        Assert.IsFalse(align);
        Assert.Contains("无法确认", reason ?? string.Empty);
        Assert.IsNull(LiveCatchUpRule.DisadvantagePercent(0m, 100m, PositionSide.Long));
    }
}
