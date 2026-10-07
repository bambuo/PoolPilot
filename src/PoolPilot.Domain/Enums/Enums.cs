using System.Diagnostics.CodeAnalysis;

namespace PoolPilot.Domain.Enums;

/// <summary>
/// 持仓方向。
///
/// 上游用 <c>sign</c> 表示：<c>1</c> 为多、<c>-1</c> 为空。
/// 注意上游的 <c>position</c> 字段**只有绝对值**，方向完全依赖 <c>sign</c>——
/// 只看 <c>position</c> 会把所有仓位读成多头。
/// </summary>
// CA1720 会认为 Long / Short 与内建类型名冲突。但这是交易领域的标准术语
// （多头 / 空头），改成 Buy / Sell 或 Up / Down 反而会丢失语义，故此处豁免。
[SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Long / Short 是持仓方向的标准领域术语，非类型名。")]
public enum PositionSide
{
    Long,
    Short,
}

/// <summary>订单方向，对应上游的 <c>is_ask</c>。</summary>
public enum OrderSide
{
    Buy,
    Sell,
}

/// <summary>持仓变化的种类，由前后两次快照差分得出。</summary>
public enum PoolEventKind
{
    /// <summary>从无到有。</summary>
    Open,

    /// <summary>同向且规模增大。</summary>
    Increase,

    /// <summary>同向且规模减小。</summary>
    Reduce,

    /// <summary>归零。</summary>
    Close,

    /// <summary>方向翻转。</summary>
    Flip,
}

/// <summary>交易中的角色。</summary>
public enum TradeRole
{
    /// <summary>挂单被动成交。</summary>
    Maker,

    /// <summary>主动吃单。</summary>
    Taker,
}
