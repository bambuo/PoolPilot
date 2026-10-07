using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 一笔未成交挂单（对应 WS <c>account_all_orders</c> 的 <c>orders</c> 项）。
///
/// 挂单是理解池子策略的关键：实测某池 96% 的成交是挂单被动成交（maker），
/// 它的边际来自"挂着等别人来吃"。跟单者必然吃单，等于把这部分边际反转成成本——
/// 所以必须先看清池子挂在哪里，再谈跟不跟。
/// </summary>
public sealed record PoolOrder
{
    public required long OrderIndex { get; init; }

    public required long ClientOrderIndex { get; init; }

    public required int MarketIndex { get; init; }

    public required OrderSide Side { get; init; }

    public required PriceAmount Price { get; init; }

    /// <summary>剩余未成交数量（基础币）。</summary>
    public required decimal RemainingBaseAmount { get; init; }

    /// <summary>下单时的原始数量（基础币）。</summary>
    public required decimal InitialBaseAmount { get; init; }

    public required string Status { get; init; }

    public required LighterTimestamp CreatedAt { get; init; }

    public required LighterTimestamp ExpiresAt { get; init; }

    /// <summary>已成交数量。用于识别"部分成交"——这是阶梯正在被逐档吃进的直接证据。</summary>
    public decimal FilledBaseAmount => InitialBaseAmount - RemainingBaseAmount;

    public bool IsPartiallyFilled => FilledBaseAmount > 0m && RemainingBaseAmount > 0m;

    /// <summary>该挂单的剩余名义价值。</summary>
    public UsdAmount RemainingNotional => UsdAmount.From(RemainingBaseAmount * Price.Value);
}
