using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 一笔成交（对应 <c>/trades</c> 或 WS 成交频道的一项）。
///
/// 这是跟单信号的原始来源。它同时携带成交双方的信息，因此可以精确回答三个问题：
/// 池子是挂单方还是吃单方、池子成交前后的仓位是多少、这笔成交让池子赚了多少。
///
/// 注意方向语义：**一笔成交有两个方向**（买侧与卖侧各一个账户），
/// 因此本类型不提供"这笔成交的方向"，只提供 <see cref="SideOf"/> / <see cref="RoleOf"/>，
/// 必须带上账户参数才能问出结果。
/// </summary>
public sealed record PoolTrade
{
    public required long TradeId { get; init; }

    public required int MarketId { get; init; }

    /// <summary>成交数量（基础币）。</summary>
    public required decimal Size { get; init; }

    public required PriceAmount Price { get; init; }

    public required UsdAmount Usd { get; init; }

    /// <summary><c>true</c> 表示挂单方在卖侧。判定角色时需要与账户索引配合。</summary>
    public required bool IsMakerAsk { get; init; }

    public required AccountIndex AskAccountIndex { get; init; }

    public required AccountIndex BidAccountIndex { get; init; }

    /// <summary>毫秒刻度（WS 成交的 <c>timestamp</c>）。</summary>
    public required LighterTimestamp Timestamp { get; init; }

    /// <summary>微秒刻度（<c>transaction_time</c>）。用于测量端到端延迟。</summary>
    public required LighterTimestamp TransactionTime { get; init; }

    /// <summary>执行类型，上游取值 <c>trade</c> / <c>liquidation</c> / <c>deleverage</c> / <c>market-settlement</c>。</summary>
    public required string ExecutionType { get; init; }

    /// <summary>挂单方的成交前仓位。上游可省略该字段（实测部分成交缺此项）。</summary>
    public decimal? MakerPositionSizeBefore { get; init; }

    /// <summary>吃单方的成交前仓位。上游可省略该字段。</summary>
    public decimal? TakerPositionSizeBefore { get; init; }

    /// <summary>普通成交（非强平 / 减仓 / 结算）。跟单信号只应基于普通成交。</summary>
    public bool IsRegularTrade => string.Equals(ExecutionType, "trade", StringComparison.Ordinal);

    /// <summary>是否涉及指定账户。</summary>
    public bool Involves(AccountIndex account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return AskAccountIndex == account || BidAccountIndex == account;
    }

    /// <summary>指定账户在这笔成交中的角色（挂单方 / 吃单方）。</summary>
    public TradeRole RoleOf(AccountIndex account)
    {
        bool isAsk = AskAccountIndex == account;
        bool isBid = BidAccountIndex == account;

        if (!isAsk && !isBid)
        {
            throw new ArgumentException($"账户 {account} 不在这笔成交（{TradeId}）中", nameof(account));
        }

        // 挂单方在 ask 侧 ⟺ 指定账户在 ask 侧的布尔值相同
        bool accountIsMaker = isAsk == IsMakerAsk;

        return accountIsMaker ? TradeRole.Maker : TradeRole.Taker;
    }

    /// <summary>指定账户在这笔成交中是买还是卖。</summary>
    public OrderSide SideOf(AccountIndex account)
    {
        if (AskAccountIndex == account)
        {
            return OrderSide.Sell;
        }

        if (BidAccountIndex == account)
        {
            return OrderSide.Buy;
        }

        throw new ArgumentException($"账户 {account} 不在这笔成交（{TradeId}）中", nameof(account));
    }

    /// <summary>指定账户在这笔成交前的仓位规模。上游未提供时返回 <c>null</c>。</summary>
    public decimal? PositionSizeBeforeOf(AccountIndex account)
    {
        TradeRole role = RoleOf(account);
        return role == TradeRole.Maker ? MakerPositionSizeBefore : TakerPositionSizeBefore;
    }

    /// <summary>
    /// 指定账户在这笔成交**之后**的仓位规模。
    ///
    /// 由"成交前仓位 ± 成交量"推导：买侧加仓、卖侧减仓。这是重建池子仓位轨迹的核心运算，
    /// 也是判定开仓 / 加仓 / 减仓 / 平仓 / 反手的依据。
    /// </summary>
    public decimal? PositionSizeAfterOf(AccountIndex account)
    {
        decimal? before = PositionSizeBeforeOf(account);

        if (before is null)
        {
            return null;
        }

        return SideOf(account) == OrderSide.Buy
            ? before.Value + Size
            : before.Value - Size;
    }
}
