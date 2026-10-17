using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 纸面跟单的一条动作记录。
///
/// 它记录的是**我方的动作**，不是池子的动作：池子发生了什么变化 → 我方目标仓位变成多少
/// → 差额是多少 → 结果要么成交、要么因约束被跳过。
///
/// 这张流水是纸面账户的**唯一事实来源**：仓位账、已实现盈亏都由它重放推出来，
/// 不再单独存一份"当前持仓"。这样避免了"持仓与流水对不上"这种最难查的脏数据——
/// 一旦两者不一致，你无法判断该信哪个。
///
/// 被跳过的信号**也要记**：漏记会让账户看起来完美跟踪了池子，
/// 而实际上本金不足时你根本没跟上，那正是最需要暴露的事实。
/// </summary>
public sealed record PaperFill
{
    /// <summary>持久化主键；未入库时为 0。</summary>
    public long Id { get; init; }

    /// <summary>
    /// 归属的跟单员。
    ///
    /// 用跟单员的自增 Id 而不是池索引：一个池可以挂多个跟单员，
    /// 池索引不足以定位"这条流水是谁的账"。池子侧的信息（池仓、池权益）本来就在下面的字段里。
    /// </summary>
    public required long FollowerId { get; init; }

    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    /// <summary>我方成交方向。<b>被跳过时为 <c>null</c></b>。</summary>
    public required OrderSide? Side { get; init; }

    // ── 池子侧（驱动信号）────────────────────────────────

    /// <summary>触发本次动作时池子在该市场的仓位（带符号）。</summary>
    public required SignedSize PoolPositionAfter { get; init; }

    /// <summary>池子当时的权益，用于复现比例。存下来才能解释当时的规模是怎么算的。</summary>
    public required UsdAmount PoolEquity { get; init; }

    // ── 我方侧 ──────────────────────────────────────────

    /// <summary>计算出的目标仓位（池仓 × 比例）。</summary>
    public required SignedSize TargetPosition { get; init; }

    /// <summary>动作发出前我方的仓位。</summary>
    public required SignedSize PositionBefore { get; init; }

    /// <summary>动作完成后我方的仓位。被跳过时等于 <see cref="PositionBefore"/>。</summary>
    public required SignedSize PositionAfter { get; init; }

    /// <summary>本次成交数量（绝对值）。被跳过时为 0。</summary>
    public required decimal Size { get; init; }

    /// <summary>我方成交均价。被跳过时为 0。</summary>
    public required PriceAmount Price { get; init; }

    /// <summary>我方成交名义金额（USDT）。被跳过时为 0。</summary>
    public required UsdAmount Notional { get; init; }

    /// <summary>盘口深度是否吃得下。为 <c>false</c> 时本次成交价系统性偏乐观。</summary>
    public required bool DepthSufficient { get; init; }

    /// <summary>手续费（USDT）。实测 Lighter Standard 档 maker/taker 均为 0，故通常为 0。</summary>
    public required UsdAmount Fee { get; init; }

    /// <summary>本次平仓部分结转的已实现盈亏。开仓或加仓时为 0。</summary>
    public required UsdAmount RealizedPnl { get; init; }

    /// <summary>
    /// 成交时源池在该市场的初始保证金率（百分数，25 表示 25%）。
    ///
    /// 纸面账户沿用源池的杠杆口径；加仓时按成交名义额加权。旧流水没有这一列时为
    /// <c>null</c>，读取时退回源池当前保证金率。
    /// </summary>
    public decimal? InitialMarginPercent { get; init; }

    /// <summary>被跳过的原因；成交时为 <c>null</c>。</summary>
    public required PaperSkipReason? SkipReason { get; init; }

    public required LighterTimestamp ObservedAt { get; init; }

    /// <summary>是否真的成交了。</summary>
    public bool IsFilled => SkipReason is null;

    /// <summary>本次动作是否缩小了敞口（用于区分开仓与平仓的语气）。</summary>
    public bool IsReducing => !PositionBefore.IsFlat
        && (PositionAfter.IsFlat || PositionBefore.HasSameDirectionAs(PositionAfter)
            ? PositionAfter.Absolute < PositionBefore.Absolute
            : true);
}
