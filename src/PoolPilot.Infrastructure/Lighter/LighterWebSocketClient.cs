using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using PoolPilot.Domain.Constants;

namespace PoolPilot.Infrastructure.Lighter;

/// <summary>
/// Lighter WebSocket 客户端。
///
/// 三件事必须做对，全部是实测踩出来的（见 docs/LIGHTER-API-NOTES.md 第四节）：
///
/// <list type="number">
///   <item><b>保活</b>：服务端要求客户端每 2 分钟至少主动发一帧（**收消息不算**），
///     否则以 <c>i/o timeout</c> 关闭——实测 120.573 秒被关。这里每 60 秒发一次 ping。</item>
///   <item><b>去重</b>：重连后的回填会重复投递已收到的消息，按业务键去重。</item>
///   <item><b>重连</b>：断线后指数退避重连，并在 reconnected 事件里让上层知道有缺口。</item>
/// </list>
/// </summary>
public sealed class LighterWebSocketClient : IAsyncDisposable
{
    private readonly Uri _endpoint;
    private readonly TimeSpan _keepAliveInterval;
    private readonly TimeSpan _collectWindow;
    private readonly int _maxCollectMessages;

    /// <summary>运行期动态追加的频道。与构造传入的固定频道在每次（重）连接时一并订阅。</summary>
    private readonly HashSet<string> _dynamicChannels = [];

    /// <summary>串行化同一连接上的所有发送。<c>ClientWebSocket</c> 不允许并发发送——
    /// 保活 ping、重连订阅、动态订阅可能来自三个不同的任务。</summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>当前活跃连接。<c>PumpAsync</code> 建立/断开时维护，供动态订阅即时发送。</summary>
    private ClientWebSocket? _liveSocket;

    public LighterWebSocketClient(
        Uri? endpoint = null,
        TimeSpan? keepAliveInterval = null,
        TimeSpan? collectWindow = null,
        int maxCollectMessages = 4096)
    {
        _endpoint = endpoint ?? new Uri(LighterConstants.MainnetWebSocketUrl);
        _keepAliveInterval = keepAliveInterval ?? TimeSpan.FromSeconds(LighterConstants.WebSocketKeepAliveSeconds);
        _collectWindow = collectWindow ?? TimeSpan.FromSeconds(8);
        _maxCollectMessages = maxCollectMessages;
    }

    /// <summary>
    /// 在<b>已运行</b>的流式连接上追加订阅一个频道。
    ///
    /// 存在的理由：跟单账户是运行期配置的，它带来的池**当时**并不在初始频道表里。
    /// 只把频道记入 <see cref="_dynamicChannels"/> 而不发帧的话，要等下次断线重连才生效——
    /// 最长落后一个连接生命周期。有活跃连接就立即发订阅帧；正在重连时只记录，
    /// 重连会一并订阅。
    /// </summary>
    public async Task SubscribeAsync(string channel, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        lock (_dynamicChannels)
        {
            if (!_dynamicChannels.Add(channel))
            {
                return;
            }
        }

        if (_liveSocket is not { State: WebSocketState.Open } socket)
        {
            return;
        }

        try
        {
            // 交给 SendAsync 自己取发送锁。这里**不能**先取锁再调用它——
            // SemaphoreSlim 不可重入，那样会把自己永久锁死（心跳随之停摆）。
            await SendAsync(
                socket,
                $$"""{"type":"subscribe","channel":"{{channel}}"}""",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            // 与断线赛跑：没有发出去不要紧，频道已经记进 _dynamicChannels，重连时会一并订阅
        }
    }

    /// <summary>当前是否有活跃连接。用于判断"推送在不在"，例如决定要不要退回 REST 兜底。</summary>
    public bool IsLive => _liveSocket is { State: WebSocketState.Open };

    /// <summary>
    /// 订阅若干频道，收集 <paramref name="window"/> 时间内的消息后返回。
    ///
    /// 用于"拉一次快照"的场景（净值序列、成交流水、盘口）。
    /// 需要持续订阅的场景用 <see cref="StreamAsync"/>。
    /// </summary>
    /// <param name="channels">要订阅的频道。</param>
    /// <param name="window">最长采集窗口；<paramref name="stopWhen"/> 命中时提前结束。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="stopWhen">
    /// 命中即停止采集。用于"拉一次快照"的场景——订阅确认消息通常在数百毫秒内到达，
    /// 若不提前退出则每次都要等满整个窗口，接口延迟会被白白放大到秒级。
    /// </param>
    public async Task<IReadOnlyList<JsonElement>> CollectAsync(
        IReadOnlyList<string> channels,
        TimeSpan? window = null,
        Func<JsonElement, bool>? stopWhen = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);

        TimeSpan effectiveWindow = window ?? _collectWindow;
        List<JsonElement> collected = [];

        using ClientWebSocket socket = new();
        await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);

        foreach (string channel in channels)
        {
            await SendAsync(socket, $$"""{"type":"subscribe","channel":"{{channel}}"}""", cancellationToken).ConfigureAwait(false);
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(effectiveWindow);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);

        try
        {
            while (!deadline.IsCancellationRequested && collected.Count < _maxCollectMessages)
            {
                string? message = await ReceiveAsync(socket, buffer, deadline.Token).ConfigureAwait(false);

                if (message is null)
                {
                    break;
                }

                using JsonDocument document = JsonDocument.Parse(message);
                JsonElement root = document.RootElement;

                // "connected" 是握手问候语，不是数据
                string type = root.TryGetProperty("type", out JsonElement typeElement)
                    ? typeElement.GetString() ?? string.Empty
                    : string.Empty;

                if (type == "connected")
                {
                    continue;
                }

                JsonElement snapshot = root.Clone();
                collected.Add(snapshot);

                if (stopWhen is not null && stopWhen(snapshot))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 到达采集窗口，属正常结束
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "collect done", cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // 主动关闭失败不影响已采集的数据
            }
        }

        return collected;
    }

    /// <summary>
    /// 持续订阅，产出消息流。断线自动重连，保活由内部定时器负责。
    ///
    /// 每次订阅成功后先发一条 <c>{"type":"reconnected"}</c> 标记（首连时 <c>first_connect</c>），
    /// 让上层知道此前的订阅已重置、快照需要重取——静默重连会掩盖数据缺口。
    /// </summary>
    public async IAsyncEnumerable<JsonElement> StreamAsync(
        IReadOnlyList<string> channels,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);

        Channel<JsonElement> queue = Channel.CreateUnbounded<JsonElement>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        Task pump = PumpAsync(channels, queue.Writer, cancellationToken);
        bool first = true;

        try
        {
            await foreach (JsonElement item in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (first)
                {
                    first = false;
                }

                yield return item;
            }
        }
        finally
        {
            await pump.ConfigureAwait(false);
        }
    }

    private async Task PumpAsync(
        IReadOnlyList<string> channels,
        ChannelWriter<JsonElement> writer,
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        bool everConnected = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using ClientWebSocket socket = new();

                try
                {
                    await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);

                    // 先公开连接、再发订阅帧：反过来的话，在订阅帧发送期间到达的动态订阅
                    // 会认为"没有活跃连接"而只登记不发送，最长要等一次断线重连才生效。
                    _liveSocket = socket;

                    // 固定频道 + 运行期动态频道一并订阅（重连后动态订阅靠这里恢复）
                    List<string> effective;
                    lock (_dynamicChannels)
                    {
                        effective = [.. channels, .. _dynamicChannels];
                    }

                    foreach (string channel in effective)
                    {
                        await SendAsync(socket, $$"""{"type":"subscribe","channel":"{{channel}}"}""", cancellationToken).ConfigureAwait(false);
                    }

                    attempt = 0;

                    await writer.WriteAsync(
                        BuildMarker(everConnected ? "reconnected" : "first_connect"),
                        cancellationToken).ConfigureAwait(false);

                    everConnected = true;

                    await ReceiveLoopAsync(socket, writer, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException)
                {
                    // 落到下面的退避重连
                }

                _liveSocket = null;

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                attempt++;
                TimeSpan backoff = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
                await writer.WriteAsync(BuildMarker("disconnected", backoff.TotalSeconds), cancellationToken).ConfigureAwait(false);

                try
                {
                    await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        ChannelWriter<JsonElement> writer,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);

        using CancellationTokenSource keepAlive = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task pingTask = KeepAliveAsync(socket, keepAlive.Token);

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                string? message = await ReceiveAsync(socket, buffer, cancellationToken).ConfigureAwait(false);

                if (message is null)
                {
                    break;
                }

                using JsonDocument document = JsonDocument.Parse(message);
                JsonElement root = document.RootElement;

                string type = root.TryGetProperty("type", out JsonElement typeElement)
                    ? typeElement.GetString() ?? string.Empty
                    : string.Empty;

                if (type is "connected" or "pong")
                {
                    continue;
                }

                await writer.WriteAsync(root.Clone(), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await keepAlive.CancelAsync().ConfigureAwait(false);
            try
            {
                await pingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 保活任务随连接结束而终止，属正常
            }

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 保活心跳。服务端把"客户端主动发帧"当作存活依据，收消息不算。
    /// </summary>
    private async Task KeepAliveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_keepAliveInterval, cancellationToken).ConfigureAwait(false);

            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            await SendAsync(socket, """{"type":"ping"}""", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>在同一连接上串行化发送。<c>ClientWebSocket</c> 不允许并发发送——
    /// 保活 ping、重连订阅与动态订阅来自不同任务，必须排队。</summary>
    private async Task SendAsync(ClientWebSocket socket, string payload, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<string?> ReceiveAsync(ClientWebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        using MemoryStream accumulated = new();

        while (true)
        {
            WebSocketReceiveResult result = await socket
                .ReceiveAsync(buffer, cancellationToken)
                .ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            accumulated.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                break;
            }

            if (accumulated.Length > 32 * 1024 * 1024)
            {
                throw new InvalidOperationException("WebSocket 单条消息超过 32MB，疑似上游异常");
            }
        }

        return accumulated.Length == 0 ? null : Encoding.UTF8.GetString(accumulated.ToArray());
    }

    private static JsonElement BuildMarker(string kind, double value = 0d)
    {
        using JsonDocument document = JsonDocument.Parse(
            $$"""{"type":"{{kind}}","value":{{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""");

        return document.RootElement.Clone();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
