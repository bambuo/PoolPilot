using System.Globalization;

using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Exceptions;

namespace PoolPilot.Domain.ValueObjects;

/// <summary>
/// Lighter 的账户索引。
///
/// 刻意的类型选择：内部是 <see cref="long"/> 而不是 <c>int</c>。
/// 公共池索引形如 281474976552443，**远超 int32 上限 2147483647**；
/// 用 int 承接会在解析时静默溢出为负数，之后所有按索引的查询都找不到数据，
/// 且不会有任何异常——这是本项目最容易埋、最难查的一类缺陷。
/// </summary>
public sealed record AccountIndex
{
    private AccountIndex(long value) => Value = value;

    public long Value { get; }

    /// <summary>是否落在公共池的索引段内。</summary>
    public bool IsPublicPoolRange => Value >= LighterConstants.PublicPoolIndexFloor;

    public static AccountIndex From(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return new AccountIndex(value);
    }

    /// <summary>
    /// 从字符串解析。上游把索引放在 JSON 数字里，但部分场景（如 URL 参数）以字符串出现。
    /// </summary>
    public static AccountIndex Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            throw new InvalidLighterFieldException(nameof(AccountIndex), raw, "应为十进制整数（可能是 int32 溢出或格式变更）");
        }

        return From(value);
    }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
