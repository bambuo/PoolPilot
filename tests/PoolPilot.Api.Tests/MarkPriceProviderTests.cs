using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.ValueObjects;
using PoolPilot.Infrastructure.Monitoring;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 盯市价取法的契约测试。
///
/// 锁住的是一件**实测出来的性能事实**：一个跟单员可能同时持有十几个市场，
/// 而每个市场的盘口都要新建一次 WebSocket 连接（实测约 0.75 秒）。串行取就是十几秒的空白页。
/// 所以这里有两条断言必须成立：**并发取回**、**短期缓存命中**。
/// 第三条同样重要：**单个市场失败不能连累其它市场**，它只该进"不可得"名单。
/// </summary>
[TestClass]
public sealed class MarkPriceProviderTests
{
    [TestMethod]
    public async Task ResolveAsync_FetchesManyMarketsInParallel()
    {
        // 6 个市场、每个 200ms：串行要 1.2 秒。放宽到 600ms 既证明并发，
        // 又不至于在拥挤的 CI 上抖动失败。
        FakePoolsClient client = new(delay: TimeSpan.FromMilliseconds(200));
        MarkPriceProvider provider = Provider(client);

        Stopwatch watch = Stopwatch.StartNew();

        MarkPrices result = await provider.ResolveAsync([1, 2, 3, 4, 5, 6], CancellationToken.None);

        watch.Stop();

        Assert.HasCount(6, result.Prices);
        Assert.IsEmpty(result.Unavailable);
        Assert.IsLessThan(
            600,
            watch.ElapsedMilliseconds,
            $"6 个市场应在并发下取回，实测 {watch.ElapsedMilliseconds}ms（串行约 1200ms）");
    }

    [TestMethod]
    public async Task ResolveAsync_WithinCacheWindow_DoesNotRefetch()
    {
        // 页面刷新是常态操作。不缓存的话每次刷新都要重付"每个市场一次连接"的钱。
        FakePoolsClient client = new(delay: TimeSpan.Zero);
        MarkPriceProvider provider = Provider(client);

        await provider.ResolveAsync([7, 8], CancellationToken.None);
        await provider.ResolveAsync([7, 8], CancellationToken.None);

        Assert.AreEqual(2, client.Calls, "第二次应命中缓存，不再请求上游");
    }

    [TestMethod]
    public async Task ResolveAsync_WhenOneMarketFails_ReportsItAndKeepsTheRest()
    {
        // 一个市场取不到价，界面显示"该市场未实现盈亏不可得"；
        // 但它绝不能把整页弄成错误页——其它市场的数据仍然有用。
        FakePoolsClient client = new(delay: TimeSpan.Zero, failingMarketId: 42);
        MarkPriceProvider provider = Provider(client);

        MarkPrices result = await provider.ResolveAsync([41, 42, 43], CancellationToken.None);

        Assert.HasCount(2, result.Prices);
        Assert.IsTrue(result.Prices.ContainsKey(41));
        Assert.IsTrue(result.Prices.ContainsKey(43));
        CollectionAssert.Contains(result.Unavailable.ToList(), 42, "取不到的市场要如实上报，而不是当成 0");
    }

    [TestMethod]
    public async Task ResolveAsync_WhenBookHasNoMid_ReportsUnavailable()
    {
        // 单边空盘口：取到了盘口但没有有效中间价。这同样属于"不知道"，
        // 不能退化成 0——0 看起来像"刚好打平"。
        FakePoolsClient client = new(delay: TimeSpan.Zero, emptyMarketId: 55);
        MarkPriceProvider provider = Provider(client);

        MarkPrices result = await provider.ResolveAsync([55], CancellationToken.None);

        Assert.IsEmpty(result.Prices);
        CollectionAssert.Contains(result.Unavailable.ToList(), 55);
    }

    private static MarkPriceProvider Provider(FakePoolsClient client)
    {
        ServiceCollection services = new();
        services.AddSingleton<ILighterPoolsClient>(client);

        return new MarkPriceProvider(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MarkPriceProvider>.Instance);
    }

    /// <summary>只实现盯市需要的那一个方法，其余抛异常——测试里不该有静默走通的假实现。</summary>
    private sealed class FakePoolsClient(
        TimeSpan delay,
        int? failingMarketId = null,
        int? emptyMarketId = null) : ILighterPoolsClient
    {
        private int _calls;

        public int Calls => _calls;

        public async Task<OrderBookSnapshot> FetchOrderBookAsync(
            int marketId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            if (marketId == failingMarketId)
            {
                throw new InvalidOperationException($"市场 {marketId} 取价失败");
            }

            decimal mid = marketId == emptyMarketId ? 0m : 100m + marketId;

            return new OrderBookSnapshot
            {
                MarketId = marketId,
                TakenAt = LighterTimestamp.FromSeconds(1_800_000_000),
                Bids = [new OrderBookLevel { Price = PriceAmount.From(mid - 0.1m), Size = 10m }],
                Asks = [new OrderBookLevel { Price = PriceAmount.From(mid + 0.1m), Size = 10m }],
            };
        }

        public Task<IReadOnlyList<Pool>> FetchPoolsAsync(PoolFilter filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LighterSystemConfig> FetchSystemConfigAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PoolSnapshot> FetchSnapshotAsync(AccountIndex account, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PoolInfo> FetchPoolInfoAsync(AccountIndex account, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PoolTrade>> FetchRecentTradesAsync(
            AccountIndex account,
            int limit = 50,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PoolOrder>> FetchOrdersAsync(
            AccountIndex account,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<InstrumentSpec>> FetchInstrumentsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
