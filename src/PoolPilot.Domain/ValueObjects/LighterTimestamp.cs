using System.Globalization;

using PoolPilot.Domain.Exceptions;

namespace PoolPilot.Domain.ValueObjects;

/// <summary>
/// Lighter 时间戳。
///
/// **本类型存在的唯一理由：同一套 API 里有四种时间刻度。**
/// 官方没有任何一处集中说明，全部靠实测确认：
///
/// <list type="table">
///   <item><term>微秒（数字）</term><description><c>transaction_time</c>，如 <c>1791163963009550</c></description></item>
///   <item><term>毫秒（数字）</term><description>WebSocket 成交的 <c>timestamp</c>，如 <c>1791197314925</c></description></item>
///   <item><term>秒（数字）</term><description><c>updated_at</c>、池的 <c>created_at</c>、挂单的 <c>timestamp</c></description></item>
///   <item><term>毫秒（字符串）</term><description><c>orderBookDetails[].created_at</c>，如 <c>"1737098461127"</c></description></item>
/// </list>
///
/// 把微秒当毫秒用会得到公元 58733 年。四种口径各有一个命名明确的工厂方法，
/// 调用方必须选对——写错不会被编译器拦住，但会让所有时间比较失效。
/// </summary>
public sealed record LighterTimestamp
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    private LighterTimestamp(DateTimeOffset utc, long rawValue, TimestampCaliber caliber, bool isUnset)
    {
        Utc = utc;
        RawValue = rawValue;
        Caliber = caliber;
        IsUnset = isUnset;
    }

    public DateTimeOffset Utc { get; }

    /// <summary>上游返回的原始数值，便于核对与持久化。</summary>
    public long RawValue { get; }

    /// <summary>该时间戳来自哪种刻度，用于诊断。</summary>
    public TimestampCaliber Caliber { get; }

    /// <summary>
    /// 上游未回填该时间戳。
    ///
    /// 实测存在这种情况：<c>account.accounts[].created_at</c> 返回 <c>0</c>，
    /// 因此**不能用它判断账户年龄**。标记为未设置而不是抛异常，让调用方自己决定如何展示。
    /// </summary>
    public bool IsUnset { get; }

    /// <summary>微秒刻度：<c>transaction_time</c>。</summary>
    public static LighterTimestamp FromMicroseconds(long microseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(microseconds);
        if (microseconds == 0)
        {
            return new LighterTimestamp(Epoch, 0, TimestampCaliber.Microseconds, true);
        }

        return new LighterTimestamp(
            GuardRange(() => Epoch.AddTicks(microseconds * 10L), microseconds, TimestampCaliber.Microseconds),
            microseconds,
            TimestampCaliber.Microseconds,
            false);
    }

    /// <summary>毫秒刻度（数字）：WebSocket 成交的 <c>timestamp</c>。</summary>
    public static LighterTimestamp FromMilliseconds(long milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        if (milliseconds == 0)
        {
            return new LighterTimestamp(Epoch, 0, TimestampCaliber.Milliseconds, true);
        }

        return new LighterTimestamp(
            GuardRange(() => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), milliseconds, TimestampCaliber.Milliseconds),
            milliseconds,
            TimestampCaliber.Milliseconds,
            false);
    }

    /// <summary>秒刻度：<c>updated_at</c>、池的 <c>created_at</c>、挂单的 <c>timestamp</c>。</summary>
    public static LighterTimestamp FromSeconds(long seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        if (seconds == 0)
        {
            return new LighterTimestamp(Epoch, 0, TimestampCaliber.Seconds, true);
        }

        return new LighterTimestamp(
            GuardRange(() => DateTimeOffset.FromUnixTimeSeconds(seconds), seconds, TimestampCaliber.Seconds),
            seconds,
            TimestampCaliber.Seconds,
            false);
    }

    /// <summary>
    /// 超出可表示范围时抛出领域异常，而不是让 .NET 的 <see cref="ArgumentOutOfRangeException"/> 裸奔。
    ///
    /// 实测意义：把微秒值当成毫秒解释会得到公元 58733 年，超出 <see cref="DateTimeOffset"/>
    /// 的上限（9999 年）。**这种越界几乎总是刻度搞错，而不是上游真的发了这样的值**，
    /// 所以错误消息要直接指出这一点，避免排查时绕远路。
    /// </summary>
    private static DateTimeOffset GuardRange(Func<DateTimeOffset> convert, long rawValue, TimestampCaliber caliber)
    {
        try
        {
            return convert();
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidLighterFieldException(
                "timestamp",
                rawValue.ToString(CultureInfo.InvariantCulture),
                $"按「{Describe(caliber)}」解释超出可表示范围——极可能是刻度判断错误（同一数值按微秒与毫秒解释相差数万年）");
        }
    }

    private static string Describe(TimestampCaliber caliber) => caliber switch
    {
        TimestampCaliber.Microseconds => "微秒",
        TimestampCaliber.Milliseconds => "毫秒",
        TimestampCaliber.Seconds => "秒",
        _ => caliber.ToString(),
    };

    /// <summary>毫秒刻度（字符串）：<c>orderBookDetails[].created_at</c>。</summary>
    public static LighterTimestamp FromMillisecondsText(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long milliseconds))
        {
            throw new InvalidLighterFieldException("created_at", raw, "应为毫秒整数的字符串形式");
        }

        return FromMilliseconds(milliseconds);
    }

    /// <summary>转回微秒，用于与上游的微秒字段直接比较。</summary>
    public long ToUnixMicroseconds() => Caliber switch
    {
        TimestampCaliber.Microseconds => RawValue,
        TimestampCaliber.Milliseconds => RawValue * 1_000L,
        TimestampCaliber.Seconds => RawValue * 1_000_000L,
        _ => throw new InvalidOperationException($"未知的时间刻度：{Caliber}"),
    };

    public override string ToString() =>
        IsUnset ? "(未设置)" : Utc.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>时间戳的来源刻度。</summary>
public enum TimestampCaliber
{
    Microseconds,
    Milliseconds,
    Seconds,
}
