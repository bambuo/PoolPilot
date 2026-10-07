using System.Globalization;

using PoolPilot.Domain.Exceptions;

namespace PoolPilot.Domain.ValueObjects;

/// <summary>
/// 保证金率。
///
/// **本类型存在的唯一理由：Lighter 有两个同名同义但刻度不同的字段。**
///
/// <list type="bullet">
///   <item><c>orderBookDetails</c> 的 <c>*_initial_margin_fraction</c> 是整数刻度，
///     <c>1000</c> 表示 10%，换算需 <b>÷ 10000</b>。</item>
///   <item><c>account.positions[]</c> 的 <c>initial_margin_fraction</c> 是百分数字符串，
///     <c>"25.00"</c> 表示 25%，换算需 <b>÷ 100</b>。</item>
/// </list>
///
/// 两个入口分开命名（<see cref="FromOrderBookScale"/> / <see cref="FromPositionPercent"/>），
/// 调用方必须明确自己拿的是哪一个——写错刻度不会编译报错，但会让杠杆算错 100 倍。
/// 实测校验：持仓名义 872.38 × 25% = 218.09，与账户报的 <c>cross_initial_margin_requirement</c> 吻合。
/// </summary>
public sealed record MarginFraction
{
    private MarginFraction(decimal ratio) => Ratio = ratio;

    /// <summary>比例形式，0.25 表示 25%。</summary>
    public decimal Ratio { get; }

    /// <summary>
    /// 该保证金率允许的最大杠杆倍数。
    ///
    /// 注意语义：这是**该仓位当前的杠杆上限**，不是"该池的风险偏好"，也不是市场属性。
    /// 实测存在仓位保证金率**高于**市场默认值的例子（某池把 DOGE 从市场默认的 6.66% 上调到 10.00%，
    /// 即主动把杠杆从 15 倍降到 10 倍）。判断池的整体杠杆要用「毛敞口 ÷ 抵押品」。
    /// </summary>
    public decimal Leverage => 1m / Ratio;

    public decimal Percent => Ratio * 100m;

    /// <summary>
    /// 从 <c>orderBookDetails</c> 的整数刻度构造（<c>1000</c> → 10%）。
    /// </summary>
    public static MarginFraction FromOrderBookScale(long raw)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(raw);
        return new MarginFraction(raw / 10_000m);
    }

    /// <summary>
    /// 从 <c>account.positions[]</c> 的百分数字符串构造（<c>"25.00"</c> → 25%）。
    /// </summary>
    public static MarginFraction FromPositionPercent(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal percent))
        {
            throw new InvalidLighterFieldException("initial_margin_fraction", raw, "应为十进制数");
        }

        return FromPositionPercent(percent);
    }

    /// <summary>
    /// 从百分数值构造（<c>25.00m</c> → 25%）。
    /// </summary>
    public static MarginFraction FromPositionPercent(decimal percent)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(percent);
        return new MarginFraction(percent / 100m);
    }

    public override string ToString() => $"{Percent.ToString("0.##", CultureInfo.InvariantCulture)}%";
}
