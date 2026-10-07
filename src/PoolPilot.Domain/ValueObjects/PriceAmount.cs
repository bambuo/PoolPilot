using System.Globalization;

namespace PoolPilot.Domain.ValueObjects;

/// <summary>
/// 价格。与 <see cref="UsdAmount"/> 分开，因为二者的精度语义不同：
/// 价格需要保留更多小数位（实测某市场 <c>supported_price_decimals</c> 为 5），
/// 而金额字段的展示精度由资产精度决定。
/// </summary>
public sealed record PriceAmount
{
    private PriceAmount(decimal value) => Value = value;

    public decimal Value { get; }

    public bool IsZero => Value == 0m;

    public static PriceAmount From(decimal value) => new(value);

    public static PriceAmount Zero => new(0m);

    public static PriceAmount Parse(string raw) =>
        new(LighterNumeric.Parse(nameof(PriceAmount), raw));

    public static PriceAmount FromDouble(double value) =>
        new(LighterNumeric.FromDouble(value));

    /// <summary>
    /// 相对另一个价格的变化百分比。用于计算跟单漏损等场景。
    /// </summary>
    public PercentValue ChangeAgainst(PriceAmount baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        if (baseline.Value == 0m)
        {
            return PercentValue.FromPercent(0m);
        }

        return PercentValue.FromPercent((Value - baseline.Value) / baseline.Value * 100m);
    }

    public override string ToString() => Value.ToString("0.########", CultureInfo.InvariantCulture);
}
