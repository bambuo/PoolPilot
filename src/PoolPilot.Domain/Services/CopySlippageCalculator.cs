using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Services;

/// <summary>走盘口的结果。</summary>
public sealed record BookWalkResult
{
    public required decimal RequestedSize { get; init; }

    public required decimal FilledSize { get; init; }

    /// <summary>盘口深度是否吃得下这个规模。为 <c>false</c> 时 <see cref="Vwap"/> 只覆盖部分成交。</summary>
    public required bool FullyFilled { get; init; }

    public required PriceAmount Vwap { get; init; }

    public required UsdAmount Notional { get; init; }

    public required int LevelsConsumed { get; init; }
}

/// <summary>跟单漏损。</summary>
public sealed record CopySlippage
{
    public required OrderSide Side { get; init; }

    public required decimal CopySize { get; init; }

    /// <summary>我方模拟成交的加权均价。</summary>
    public required PriceAmount OurVwap { get; init; }

    /// <summary>池子的实际成交价。</summary>
    public required PriceAmount PoolPrice { get; init; }

    public required bool DepthSufficient { get; init; }

    /// <summary>
    /// 漏损（基点）。**正数表示我方比池子差**，即真实的跟单成本。
    ///
    /// 符号约定刻意统一成"正=亏"：买单时我方均价高于池子即为正，
    /// 卖单时我方均价低于池子即为正。这样两条腿可以直接相加。
    /// </summary>
    public required decimal SlippageBps { get; init; }

    /// <summary>换算成金额的成本。用于累加总摩擦成本。</summary>
    public UsdAmount CostAmount =>
        UsdAmount.From(CopySize * PoolPrice.Value * Math.Abs(SlippageBps) / 10_000m);
}

/// <summary>
/// 跟单漏损计算器——**本项目的关键决策门**。
///
/// 它回答的问题是：池子成交在 P，我们在检测到之后以市价跟同样的方向与规模，
/// 能拿到什么价？差值就是跟单的真实摩擦成本。
///
/// 为什么必须走真实盘口而不是假设：池子绝大多数成交是**挂单被动成交**（实测某池 96%），
/// 它赚的是价差；跟单者必然吃单，付的也是价差。这个方向反转的成本只能从盘口算出来，
/// 用固定滑点百分比假设会得出毫无决策价值的数字。
/// </summary>
public static class CopySlippageCalculator
{
    /// <summary>
    /// 走盘口算加权成交均价。
    ///
    /// 深度不足时返回部分成交的结果并置 <see cref="BookWalkResult.FullyFilled"/> 为 <c>false</c>，
    /// 而不是静默按最后一档价格成交——那会系统性低估大额跟单的成本。
    /// </summary>
    public static BookWalkResult Walk(OrderBookSnapshot book, OrderSide side, decimal size)
    {
        ArgumentNullException.ThrowIfNull(book);

        IReadOnlyList<OrderBookLevel> levels = book.LevelsFor(side);

        if (size <= 0m || levels.Count == 0)
        {
            return new BookWalkResult
            {
                RequestedSize = size,
                FilledSize = 0m,
                FullyFilled = false,
                Vwap = PriceAmount.Zero,
                Notional = UsdAmount.Zero,
                LevelsConsumed = 0,
            };
        }

        decimal remaining = size;
        decimal notional = 0m;
        int consumed = 0;

        foreach (OrderBookLevel level in levels)
        {
            if (remaining <= 0m)
            {
                break;
            }

            decimal take = Math.Min(remaining, level.Size);
            notional += take * level.Price.Value;
            remaining -= take;
            consumed++;
        }

        decimal filled = size - remaining;

        return new BookWalkResult
        {
            RequestedSize = size,
            FilledSize = filled,
            FullyFilled = remaining <= 0m,
            Vwap = filled > 0m ? PriceAmount.From(notional / filled) : PriceAmount.Zero,
            Notional = UsdAmount.From(notional),
            LevelsConsumed = consumed,
        };
    }

    /// <summary>
    /// 计算单腿漏损。
    ///
    /// 返回值的符号约定见 <see cref="CopySlippage.SlippageBps"/>：正数表示我方更差。
    /// </summary>
    public static CopySlippage Compute(OrderBookSnapshot book, OrderSide side, decimal copySize, PriceAmount poolPrice)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(poolPrice);

        BookWalkResult walk = Walk(book, side, copySize);

        return new CopySlippage
        {
            Side = side,
            CopySize = copySize,
            OurVwap = walk.Vwap,
            PoolPrice = poolPrice,
            DepthSufficient = walk.FullyFilled,
            SlippageBps = ComputeSlippageBps(walk.Vwap, poolPrice, side),
        };
    }

    /// <summary>
    /// 单腿漏损（基点，正数＝我方更差）。
    ///
    /// 买腿：我方均价高于池子即成本为正。
    /// 卖腿：我方均价低于池子即成本为正。
    /// </summary>
    public static decimal ComputeSlippageBps(PriceAmount ourPrice, PriceAmount poolPrice, OrderSide side)
    {
        ArgumentNullException.ThrowIfNull(ourPrice);
        ArgumentNullException.ThrowIfNull(poolPrice);

        if (ourPrice.Value <= 0m || poolPrice.Value <= 0m)
        {
            return 0m;
        }

        return side == OrderSide.Buy
            ? (ourPrice.Value / poolPrice.Value - 1m) * 10_000m
            : (poolPrice.Value / ourPrice.Value - 1m) * 10_000m;
    }

    /// <summary>
    /// 往返总成本：入场腿 + 出场腿。这是判断"跟单是否还剩边际"的最终依据。
    /// </summary>
    public static decimal TotalRoundTripBps(decimal entryBps, decimal exitBps) => entryBps + exitBps;
}
