using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 纸面账户在某一时刻的盯市估值点，构成权益曲线。
///
/// 为什么要落库而不是每次读时现算：未实现盈亏依赖**当时的盯市价**，
/// 而盘口是过期不候的。事后拿当前价格重算历史权益，得到的曲线是失真的
/// （等价于假设"持仓一直是现在这个价"）。所以每次监控到变化就当场记一个点。
/// </summary>
public sealed record PaperEquityPoint
{
    public long Id { get; init; }

    /// <summary>归属的跟单员（自增 Id，见 <see cref="PaperFill.FollowerId"/> 的说明）。</summary>
    public required long FollowerId { get; init; }

    /// <summary>权益 = 本金 + 已实现盈亏 + 未实现盈亏 − 手续费。</summary>
    public required UsdAmount Equity { get; init; }

    public required UsdAmount RealizedPnl { get; init; }

    public required UsdAmount UnrealizedPnl { get; init; }

    public required UsdAmount Fees { get; init; }

    /// <summary>该点的持仓名义总额，用于分辨"权益没动是因为没持仓还是因为价格没动"。</summary>
    public required UsdAmount GrossNotional { get; init; }

    public required LighterTimestamp TakenAt { get; init; }
}
