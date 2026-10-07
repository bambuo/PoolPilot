using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.ValueObjects;
using PoolPilot.Infrastructure.Lighter;

namespace PoolPilot.Infrastructure.Monitoring;

/// <summary>
/// 带单员的运行期配置。取代原来的"监控名单"：被观察的池由**带单员表**决定，
/// 这里的 <see cref="SeedAccountIndexes"/> 只是首次运行时给表播种的初始名单
/// （"这个池我只想看着，不跟"就写在这里）。
/// </summary>
public sealed class LeadTraderOptions
{
    /// <summary>首次启动时播种成带单员的池账户索引。之后以库里的带单员表为准。</summary>
    public IReadOnlyList<long> SeedAccountIndexes { get; set; } = [];

    /// <summary>盘口缓存的存活时间（秒）。跟单漏损要对每笔成交取盘口，但盘口变化快、请求重，故做短时缓存。</summary>
    public int OrderBookCacheSeconds { get; set; } = 20;

    /// <summary>是否启用监控。关掉后服务不订阅任何频道。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 状态心跳间隔（秒）。
    ///
    /// 状态只在**变化**时广播，而"最近一条上游消息时间"是**持续变化**的量。
    /// 没有心跳的话，界面拿到的永远是上一次状态变化时的旧时间戳——
    /// 于是"管道空闲"和"管道已死"看起来一模一样，恰好毁掉状态通道存在的理由。
    /// 心跳同时是跟单的兜底驱动：仓位频道只在真正变化时推送，心跳负责把
    /// "池子一直没动"与"我落后了"这两种情况分开。
    /// </summary>
    public int StatusHeartbeatSeconds { get; set; } = 15;

    /// <summary>
    /// 权益曲线上两个点之间的最小间隔（秒）。
    ///
    /// 盯市曲线需要的是"随时间变化"的形状，所以有持仓时要定期记点；
    /// 但按心跳（15 秒）记会产生一天 5760 个点，其中绝大多数与前一帧只差毫厘，
    /// 曲线没有变得更细，只是更慢更花。5 分钟粒度对"一天交易数次"的策略足够，
    /// 且一有成交就立即补一个点，不会错过关键时刻。
    /// </summary>
    public int EquityPointIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// **连接断开期间**的 REST 兜底间隔（秒）。
    ///
    /// 连接正常时这条路径永不触发：推送始终比 REST 快照新，叠加轮询只会让更新的推送
    /// 被更旧的快照覆盖，凭空造出"平了又开"的假动作。但没有它的话，断线期间
    /// 跟单员会一直静止到重连为止——界面上看起来和正常运行一模一样，那是最难排查的一类"坏"。
    /// </summary>
    public int PaperRestReconcileSeconds { get; set; } = 60;
}

/// <summary>
/// 带单员与跟单员共用的市场数据缓存：合约规格、盘口、符号、池名。
///
/// **不加锁**：所有调用点都在监控的串行路径上（帧处理与心跳共用一把派发锁）。
/// 这里不是通用缓存，是一个"监控进程内的热点数据视图"，把它做成线程安全会让读代码的人
/// 以为它可以被任意并发调用。
/// </summary>
public sealed class MarketDataCache(
    IServiceScopeFactory scopeFactory,
    IOptions<LeadTraderOptions> options,
    ILogger<MarketDataCache> logger)
{
    private readonly Dictionary<int, InstrumentSpec> _instruments = [];
    private readonly Dictionary<int, (OrderBookSnapshot Book, DateTimeOffset At)> _books = [];
    private readonly Dictionary<long, string> _poolNames = [];

    /// <summary>
    /// 合约规格（市场 id → 规格）。符号由它派生，因此不再单独维护符号表。
    /// 规格里含最小下单数量与最小下单金额——纸面跟单的物理约束就靠它判定。
    /// </summary>
    public async Task<Dictionary<int, InstrumentSpec>> InstrumentsAsync(CancellationToken cancellationToken)
    {
        if (_instruments.Count > 0)
        {
            return _instruments;
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IReadOnlyList<InstrumentSpec> specs = await scope.ServiceProvider
            .GetRequiredService<ILighterPoolsClient>()
            .FetchInstrumentsAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (InstrumentSpec spec in specs)
        {
            _instruments[spec.MarketId] = spec;
        }

        return _instruments;
    }

    /// <summary>
    /// 市场 id → 符号。查不到时刷新一次缓存，新上市的市场也能覆盖到；
    /// 仍解析不到就退回占位符号——宁可界面上显示 market-29，也不能因为一次网络失败丢掉整条记录。
    /// </summary>
    public async Task<string> SymbolAsync(int marketId, CancellationToken cancellationToken)
    {
        if (_instruments.TryGetValue(marketId, out InstrumentSpec? cached))
        {
            return cached.Symbol;
        }

        try
        {
            await InstrumentsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "市场符号解析失败，使用占位符号");
        }

        return _instruments.TryGetValue(marketId, out InstrumentSpec? resolved)
            ? resolved.Symbol
            : $"market-{marketId}";
    }

    /// <summary>池名。解析一次后缓存——池名不常变，而解析要拉全量池列表。</summary>
    public async Task<string> PoolNameAsync(AccountIndex pool, CancellationToken cancellationToken)
    {
        if (_poolNames.TryGetValue(pool.Value, out string? cached))
        {
            return cached;
        }

        string name;

        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            IReadOnlyList<Pool> pools = await scope.ServiceProvider
                .GetRequiredService<ILighterPoolsClient>()
                .FetchPoolsAsync(PoolFilter.All, cancellationToken)
                .ConfigureAwait(false);

            name = pools.FirstOrDefault(p => p.AccountIndex == pool)?.Name ?? $"池 {pool.Value}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "池名解析失败，使用兜底名称");
            return $"池 {pool.Value}";
        }

        _poolNames[pool.Value] = name;

        return name;
    }

    /// <param name="forceRefresh">
    /// 为 <c>true</c> 时忽略缓存。**要真的按这个盘口成交时必须传 true**：
    /// 缓存的意义是省掉重复请求（例如漏损测量对同一市场反复取盘口），
    /// 但用它当作成交价会引入一笔凭空的时间旅行——缓存里的盘口可能来自池子那笔交易**之前**。
    /// </param>
    public async Task<OrderBookSnapshot> BookAsync(
        int marketId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (!forceRefresh &&
            _books.TryGetValue(marketId, out (OrderBookSnapshot Book, DateTimeOffset At) cached) &&
            DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(options.Value.OrderBookCacheSeconds))
        {
            return cached.Book;
        }

        OrderBookSnapshot book;

        await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
        {
            book = await scope.ServiceProvider
                .GetRequiredService<ILighterPoolsClient>()
                .FetchOrderBookAsync(marketId, cancellationToken)
                .ConfigureAwait(false);
        }

        _books[marketId] = (book, DateTimeOffset.UtcNow);

        return book;
    }

    /// <summary>取我方持仓的盯市中间价。取不到的市场会退化成"按开仓均价估"，即未实现盈亏为 0。</summary>
    public async Task<IReadOnlyDictionary<int, PriceAmount>> MarksAsync(
        IReadOnlyList<PaperPosition> openPositions,
        CancellationToken cancellationToken)
    {
        Dictionary<int, PriceAmount> marks = [];

        foreach (PaperPosition position in openPositions)
        {
            try
            {
                OrderBookSnapshot book = await BookAsync(position.MarketId, cancellationToken).ConfigureAwait(false);

                if (book.MidPrice is { Value: > 0m } mid)
                {
                    marks[position.MarketId] = mid;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单个市场取价失败不该中断整轮对账；该市场的未实现盈亏会退化为 0，
                // 而读取接口会把"取不到价"作为 MarksUnavailable 显式告诉界面。
                logger.LogDebug(ex, "取市场 {MarketId} 盯市价失败", position.MarketId);
            }
        }

        return marks;
    }
}
