using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Services;

/// <summary>池在某个标的上的单轮交易（从建仓到清仓）。</summary>
public sealed record RoundTrip
{
    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    public required PositionSide Side { get; init; }

    /// <summary>本轮的建仓总量（累计开仓侧数量，不因中途减仓而减少）。</summary>
    public required decimal EntrySize { get; init; }

    public required PriceAmount EntryVwap { get; init; }

    public required PriceAmount ExitVwap { get; init; }

    public required UsdAmount EntryNotional { get; init; }

    public required UsdAmount ExitNotional { get; init; }

    /// <summary>毛利（未扣费）。上游成交流里的费率字段单位未定义，故不在此扣减。</summary>
    public required UsdAmount GrossPnl { get; init; }

    public required LighterTimestamp OpenedAt { get; init; }

    public required LighterTimestamp ClosedAt { get; init; }

    public required TradeRole EntryRole { get; init; }

    public required TradeRole ExitRole { get; init; }

    /// <summary>是否已清仓。未清仓表示观察窗口结束时仍有持仓。</summary>
    public required bool IsClosed { get; init; }

    /// <summary>收益率（百分数）。</summary>
    public PercentValue ReturnPercent =>
        EntryNotional.Value == 0m
            ? PercentValue.FromPercent(0m)
            : PercentValue.FromPercent(GrossPnl.Value / EntryNotional.Value * 100m);

    public TimeSpan HoldingPeriod => ClosedAt.Utc - OpenedAt.Utc;
}

/// <summary>池的成交构成概要。</summary>
public sealed record TradeComposition
{
    public required int TradeCount { get; init; }

    public required int MakerCount { get; init; }

    public required int TakerCount { get; init; }

    public required UsdAmount BoughtNotional { get; init; }

    public required UsdAmount SoldNotional { get; init; }

    public required decimal MedianTradeNotional { get; init; }

    /// <summary>挂单成交占比（百分数）。接近 100% 说明池子是纯被动成交型。</summary>
    public PercentValue MakerSharePercent =>
        TradeCount == 0
            ? PercentValue.FromPercent(0m)
            : PercentValue.FromPercent((decimal)MakerCount / TradeCount * 100m);
}

/// <summary>
/// 成交分类器。
///
/// 回答两个决定"能不能跟单"的问题：
/// <list type="number">
///   <item><b>池子是挂单方还是吃单方</b>——实测某池 96% 的成交是挂单被动成交（maker），
///     它的边际来自"挂着等别人来吃"；跟单者必然吃单，等于把这部分边际反转成成本。</item>
///   <item><b>单轮的真实盈亏与持仓时长</b>——决定跟单的时间成本与收益幅度。</item>
/// </list>
/// </summary>
public static class TradeClassifier
{
    /// <summary>统计成交构成。只统计普通成交，排除强平 / 减仓 / 结算。</summary>
    public static TradeComposition Summarize(IEnumerable<PoolTrade> trades, AccountIndex account)
    {
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentNullException.ThrowIfNull(account);

        List<PoolTrade> relevant = [.. trades.Where(t => t.IsRegularTrade && t.Involves(account))];

        if (relevant.Count == 0)
        {
            return new TradeComposition
            {
                TradeCount = 0,
                MakerCount = 0,
                TakerCount = 0,
                BoughtNotional = UsdAmount.Zero,
                SoldNotional = UsdAmount.Zero,
                MedianTradeNotional = 0m,
            };
        }

        int makerCount = relevant.Count(t => t.RoleOf(account) == TradeRole.Maker);

        decimal bought = relevant
            .Where(t => t.SideOf(account) == OrderSide.Buy)
            .Sum(t => t.Usd.Value);

        decimal sold = relevant
            .Where(t => t.SideOf(account) == OrderSide.Sell)
            .Sum(t => t.Usd.Value);

        return new TradeComposition
        {
            TradeCount = relevant.Count,
            MakerCount = makerCount,
            TakerCount = relevant.Count - makerCount,
            BoughtNotional = UsdAmount.From(bought),
            SoldNotional = UsdAmount.From(sold),
            MedianTradeNotional = Median(relevant.Select(t => t.Usd.Value)),
        };
    }

    /// <summary>
    /// 从成交流水重建单轮交易。
    ///
    /// 算法：按时间顺序遍历，逐市场维护一个「进行中的一轮」。同向成交累加建仓侧，
    /// 反向成交按加权平均成本结转已实现盈亏；仓位归零即结算本轮，方向翻转时先结旧再开新。
    /// </summary>
    /// <param name="trades">成交流水，内部会按时间排序。</param>
    /// <param name="account">要分析哪个账户。</param>
    /// <param name="symbolResolver">市场 ID → 符号。缺省时用 <c>market-{id}</c> 占位。</param>
    public static IReadOnlyList<RoundTrip> ReconstructRoundTrips(
        IEnumerable<PoolTrade> trades,
        AccountIndex account,
        Func<int, string>? symbolResolver = null)
    {
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentNullException.ThrowIfNull(account);

        List<PoolTrade> ordered = [.. trades
            .Where(t => t.IsRegularTrade && t.Involves(account))
            .OrderBy(t => t.Timestamp.Utc)];

        Dictionary<int, InProgressTrip> inProgress = [];
        List<RoundTrip> completed = [];

        foreach (PoolTrade trade in ordered)
        {
            OrderSide side = trade.SideOf(account);
            TradeRole role = trade.RoleOf(account);
            decimal signedQuantity = side == OrderSide.Buy ? trade.Size : -trade.Size;

            inProgress.TryGetValue(trade.MarketId, out InProgressTrip? state);
            state ??= InProgressTrip.Start(trade.MarketId, trade.Timestamp);

            if (state.IsFlat || Math.Sign(state.SignedSize) == Math.Sign(signedQuantity))
            {
                state = state.AddToPosition(signedQuantity, trade.Usd.Value, trade.Timestamp, role);
            }
            else
            {
                (state, RoundTrip? closedTrip) = state.ReducePosition(
                    signedQuantity,
                    trade.Price.Value,
                    trade.Timestamp,
                    role);

                if (closedTrip is not null)
                {
                    completed.Add(closedTrip with
                    {
                        Symbol = symbolResolver?.Invoke(trade.MarketId) ?? $"market-{trade.MarketId}",
                    });
                }
            }

            if (state.IsFlat)
            {
                inProgress.Remove(trade.MarketId);
            }
            else
            {
                inProgress[trade.MarketId] = state;
            }
        }

        // 观察窗口结束时仍未平掉的仓位：产出未完结的一轮，让调用方看见它
        foreach (InProgressTrip state in inProgress.Values)
        {
            completed.Add(state.ToClosedRoundTrip() with
            {
                Symbol = symbolResolver?.Invoke(state.MarketId) ?? $"market-{state.MarketId}",
                IsClosed = false,
            });
        }

        return [.. completed.OrderBy(r => r.OpenedAt.Utc)];
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        List<decimal> sorted = [.. values.Order()];

        if (sorted.Count == 0)
        {
            return 0m;
        }

        int middle = sorted.Count / 2;

        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2m;
    }

    /// <summary>
    /// 进行中的一轮交易。
    ///
    /// 关键设计：同时维护两组量——<see cref="SignedSize"/> 是**当前持仓**（会被减仓削减），
    /// <see cref="EntrySize"/> / <see cref="EntryNotional"/> 是本轮的**累计建仓侧**（只增不减）。
    /// 已完结的一轮必须用后者汇总，否则平仓后当前持仓归零，会报出 size=0、入场价=0 的空报告。
    /// </summary>
    private sealed record InProgressTrip
    {
        public required int MarketId { get; init; }

        /// <summary>带符号当前持仓，正为多。</summary>
        public required decimal SignedSize { get; init; }

        /// <summary>当前持仓的剩余成本（名义金额）。</summary>
        public required decimal OpenCost { get; init; }

        /// <summary>本轮累计建仓数量（只增不减）。</summary>
        public required decimal EntrySize { get; init; }

        /// <summary>本轮累计建仓名义金额（只增不减）。</summary>
        public required decimal EntryNotional { get; init; }

        public required decimal RealizedPnl { get; init; }

        public required LighterTimestamp OpenedAt { get; init; }

        public required LighterTimestamp LastAt { get; init; }

        public required TradeRole EntryRole { get; init; }

        public required TradeRole LastRole { get; init; }

        public bool IsFlat => SignedSize == 0m;

        private decimal AverageEntryPrice => SignedSize == 0m ? 0m : OpenCost / Math.Abs(SignedSize);

        public static InProgressTrip Start(int marketId, LighterTimestamp at) => new()
        {
            MarketId = marketId,
            SignedSize = 0m,
            OpenCost = 0m,
            EntrySize = 0m,
            EntryNotional = 0m,
            RealizedPnl = 0m,
            OpenedAt = at,
            LastAt = at,
            EntryRole = TradeRole.Maker,
            LastRole = TradeRole.Maker,
        };

        public InProgressTrip AddToPosition(
            decimal signedQuantity,
            decimal usd,
            LighterTimestamp at,
            TradeRole role)
        {
            bool wasFlat = IsFlat;

            return this with
            {
                SignedSize = SignedSize + signedQuantity,
                OpenCost = OpenCost + usd,
                EntrySize = EntrySize + Math.Abs(signedQuantity),
                EntryNotional = EntryNotional + usd,
                LastAt = at,
                EntryRole = wasFlat ? role : EntryRole,
                LastRole = role,
            };
        }

        /// <summary>
        /// 反向成交：按加权平均成本结转已实现盈亏。
        ///
        /// 返回更新后的状态，以及**若本轮就此完结或反手**则返回已完结的那一轮。
        /// </summary>
        public (InProgressTrip State, RoundTrip? ClosedTrip) ReducePosition(
            decimal signedQuantity,
            decimal price,
            LighterTimestamp at,
            TradeRole role)
        {
            decimal averageEntry = AverageEntryPrice;
            decimal direction = Math.Sign(SignedSize);
            decimal closingSize = Math.Min(Math.Abs(signedQuantity), Math.Abs(SignedSize));
            decimal realized = (price - averageEntry) * closingSize * direction;

            InProgressTrip afterClose = this with
            {
                SignedSize = SignedSize - (direction * closingSize),
                OpenCost = OpenCost - (averageEntry * closingSize),
                RealizedPnl = RealizedPnl + realized,
                LastAt = at,
                LastRole = role,
            };

            if (afterClose.IsFlat)
            {
                return (afterClose, afterClose.ToClosedRoundTrip());
            }

            decimal remainder = Math.Abs(signedQuantity) - closingSize;

            if (remainder <= 0m)
            {
                return (afterClose, null);
            }

            // 反手：先结掉旧方向，剩余部分开新方向
            RoundTrip closedTrip = afterClose.ToClosedRoundTrip();

            InProgressTrip flipped = new()
            {
                MarketId = MarketId,
                SignedSize = Math.Sign(signedQuantity) * remainder,
                OpenCost = remainder * price,
                EntrySize = remainder,
                EntryNotional = remainder * price,
                RealizedPnl = 0m,
                OpenedAt = at,
                LastAt = at,
                EntryRole = role,
                LastRole = role,
            };

            return (flipped, closedTrip);
        }

        public RoundTrip ToClosedRoundTrip()
        {
            decimal entryVwap = EntrySize == 0m ? 0m : EntryNotional / EntrySize;
            decimal exitVwap = EntrySize == 0m ? entryVwap : entryVwap + (RealizedPnl / EntrySize);

            return new RoundTrip
            {
                MarketId = MarketId,
                Symbol = $"market-{MarketId}",
                Side = SignedSize < 0m ? PositionSide.Short : PositionSide.Long,
                EntrySize = EntrySize,
                EntryVwap = PriceAmount.From(entryVwap),
                ExitVwap = PriceAmount.From(exitVwap),
                EntryNotional = UsdAmount.From(EntryNotional),
                ExitNotional = UsdAmount.From(EntryNotional + RealizedPnl),
                GrossPnl = UsdAmount.From(RealizedPnl),
                OpenedAt = OpenedAt,
                ClosedAt = LastAt,
                EntryRole = EntryRole,
                ExitRole = LastRole,
                IsClosed = true,
            };
        }
    }
}
