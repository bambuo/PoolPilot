using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 池的持仓变化事件——两次快照相减的结果。
///
/// 这是纸面跟单的信号源。它由本地差分产出，而不是上游推送的语义事件：
/// 上游只会告诉你"某个市场的仓位现在是 X"，"这是开仓还是加仓"必须自己判断。
/// </summary>
public sealed record PoolEvent
{
    /// <summary>事件所属的池。事件必然属于某个池，缺它既无法持久化也无法按池查询。</summary>
    public required AccountIndex AccountIndex { get; init; }

    public required PoolEventKind Kind { get; init; }

    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    /// <summary>变化后的方向。平仓事件时沿用变化前的方向。</summary>
    public required PositionSide Side { get; init; }

    /// <summary>变化前的仓位规模（带符号，正为多）。</summary>
    public required decimal SizeBefore { get; init; }

    /// <summary>变化后的仓位规模（带符号，正为多）。</summary>
    public required decimal SizeAfter { get; init; }

    public required LighterTimestamp DetectedAt { get; init; }

    /// <summary>事件发生时的参考价，用于把规模变化折算成名义金额。</summary>
    public required PriceAmount ReferencePrice { get; init; }

    /// <summary>仓位数量变化（带符号）。</summary>
    public decimal SizeDelta => SizeAfter - SizeBefore;

    /// <summary>名义金额变化。加仓为正、减仓为负。</summary>
    public UsdAmount NotionalDelta =>
        UsdAmount.From(SizeDelta * ReferencePrice.Value);

    /// <summary>是否在增加敞口（开仓或加仓）。</summary>
    public bool IsExposureIncreasing => Kind is PoolEventKind.Open or PoolEventKind.Increase;

    /// <summary>
    /// 事件的时间桶（秒级），用于构造幂等键。
    ///
    /// 不用"仓位规模的绝对值"做键的一部分，是因为连续的加仓可能规模相同，
    /// 而它们必须被识别为两个独立事件。
    /// </summary>
    public long TimeBucket => DetectedAt.Utc.ToUnixTimeSeconds() / 5;

    public override string ToString() =>
        $"{Symbol} {Kind}: {SizeBefore} → {SizeAfter} @ {ReferencePrice}";
}
