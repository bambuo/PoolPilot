using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using PoolPilot.Domain.Entities;

namespace PoolPilot.Infrastructure.Brokers;

/// <summary>
/// 币安的**每秒标记价**流（公开行情，走 <c>/market</c> 路由）。
///
/// 它存在的唯一理由：未实现盈亏是标记价的函数，而**币安的用户数据流不推价格**——
/// 只在仓位真的变化时推。想实时看到盈亏，就得自己拿标记价当场算
/// （公式与验证见 <c>LivePnl</c>）。
///
/// 一条连接订阅全部持仓合约（组合流 <c>/market/stream?streams=a@markPrice@1s/b@markPrice@1s</c>），
/// 不是每个合约一条：九个仓位开九条 WebSocket，光是握手与心跳就够把限额吃掉。
///
/// 持仓集合变了就**重连换订阅**：币安不支持在已建立的连接上改订阅列表（与 Lighter 不同），
/// 所以换订阅只有重连这一条路。调用方按"集合真的变了"才触发重连，别每秒都换。
/// </summary>
public sealed class BinanceMarkPriceFeed(
    ILogger<BinanceMarkPriceFeed> logger)
{
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    /// <summary>
    /// 订阅给定的合约集合，一直跑到取消。每收到一批标记价就回调一次
    /// （回调里不要做重活：那是接收循环）。
    /// </summary>
    public async Task RunAsync(
        ExchangeConfig config,
        Func<IReadOnlyList<string>> symbols,
        Func<IReadOnlyList<(string Symbol, decimal Price)>, CancellationToken, Task> onMarks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(onMarks);

        int attempt = 0;

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

            try
            {
                await PumpAsync(config, watching, onMarks, cancellationToken).ConfigureAwait(false);

                // 正常返回说明订阅集合变了（调用方在取消里表达）：立刻换新订阅，不退避
                attempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                TimeSpan delay = Backoff[Math.Min(attempt, Backoff.Length - 1)];
                attempt++;

                logger.LogWarning(
                    ex,
                    "{Config} 的标记价流断开，{Delay} 秒后重连（第 {Attempt} 次）",
                    config.DisplayName,
                    delay.TotalSeconds,
                    attempt);

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task PumpAsync(
        ExchangeConfig config,
        IReadOnlyList<string> watching,
        Func<IReadOnlyList<(string Symbol, decimal Price)>, CancellationToken, Task> onMarks,
        CancellationToken cancellationToken)
    {
        // 组合流：一条连接订阅全部持仓合约。合约名一律小写（币安对 stream 名大小写敏感）
        string streams = string.Join('/', watching.Select(s => $"{s.ToLowerInvariant()}@markPrice@1s"));
        Uri endpoint = new($"{WsBaseOf(config)}/market/stream?streams={streams}");

        using ClientWebSocket socket = new();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "{Config} 的标记价流已订阅 {Count} 个合约",
            config.DisplayName,
            watching.Count);

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

            if (Parse(Encoding.UTF8.GetString(payload.ToArray())) is { Count: > 0 } marks)
            {
                await onMarks(marks, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 从一帧（组合流包装或单流裸载荷）里取出标记价。认不出就返回空表：推送通道上不该为脏数据中断。
    /// </summary>
    internal static List<(string Symbol, decimal Price)> Parse(string json)
    {
        List<(string Symbol, decimal Price)> marks = [];

        if (string.IsNullOrWhiteSpace(json))
        {
            return marks;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            // 组合流是 {"stream":"...","data":{...}}；单流是裸的那个对象
            JsonElement root = document.RootElement;
            JsonElement payload = root.TryGetProperty("data", out JsonElement wrapped) ? wrapped : root;

            if (!payload.TryGetProperty("e", out JsonElement kind) || kind.GetString() != "markPriceUpdate")
            {
                return marks;
            }

            string? symbol = payload.TryGetProperty("s", out JsonElement s) ? s.GetString() : null;

            // p = 标记价（i 是指数价、r 是资金费率，都不是我们要的）
            if (symbol is null
                || !payload.TryGetProperty("p", out JsonElement price)
                || !decimal.TryParse(
                    price.GetString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out decimal parsed))
            {
                return marks;
            }

            marks.Add((symbol, parsed));
        }
        catch (JsonException)
        {
            // 丢掉这一帧
        }

        return marks;
    }

    private static string WsBaseOf(ExchangeConfig config) =>
        config.RestBaseUrl?.Contains("testnet", StringComparison.OrdinalIgnoreCase) == true
            ? "wss://stream.binancefuture.com"
            : "wss://fstream.binance.com";
}
