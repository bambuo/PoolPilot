using System.Threading.Channels;

using Binance.Net.Interfaces.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;
using PoolPilot.Infrastructure.Brokers;

namespace PoolPilot.Infrastructure.Monitoring;

/// <summary>
/// 实盘账号的**用户数据流**管理：每个交易所账号一条流，按跟单员订阅的是**账号**而不是人。
///
/// 为什么单位是账号而不是跟单员：<c>listenKey</c> 是账号级的（挂在 API key 上），
/// 币安对同一账号能同时开的 listenKey 有上限。同一个账号下挂两个实盘跟单员，
/// 它们看到的仓位本来就是同一份——连两条流既浪费名额，也会让同一件事被推送两次。
///
/// **推送说"变了"，REST 说"是什么"**：事件到达后不拿推送里的碎片去拼账户，
/// 而是防抖一小段（成交常是连着的几笔）再重读一次账户，然后广播出去。
/// 这样界面上看到的数永远与交易所一致，而不是"由变化量推算出来的一份"。
/// </summary>
public sealed class AccountStreamRegistry(
    IServiceScopeFactory scopeFactory,
    IBinanceUserClientProvider binance,
    IMonitorBroadcaster broadcaster,
    ILoggerFactory loggerFactory,
    ILogger<AccountStreamRegistry> logger) : IDisposable
{
    /// <summary>把一串事件合成一次读取的窗口。成交常常连着一串，逐个读会把交易所问爆。</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1.5);

    /// <summary>金丝雀的检查间隔：查太勤是白费连接，查太疏则问题被发现得太晚。</summary>
    private static readonly TimeSpan CanaryEvery = TimeSpan.FromMinutes(5);

    private readonly Dictionary<long, RunningStream> _streams = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>此刻在跑的流（配置 Id → 平台）。给状态接口与排查用。</summary>
    public IReadOnlyDictionary<long, string> Running()
    {
        // 加锁读：与 Ensure/Stop 互斥，避免读到半改的字典
        _gate.Wait();

        try
        {
            return _streams.ToDictionary(kv => kv.Key, kv => kv.Value.Platform);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>这个账号该有一条流在跑。幂等：已在跑就什么都不做。</summary>
    public async Task EnsureAsync(ExchangeConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_streams.ContainsKey(config.Id))
            {
                return;
            }

            if (config.Code != ExchangeCode.Binance)
            {
                // 只有币安有执行侧实现；别的平台在接入实现时再补
                logger.LogDebug("{Platform} 还没有用户数据流实现，跳过", config.Code);

                return;
            }

            CancellationTokenSource cts = new();
            Channel<AccountStreamEvent> events = Channel.CreateUnbounded<AccountStreamEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });

            // 这个账号的库客户端：listenKey 的建/续/删、连接与重连都由库管，
            // 地址也由库的环境决定（配置里的接入地址对执行侧不再作数）
            IBinanceSocketClient socket = BinanceClients.Socket(binance, config);

            BinanceAccountFeed stream = new(loggerFactory.CreateLogger<BinanceAccountFeed>());

            RunningStream running = new(config, config.DisplayName, ExchangeEnumText.ToText(config.Code), cts, events);
            _streams[config.Id] = running;

            // 金丝雀先跑一轮：链路不通的话，界面与日志立刻就能说清是"网络推不出数据"，
            // 而不是留一个"已连接"让人以为实时在跑（实测踩过：握手通、数据一条不来）
            running.Health = Task.Run(() => GuardAsync(config, running, cts.Token), CancellationToken.None);

            running.Pump = Task.Run(
                () => stream.RunAsync(socket, config, (e, ct) => PulseAsync(events.Writer, e, ct), cts.Token),
                CancellationToken.None);

            running.Drain = Task.Run(() => DrainAsync(config.Id, events.Reader, cts.Token), CancellationToken.None);

            // 标记价流：未实现盈亏是标记价的函数，而用户数据流不推价格，所以它得单开一条
            StartMarkFeed(config, running, cts);

            logger.LogInformation("{Config} 的用户数据流已启动", config.DisplayName);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>这个账号不再需要流（没有实盘跟单员了）：停掉并关掉 listenKey。</summary>
    public async Task StopAsync(long configId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        RunningStream? running;

        try
        {
            if (!_streams.Remove(configId, out running))
            {
                return;
            }
        }
        finally
        {
            _gate.Release();
        }

        await running.DisposeAsync().ConfigureAwait(false);

        logger.LogInformation("{Config} 的用户数据流已停止（该账号下已无实盘跟单员）", running.Name);
    }

    private static string[] SnapshotOf(RunningStream running)
    {
        lock (running.MarkSymbols)
        {
            return [.. running.MarkSymbols];
        }
    }

    /// <summary>
    /// 标记价到达：用它当场算各笔的未实现盈亏并推给界面。
    ///
    /// 算在**后端**而不是前端：这是"我的钱现在怎么样"的数字，公式只该有一处实现
    /// （已与交易所对齐到 8 位小数，见 <c>LivePnl</c>），前端只负责显示。
    /// 价格只是触发条件——盈亏本身仍用**交易所给的**开仓均价与数量，我们只代它算这一步。
    /// </summary>
    private async Task OnMarksAsync(
        long configId,
        IReadOnlyList<(string Symbol, decimal Price)> marks,
        CancellationToken cancellationToken)
    {
        if (!_streams.TryGetValue(configId, out RunningStream? running))
        {
            return;
        }

        lock (running.Marks)
        {
            foreach ((string symbol, decimal price) in marks)
            {
                running.Marks[symbol] = price;
            }
        }

        // 节流：8 个合约每秒 8 帧，但界面看的是"现在赚多少"，
        // 每秒推 4 次与推 8 次在屏幕上没有区别，却把 hub 消息量翻倍
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (now - running.LastPnlAt < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        running.LastPnlAt = now;

        Dictionary<string, decimal> prices;

        lock (running.Marks)
        {
            prices = new Dictionary<string, decimal>(running.Marks, StringComparer.OrdinalIgnoreCase);
        }

        KeyValuePair<long, LiveAccountDto>[] snapshots;

        lock (running.Snapshots)
        {
            snapshots = [.. running.Snapshots];
        }

        foreach ((long followerId, LiveAccountDto snapshot) in snapshots)
        {
            Dictionary<string, decimal> byPair = [];
            Dictionary<string, decimal> marksByPair = [];
            List<decimal?> pnls = [];

            foreach (LivePositionDto position in snapshot.Positions)
            {
                decimal signed = position.Direction == "short" ? -Math.Abs(position.Size) : Math.Abs(position.Size);
                decimal? mark = prices.TryGetValue(position.VenueSymbol, out decimal price) ? price : null;
                decimal? pnl = LivePnl.Position(position.EntryPrice, signed, mark);

                // 按**系统标准交易对**给键：界面上的持仓行就是按它渲染的
                if (position.Pair is { } pair)
                {
                    byPair[pair] = pnl ?? 0m;

                    if (mark is { } markValue)
                    {
                        marksByPair[pair] = markValue;
                    }
                }

                pnls.Add(pnl);
            }

            if (byPair.Count == 0)
            {
                continue;
            }

            decimal? total = LivePnl.Total(pnls);

            if (total is not { } totalValue)
            {
                // 有合约还没拿到标记价：这一轮不推（宁可不推，也不推一个少算一笔的数）
                continue;
            }

            await broadcaster
                .BroadcastLivePnlAsync(followerId, byPair, totalValue, marksByPair, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 盯着链路健康：定期用公开行情当探针，判断"这条主机推不推得出数据"。
    ///
    /// 只在**还没收到过任何事件**时才报警：账户安静时用户流本来就不发帧，
    /// 但那种情况下金丝雀是通的（公开行情每秒都有），两者一对照就能分辨。
    /// </summary>
    private async Task GuardAsync(ExchangeConfig config, RunningStream running, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool receivedAny = Volatile.Read(ref running.EventCount) > 0;
            int frames = await BinanceAccountFeed
                .ProbeMarketDataAsync(BinanceClients.Socket(binance, config), cancellationToken)
                .ConfigureAwait(false);

            bool healthy = frames > 0 || receivedAny;

            if (running.LinkHealthy != healthy)
            {
                running.LinkHealthy = healthy;

                if (healthy)
                {
                    logger.LogInformation("{Config} 的行情链路正常（金丝雀取到 {Frames} 帧）", config.DisplayName, frames);
                }
                else
                {
                    logger.LogWarning(
                        "{Config} 的用户数据流收不到任何数据，且同一台主机的公开行情也取不到"
                        + "——**这条链路被网络挡住了**（握手能过、数据不来）。"
                        + "实时账户更新在此网络下不可用：换网络/代理，或把跟单员的「实时」当作不可用来读。"
                        + "界面会显示为「通道不可用」。",
                        config.DisplayName);
                }

                await PublishHealthAsync(config.Id, healthy, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(CanaryEvery, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>把链路健康推给界面：否则"实时"这个标签会在一条不通的链路上亮着。</summary>
    private async Task PublishHealthAsync(long configId, bool healthy, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        try
        {
            IFollowerRepository followers = scope.ServiceProvider.GetRequiredService<IFollowerRepository>();
            IReadOnlyList<Follower> all = await followers.ListAsync(cancellationToken).ConfigureAwait(false);

            foreach (Follower follower in all.Where(f =>
                f.Mode == CopyMode.Live && f.ExchangeConfigId == configId))
            {
                await broadcaster
                    .BroadcastAccountStreamHealthAsync(follower.Id, healthy, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "推送链路健康状态失败（不影响流本身）");
        }
    }

    /// <summary>
    /// 起一个标记价流任务。**换订阅就是换任务**：
    /// 币安不支持在已建立的连接上改订阅（与 Lighter 不同），只能断开重连，
    /// 所以"改订"这件事落地成"停掉旧任务、用新令牌起一个新任务"。
    ///
    /// ⚠️ 曾经只取消了旧任务的令牌而没起新的——那样第一次改订之后这条流就再也没了，
    /// 而日志上看起来只是"改订了"（一个不会报错的死法）。
    /// </summary>
    private void StartMarkFeed(ExchangeConfig config, RunningStream running, CancellationTokenSource lifetime)
    {
        running.MarkReload?.Cancel();
        running.MarkReload?.Dispose();
        running.MarkReload = new CancellationTokenSource();

        CancellationTokenSource reload = running.MarkReload;
        BinanceMarkPriceFeed feed = new(loggerFactory.CreateLogger<BinanceMarkPriceFeed>());

        // 同一个账号的客户端（库按账号缓存）：标记价与用户流共用一条连接与一份限流
        IBinanceSocketClient socket = BinanceClients.Socket(binance, config);

        running.MarksJob = Task.Run(
            () => feed.RunAsync(
                socket,
                config,
                () => SnapshotOf(running),
                (marks, ct) => OnMarksAsync(config.Id, marks, ct),
                CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, reload.Token).Token),
            CancellationToken.None);
    }

    /// <summary>
    /// 收事件：写进队列就返回。**不在连接线程里做任何网络与数据库动作**——
    /// 那是接收循环，卡住它会让心跳帧都处理不了（币安会因此断连）。
    /// </summary>
    private static async Task PulseAsync(
        ChannelWriter<AccountStreamEvent> writer,
        AccountStreamEvent streamEvent,
        CancellationToken cancellationToken)
    {
        await writer.WriteAsync(streamEvent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 消费事件：防抖后重读账户并广播。串行处理，所以一次突发只换一次读取。
    /// </summary>
    private async Task DrainAsync(
        long configId,
        ChannelReader<AccountStreamEvent> reader,
        CancellationToken cancellationToken)
    {
        RunningStream? running = _streams.GetValueOrDefault(configId);

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.TryRead(out AccountStreamEvent? first))
                {
                    continue;
                }

                if (running is not null)
                {
                    Interlocked.Increment(ref running.EventCount);
                }

                // 强平与保证金不足立刻刷新：这两件事等不起一个防抖窗口。
                // 其余（成交造成的仓位变化）防抖，把一串合成一次读取
                bool urgent = first!.Kind is not (AccountStreamEventKind.AccountUpdate
                    or AccountStreamEventKind.OrderUpdate
                    or AccountStreamEventKind.ConfigUpdate);

                if (!urgent)
                {
                    await Task.Delay(Debounce, cancellationToken).ConfigureAwait(false);
                }

                // 丢掉窗口内堆积的其余事件：它们要的是同一次刷新
                while (reader.TryRead(out _))
                {
                }

                await BroadcastAsync(configId, first, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出（账号不再需要流）
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "用户数据流的消费循环异常退出（配置 {ConfigId}）", configId);
        }
    }

    private async Task BroadcastAsync(long configId, AccountStreamEvent streamEvent, CancellationToken cancellationToken)
    {
        RunningStream? running = _streams.GetValueOrDefault(configId);
        string configName = running?.Name ?? configId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CancellationTokenSource lifetime = running?.Lifetime ?? new CancellationTokenSource();
        ExchangeConfig config = running?.Config
            ?? throw new InvalidOperationException($"流 {configId} 不存在，无法处理它的事件。");

        // 一行一个突发：这条日志回答的是"流到底有没有在推"——
        // 没有它的话，"交易所没动静"与"流收不到消息"在日志里长得一模一样
        logger.LogInformation(
            "用户数据流事件 {Kind}（{Symbol}）→ 重读账户并广播（配置 {ConfigId}）",
            streamEvent.Kind,
            streamEvent.Symbol ?? "—",
            configId);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;

        IFollowerRepository followers = provider.GetRequiredService<IFollowerRepository>();
        GetLiveAccountUseCase liveAccount = provider.GetRequiredService<GetLiveAccountUseCase>();

        IReadOnlyList<Follower> subscribed = await followers.ListAsync(cancellationToken).ConfigureAwait(false);

        foreach (Follower follower in subscribed)
        {
            if (follower.Mode != CopyMode.Live || follower.ExchangeConfigId != configId)
            {
                continue;
            }

            try
            {
                // 每个跟单员各读一次：账户快照里带着**这个跟单员的**本金与标识，
                // 把同一个账号的读数共用给多人，等于把别人的规模基准挂在他身上。
                // 防抖已经把突发合成一次，这点开销不值得用错数据去省
                LiveAccountDto snapshot = await liveAccount
                    .ExecuteAsync(follower.Id, cancellationToken)
                    .ConfigureAwait(false);

                await broadcaster
                    .BroadcastLiveAccountAsync(follower.Id, snapshot, cancellationToken)
                    .ConfigureAwait(false);

                if (running is null)
                {
                    // 流已被停掉（该账号不再需要）：快照记了也没人来算
                    continue;
                }

                // 记下快照：标记价每秒都可能来，届时用它当场算盈亏
                lock (running.Snapshots)
                {
                    running.Snapshots[follower.Id] = snapshot;
                }

                // 持仓集合变了就换订阅（币安不支持在已建立连接上改订阅，只能重连）
                HashSet<string> wanted = [.. snapshot.Positions
                    .Where(p => p.Size != 0m)
                    .Select(p => p.VenueSymbol)];

                if (!wanted.SetEquals(running.MarkSymbols))
                {
                    running.MarkSymbols.Clear();
                    running.MarkSymbols.UnionWith(wanted);

                    if (running.MarkSymbols.Count > 0)
                    {
                        logger.LogInformation(
                            "{Config} 的持仓集合变化，标记价流改订 {Count} 个合约",
                            configName,
                            running.MarkSymbols.Count);
                    }

                    // 换订阅 = 停掉旧任务、起新任务（令牌在 StartMarkFeed 里换）
                    StartMarkFeed(config, running, lifetime);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单个跟单员读不到不该让整轮广播失败（其余跟单员仍应拿到）
                logger.LogWarning(
                    ex,
                    "给跟单员 {FollowerId} 广播实时账户失败（触发事件 {Kind}）",
                    follower.Id,
                    streamEvent.Kind);
            }
        }
    }

    public void Dispose()
    {
        // 只发取消、不等：同步 Dispose 里等异步任务有死锁风险，
        // 而 listenKey 有 60 分钟寿命，进程退出时没来得及删也不会留下永久垃圾
        foreach (RunningStream running in _streams.Values)
        {
            running.Cancel();
        }

        _streams.Clear();
        _gate.Dispose();
    }

    /// <summary>一条在跑的流：取消源 + 队列 + 两个后台任务。</summary>
    private sealed class RunningStream(
        ExchangeConfig config,
        string name,
        string platform,
        CancellationTokenSource cts,
        Channel<AccountStreamEvent> events) : IAsyncDisposable
    {
        public string Name { get; } = name;

        public string Platform { get; } = platform;

        public ExchangeConfig Config { get; } = config;

        public CancellationTokenSource Lifetime { get; } = cts;

        public Task? Pump { get; set; }

        public Task? Drain { get; set; }

        public Task? Health { get; set; }

        /// <summary>标记价流的后台任务。</summary>
        public Task? MarksJob { get; set; }

        /// <summary>链路是否推得出数据：null 表示还没查出来。</summary>
        public bool? LinkHealthy { get; set; }

        /// <summary>累计收到的事件数。>0 就说明这条链路真的在推东西。</summary>
        public int EventCount;

        /// <summary>当前订阅的持仓合约（小写平台写法）。集合变了就重连换订阅。</summary>
        public HashSet<string> MarkSymbols { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>请求标记价流换订阅：取消当前连接，让循环用新集合重连。</summary>
        public CancellationTokenSource? MarkReload { get; set; }

        /// <summary>各跟单员最近一次的账户快照，用来在标记价到达时当场算盈亏。</summary>
        public Dictionary<long, LiveAccountDto> Snapshots { get; } = [];

        /// <summary>
        /// 累积的最新标记价（平台写法 → 价）。
        ///
        /// **必须累积**：每个合约是各自每秒推一帧的，一帧里只有一个合约的价。
        /// 把单帧当成"全套报价"会让每一帧都缺其余合约的价，于是一条都推不出去
        /// （实测踩过：界面上的盈亏仍然只跟着 10 秒的账户刷新走）。
        /// </summary>
        public Dictionary<string, decimal> Marks { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>上次推送盈亏的时刻：给推送节流用（8 个合约每秒 8 帧，没必要帧帧都推）。</summary>
        public DateTimeOffset LastPnlAt { get; set; } = DateTimeOffset.MinValue;

        /// <summary>立刻取消（不等待任务收尾）。</summary>
        public void Cancel() => Lifetime.Cancel();

        public async ValueTask DisposeAsync()
        {
            await Lifetime.CancelAsync().ConfigureAwait(false);

            events.Writer.TryComplete();

            foreach (Task? task in (Task?[])[Pump, Drain, Health, MarksJob])
            {
                if (task is null)
                {
                    continue;
                }

                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 停不下来的任务不该阻塞退出流程；取消令牌已经发过了
                }
            }

            Lifetime.Dispose();
        }
    }
}
