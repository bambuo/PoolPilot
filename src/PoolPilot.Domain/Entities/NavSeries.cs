using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>净值曲线上的一点。</summary>
public sealed record NavPoint
{
    public required LighterTimestamp Timestamp { get; init; }

    /// <summary>份额净值（每份资产的价值）。</summary>
    public required decimal SharePrice { get; init; }
}

/// <summary>
/// 池的净值序列（来自 <c>pool_info.share_prices</c>）。
///
/// **必须用份额净值而不是 <c>total_asset_value</c>**：后者会被申购与赎回影响，
/// 直接看它的变化率会把"有人存入"误判成"赚了"。份额净值对申赎免疫。
/// </summary>
public sealed record NavSeries
{
    public required AccountIndex AccountIndex { get; init; }

    public required IReadOnlyList<NavPoint> Points { get; init; }

    /// <summary>按时间升序排列，去重后返回。上游返回的顺序不保证。</summary>
    public NavSeries Normalized()
    {
        List<NavPoint> ordered = [.. Points
            .Where(p => p.SharePrice > 0m)
            .GroupBy(p => p.Timestamp.RawValue)
            .Select(g => g.First())
            .OrderBy(p => p.Timestamp.Utc)];

        return this with { Points = ordered };
    }

    public bool HasEnoughPoints => Points.Count >= 2;
}
