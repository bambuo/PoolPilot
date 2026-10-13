using Microsoft.Extensions.Logging;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;

using BinanceOrderStatus = Binance.Net.Enums.OrderStatus;
using BinanceOrderType = Binance.Net.Enums.FuturesOrderType;
using BinanceSocketClient = Binance.Net.Interfaces.Clients.IBinanceSocketClient;
using CryptoSubscriptionStatus = CryptoExchange.Net.Objects.SubscriptionStatus;
using DomainOrderSide = PoolPilot.Domain.Enums.OrderSide;

namespace PoolPilot.Infrastructure.Brokers;

/// <summary>
/// 币安 U 本位合约的**用户数据流**——建立在 CryptoClients.Net 的 Binance.Net 之上。
///
/// 与轮询的区别是"谁知道变化"：轮询里变化只有在我们去问的时候才被发现；
/// 用户数据流里**交易所主动推**——下单成交、仓位变动、保证金不足、强平，
/// 都在毫秒级到达。实盘跟单要盯的是自己的钱，这件事不该靠定时器猜。
///
/// 迁移前这里自己管 listenKey 的建/续/删、WebSocket 连接、指数退避重连，
/// 以及"币安 2026 年把合约 WS 拆成 /public /market /private 路由路径"那一段实测教训。
/// 那些现在归库管：订阅由库建立并保活，断线由库恢复。
///
/// ⚠ 但有一条**业务**语义必须留在本项目里，不能跟着连接一起交给库：
/// 库负责"把事件送到"，我们负责"它对我们意味着什么"。特别是**重连之后**——
/// 断开期间的事件拿不回来，所以上层收到 <see cref="AccountStreamEventKind.Reconnected"/>
/// 时应当去补一次状态（本系统的做法是重读账户）。
///
/// 解析刻意只做"这是哪一类事件 + 关键字段"，不去重建账户模型：
/// 推送里的账户更新给的是**变化量**，拿它拼出一个账户快照要补一堆默认值，
/// 拼错了不会报错、只会显示错数。上层收到事件后重新读一次账户（REST）才是权威的那份数据。
/// </summary>
public sealed class BinanceAccountFeed(ILogger<BinanceAccountFeed> logger)
{
    /// <summary>
    /// 一直跑下去：订阅用户数据流、把事件交给 <paramref name="onEvent"/>，直到取消。
    ///
    /// 库的订阅**没有**"收到事件才算连上"的信号，但订阅调用成功就说明 listenKey 已建、
    /// 连接已建立——所以订阅成功即发一次 <see cref="AccountStreamEventKind.Reconnected"/>，
    /// 与自研版"连上就通知上层补状态"的行为一致。
    /// </summary>
    public async Task RunAsync(
        BinanceSocketClient socket,
        ExchangeConfig config,
        Func<AccountStreamEvent, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(onEvent);

        bool firstSubscribe = true;

        // 库自己申请并续期 listenKey（传 null 表示"你来管"），也自己重连与重订阅
        CryptoExchange.Net.Objects.WebSocketResult<CryptoExchange.Net.Objects.Sockets.UpdateSubscription> subscribed =
            await socket.UsdFuturesApi.Account
                .SubscribeToUserDataUpdatesAsync(
                    onAccountUpdate: update => Pulse(onEvent, MapAccountUpdate(update.Data), cancellationToken),
                    onOrderUpdate: update => Pulse(onEvent, MapOrderUpdate(update.Data), cancellationToken),
                    onMarginUpdate: _ => Pulse(onEvent, MarginCall(), cancellationToken),
                    onLeverageUpdate: _ => Pulse(onEvent, ConfigUpdate(), cancellationToken),
                    onListenKeyExpired: _ => Pulse(onEvent, ListenKeyExpired(), cancellationToken),
                    ct: cancellationToken)
                .ConfigureAwait(false);

        if (!subscribed.Success)
        {
            throw BinanceClients.Failure(subscribed, "订阅用户数据流");
        }

        logger.LogInformation("{Config} 的用户数据流已订阅（listenKey 与保活由库负责）", config.DisplayName);

        // 重连后库会把订阅恢复；恢复成功时状态会再次走到 Subscribed。
        // 那时必须再通知一次上层——断开期间的事件拿不回来了。
        subscribed.Data.SubscriptionStatusChanged += status =>
        {
            if (status != CryptoSubscriptionStatus.Subscribed)
            {
                return;
            }

            if (firstSubscribe)
            {
                firstSubscribe = false;

                return;
            }

            logger.LogWarning("{Config} 的用户数据流已重连，上层需要补一次账户状态", config.DisplayName);

            Pulse(onEvent, new AccountStreamEvent(AccountStreamEventKind.Reconnected, null, "重连"), cancellationToken);
        };

        try
        {
            // 首次订阅也要通知一次：上层的"补状态"逻辑对首次与重连是同一件事
            await onEvent(
                new AccountStreamEvent(AccountStreamEventKind.Reconnected, null, "已连接"),
                cancellationToken).ConfigureAwait(false);

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消即退出：上层不再需要这条流（该账号下已无实盘跟单员）
        }
        finally
        {
            await subscribed.Data.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 把事件交给上层。**不 await**：库的回调在接收线程上执行，
    /// 在那里做网络或数据库动作会卡住心跳帧。真正的工作由上层排队串行做
    /// （见 AccountStreamRegistry 的 Channel）。
    /// </summary>
    private void Pulse(
        Func<AccountStreamEvent, CancellationToken, Task> onEvent,
        AccountStreamEvent streamEvent,
        CancellationToken cancellationToken)
    {
        if (streamEvent.IsNotable)
        {
            // 强平与保证金不足要显眼：它们直接影响钱
            logger.LogWarning(
                "用户数据流事件：{Kind} {Symbol} —— {Detail}",
                streamEvent.Kind,
                streamEvent.Symbol ?? "—",
                streamEvent.Detail ?? "—");
        }

        _ = onEvent(streamEvent, cancellationToken);
    }

    /// <summary>
    /// 账户更新 → 事件。**只取线索**：一个事件里可能带多笔仓位变动，取第一笔的标的即可，
    /// 上层不解析它，只拿它当"该去重读账户了"的触发器。
    /// </summary>
    internal static AccountStreamEvent MapAccountUpdate(
        Binance.Net.Objects.Models.Futures.Socket.BinanceFuturesStreamAccountUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        Binance.Net.Objects.Models.Futures.Socket.BinanceFuturesStreamAccountUpdateData data = update.UpdateData;

        string? symbol = data?.Positions is { Length: > 0 } positions ? positions[0].Symbol : null;

        // 变动的来源（下单、资金费、划转、强平……）。枚举里没有强平这一档（实测上游会发 LIQUIDATION），
        // 所以强平另有来源：订单事件里的 OriginalType，见 MapOrderUpdate
        return new AccountStreamEvent(
            AccountStreamEventKind.AccountUpdate,
            symbol,
            data?.Reason.ToString().ToUpperInvariant());
    }

    /// <summary>
    /// 订单更新 → 事件。强平要显眼：它不是我们的决定，也不是池子的决定。
    /// 实测上游用**原始订单类型**标出强平（下单时是 MARKET，被强平时 OriginalType 变成 LIQUIDATION），
    /// 库照原样把两者分开给了，所以这里判定的是 <c>OriginalType</c> 而不是 Type。
    /// </summary>
    internal static AccountStreamEvent MapOrderUpdate(
        Binance.Net.Objects.Models.Futures.Socket.BinanceFuturesStreamOrderUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        Binance.Net.Objects.Models.Futures.Socket.BinanceFuturesStreamOrderUpdateData order = update.UpdateData;

        bool liquidated = order?.OriginalType == BinanceOrderType.Liquidation
            || order?.Status == BinanceOrderStatus.ExpiredInMatch;

        string type = order?.Type.ToString().ToUpperInvariant() ?? "ORDER";
        string status = order?.Status.ToString().ToUpperInvariant() ?? string.Empty;

        return new AccountStreamEvent(
            liquidated ? AccountStreamEventKind.Liquidation : AccountStreamEventKind.OrderUpdate,
            order?.Symbol,
            status.Length > 0 ? $"{type} {status}" : type);
    }

    /// <summary>保证金不足：账户快要撑不住了，这是实盘最该被看到的预警。</summary>
    internal static AccountStreamEvent MarginCall() =>
        new(AccountStreamEventKind.MarginCall, null, "保证金不足，存在强平风险");

    /// <summary>杠杆或持仓模式被改动（交易所侧或人工改的）。</summary>
    internal static AccountStreamEvent ConfigUpdate() =>
        new(AccountStreamEventKind.ConfigUpdate, null, "杠杆或持仓模式变化");

    /// <summary>
    /// 过期不是错误：库会重新申请一把并重连（重连成功后会再发一次 Reconnected）。
    /// </summary>
    internal static AccountStreamEvent ListenKeyExpired() =>
        new(AccountStreamEventKind.ListenKeyExpired, null, "listenKey 已过期");

    /// <summary>
    /// **金丝雀**：连同一台主机取一段公开行情，看这条链路到底通不通。
    ///
    /// 为什么需要它：用户数据流在**账户没动作时本来就不发任何帧**，所以"一直没收到消息"
    /// 与"这条链路根本不通"在日志里长得一模一样。公开行情每秒都在跳，拿它当探针
    /// 能在十秒内给出确定答案。
    ///
    /// 迁移前这条探针自己拼 WS 路径，还踩过"旧路由静默不通、把好链路判成网络阻断"的坑
    /// （见 docs/EXCHANGES.md）。现在路径由库决定，探针只回答"这台主机推得出数据吗"。
    /// </summary>
    /// <returns>十秒内收到的帧数；0 表示这条路推不出数据。</returns>
    public static async Task<int> ProbeMarketDataAsync(
        BinanceSocketClient socket,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            int frames = 0;

            CryptoExchange.Net.Objects.WebSocketResult<CryptoExchange.Net.Objects.Sockets.UpdateSubscription> subscribed =
                await socket.UsdFuturesApi.ExchangeData
                    .SubscribeToTickerUpdatesAsync(
                        "BTCUSDT",
                        _ => Interlocked.Increment(ref frames),
                        timeout.Token)
                    .ConfigureAwait(false);

            if (!subscribed.Success)
            {
                return 0;
            }

            try
            {
                // 收到至少三帧就够了：这已经证明"主机在推数据"
                while (Volatile.Read(ref frames) < 3 && !timeout.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 十秒到点，按收到的帧数回答
            }
            finally
            {
                await subscribed.Data.CloseAsync().ConfigureAwait(false);
            }

            return Volatile.Read(ref frames);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }
}

/// <summary>用户数据流上的事件类别。</summary>
public enum AccountStreamEventKind
{
    /// <summary>仓位或余额变了（成交、强平、资金费都会触发）。</summary>
    AccountUpdate = 0,

    /// <summary>订单状态变了（下单、成交、撤销）。</summary>
    OrderUpdate = 1,

    /// <summary>被强平。</summary>
    Liquidation = 2,

    /// <summary>保证金不足预警。</summary>
    MarginCall = 3,

    /// <summary>杠杆或持仓模式被改动（交易所侧或人工改的）。</summary>
    ConfigUpdate = 4,

    /// <summary>listenKey 过期，需要重连换新 key。</summary>
    ListenKeyExpired = 5,

    /// <summary>（重）连上了。断开期间的事件拿不回来，上层应补一次状态。</summary>
    Reconnected = 6,
}

/// <summary>
/// 一条用户数据流事件。**携带的是"哪里变了"的线索，不是权威数据**——
/// 权威数据要以交易所的账户接口再读一次（见 <see cref="BinanceAccountFeed"/> 的类说明）。
/// </summary>
public sealed record AccountStreamEvent(
    AccountStreamEventKind Kind,
    string? Symbol,
    string? Detail)
{
    /// <summary>该不该在日志里显眼：强平与保证金不足会影响钱，别的只驱动刷新。</summary>
    public bool IsNotable => Kind is AccountStreamEventKind.Liquidation
        or AccountStreamEventKind.MarginCall
        or AccountStreamEventKind.ListenKeyExpired;
}
