using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Services;

/// <summary>
/// 持仓差分器：把两次快照相减，判定池子做了什么。
///
/// 上游不会告诉你"这是开仓还是加仓"——它只报"某个市场的仓位现在是 X"。
/// 语义必须由本地差分产出，而差分的关键是**用带符号的规模**做判断：
/// <c>PoolPosition.Size</c> 只有绝对值，方向在 <c>Side</c> 里，
/// 直接用绝对值相减会把"反手"误判成"加仓"。
/// </summary>
public static class PositionDiffer
{
    private const decimal SizeTolerance = 0.0000001m;

    /// <summary>比较两次快照，返回发生变化的市场的事件列表。</summary>
    public static IReadOnlyList<PoolEvent> Diff(PoolSnapshot previous, PoolSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        Dictionary<int, PoolPosition> before = ToSignedMap(previous);
        Dictionary<int, PoolPosition> after = ToSignedMap(current);

        List<PoolEvent> events = [];

        foreach (int marketId in before.Keys.Union(after.Keys).Order())
        {
            before.TryGetValue(marketId, out PoolPosition? previousPosition);
            after.TryGetValue(marketId, out PoolPosition? currentPosition);

            decimal sizeBefore = previousPosition?.Size ?? 0m;
            decimal sizeAfter = currentPosition?.Size ?? 0m;

            if (Math.Abs(sizeAfter - sizeBefore) <= SizeTolerance)
            {
                continue;
            }

            PoolPosition reference = currentPosition ?? previousPosition!;

            events.Add(new PoolEvent
            {
                AccountIndex = current.AccountIndex,
                Kind = Classify(sizeBefore, sizeAfter),
                MarketId = marketId,
                Symbol = reference.Symbol,
                Side = ResolveSide(sizeAfter, sizeBefore),
                SizeBefore = sizeBefore,
                SizeAfter = sizeAfter,
                DetectedAt = current.TakenAt,
                ReferencePrice = reference.AvgEntryPrice.Value > 0m
                    ? reference.AvgEntryPrice
                    : PriceAmount.Zero,
            });
        }

        return events;
    }

    /// <summary>
    /// 按带符号规模判定事件种类。
    ///
    /// 注意"反手"必须单独识别：多头 −100 变成空头 −50 时，
    /// 若只看绝对值（100 → 50）会误判成减仓，而实际上它平了多单还反手做空。
    /// </summary>
    private static PoolEventKind Classify(decimal sizeBefore, decimal sizeAfter)
    {
        bool wasFlat = sizeBefore == 0m;
        bool isFlat = sizeAfter == 0m;

        if (wasFlat)
        {
            return PoolEventKind.Open;
        }

        if (isFlat)
        {
            return PoolEventKind.Close;
        }

        if (Math.Sign(sizeBefore) != Math.Sign(sizeAfter))
        {
            return PoolEventKind.Flip;
        }

        return Math.Abs(sizeAfter) > Math.Abs(sizeBefore)
            ? PoolEventKind.Increase
            : PoolEventKind.Reduce;
    }

    /// <summary>
    /// 变化后的方向。平仓事件时（<paramref name="sizeAfter"/> 为 0）沿用变化前的方向，
    /// 让"平掉的是多单还是空单"能被读出来。
    /// </summary>
    private static PositionSide ResolveSide(decimal sizeAfter, decimal sizeBefore)
    {
        decimal effective = sizeAfter != 0m ? sizeAfter : sizeBefore;

        return effective >= 0m ? PositionSide.Long : PositionSide.Short;
    }

    /// <summary>
    /// 把快照转成「市场 → 带符号规模」的映射。
    ///
    /// 用临时的合成 PoolPosition 承载带符号规模，避免为此再定义一套中间类型——
    /// 这个映射只在差分内部使用，不对外暴露。
    /// </summary>
    private static Dictionary<int, PoolPosition> ToSignedMap(PoolSnapshot snapshot)
    {
        Dictionary<int, PoolPosition> map = [];

        foreach (PoolPosition position in snapshot.ActivePositions)
        {
            decimal signed = position.Side == PositionSide.Long ? position.Size : -position.Size;
            map[position.MarketId] = position with { Size = signed };
        }

        return map;
    }
}
