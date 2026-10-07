using System.Globalization;

using PoolPilot.Domain.Exceptions;

namespace PoolPilot.Domain.ValueObjects;

/// <summary>
/// Lighter 数值字段的共享解析入口。
///
/// 上游把数值混用两种形态：**字符串**（大额金额，避免 IEEE 754 精度丢失）与
/// **JSON 数字**（统计类字段）。两种都要能安全解析，且失败必须报错而非返回 0。
/// </summary>
public static class LighterNumeric
{
    /// <summary>
    /// 从字符串解析十进制数。
    ///
    /// 特殊处理：上游的零仓 <c>position_value</c> 会返回 <c>"-0.000000"</c>（**负零**）。
    /// 它数值上等于 0，若当成负数参与比较会得出错误结论，故统一归一化为 0。
    /// </summary>
    public static decimal Parse(string fieldName, string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
        {
            throw new InvalidLighterFieldException(fieldName, raw, "应为十进制数字符串");
        }

        return value == 0m ? 0m : value;
    }

    /// <summary>从 JSON 数字解析。上游的 <c>long</c>/<c>double</c> 字段走这里。</summary>
    public static decimal FromDouble(double value) =>
        double.IsFinite(value) ? (decimal)value : 0m;
}
