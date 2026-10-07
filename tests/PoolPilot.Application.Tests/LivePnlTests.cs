using PoolPilot.Application.UseCases;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 实时未实现盈亏的算法。
///
/// 这里钉的是**口径**：公式必须与交易所自己用的那条一致
/// （USDT 本位线性：<c>(标记价 − 开仓均价) × 数量</c>，数量空头为负，式子不用分方向）。
/// 实测用交易所同一份响应里的标记价代入，与它报的 <c>unRealizedProfit</c>
/// 最大偏差 0.0000008 USDT —— 也就是它自身的取整。
///
/// 为什么要自己算而不是等交易所报：它的用户数据流只在**仓位真的变化**时推，
/// 不推价格（实测持有 9 笔仓位、价格在动的 30 秒里一帧都没来）。
/// </summary>
[TestClass]
public sealed class LivePnlTests
{
    [TestMethod]
    public void Position_Long_IsGainWhenMarkIsAboveEntry()
    {
        // 实测那笔真仓位（21.1 个 @0.7509，标记价 0.7499）按**这四个数**算出来是 -0.0211。
        // 交易所当时报 -0.021160：它用的是自己那个未取整的标记价，差在小数第 4 位。
        // 这里断言的是"给定这三个输入，式子得出什么"——不是去复刻交易所的取整
        Assert.AreEqual(-0.0211m, LivePnl.Position(0.7509m, 21.1m, 0.7499m));

        // 干净的数上，方向与盈亏的关系一眼可见
        Assert.AreEqual(4m, LivePnl.Position(100m, 2m, 102m), "多头涨了是赚");
        Assert.AreEqual(-4m, LivePnl.Position(100m, 2m, 98m), "多头跌了是亏");
    }

    [TestMethod]
    public void Position_Short_IsGainWhenMarkIsBelowEntry()
    {
        // 空头的数量带负号，式子不用为它分岔
        Assert.AreEqual(5m, LivePnl.Position(100m, -1m, 95m), "空头跌了是赚");
        Assert.AreEqual(-5m, LivePnl.Position(100m, -1m, 105m), "空头涨了是亏");
    }

    [TestMethod]
    public void Position_WithoutMarkPrice_IsUnknownNotZero()
    {
        // 取不到标记价时说"不知道"：填 0 会被读成"不赚不亏"，那是一个具体的结论
        Assert.IsNull(LivePnl.Position(100m, 1m, null));
        Assert.IsNull(LivePnl.Position(100m, 1m, 0m));
    }

    [TestMethod]
    public void Total_WithEveryPositionPriced_SumsThem()
    {
        Assert.AreEqual(-3m, LivePnl.Total([-1m, 2m, -4m]));
    }

    [TestMethod]
    public void Total_WithOneMissingPrice_RefusesInsteadOfUndercounting()
    {
        // 少算一笔的总数比"不可得"更容易误导：看的人会以为那就是全部
        Assert.IsNull(LivePnl.Total([-1m, null, 2m]));
    }

    [TestMethod]
    public void Total_WithNoPositions_IsZero()
    {
        Assert.AreEqual(0m, LivePnl.Total([]), "空仓的总盈亏是 0，不是未知");
    }
}
