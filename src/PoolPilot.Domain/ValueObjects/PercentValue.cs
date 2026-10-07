using System.Globalization;

using PoolPilot.Domain.Exceptions;

namespace PoolPilot.Domain.ValueObjects;

/// <summary>
/// 百分数。
///
/// **本类型存在的唯一理由：Lighter 有一批字段的值本身就是百分数，
/// 再次乘 100 会得出荒谬结果。**
///
/// 实测确认的三个字段：
/// <list type="bullet">
///   <item><c>annual_percentage_yield</c>：<c>7.372113</c> 表示 <b>7.37%</b>（不是 737%）。</item>
///   <item><c>operator_fee</c>：<c>"20.0000"</c> 表示 <b>20%</b>（且是字符串类型）。</item>
///   <item><c>daily_price_change</c>：<c>5.3257</c> 表示 <b>+5.33%</b>。</item>
/// </list>
///
/// <c>daily_price_change</c> 的单位曾用 24h 价格区间反推验证：按百分数解释时反推出的
/// 24 小时前价格全部落在区间内；按小数解释会得出**负价格**（如某市场 −0.0038），
/// 物理上不可能。
/// </summary>
public sealed record PercentValue
{
    private PercentValue(decimal percent) => Percent = percent;

    /// <summary>百分数原值，<c>7.37</c> 表示 7.37%。不做任何缩放。</summary>
    public decimal Percent { get; }

    /// <summary>小数形式，<c>0.0737</c>。仅在确实需要参与比例运算时使用。</summary>
    public decimal Fraction => Percent / 100m;

    public static PercentValue FromPercent(decimal percent) => new(percent);

    public static PercentValue Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal percent))
        {
            throw new InvalidLighterFieldException(nameof(PercentValue), raw, "应为十进制数（该字段本身就是百分数）");
        }

        return new PercentValue(percent);
    }

    public override string ToString() => $"{Percent.ToString("0.####", CultureInfo.InvariantCulture)}%";
}
