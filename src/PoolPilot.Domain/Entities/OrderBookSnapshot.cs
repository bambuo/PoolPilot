using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>盘口的一档。</summary>
public sealed record OrderBookLevel
{
    public required PriceAmount Price { get; init; }

    /// <summary>该价位的挂单量（基础币）。</summary>
    public required decimal Size { get; init; }

    public UsdAmount Notional => UsdAmount.From(Price.Value * Size);
}

/// <summary>
/// 盘口快照（来自 WS <c>order_book/{market_id}</c>）。
///
/// 用途单一但关键：算"我们跟着下单能拿到什么价"。这是纸面跟单里唯一能产出
/// 真实摩擦成本的数据源——没有它，跟单收益只能靠假设，而假设出来的数字没有决策价值。
/// </summary>
public sealed record OrderBookSnapshot
{
    public required int MarketId { get; init; }

    /// <summary>买侧，按价格降序（最优买价在前）。</summary>
    public required IReadOnlyList<OrderBookLevel> Bids { get; init; }

    /// <summary>卖侧，按价格升序（最优卖价在前）。</summary>
    public required IReadOnlyList<OrderBookLevel> Asks { get; init; }

    public required LighterTimestamp TakenAt { get; init; }

    public PriceAmount? BestBid => Bids.Count > 0 ? Bids[0].Price : null;

    public PriceAmount? BestAsk => Asks.Count > 0 ? Asks[0].Price : null;

    /// <summary>中间价。</summary>
    public PriceAmount? MidPrice =>
        BestBid is { } bid && BestAsk is { } ask
            ? PriceAmount.From((bid.Value + ask.Value) / 2m)
            : null;

    /// <summary>买卖价差（基点）。深度差的标的上这个数字会显著放大跟单成本。</summary>
    public decimal SpreadBps
    {
        get
        {
            if (BestBid is not { } bid || BestAsk is not { } ask || bid.Value <= 0m)
            {
                return 0m;
            }

            return (ask.Value - bid.Value) / bid.Value * 10_000m;
        }
    }

    /// <summary>取指定方向的可成交档位。</summary>
    public IReadOnlyList<OrderBookLevel> LevelsFor(Enums.OrderSide side) =>
        side == Enums.OrderSide.Buy ? Asks : Bids;

    /// <summary>
    /// 该方向的最优可成交价（买看卖一、卖看买一）。无档位时返回 0。
    ///
    /// 与 <see cref="BestBid"/>/<see cref="BestAsk"/> 的区别：那两个是"别人挂的价"，
    /// 这个是"我下单会立刻碰到的价"，用于在成交前估算名义金额是否够最小下单额。
    /// </summary>
    public decimal BestPriceFor(Enums.OrderSide side) =>
        LevelsFor(side) is { Count: > 0 } levels ? levels[0].Price.Value : 0m;
}
