using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 回测入参。
///
/// **本回测是参数化情景模型，不是逐笔模拟。** 原因很实在：上游不提供历史逐笔成交
/// （<c>export/historicalTrades</c> 需要一次性 100 LIT 解锁），我们只有净值序列。
/// 所以跟单曲线的构造方式是「池子的毛收益 − 按假设的摩擦成本逐日累积的拖累」，
/// 而不是真的把每一笔成交重放一遍。
///
/// 假设值应当来自实测：台账页测出的往返成本与日均交易笔数，直接填进来即可。
/// 界面上必须显式呈现这两个假设，避免读者把它当成精确预测。
/// </summary>
public sealed record BacktestOptions
{
    /// <summary>往返摩擦成本（基点）。默认 5 bps 来自深盘标的的实测量级。</summary>
    public decimal AssumedRoundTripCostBps { get; init; } = 5m;

    /// <summary>日均跟单笔数。实测某池约每小时 1~2 次动作，取 24 偏保守。</summary>
    public decimal AssumedTradesPerDay { get; init; } = 2m;

    /// <summary>
    /// 跟单时的敞口比例。跟单者为按比例复刻，取池的敞口比例。
    /// 为 0 时退化为用池的历史平均敞口（无法得知，故取一个保守默认值 0.05）。
    /// </summary>
    public decimal ExposureRatioOverride { get; init; } = 0m;
}

/// <summary>回测：三方净值对照。</summary>
public sealed class RunBacktestUseCase(ILighterPoolsClient client)
{
    /// <summary>无法得知池的历史敞口时采用的保守默认值（5%）。</summary>
    private const decimal DefaultExposureRatio = 0.05m;

    public async Task<BacktestResultDto?> ExecuteAsync(
        AccountIndex account,
        BacktestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        BacktestOptions opts = options ?? new BacktestOptions();

        Task<IReadOnlyList<Pool>> poolsTask = client.FetchPoolsAsync(PoolFilter.All, cancellationToken);
        Task<PoolInfo> infoTask = client.FetchPoolInfoAsync(account, cancellationToken);

        await Task.WhenAll(poolsTask, infoTask).ConfigureAwait(false);

        Pool? pool = poolsTask.Result.FirstOrDefault(p => p.AccountIndex == account);

        if (pool is null)
        {
            return null;
        }

        NavSeries nav = infoTask.Result.Nav.Normalized();

        if (!nav.HasEnoughPoints)
        {
            return null;
        }

        decimal firstPrice = nav.Points[0].SharePrice;
        decimal operatorFeeFraction = pool.OperatorFee.Fraction;
        decimal exposureRatio = opts.ExposureRatioOverride > 0m
            ? opts.ExposureRatioOverride
            : DefaultExposureRatio;

        // 每笔跟单的成本（占跟单本金的比例）= 敞口比例 × 往返成本
        decimal costPerTradeFraction = exposureRatio * opts.AssumedRoundTripCostBps / 10_000m;

        // 每日拖累（相对跟单本金的收益百分点）
        decimal dailyDragPercent = opts.AssumedTradesPerDay * costPerTradeFraction * 100m;

        List<BacktestCurvePointDto> curve = [];
        decimal cumulativeDragPercent = 0m;

        for (int i = 0; i < nav.Points.Count; i++)
        {
            NavPoint point = nav.Points[i];
            decimal poolNav = point.SharePrice / firstPrice;
            decimal daysElapsed = i;

            // 池子自身：净值序列即真相
            // 入池方案：利润扣运营费，本金不受影响
            decimal depositNav = 1m + ((poolNav - 1m) * (1m - operatorFeeFraction));

            // 跟单方案：先按池子毛收益走，再扣累积摩擦成本。
            // 成本按日累计而非按净值比例——它是本金上的固定消耗，不随盈利放大。
            cumulativeDragPercent = dailyDragPercent * daysElapsed / 100m;
            decimal copyNav = poolNav - cumulativeDragPercent;

            curve.Add(new BacktestCurvePointDto
            {
                At = point.Timestamp.Utc,
                PoolNav = poolNav,
                CopyNav = copyNav,
                DepositNav = depositNav,
            });
        }

        BacktestCurvePointDto last = curve[^1];
        int spanDays = (int)Math.Round((nav.Points[^1].Timestamp.Utc - nav.Points[0].Timestamp.Utc).TotalDays);

        return new BacktestResultDto
        {
            AccountIndex = account.Value,
            PoolName = pool.Name,
            PoolTotalReturnPercent = (last.PoolNav - 1m) * 100m,
            CopyTotalReturnPercent = (last.CopyNav - 1m) * 100m,
            DepositTotalReturnPercent = (last.DepositNav - 1m) * 100m,
            AssumedRoundTripCostBps = opts.AssumedRoundTripCostBps,
            OperatorFeePercent = pool.OperatorFee.Percent,
            SampleDays = spanDays,
            Curve = curve,
        };
    }
}
