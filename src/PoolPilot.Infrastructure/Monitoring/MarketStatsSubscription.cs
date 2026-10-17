using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using PoolPilot.Application.Abstractions;
using PoolPilot.Infrastructure.Lighter;

namespace PoolPilot.Infrastructure.Monitoring;

/// <summary>
/// 按市场追加 <c>market_stats/{market_id}</c> 订阅（WS 客户端内部会去重并处理重连回填）。
///
/// 触发点是"公开详情/工作台跟单页打开了、且这一行持有该市场"——不在启动时订阅全市场。
/// 协议没有验证过的退订帧，所以频道一旦订上就保留到进程结束；重复调用只做一次。
/// </summary>
public sealed class MarketStatsSubscription(
    LighterWebSocketClient socket,
    ILogger<MarketStatsSubscription> logger) : IMarketStatsSubscription
{
    private readonly ConcurrentDictionary<int, byte> _subscribed = [];

    public void EnsureSubscribed(int marketId)
    {
        if (!_subscribed.TryAdd(marketId, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await socket.SubscribeAsync($"market_stats/{marketId}", CancellationToken.None)
                    .ConfigureAwait(false);

                logger.LogDebug("已订阅市场 {MarketId} 的实时行情", marketId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 发送失败就允许下次重试；连接若正在重建，客户端会在重连时带上动态频道
                _subscribed.TryRemove(marketId, out _);
                logger.LogWarning(
                    exception,
                    "订阅市场 {MarketId} 的实时行情失败；下次打开公开详情或跟单页会重试。",
                    marketId);
            }
        });
    }
}
