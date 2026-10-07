using PoolPilot.Domain.Entities;

namespace PoolPilot.Domain.Services;

/// <summary>从净值序列实算出的指标。与上游点估计并列展示，用于暴露偏差。</summary>
public sealed record NavMetrics
{
    public required decimal TotalReturnPercent { get; init; }

    public required decimal AnnualizedReturnPercent { get; init; }

    /// <summary>最大回撤（百分数，负值）。**上游完全不提供这一项。**</summary>
    public required decimal MaxDrawdownPercent { get; init; }

    /// <summary>年化夏普（无风险利率按 0 处理）。</summary>
    public required decimal Sharpe { get; init; }

    public required decimal PeriodVolatilityPercent { get; init; }

    public required decimal MeanPeriodReturnPercent { get; init; }

    public required int LossPeriods { get; init; }

    public required int TotalPeriods { get; init; }

    public required int SpanDays { get; init; }

    public required DateTimeOffset FirstAt { get; init; }

    public required DateTimeOffset LastAt { get; init; }

    /// <summary>样本是否足够支撑结论。少于 30 个点或跨度少于 30 天时，指标应当谨慎对待。</summary>
    public bool IsStatisticallyMeaningful => TotalPeriods >= 30 && SpanDays >= 30;
}

/// <summary>
/// 净值分析器。
///
/// 存在的理由：上游的 <c>annual_percentage_yield</c> 与 <c>sharpe_ratio</c> 是**点估计**，
/// 实测见过它同时低估收益与风险的例子——某池报年化 7.37%，从份额净值实算为 34.21%；
/// 而它根本不报最大回撤，实算是 −26.32%。
///
/// 所以本分析器产出的不是"更准的同一个数"，而是**上游拿不到的维度**（回撤、波动、亏损期数），
/// 让使用者能判断那条漂亮的净值曲线背后是什么形状。
/// </summary>
public static class NavAnalyzer
{
    private const int DaysPerYear = 365;

    public static NavMetrics Compute(NavSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        NavSeries normalized = series.Normalized();

        if (!normalized.HasEnoughPoints)
        {
            return Empty();
        }

        List<NavPoint> points = [.. normalized.Points];
        NavPoint first = points[0];
        NavPoint last = points[^1];

        decimal totalReturnPercent = (last.SharePrice / first.SharePrice - 1m) * 100m;
        double spanDays = (last.Timestamp.Utc - first.Timestamp.Utc).TotalDays;

        return new NavMetrics
        {
            TotalReturnPercent = totalReturnPercent,
            AnnualizedReturnPercent = Annualize(totalReturnPercent, spanDays),
            MaxDrawdownPercent = ComputeMaxDrawdownPercent(points),
            Sharpe = ComputeSharpe(points, spanDays),
            PeriodVolatilityPercent = ComputePeriodVolatilityPercent(points),
            MeanPeriodReturnPercent = ComputeMeanPeriodReturnPercent(points),
            LossPeriods = CountLossPeriods(points),
            TotalPeriods = points.Count - 1,
            SpanDays = (int)Math.Round(spanDays),
            FirstAt = first.Timestamp.Utc,
            LastAt = last.Timestamp.Utc,
        };
    }

    private static NavMetrics Empty() => new()
    {
        TotalReturnPercent = 0m,
        AnnualizedReturnPercent = 0m,
        MaxDrawdownPercent = 0m,
        Sharpe = 0m,
        PeriodVolatilityPercent = 0m,
        MeanPeriodReturnPercent = 0m,
        LossPeriods = 0,
        TotalPeriods = 0,
        SpanDays = 0,
        FirstAt = DateTimeOffset.UnixEpoch,
        LastAt = DateTimeOffset.UnixEpoch,
    };

    private static decimal Annualize(decimal totalReturnPercent, double spanDays)
    {
        if (spanDays <= 0d)
        {
            return 0m;
        }

        double growth = 1d + (double)totalReturnPercent / 100d;

        // 累计亏损超过 100% 时幂运算无意义，直接返回累计值
        if (growth <= 0d)
        {
            return totalReturnPercent;
        }

        double annualized = Math.Pow(growth, DaysPerYear / spanDays) - 1d;

        // 样本跨度极短时年化会被放大到荒谬量级，做一次上限截断并保留符号
        double clamped = Math.Clamp(annualized, -1d, 100d);
        return (decimal)clamped * 100m;
    }

    /// <summary>
    /// 最大回撤：逐点更新历史峰值，取回撤最深处。
    /// 这是上游完全不提供的指标，也是判断"高夏普是否靠极小回撤堆出来的"的唯一依据。
    /// </summary>
    private static decimal ComputeMaxDrawdownPercent(List<NavPoint> points)
    {
        decimal peak = points[0].SharePrice;
        decimal maxDrawdown = 0m;

        foreach (NavPoint point in points)
        {
            if (point.SharePrice > peak)
            {
                peak = point.SharePrice;
            }

            if (peak <= 0m)
            {
                continue;
            }

            decimal drawdown = (point.SharePrice / peak - 1m) * 100m;

            if (drawdown < maxDrawdown)
            {
                maxDrawdown = drawdown;
            }
        }

        return maxDrawdown;
    }

    private static List<decimal> ComputePeriodReturns(IReadOnlyList<NavPoint> points)
    {
        List<decimal> returns = [];

        for (int i = 1; i < points.Count; i++)
        {
            decimal previous = points[i - 1].SharePrice;

            if (previous <= 0m)
            {
                continue;
            }

            returns.Add(points[i].SharePrice / previous - 1m);
        }

        return returns;
    }

    private static decimal ComputeMeanPeriodReturnPercent(IReadOnlyList<NavPoint> points)
    {
        List<decimal> returns = ComputePeriodReturns(points);
        return returns.Count == 0 ? 0m : returns.Average() * 100m;
    }

    private static decimal ComputePeriodVolatilityPercent(IReadOnlyList<NavPoint> points)
    {
        List<decimal> returns = ComputePeriodReturns(points);

        if (returns.Count < 2)
        {
            return 0m;
        }

        return SampleStandardDeviation(returns) * 100m;
    }

    private static int CountLossPeriods(IReadOnlyList<NavPoint> points) =>
        ComputePeriodReturns(points).Count(r => r < 0m);

    /// <summary>
    /// 年化夏普。周期数按实际采样间隔推算（上游的份额净值是每日一条，但不做硬编码假设）。
    /// </summary>
    private static decimal ComputeSharpe(IReadOnlyList<NavPoint> points, double spanDays)
    {
        List<decimal> returns = ComputePeriodReturns(points);

        if (returns.Count < 2 || spanDays <= 0d)
        {
            return 0m;
        }

        decimal mean = returns.Average();
        decimal deviation = SampleStandardDeviation(returns);

        if (deviation <= 0m)
        {
            return 0m;
        }

        double periodsPerYear = returns.Count / (spanDays / DaysPerYear);

        return mean / deviation * (decimal)Math.Sqrt(periodsPerYear);
    }

    private static decimal SampleStandardDeviation(List<decimal> values)
    {
        if (values.Count < 2)
        {
            return 0m;
        }

        decimal mean = values.Average();
        decimal sumOfSquares = values.Sum(v => (v - mean) * (v - mean));

        return (decimal)Math.Sqrt((double)(sumOfSquares / (values.Count - 1)));
    }
}
