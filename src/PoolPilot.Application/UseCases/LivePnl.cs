namespace PoolPilot.Application.UseCases;

/// <summary>
/// 未实现盈亏的计算。**这不是"我们另算一份账"**，而是把交易所的公式搬过来用——
/// 交易所自己就是按这条式子算的，验证过：拿同一份持仓响应里的标记价代入，
/// 与交易所报的 <c>unRealizedProfit</c> 最大偏差 0.0000008 USDT（8 位小数，即它自身的取整）。
///
/// 为什么要自己算：币安的用户数据流**只在仓位真的变了的时候**才推（成交、强平、资金费、划转），
/// **价格变动不推**——实测持有 9 笔仓位、价格在动的 30 秒里，私有流一帧都没来。
/// 而它另有每秒推一次的标记价（<c>&lt;symbol&gt;@markPrice@1s</c>）。
/// 于是"实时的未实现盈亏"只能这么得到：**用推送的标记价 + 交易所的公式当场算**。
///
/// 口径（USDT 本位线性合约）：<c>未实现盈亏 = (标记价 − 开仓均价) × 仓位数量</c>，
/// 数量为空头时为负值，公式不用分方向——这正是它简洁的地方。
/// </summary>
public static class LivePnl
{
    /// <summary>一笔仓位的未实现盈亏。取不到标记价时返回 null（"不知道"而不是 0）。</summary>
    public static decimal? Position(decimal entryPrice, decimal signedSize, decimal? markPrice)
    {
        if (markPrice is not { } mark || mark <= 0m)
        {
            return null;
        }

        return (mark - entryPrice) * signedSize;
    }

    /// <summary>把各笔的未实现盈亏汇总。**有任何一笔取不到价就不给总数**——
    /// 用一个"少算了一笔"的数当总数，比说"不可得"更容易误导。</summary>
    public static decimal? Total(IEnumerable<decimal?> positionPnls)
    {
        ArgumentNullException.ThrowIfNull(positionPnls);

        decimal sum = 0m;

        foreach (decimal? pnl in positionPnls)
        {
            if (pnl is not { } value)
            {
                return null;
            }

            sum += value;
        }

        return sum;
    }
}
