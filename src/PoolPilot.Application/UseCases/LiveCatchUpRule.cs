using PoolPilot.Domain.Enums;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// **来晚了要不要补仓**的判据：只有当现价不比池子的开仓价差时才对。
///
/// 这条规则存在的理由很具体：跟单的价值在于"和领单人站在同一个价位上"。
/// 池子在 P₀ 建了多仓、现在价格已经涨到 P₁，此时补仓等于**买在比它高的位置**——
/// 之后每一分钱波动都要先填平这个差价，从跟上那一刻起就是一笔劣势交易。
/// 所以"来晚了"不该自动补，而该看价：现价 ≤ 池子开仓价（多头）才补，
/// 否则如实记一笔"没跟上"并说明原因，等池子下次动作再说。
///
/// 口径用**价格**而不是池子账户的盈亏百分比：后者按保证金算、受杠杆影响，
/// 而这里要比的是"我能不能拿到比它更好的入场价"，那是价格的事。
///
/// ⚠️ 跨场所的价格有一点基差（两家各自的现货/合约价差，通常几个基点），
/// 所以这个判据的精度就是几个基点——它挡的是"已经涨了一截还去追"，
/// 不是"差一个基点也要卡住"。
/// </summary>
public static class LiveCatchUpRule
{
    /// <summary>当前价相对池子开仓价的**不利幅度**（多头：现价高于开仓价多少；空头反之）。
    /// 为负表示现价更优（补仓有利），为正表示现价更差（补仓是追高）。</summary>
    public static decimal? DisadvantagePercent(decimal entryPrice, decimal markPrice, PositionSide side)
    {
        if (entryPrice <= 0m || markPrice <= 0m)
        {
            return null;
        }

        decimal move = (markPrice - entryPrice) / entryPrice * 100m;

        // 空头的方向相反：价格涨了对空头不利
        return side == PositionSide.Short ? -move : move;
    }

    /// <summary>
    /// 该不该现在补上这一笔。
    ///
    /// <returns>不该补时给出原因（直接进流水，说明"为什么没跟"）。</returns>
    /// </summary>
    public static (bool Align, string? Reason) Decide(
        string symbol,
        decimal entryPrice,
        decimal markPrice,
        PositionSide side)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        if (DisadvantagePercent(entryPrice, markPrice, side) is not { } disadvantage)
        {
            // 价格拿不到就**不补**：这条规则的全部意义是"别买贵了"，
            // 而在不知道贵不贵的时候买，正是它要防的事
            return (false, $"{symbol}：取不到开仓价或现价，无法确认现价不比池子差，先不补");
        }

        if (disadvantage > 0m)
        {
            string sideText = side == PositionSide.Short ? "空" : "多";

            return (false,
                $"{symbol}：池子该仓位是{sideText}头，现价已比它的开仓价差 {disadvantage:F2}%，"
                + "现在补上去等于替它接盘——等池子下次动作，或现价回到它的开仓价以内");
        }

        return (true, null);
    }
}
