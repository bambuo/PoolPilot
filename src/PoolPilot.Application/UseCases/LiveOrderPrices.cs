using PoolPilot.Application.Abstractions;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 实盘流水一行上该显示的**开仓价与平仓价**。
///
/// 这两个数的来源必须分清：
/// <list type="bullet">
///   <item><b>成交均价</b>（交易所回执的 <c>avgPrice</c>）是唯一权威的"这笔单子成交在哪"。</item>
///   <item>参考价（下单时把名义额换算成数量用的那个）只是回执缺均价时的兜底——
///     市价单的成交价与它相差通常不到一个基点。</item>
/// </list>
///
/// 一行的身份决定两个价怎么填：
/// <list type="bullet">
///   <item>**开仓单**：开仓价 = 本单成交均价（它就是新仓位的入场价）；平仓价 = 无。</item>
///   <item>**平仓单**：开仓价 = **被平的那个仓位**的开仓均价（下单前从账户里读的），
///     平仓价 = 本单成交均价——一行看完整趟回合，单位盈亏（平仓价 − 开仓价）直接可算。</item>
/// </list>
/// </summary>
public static class LiveOrderPrices
{
    /// <summary>该行有没有平仓价：平仓意图才有。</summary>
    private static bool IsClose(BrokerOrderIntent intent) =>
        intent is BrokerOrderIntent.CloseLong or BrokerOrderIntent.CloseShort;

    /// <summary>
    /// 算出流水一行上要记的两个价。
    /// </summary>
    /// <param name="intent">这一单的意图。</param>
    /// <param name="positionEntryPrice">被平仓位的开仓均价（开仓单传 0——它没有"被平的仓位"）。</param>
    /// <param name="referencePrice">下单时的参考价（名义额换算依据）。回执缺均价时的兜底。</param>
    /// <param name="fillPrice">交易所回执的成交均价；0 表示回执里没有（此时用参考价兜底并如实兜底）。</param>
    public static (decimal EntryPrice, decimal? ClosePrice) Resolve(
        BrokerOrderIntent intent,
        decimal positionEntryPrice,
        decimal referencePrice,
        decimal fillPrice)
    {
        decimal filled = fillPrice > 0m ? fillPrice : referencePrice;

        if (IsClose(intent))
        {
            // 平仓单：开仓价是被平仓位的成本线。账户里读不到（0）时用参考价兜底
            // 并不额外标记——那一列的语义是"这趟回合的入场价"，给一个近似的数
            // 比留空更接近看表人的目的，而误差不会超过市价单的点差
            decimal entry = positionEntryPrice > 0m ? positionEntryPrice : referencePrice;

            return (entry, filled);
        }

        return (filled, null);
    }
}
