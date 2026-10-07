using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.Mapping;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>枚举池列表。</summary>
public sealed class GetPoolListUseCase(ILighterPoolsClient client)
{
    /// <summary>
    /// 枚举公共池。**返回全量**，包含已冻结的池。
    ///
    /// 不在服务端过滤冻结池：实测约 43% 的池已冻结，把它们藏起来会让池子"凭空消失"，
    /// 而使用者往往正需要看到它们（例如判断某个运营者是否已经收摊）。
    /// 冻结与否作为**状态列**如实呈现，让筛选发生在使用者的眼睛里而不是我们的代码里。
    /// </summary>
    public async Task<IReadOnlyList<PoolSummaryDto>> ExecuteAsync(
        PoolFilter filter = PoolFilter.User,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Pool> pools = await client.FetchPoolsAsync(filter, cancellationToken).ConfigureAwait(false);

        return [.. pools.Select(PoolMapper.ToSummary)];
    }
}

/// <summary>读取单个池的完整观察视图。</summary>
public sealed class GetPoolDetailUseCase(ILighterPoolsClient client)
{
    public async Task<PoolDetailDto?> ExecuteAsync(
        AccountIndex account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        // 池元数据、池详情（净值 + 份额）、快照、挂单、成交 —— 五路并发
        Task<IReadOnlyList<Pool>> poolsTask = client.FetchPoolsAsync(PoolFilter.All, cancellationToken);
        Task<PoolInfo> infoTask = client.FetchPoolInfoAsync(account, cancellationToken);
        Task<PoolSnapshot> snapshotTask = client.FetchSnapshotAsync(account, cancellationToken);
        Task<IReadOnlyList<PoolOrder>> ordersTask = client.FetchOrdersAsync(account, cancellationToken);
        Task<IReadOnlyList<PoolTrade>> tradesTask = client.FetchRecentTradesAsync(account, cancellationToken: cancellationToken);
        Task<IReadOnlyList<InstrumentSpec>> instrumentsTask = client.FetchInstrumentsAsync(cancellationToken);

        await Task.WhenAll(poolsTask, infoTask, snapshotTask, ordersTask, tradesTask, instrumentsTask).ConfigureAwait(false);

        Pool? pool = poolsTask.Result.FirstOrDefault(p => p.AccountIndex == account);

        if (pool is null)
        {
            return null;
        }

        PoolInfo info = infoTask.Result;
        PoolSnapshot snapshot = snapshotTask.Result;
        IReadOnlyList<PoolOrder> orders = ordersTask.Result;
        IReadOnlyList<PoolTrade> trades = tradesTask.Result;

        NavSeries nav = info.Nav;
        Domain.Services.NavMetrics metrics = Domain.Services.NavAnalyzer.Compute(nav);

        // 单轮重建需要市场符号——从持仓里取，取不到时用 market-{id} 占位
        // 符号表取自**合约规格**而不是只从当前持仓取。
        //
        // 理由：单轮盈亏重建覆盖的是历史成交，其中会有池子**早已平掉**的市场。
        // 只查当前持仓时，那些行会退化成 "market-60" 这种占位符——
        // 而 60 号市场究竟是什么资产，使用者根本无从得知。
        // 规格表（orderBookDetails）给出全部市场的 market_id → symbol，是唯一完整来源；
        // 成交载荷里只有 market_id、没有 symbol（实测字段清单已确认）。
        Dictionary<int, string> symbols = instrumentsTask.Result
            .GroupBy(i => i.MarketId)
            .ToDictionary(g => g.Key, g => g.First().Symbol);

        // 规格表若缺某个市场（理论上不该发生），用持仓里的符号兜底
        foreach (PoolPosition position in snapshot.ActivePositions)
        {
            symbols.TryAdd(position.MarketId, position.Symbol);
        }

        IReadOnlyList<Domain.Services.RoundTrip> roundTrips = Domain.Services.TradeClassifier
            .ReconstructRoundTrips(trades, account, id => symbols.GetValueOrDefault(id, $"market-{id}"));

        // 主交易标的：成交笔数最多的市场，用于默认展示盘口与规模门槛
        int? primaryMarketId = trades
            .Where(t => t.Involves(account))
            .GroupBy(t => t.MarketId)
            .OrderByDescending(g => g.Count())
            .Select(g => (int?)g.Key)
            .FirstOrDefault();

        return new PoolDetailDto
        {
            Summary = PoolMapper.ToSummary(pool),
            OperatorSharePercent = info.OperatorShareRatio.Percent,
            MinOperatorSharePercent = info.MinOperatorShareRate.Percent,
            IsDepositLimited = info.IsDepositLimited,
            OperatorShareHeadroomPercent = info.OperatorShareHeadroom.Percent,
            Metrics = PoolMapper.ToMetrics(pool, metrics),
            NavCurve = PoolMapper.ToNavCurve(nav),
            Snapshot = PoolMapper.ToSnapshot(snapshot),
            Positions = [.. snapshot.ActivePositions.Select(PoolMapper.ToPosition)],
            Orders =
            [
                .. orders.Select(o => PoolMapper.ToOrder(
                    o,
                    symbols.GetValueOrDefault(o.MarketIndex, $"market-{o.MarketIndex}"))),
            ],
            RoundTrips = [.. roundTrips.Select(PoolMapper.ToRoundTrip)],
            TradeComposition = PoolMapper.ToComposition(
                Domain.Services.TradeClassifier.Summarize(trades, account)),
            PrimaryMarketId = primaryMarketId,
        };
    }
}
