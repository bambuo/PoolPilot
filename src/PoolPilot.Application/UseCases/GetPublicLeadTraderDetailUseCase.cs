using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.Mapping;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 公开带单员详情（带单广场点卡片打开）。
///
/// 数据源与工作台池详情同源：池元数据、<c>pool_info</c> 的净值序列、账户快照；
/// 指标仍由 <see cref="NavAnalyzer"/> 实算，上报值与实算值并列。
///
/// **刻意不取**：挂单、逐笔成交、清算价、入场价、运营者钱包地址。
/// 匿名访客需要的是"这个池值不值得跟"，不是交易执行细节——后者既可能被用来抢先交易，
/// 也超出公共留言板的职责。持仓只给前几大的方向/名义额/占比。
///
/// 上游调用是三路（池清单、净值、快照），比工作台池详情少挂单与成交两路；
/// 打开弹窗只发生一次，不是轮询。
/// </summary>
public sealed class GetPublicLeadTraderDetailUseCase(
    ILeadTraderRepository leads,
    ListLeadTradersUseCase leadList,
    ILighterPoolsClient client)
{
    public async Task<PublicLeadTraderDetailDto?> ExecuteAsync(
        long accountIndex,
        CancellationToken cancellationToken = default)
    {
        AccountIndex pool;

        try
        {
            pool = AccountIndex.From(accountIndex);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        LeadTrader? lead = await leads.GetAsync(pool, cancellationToken).ConfigureAwait(false);

        if (lead is null)
        {
            return null;
        }

        Task<IReadOnlyList<Pool>> poolsTask = client.FetchPoolsAsync(PoolFilter.All, cancellationToken);
        Task<PoolInfo> infoTask = client.FetchPoolInfoAsync(pool, cancellationToken);
        Task<PoolSnapshot> snapshotTask = client.FetchSnapshotAsync(pool, cancellationToken);

        LeadTraderDto leadDto;

        try
        {
            leadDto = await leadList.ExecuteAsync(pool, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // 检查与列表查询之间被删了：公开面当作不存在
            return null;
        }

        await Task.WhenAll(poolsTask, infoTask, snapshotTask).ConfigureAwait(false);

        Pool? poolEntity = (await poolsTask.ConfigureAwait(false))
            .FirstOrDefault(p => p.AccountIndex == pool);

        if (poolEntity is null)
        {
            return null;
        }

        PoolInfo info = await infoTask.ConfigureAwait(false);
        PoolSnapshot snapshot = await snapshotTask.ConfigureAwait(false);
        PoolSummaryDto summary = PoolMapper.ToSummary(poolEntity);
        NavMetrics metrics = NavAnalyzer.Compute(info.Nav);
        IReadOnlyList<NavPointDto> fullCurve = PoolMapper.ToNavCurve(info.Nav);

        NavPointDto? lastNav = fullCurve.Count > 0 ? fullCurve[^1] : null;

        decimal gross = snapshot.GrossExposure.Value;

        return new PublicLeadTraderDetailDto
        {
            AccountIndex = leadDto.AccountIndex,
            Name = leadDto.Name,
            Origin = summary.Origin,
            IsFrozen = summary.IsFrozen,
            IsWatching = leadDto.IsWatching,
            FollowerCount = leadDto.FollowerCount,
            SignalCount = leadDto.SignalCount,
            LastSignalAt = leadDto.LastSignalAt,
            ObservedSince = leadDto.CreatedAt,
            PoolCreatedAt = summary.CreatedAt,
            TotalAssetValue = summary.TotalAssetValue,
            TotalShares = summary.TotalShares,
            SharePrice = lastNav?.SharePrice,
            NavAt = lastNav?.At,
            OperatorFeePercent = summary.OperatorFeePercent,
            OperatorSharePercent = info.OperatorShareRatio.Percent,
            MinOperatorSharePercent = info.MinOperatorShareRate.Percent,
            IsDepositLimited = info.IsDepositLimited,
            Metrics = new PublicLeadMetricsDto
            {
                ReportedApyPercent = summary.ReportedApyPercent,
                ReportedSharpe = summary.ReportedSharpe,
                RealTotalReturnPercent = metrics.TotalReturnPercent,
                RealAnnualizedReturnPercent = metrics.AnnualizedReturnPercent,
                RealMaxDrawdownPercent = metrics.MaxDrawdownPercent,
                RealSharpe = metrics.Sharpe,
                PeriodVolatilityPercent = metrics.PeriodVolatilityPercent,
                LossPeriods = metrics.LossPeriods,
                TotalPeriods = metrics.TotalPeriods,
                SpanDays = metrics.SpanDays,
                IsStatisticallyMeaningful = metrics.IsStatisticallyMeaningful,
            },
            Exposure = new PublicLeadExposureDto
            {
                GrossExposure = gross,
                NetExposure = snapshot.NetExposure.Value,
                ExposureRatioPercent = snapshot.ExposureRatio.Percent,
                ImbalanceRatio = snapshot.ImbalanceRatio,
                LiquidationBuffer = snapshot.LiquidationBuffer.Value,
                ActivePositionCount = snapshot.ActivePositions.Count,
            },
            Positions =
            [
                .. snapshot.ActivePositions
                    .OrderByDescending(p => Math.Abs(p.Notional.Value))
                    .Take(PublicPageLimits.PositionsOnLeadDetail)
                    .Select(p => new PublicLeadPositionDto
                    {
                        MarketId = p.MarketId,
                        Symbol = p.Symbol,
                        Side = p.Side == PositionSide.Long ? "long" : "short",
                        Notional = Math.Abs(p.Notional.Value),
                        WeightPercent = gross <= 0m
                            ? 0m
                            : Math.Abs(p.Notional.Value) / gross * 100m,
                    }),
            ],
            NavCurve =
            [
                .. fullCurve
                    .Skip(Math.Max(0, fullCurve.Count - PublicPageLimits.NavPointsOnLeadDetail))
                    .Select(p => new PublicLeadNavPointDto
                    {
                        At = p.At,
                        SharePrice = p.SharePrice,
                    }),
            ],
        };
    }
}
