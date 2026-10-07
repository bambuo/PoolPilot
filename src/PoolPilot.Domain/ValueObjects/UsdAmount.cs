using System.Globalization;

namespace PoolPilot.Domain.ValueObjects;

/// <summary>
/// 金额（以美元稳定币计价，Lighter 上为 USDC）。
///
/// 上游以**字符串**返回金额（如 <c>"1633519.9729449996"</c>），这是为了避免
/// IEEE 754 双精度在大额数值上的精度丢失——属于合理设计，但调用方必须显式转换。
///
/// 一律使用 <see cref="decimal"/> 承载，不用 <c>double</c>：货币运算用二进制浮点
/// 会在累加（如逐币种资产求和）时产生可见误差。
/// </summary>
public sealed record UsdAmount
{
    private UsdAmount(decimal value) => Value = value;

    public decimal Value { get; }

    public bool IsZero => Value == 0m;

    public bool IsPositive => Value > 0m;

    public static UsdAmount From(decimal value) => new(value);

    public static UsdAmount Zero => new(0m);

    public static UsdAmount Parse(string raw) =>
        new(LighterNumeric.Parse(nameof(UsdAmount), raw));

    public static UsdAmount FromDouble(double value) =>
        new(LighterNumeric.FromDouble(value));

    public UsdAmount Abs() => new(Math.Abs(Value));

    public override string ToString() => Value.ToString("0.######", CultureInfo.InvariantCulture);
}
