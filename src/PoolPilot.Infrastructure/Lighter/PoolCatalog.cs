using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;

namespace PoolPilot.Infrastructure.Lighter;

/// <summary>
/// 公共池目录的缓存读口（<see cref="IPoolCatalog"/>）。
///
/// 形态照 <c>Monitoring/MarketDataCache</c>：单例，内部按作用域取
/// <see cref="ILighterPoolsClient"/>（那个客户端是 Scoped，直接装进单例就是把每个请求的
/// 连接挂到一个永生对象上）。与它不同的是**这里要加锁**：那条缓存只服务监控的串行路径，
/// 这一条服务公开页的匿名并发——同一次过期被十个请求同时发现，就会变成十次全量池枚举。
/// 于是取数在 <see cref="_gate"/> 里做一次，其余请求等它出来读同一份。
///
/// 陈旧窗口 <see cref="PublicPageLimits.PoolCatalogCacheSeconds"/> 秒。它只影响
/// "候选框里有没有刚上市的那个池"，不影响任何账与估值：被选中的索引随后送去建带单员时
/// 走的是不带缓存的 <c>FetchPoolsAsync</c>，池不存在会被明确拒绝，不会静默建出一个看不见的观察者。
/// </summary>
public sealed class PoolCatalog(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock) : IPoolCatalog, IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(PublicPageLimits.PoolCatalogCacheSeconds);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>清单与它的取回时刻**装在同一份不可变快照里**：分两个字段读会出现
    /// "新清单配旧时刻"这种既不成立也不会报错的组合。</summary>
    private volatile Snapshot? _snapshot;

    public async Task<IReadOnlyList<Pool>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        // 双检的第一次：无锁看一眼，命中就直接返回
        if (IsFresh(_snapshot))
        {
            return _snapshot!.Pools;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 拿到锁时可能已经有人取好了。不再检一次的话，每个等待者都要重付一次全量枚举
            if (IsFresh(_snapshot))
            {
                return _snapshot!.Pools;
            }

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            IReadOnlyList<Pool> pools = await scope.ServiceProvider
                .GetRequiredService<ILighterPoolsClient>()
                .FetchPoolsAsync(PoolFilter.All, cancellationToken)
                .ConfigureAwait(false);

            _snapshot = new Snapshot(pools, clock.GetUtcNow());

            return pools;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// 缓存里只存成功取回的那一份：取数失败时 <c>_snapshot</c> 不会被写成空清单，
    /// 一次网络抖动因此不会变成一个窗口的"没有池子可选"（那在界面上与真的没有池子长得一样）。
    /// 异常原样抛出去，由中间件按上游故障映射，下一次请求重试。
    /// </summary>
    private bool IsFresh(Snapshot? snapshot) =>
        snapshot is not null && clock.GetUtcNow() - snapshot.At < Lifetime;

    private sealed record Snapshot(IReadOnlyList<Pool> Pools, DateTimeOffset At);
}
