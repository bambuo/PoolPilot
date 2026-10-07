using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using PoolPilot.Domain.Entities;

namespace PoolPilot.Infrastructure.Brokers;

/// <summary>
/// 币安 U 本位合约的**用户数据流**（listenKey + WebSocket）。
///
/// 与轮询的区别是"谁知道变化"：轮询里变化只有在我们去问的时候才被发现；
/// 用户数据流里**交易所主动推**——下单成交、仓位变动、保证金不足、强平，
/// 都在毫秒级到达。实盘跟单要盯的是自己的钱，这件事不该靠定时器猜。
///
/// ⚠️ **路径本身就是要害**：币安在 2026 年把合约的 WebSocket 拆成了路由路径
/// （<c>/public</c> 高频公开、<c>/market</c> 常规行情、<c>/private</c> 用户数据），
/// 旧的 <c>wss://fstream.binance.com/ws/&lt;listenKey&gt;</c> 已于 2026-04-23 永久退役。
/// 退役后的表现是**握手照样成功、一条数据都不来**（不报错、不关闭），
/// 实测因此被误判成"网络阻断"半天。用户数据流现在的地址是
/// <c>wss://fstream.binance.com/private/ws?listenKey=…</c>。
///
/// 三件必须做对的事（与 Lighter 客户端同一套教训）：
/// <list type="number">
///   <item><b>listenKey 会过期</b>：60 分钟不用就失效，所以要每 25 分钟 PUT 一次续期；
///     真过期了服务端会推来 <c>listenKeyExpired</c>，那时要重新申请一把并重连。</item>
///   <item><b>要重连</b>：网络抖动、交易所重启都会断。断线后指数退避重连，重新申请 listenKey——
///     旧的连接期间发生的事件拿不回来了，所以上层收到 <see cref="AccountStreamEventKind.Reconnected"/>
///     时应当去补一次状态（本系统的做法是重读账户）。</item>
///   <item><b>按账号一条流</b>：listenKey 是**账号级**的（挂在 API key 上），
///     同一个账号下的多个跟单员共用这一条流，不是每个跟单员连一次。</item>
/// </list>
///
/// 解析刻意只做"这是哪一类事件 + 关键字段"，不去重建账户模型：
/// 推送里的 <c>ACCOUNT_UPDATE</c> 给的是**变化量**（仓位数量、余额变动），
/// 拿它拼出一个账户快照要补一堆默认值，拼错了不会报错、只会显示错数。
/// 上层收到事件后重新读一次账户（REST）才是权威的那份数据。
/// </summary>
public sealed class BinanceAccountFeed(
    HttpClient http,
    ILogger<BinanceAccountFeed> logger)
{
    /// <summary>续期间隔。listenKey 的寿命是 60 分钟，25 分钟续一次留足余量。</summary>
    private static readonly TimeSpan KeepAliveEvery = TimeSpan.FromMinutes(25);

    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    /// <summary>
    /// 一直跑下去：申请 listenKey → 连 WS → 收消息 → 续期；断了就退避重连。
    /// 取消 <paramref name="cancellationToken"/> 时退出（用于账号不再需要流的时候）。
    /// </summary>
    public async Task RunAsync(
        ExchangeConfig config,
        Func<AccountStreamEvent, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(onEvent);

        int attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            string? listenKey = null;

            try
            {
                listenKey = await CreateListenKeyAsync(config, cancellationToken).ConfigureAwait(false);

                logger.LogInformation(
                    "{Config} 用户数据流已订阅（listenKey …{Tail}）",
                    config.DisplayName,
                    listenKey[^6..]);

                // 连上就算一次成功的尝试：退避从头开始
                attempt = 0;

                await PumpAsync(config, listenKey, onEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 连不上不该让整个进程抖：退避后重来，并把原因说清楚（key 权限、IP 白名单最常见）
                TimeSpan delay = Backoff[Math.Min(attempt, Backoff.Length - 1)];
                attempt++;

                logger.LogWarning(
                    ex,
                    "{Config} 用户数据流断开，{Delay} 秒后重连（第 {Attempt} 次）",
                    config.DisplayName,
                    delay.TotalSeconds,
                    attempt);

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                // 主动关掉 listenKey：留着它会占满该账号的名额（币安对每个账号的 listenKey 有上限）
                if (listenKey is not null)
                {
                    await TryCloseListenKeyAsync(config, listenKey, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task PumpAsync(
        ExchangeConfig config,
        string listenKey,
        Func<AccountStreamEvent, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken)
    {
        using ClientWebSocket socket = new();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        // 私有流走 /private 路由。写成旧的 /ws/<listenKey> 会静默收不到任何数据（见类说明）
        Uri endpoint = new($"{WsBaseOf(config)}/private/ws?listenKey={listenKey}");

        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

        // 重连后上层要补一次状态：断开期间的事件拿不回来了
        await onEvent(new AccountStreamEvent(AccountStreamEventKind.Reconnected, null, "已连接"), cancellationToken)
            .ConfigureAwait(false);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task keepAlive = KeepAliveAsync(config, listenKey, linked.Token);

        try
        {
            await ReceiveLoopAsync(socket, onEvent, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);

            try
            {
                await keepAlive.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常收尾
            }
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        Func<AccountStreamEvent, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using MemoryStream payload = new();

            WebSocketReceiveResult result;

            do
            {
                result = await socket
                    .ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken)
                        .ConfigureAwait(false);

                    return;
                }

                payload.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            AccountStreamEvent? parsed = Parse(Encoding.UTF8.GetString(payload.ToArray()));

            if (parsed is { } parsedEvent)
            {
                if (parsedEvent.IsNotable)
                {
                    logger.LogWarning(
                        "用户数据流事件：{Kind} {Symbol} —— {Detail}",
                        parsedEvent.Kind,
                        parsedEvent.Symbol ?? "—",
                        parsedEvent.Detail ?? "—");
                }

                await onEvent(parsedEvent, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task KeepAliveAsync(
        ExchangeConfig config,
        string listenKey,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(KeepAliveEvery, cancellationToken).ConfigureAwait(false);

            using HttpRequestMessage request = new(HttpMethod.Put, "/fapi/v1/listenKey")
            {
                Content = JsonContent.Create(new { listenKey }),
            };

            request.Headers.Add("X-MBX-APIKEY", config.ApiKey);

            using HttpResponseMessage response = await http
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 续期失败不是致命的：真过期了服务端会推 listenKeyExpired，那时会重连并换新 key
                logger.LogWarning(
                    "{Config} 的 listenKey 续期失败：HTTP {Status}",
                    config.DisplayName,
                    (int)response.StatusCode);
            }
        }
    }

    private async Task<string> CreateListenKeyAsync(ExchangeConfig config, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/fapi/v1/listenKey");
        request.Headers.Add("X-MBX-APIKEY", config.ApiKey);

        using HttpResponseMessage response = await http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"申请 listenKey 失败：HTTP {(int)response.StatusCode} {body}。"
                + "常见原因：key 无效、IP 不在白名单里。");
        }

        using JsonDocument document = JsonDocument.Parse(body);

        return document.RootElement.TryGetProperty("listenKey", out JsonElement key)
            ? key.GetString() ?? throw new InvalidOperationException("listenKey 为空。")
            : throw new InvalidOperationException($"响应里没有 listenKey：{body}");
    }

    private async Task TryCloseListenKeyAsync(
        ExchangeConfig config,
        string listenKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Delete, "/fapi/v1/listenKey")
            {
                Content = JsonContent.Create(new { listenKey }),
            };

            request.Headers.Add("X-MBX-APIKEY", config.ApiKey);

            using HttpResponseMessage response = await http
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "{Config} 关闭 listenKey 返回 HTTP {Status}（不影响功能）",
                    config.DisplayName,
                    (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 关不掉只是占一个名额，不值得把退出流程搞复杂
            logger.LogDebug(ex, "{Config} 关闭 listenKey 时出错", config.DisplayName);
        }
    }

    /// <summary>
    /// 从推送帧里认出事件类型。**只认类型与关键字段，不重建账户**（理由见类说明）。
    /// 认不出的（币安会不打招呼地加新事件类型）返回 null：忽略比猜好。
    /// </summary>
    internal static AccountStreamEvent? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("e", out JsonElement kindElement))
            {
                // 对 ping/pong 之类的无事件名帧：币安的用户流是纯 JSON，不该出现；
                // 真出现了也不当事件处理
                return null;
            }

            string kind = kindElement.GetString() ?? string.Empty;

            switch (kind)
            {
                case "ACCOUNT_UPDATE":
                {
                    // 一个事件里可能带多笔仓位变动，取第一笔的标的当线索即可——
                    // 上层不解析它，只拿它当"该去重读账户了"的触发器
                    string? symbol = null;
                    string? reason = null;

                    if (document.RootElement.TryGetProperty("a", out JsonElement account))
                    {
                        if (account.TryGetProperty("P", out JsonElement positions)
                            && positions.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement position in positions.EnumerateArray())
                            {
                                symbol = position.TryGetProperty("s", out JsonElement s) ? s.GetString() : null;

                                break;
                            }
                        }

                        // m = 这次变动的原因（DEPOSIT / ORDER / FUNDING_FEE / LIQUIDATION…）
                        reason = account.TryGetProperty("m", out JsonElement m) ? m.GetString() : null;
                    }

                    return new AccountStreamEvent(AccountStreamEventKind.AccountUpdate, symbol, reason);
                }

                case "ORDER_TRADE_UPDATE":
                {
                    if (!document.RootElement.TryGetProperty("o", out JsonElement order))
                    {
                        return new AccountStreamEvent(AccountStreamEventKind.OrderUpdate, null, null);
                    }

                    string status = order.TryGetProperty("X", out JsonElement st) ? st.GetString() ?? "" : "";
                    string type = order.TryGetProperty("o", out JsonElement ot) ? ot.GetString() ?? "" : "";
                    string? symbol = order.TryGetProperty("s", out JsonElement sym) ? sym.GetString() : null;

                    // 强平要显眼：它不是我们的决定，也不是池子的决定
                    bool liquidated = type.Equals("LIQUIDATION", StringComparison.OrdinalIgnoreCase)
                        || status.Equals("EXPIRED_IN_MATCH", StringComparison.OrdinalIgnoreCase);

                    return new AccountStreamEvent(
                        liquidated ? AccountStreamEventKind.Liquidation : AccountStreamEventKind.OrderUpdate,
                        symbol,
                        $"{(type.Length > 0 ? type : "ORDER")} {status}");
                }

                case "MARGIN_CALL":
                    // 保证金不足：账户快要撑不住了，这是实盘最该被看到的预警
                    return new AccountStreamEvent(
                        AccountStreamEventKind.MarginCall,
                        null,
                        "保证金不足，存在强平风险");

                case "ACCOUNT_CONFIG_UPDATE":
                    return new AccountStreamEvent(AccountStreamEventKind.ConfigUpdate, null, "杠杆或持仓模式变化");

                case "listenKeyExpired":
                    // 过期不是错误：外层会重连并换一把新 key
                    return new AccountStreamEvent(AccountStreamEventKind.ListenKeyExpired, null, "listenKey 已过期");

                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            // 解析不了的帧直接丢：推送通道上没必要为一条脏数据中断连接
            return null;
        }
    }

    /// <summary>
    /// **金丝雀**：连同一台主机取一次公开行情，看这条链路到底通不通。
    ///
    /// 为什么需要它：用户数据流在**账户没动作时本来就不发任何帧**，所以"一直没收到消息"
    /// 与"这条链路根本不通"在日志里长得一模一样。公开行情每秒都在跳，拿它当探针
    /// 能在十秒内给出确定答案。
    ///
    /// ⚠️ 探针必须走**新路由的 /market 路径**：旧路径已经退役，而那正是"静默不通"的形态——
    /// 曾经用旧路径当探针，结果把一条好链路判成了"网络阻断"（见类说明）。
    /// 走错路径的探针比没有探针更糟：它会把一个确定的错误结论说得理直气壮。
    /// </summary>
    /// <returns>十秒内收到的帧数；0 表示这条路推不出数据。</returns>
    public static async Task<int> ProbeMarketDataAsync(ExchangeConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        using ClientWebSocket socket = new();

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            await socket
                .ConnectAsync(new Uri($"{WsBaseOf(config)}/market/ws/btcusdt@ticker"), timeout.Token)
                .ConfigureAwait(false);

            byte[] buffer = new byte[16 * 1024];
            int frames = 0;

            while (frames < 3 && !timeout.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await socket
                    .ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                frames++;
            }

            return frames;
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

    /// <summary>
    /// 合约的 WS 域名与 REST 域名不是一回事（<c>fapi.binance.com</c> ↔ <c>fstream.binance.com</c>）。
    /// 路径（<c>/private</c>、<c>/market</c>）由调用处拼——它们是路由的一部分，不是可选修饰。
    /// </summary>
    private static string WsBaseOf(ExchangeConfig config) =>
        config.RestBaseUrl?.Contains("testnet", StringComparison.OrdinalIgnoreCase) == true
            ? "wss://stream.binancefuture.com"
            : "wss://fstream.binance.com";
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
