using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Following;

/// <summary>
/// 带单员发出的一次信号：池子在某个市场上的一次仓位变化。
///
/// 信号复用 <see cref="PoolEvent"/>（它已经是"池子这一刻动了什么"的完备描述），
/// 外面再包两样跟单员自己拿不到的东西：
/// <list type="bullet">
///   <item><see cref="PoolName"/> —— 记账与界面要可读，不能只有池索引。</item>
///   <item><see cref="PoolEquity"/> —— 规模比例是"我的本金 ÷ 池子权益"。
///     缺了它，跟单员为了算目标仓位还得自己回取一次池子状态，
///     那就不叫"订阅信号即可交易"了。</item>
/// </list>
/// </summary>
public sealed record LeadSignal(
    PoolEvent Event,
    string PoolName,
    UsdAmount PoolEquity)
{
    public AccountIndex Pool => Event.AccountIndex;

    public PoolEventKind Kind => Event.Kind;

    public int MarketId => Event.MarketId;

    public LighterTimestamp DetectedAt => Event.DetectedAt;

    /// <summary>变化后池子在该市场的仓位（带符号；平仓为零）。跟单员的目标仓位由它乘比例得到。</summary>
    public SignedSize PositionAfter => SignedSize.FromSide(Event.SizeAfter, Event.Side);
}
