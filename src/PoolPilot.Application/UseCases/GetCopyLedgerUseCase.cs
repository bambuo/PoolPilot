using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.Mapping;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 跟单台账与漏损报告。
///
/// 这是把"跟单值不值得做"从推测变成数字的地方：台账里每一条都是
/// 「池子成交价 vs 我方按真实盘口走盘的模拟价」的对照，报告则把它们聚合成
/// 可决策的摩擦成本，并对照池子自身的单轮收益幅度。
/// </summary>
public sealed class GetCopyLedgerUseCase(
    ILighterPoolsClient client,
    ICopyFillRepository copyFills)
{
    /// <summary>报告的默认样本量。</summary>
    private const int DefaultLimit = 200;

    public async Task<CopyLedgerDto?> ExecuteAsync(
        AccountIndex account,
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        Task<IReadOnlyList<CopyFill>> fillsTask = copyFills.GetRecentAsync(account, limit, cancellationToken);
        Task<IReadOnlyList<Pool>> poolsTask = client.FetchPoolsAsync(PoolFilter.All, cancellationToken);
        Task<IReadOnlyList<PoolTrade>> tradesTask = client.FetchRecentTradesAsync(account, cancellationToken: cancellationToken);

        await Task.WhenAll(fillsTask, poolsTask, tradesTask).ConfigureAwait(false);

        IReadOnlyList<CopyFill> fills = fillsTask.Result;
        string poolName = poolsTask.Result.FirstOrDefault(p => p.AccountIndex == account)?.Name ?? $"池 {account.Value}";

        // 池子自身的单轮收益幅度：用于判断摩擦成本吃掉了多少
        IReadOnlyList<RoundTrip> trips = TradeClassifier.ReconstructRoundTrips(
            tradesTask.Result,
            account,
            id => $"market-{id}");

        decimal averagePoolReturnPercent = trips.Count == 0
            ? 0m
            : trips.Average(t => t.ReturnPercent.Percent);

        return new CopyLedgerDto
        {
            AccountIndex = account.Value,
            PoolName = poolName,
            Entries = [.. fills.Select(f => PoolMapper.ToCopyFill(f, poolName))],
            Report = BuildReport(account, poolName, fills, averagePoolReturnPercent),
        };
    }

    private static SlippageReportDto BuildReport(
        AccountIndex account,
        string poolName,
        IReadOnlyList<CopyFill> fills,
        decimal averagePoolReturnPercent)
    {
        if (fills.Count == 0)
        {
            return new SlippageReportDto
            {
                AccountIndex = account.Value,
                PoolName = poolName,
                SampleCount = 0,
                MedianEntrySlippageBps = 0m,
                P90EntrySlippageBps = 0m,
                EstimatedRoundTripBps = 0m,
                AveragePoolRoundTripReturnPercent = averagePoolReturnPercent,
                ByMarket = [],
            };
        }

        // 只有延迟在有效窗口内的样本才参与聚合。
        // 拿当前盘口去比历史成交价算出的是价格变动而非摩擦成本——
        // 实测会产生 −107 bps 的"负滑点"，即"我方价格比池子好"，逻辑上不可能。
        List<CopyFill> valid = [.. fills.Where(f => f.DetectionLatencyMs <= LighterConstants.SlippageValidityWindowMs)];

        if (valid.Count == 0)
        {
            return new SlippageReportDto
            {
                AccountIndex = account.Value,
                PoolName = poolName,
                SampleCount = 0,
                MedianEntrySlippageBps = 0m,
                P90EntrySlippageBps = 0m,
                EstimatedRoundTripBps = 0m,
                AveragePoolRoundTripReturnPercent = averagePoolReturnPercent,
                ByMarket = [],
            };
        }

        decimal[] slippages = [.. valid.Select(f => f.SlippageBps).Order()];
        decimal median = Percentile(slippages, 0.5m);
        decimal p90 = Percentile(slippages, 0.9m);

        List<MarketSlippageDto> byMarket = [];

        foreach (IGrouping<int, CopyFill> group in valid.GroupBy(f => f.MarketId))
        {
            decimal[] values = [.. group.Select(f => f.SlippageBps).Order()];

            byMarket.Add(new MarketSlippageDto
            {
                MarketId = group.Key,
                Symbol = group.First().Symbol,
                SampleCount = group.Count(),
                MedianSlippageBps = Percentile(values, 0.5m),
                SpreadBps = 0m,
                DepthSufficient = group.All(f => f.DepthSufficient),
            });
        }

        return new SlippageReportDto
        {
            AccountIndex = account.Value,
            PoolName = poolName,
            SampleCount = valid.Count,

            // 单腿漏损的中位与 P90（正数表示我方更差）
            MedianEntrySlippageBps = median,
            P90EntrySlippageBps = p90,

            // 往返 = 两条腿。这是与池子单轮收益直接可比的口径：
            // 若往返成本吃掉了单轮收益的显著比例，跟单在经济上就不成立。
            EstimatedRoundTripBps = median * 2m,

            AveragePoolRoundTripReturnPercent = averagePoolReturnPercent,
            ByMarket = byMarket,
        };
    }

    /// <summary>分位数。输入必须已升序排列。</summary>
    private static decimal Percentile(decimal[] sortedAscending, decimal quantile)
    {
        if (sortedAscending.Length == 0)
        {
            return 0m;
        }

        int index = (int)Math.Round((sortedAscending.Length - 1) * quantile);

        return sortedAscending[Math.Clamp(index, 0, sortedAscending.Length - 1)];
    }
}
