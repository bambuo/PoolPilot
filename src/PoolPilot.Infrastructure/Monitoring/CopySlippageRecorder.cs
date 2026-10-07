using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Mapping;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;
using PoolPilot.Infrastructure.Lighter;

namespace PoolPilot.Infrastructure.Monitoring;

/// <summary>
/// 跟单对照记录：拿池子的每一笔真实成交去量"如果那时我也下单，会差几个基点"。
///
/// 这是**池子级**的观测，不是某个跟单员的账：它回答"这个池子的动作跟起来贵不贵"，
/// 与"某个账户赚了多少"是两件事，所以没有跟单员时也照常记录。
///
/// 记账口径见 <see cref="CopySlippageCalculator"/>：走盘口算我方 VWAP，与池子成交价相比。
/// </summary>
public sealed class CopySlippageRecorder(
    IServiceScopeFactory scopeFactory,
    LeadTraderRegistry registry,
    MarketDataCache market,
    IMonitorBroadcaster broadcaster,
    ILogger<CopySlippageRecorder> logger)
{
    /// <summary>
    /// 已处理过的成交号。重连回填会重复投递同一笔成交，不去重会重复记账。
    ///
    /// 只增不删：一笔成交在几十秒内就会因为超出测量窗口而被丢弃，
    /// 而进程重启后这张表是空的——重复投递只发生在同一连接内，因此够用。
    /// </summary>
    private readonly HashSet<long> _seenTradeIds = [];

    /// <summary>处理一个 <c>account_all_trades</c> 帧。</summary>
    public async Task RecordAsync(JsonElement tradesContainer, CancellationToken cancellationToken)
    {
        List<PoolTrade> all = [];

        foreach (JsonProperty marketEntry in tradesContainer.EnumerateObject())
        {
            IReadOnlyList<JsonElement> items = marketEntry.Value.ValueKind == JsonValueKind.Array
                ? [.. marketEntry.Value.EnumerateArray()]
                : [marketEntry.Value];

            foreach (JsonElement item in items)
            {
                all.Add(LighterEntityParser.ParseTrade(item));
            }
        }

        IReadOnlyList<long> watched = registry.WatchedAccounts();
        List<CopyFill> fills = [];

        // 一个作用域覆盖整批：符号解析、落库、池名解析都用它。
        // 逐笔成交各开一个作用域会把 DbContext 反复构建，而这里并不需要事务隔离。
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;

        foreach (PoolTrade trade in all)
        {
            if (!trade.IsRegularTrade)
            {
                continue;
            }

            AccountIndex? account = watched
                .Select(i => (AccountIndex?)AccountIndex.From(i))
                .FirstOrDefault(a => a is not null && trade.Involves(a));

            if (account is null)
            {
                continue;
            }

            // 幂等：重连回填会重复投递同一笔成交
            if (!_seenTradeIds.Add(trade.TradeId))
            {
                continue;
            }

            // 只测实时到达的成交。订阅时上游会回放一段历史成交，
            // 而那些成交的盘口已经过去——用当前盘口去比会得出"负滑点"这种无意义的数字。
            double latencyMs = (DateTimeOffset.UtcNow - trade.TransactionTime.Utc).TotalMilliseconds;

            if (latencyMs > LighterConstants.SlippageValidityWindowMs)
            {
                logger.LogDebug("跳过历史回填成交 {TradeId}（延迟 {Latency:F0}ms 超出测量窗口）", trade.TradeId, latencyMs);
                continue;
            }

            OrderBookSnapshot book = await market
                .BookAsync(trade.MarketId, cancellationToken)
                .ConfigureAwait(false);

            OrderSide side = trade.SideOf(account);
            CopySlippage slippage = CopySlippageCalculator.Compute(book, side, trade.Size, trade.Price);

            fills.Add(new CopyFill
            {
                AccountIndex = account,
                SourceTradeId = trade.TradeId,
                MarketId = trade.MarketId,
                Symbol = await market.SymbolAsync(trade.MarketId, cancellationToken).ConfigureAwait(false),
                Side = side,
                Size = trade.Size,
                PoolPrice = trade.Price,
                OurVwap = slippage.OurVwap,
                SlippageBps = slippage.SlippageBps,
                DepthSufficient = slippage.DepthSufficient,
                DetectionLatencyMs = latencyMs,
                ObservedAt = LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            });
        }

        if (fills.Count == 0)
        {
            return;
        }

        await provider
            .GetRequiredService<ICopyFillRepository>()
            .AppendAsync(fills, cancellationToken)
            .ConfigureAwait(false);

        // 池名按账户解析并缓存：一个批次可能跨多个池，而解析池名要拉全量池列表，
        // 逐条填都去拉一遍是浪费。
        Dictionary<long, string> poolNames = [];

        foreach (CopyFill fill in fills)
        {
            if (!poolNames.TryGetValue(fill.AccountIndex.Value, out string? poolName))
            {
                poolName = await market.PoolNameAsync(fill.AccountIndex, cancellationToken).ConfigureAwait(false);
                poolNames[fill.AccountIndex.Value] = poolName;
            }

            await broadcaster.BroadcastCopyFillAsync(
                PoolMapper.ToCopyFill(fill, poolName),
                cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("记录 {Count} 条跟单对照（池成交）", fills.Count);
    }
}
