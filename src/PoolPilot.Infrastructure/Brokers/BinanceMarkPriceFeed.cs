using Microsoft.Extensions.Logging;

using PoolPilot.Domain.Entities;

using BinanceSocketClient = Binance.Net.Interfaces.Clients.IBinanceSocketClient;

namespace PoolPilot.Infrastructure.Brokers;

/// <summary>
/// 币安的**每秒标记价**流——建立在 CryptoClients.Net 的 Binance.Net 之上。
///
/// 它存在的唯一理由：未实现盈亏是标记价的函数，而**币安的用户数据流不推价格**——
/// 只在仓位真的变化时推。想实时看到盈亏，就得自己拿标记价当场算
/// （公式与验证见 <c>LivePnl</c>）。
///
/// 一条连接订阅全部持仓合约，不是每个合约一条：九个仓位开九条 WebSocket，
/// 光是握手与心跳就够把限额吃掉。组合流、路由路径、重连这些迁移前是自己拼的，
/// 现在由库负责（库把多个订阅复用在同一连接上）。
///
/// 持仓集合变了就**换一份订阅**：调用方按"集合真的变了"取消令牌、再起一个新任务
/// （见 AccountStreamRegistry.StartMarkFeed），别每秒都换。
/// </summary>
public sealed class BinanceMarkPriceFeed(ILogger<BinanceMarkPriceFeed> logger)
{
    /// <summary>
    /// 订阅给定的合约集合，一直跑到取消。每收到一批标记价就回调一次
    /// （回调里不要做重活：那是接收循环）。
    /// </summary>
    public async Task RunAsync(
        BinanceSocketClient socket,
        ExchangeConfig config,
        Func<IReadOnlyList<string>> symbols,
        Func<IReadOnlyList<(string Symbol, decimal Price)>, CancellationToken, Task> onMarks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(onMarks);

        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<string> watching = symbols();

            if (watching.Count == 0)
            {
                // 没有持仓就没什么可订阅的：等一会儿再看（持仓可能在下一轮对账里出现）
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            // 每个合约一份订阅：库把它们复用在同一条连接上（组合流是它内部的事），
            // 所以这里不必自己拼 stream 名。每秒一次，与迁移前那条 @markPrice@1s 同口径。
            // 回调里不 await 具体动作——它在接收线程上跑，重活会顶掉心跳
            List<CryptoExchange.Net.Objects.Sockets.UpdateSubscription> subscriptions = [];

            foreach (string symbol in watching)
            {
                CryptoExchange.Net.Objects.WebSocketResult<CryptoExchange.Net.Objects.Sockets.UpdateSubscription> subscribed =
                    await socket.UsdFuturesApi.ExchangeData
                        .SubscribeToMarkPriceUpdatesAsync(
                            symbol,

                            // 毫秒，且库只接受 1000 与 3000（1000 → 上游的 @markPrice@1s）
                            1000,
                            update => _ = onMarks(
                                [(update.Data.Symbol, update.Data.MarkPrice)],
                                cancellationToken),
                            cancellationToken)
                        .ConfigureAwait(false);

                if (!subscribed.Success)
                {
                    await CloseAllAsync(subscriptions).ConfigureAwait(false);

                    throw BinanceClients.Failure(subscribed, "订阅标记价");
                }

                subscriptions.Add(subscribed.Data);
            }

            logger.LogInformation(
                "{Config} 的标记价流已订阅 {Count} 个合约",
                config.DisplayName,
                watching.Count);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                // 换订阅或收工：先把这一份退掉（库会把连接留给别的订阅）
                await CloseAllAsync(subscriptions).ConfigureAwait(false);
            }
        }
    }

    private static async Task CloseAllAsync(
        IReadOnlyList<CryptoExchange.Net.Objects.Sockets.UpdateSubscription> subscriptions)
    {
        foreach (CryptoExchange.Net.Objects.Sockets.UpdateSubscription subscription in subscriptions)
        {
            await subscription.CloseAsync().ConfigureAwait(false);
        }
    }
}
