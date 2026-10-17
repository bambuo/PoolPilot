using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Infrastructure.Monitoring;

/// <summary>
/// 盯市价提供者：短缓存 + 有上限的并发。
///
/// 两处刻意的取舍：
/// <list type="number">
///   <item>**缓存 60 秒**。盯市价是估值输入，不是成交依据；而一个跟单员可能持有十几个市场，
///     每个市场的盘口都要新建一次连接（实测约 0.75 秒），不缓存的话每次翻页都要重付这笔钱。
///     **被实时行情频道覆盖的市场不走这个缓存**：<c>Update</c> 在每条 market_stats 帧到达时
///     刷新缓存，后续 ResolveAsync 直接拿到最新价；REST 只服务没订阅频道的市场。
///     缓存**不与监控用的那份共享**：两边对"新鲜"的要求不同（监控要按盘口定价成交），
///     混在一起会让"成交必须最新"这条规则被估值的宽松需求悄悄放宽。</item>
///   <item>**并发上限 6**。全并发会在页面刷新时堆出十几条连接，既招限速也抢资源；
///     串行则让十几个市场的页面等到十几秒。上限值取的是"两三个批次就能取完"。</item>
/// </list>
///
/// 单个市场失败**不扔异常**：它进 Unavailable 名单，界面据此显示"未实现盈亏不可得"。
/// 一个市场取了三次都失败，不该让整页打不开。
/// </summary>
public sealed class MarkPriceProvider(
    IServiceScopeFactory scopeFactory,
    ILogger<MarkPriceProvider> logger) : IMarkPrices, IMarkPriceSink
{
    /// <summary>盘口缓存存活时间（秒）。</summary>
    private const int CacheSeconds = 60;

    /// <summary>同时取多少个市场的盘口。</summary>
    private const int MaxParallel = 6;

    private readonly ConcurrentDictionary<int, (PriceAmount Mid, DateTimeOffset At)> _cache = [];

    /// <summary>
    /// 实时行情更新：market_stats 帧的值直接覆盖缓存。订阅了频道的市场从此不再等
    /// 60 秒过期，也不会被 REST 的旧盘口覆盖——下一次估值读取到的就是这一帧的中间价。
    /// </summary>
    public void Update(int marketId, PriceAmount mid) =>
        _cache[marketId] = (mid, DateTimeOffset.UtcNow);

    public async Task<MarkPrices> ResolveAsync(
        IReadOnlyList<int> marketIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marketIds);

        Dictionary<int, PriceAmount> prices = [];
        List<int> unavailable = [];
        List<int> missing = [];
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (int marketId in marketIds.Distinct())
        {
            if (_cache.TryGetValue(marketId, out (PriceAmount Mid, DateTimeOffset At) cached)
                && now - cached.At < TimeSpan.FromSeconds(CacheSeconds))
            {
                prices[marketId] = cached.Mid;
            }
            else
            {
                missing.Add(marketId);
            }
        }

        if (missing.Count == 0)
        {
            return new MarkPrices(prices, unavailable);
        }

        // 每个市场取一次，各自返回结果，最后在**单线程**里合并——
        // 让多个任务直接往同一个字典里写会在页面刷新时撞出难以复现的异常
        (int MarketId, PriceAmount? Mid)[] results = new (int, PriceAmount?)[missing.Count];
        using SemaphoreSlim gate = new(MaxParallel, MaxParallel);

        await Task.WhenAll(missing.Select(async (marketId, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                results[index] = (marketId, await FetchMidAsync(marketId, cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        DateTimeOffset fetchedAt = DateTimeOffset.UtcNow;

        foreach ((int marketId, PriceAmount? mid) in results)
        {
            if (mid is { Value: > 0m } value)
            {
                prices[marketId] = value;
                _cache[marketId] = (value, fetchedAt);
            }
            else
            {
                if (mid is not null)
                {
                    // 取到了盘口却没有有效中间价（单边空盘口）：同样属于"不知道"
                    logger.LogDebug("市场 {MarketId} 的盘口没有有效中间价", marketId);
                }

                unavailable.Add(marketId);
            }
        }

        return new MarkPrices(prices, unavailable);
    }

    private async Task<PriceAmount?> FetchMidAsync(int marketId, CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            OrderBookSnapshot book = await scope.ServiceProvider
                .GetRequiredService<ILighterPoolsClient>()
                .FetchOrderBookAsync(marketId, cancellationToken)
                .ConfigureAwait(false);

            return book.MidPrice;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "取市场 {MarketId} 的盯市价失败", marketId);

            return null;
        }
    }
}
