using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Mapping;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Following;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;
using PoolPilot.Infrastructure.Lighter;

namespace PoolPilot.Infrastructure.Monitoring;

/// <summary>
/// 带单员与跟单员的注册表：谁在被观察、谁在跟谁。
///
/// 这里是"按事件驱动"的落点，三条关系写死在这里：
/// <list type="number">
///   <item>**订阅集合由带单员决定**（带单员表 ∪ 首次播种名单），与他有没有订阅者无关。
///     取消跟单只是摘掉订阅者，池子照旧被观察、信号照旧落库。</item>
///   <item>**信号按池分发**：只有订阅了某个带单员的跟单员会收到它的信号，
///     因为订阅者列表挂在带单员实例上。</item>
///   <item>**跟单员实例按 Id 复用**（含内存里的仓位账与去重表），配置变了就地更新。</item>
/// </list>
///
/// 线程模型：既然所有可变状态都住在这里，串行锁也就放在这里（原先在主机上）。
/// 帧处理、心跳、以及配置写完之后的那次即时同步，都从这几个入口进，
/// 于是它们互相排队。只有 <see cref="IsWatching"/> 会被 HTTP 请求线程读到，
/// 所以它只碰 <see cref="ConcurrentDictionary{TKey,TValue}"/> 且不改动它。
/// </summary>
public sealed class LeadTraderRegistry(
    IServiceScopeFactory scopeFactory,
    LighterWebSocketClient socket,
    MarketDataCache market,
    IOptions<LeadTraderOptions> options,
    IExchangeBrokerFactory brokers,
    IExchangeSymbolCatalogFactory catalogs,
    AccountStreamRegistry accountStreams,
    LiveTradingLimits limits,
    IFollowerLedgerActivity ledgerActivity,
    ILeadActivity leadActivity,
    IFollowerLiveBroadcaster followerLive,
    ILogger<LeadTraderRegistry> logger,
    ILoggerFactory loggerFactory) : ILeadWatchState, ILeadTraderSync, IDisposable
{
    private readonly ConcurrentDictionary<long, LeadTraderRuntime> _leads = new();
    private readonly Dictionary<long, IFollower> _followers = [];
    private readonly Dictionary<long, DateTimeOffset> _restFallbackAt = [];

    /// <summary>
    /// 串行化所有会改动带单员/跟单员状态的入口。
    ///
    /// 不加锁的后果不是"偶尔算错"，而是两边互相覆盖：心跳拿着旧快照对账，
    /// 把刚推送来的仓位改回去；或者同步正在重建订阅者列表，帧处理正在给它派发信号。
    /// 这些操作都在毫秒级，排队不会让可感知延迟变差。
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>此刻是否真的在接收该池的推送。HTTP 读取，故只碰并发字典。</summary>
    public bool IsWatching(AccountIndex account) =>
        socket.IsLive
        && _leads.TryGetValue(account.Value, out LeadTraderRuntime? runtime)
        && runtime.IsEnabled;

    /// <summary>此刻被观察的池。频道表与状态上报都以它为准。</summary>
    public IReadOnlyList<long> WatchedAccounts() => [.. _leads.Values.Select(l => l.AccountIndex.Value).Order()];

    /// <summary>这个池有没有带单员（用来判断一帧推送是否该被处理）。</summary>
    public bool IsObserved(long accountIndex) => _leads.TryGetValue(accountIndex, out LeadTraderRuntime? runtime)
        && runtime.IsEnabled;

    /// <summary>
    /// 与库同步：这是运行期一切配置变更的唯一入口。
    ///
    /// 三件事：新带单员上线（订阅频道 + 建基线）、已有带单员更新（启停）、
    /// 跟单员挂到各自的带单员实例上。**跟单员的增删不改变订阅集合**——那正是这次拆分要的分工。
    /// </summary>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await SyncCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 配置写完之后立刻同步一次，让页面上的状态不用等下一次心跳（≤15 秒）。
    ///
    /// 与 <see cref="SyncAsync"/> 是同一件事：心跳那条路是定时兜底，这条是"有人刚改了配置"。
    /// </summary>
    public Task RequestSyncAsync(CancellationToken cancellationToken = default) => SyncAsync(cancellationToken);

    private async Task SyncCoreAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;

        ILeadTraderRepository leadsRepo = provider.GetRequiredService<ILeadTraderRepository>();

        IReadOnlyList<Follower> followers = await provider
            .GetRequiredService<IFollowerRepository>()
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<LeadTrader> leads = await leadsRepo.ListAsync(cancellationToken).ConfigureAwait(false);

        bool added = await SeedFromConfigAsync(leadsRepo, leads, cancellationToken).ConfigureAwait(false);

        // 订阅关系本身就意味着带单员该存在。两种情况会走到这里：老库迁过来的跟单员
        // （它们原来的身份是"账户"，没有带单员这一层），以及带单员行被手工删过。
        // 不补的话跟单员会停在"订阅了一个不存在的观察者"上，而界面上它跟正常一模一样。
        added |= await EnsureLeadsForFollowersAsync(leadsRepo, leads, followers, cancellationToken)
            .ConfigureAwait(false);

        if (added)
        {
            // 播种/补齐写进了新行，必须重读。否则下面的"上线"循环拿着旧列表，
            // 一个实例都不会建——实测踩过：跟单员全部报"订阅的带单员不存在"。
            leads = await leadsRepo.ListAsync(cancellationToken).ConfigureAwait(false);
        }

        List<long> known = [.. leads.Select(l => l.AccountIndex.Value)];

        // 库里没有了 ⇒ 实例退场。频道退不掉（协议里没有验证过退订帧），帧照旧到达，
        // 但没有实例也就是没有观察者，帧被直接丢掉——宁可多收几帧，也不猜协议。
        foreach (long index in _leads.Keys.Where(i => !known.Contains(i)).ToArray())
        {
            _leads.TryRemove(index, out _);
            logger.LogInformation("带单员 {Account} 已从库中移除，停止观察", index);
        }

        foreach (LeadTrader lead in leads)
        {
            if (_leads.TryGetValue(lead.AccountIndex.Value, out LeadTraderRuntime? existing))
            {
                if (existing.Lead != lead)
                {
                    existing.Reconfigure(lead);
                }

                continue;
            }

            LeadTraderRuntime runtime = new(lead);
            _leads[lead.AccountIndex.Value] = runtime;

            // 新带单员：频道订上，并先取一次 REST 快照当基线，否则第一次推送无法差分出"开仓"
            await socket.SubscribeAsync($"account_all_positions/{lead.AccountIndex.Value}", cancellationToken)
                .ConfigureAwait(false);
            await socket.SubscribeAsync($"account_all_trades/{lead.AccountIndex.Value}", cancellationToken)
                .ConfigureAwait(false);

            await CaptureBaselineAsync(runtime, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "带单员上线：{Name}（{Account}）{State}",
                lead.Name,
                lead.AccountIndex.Value,
                lead.IsEnabled ? "开始观察" : "已暂停（保留实例与历史）");
        }

        await SyncFollowersAsync(provider, followers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把库里的跟单员挂到各自的带单员实例上。
    ///
    /// 实例按 Id 复用：内存里的仓位账与去重表就是这么保住的。配置变了就地更新，
    /// 本金变了则由实例自己发现并整体重载。
    /// </summary>
    private async Task SyncFollowersAsync(
        IServiceProvider provider,
        IReadOnlyList<Follower> configured,
        CancellationToken cancellationToken)
    {
        // 实盘跟单员在场就意味着"那个账号要有一条用户数据流"：
        // 界面上看到的仓位应当是交易所**推**过来的，而不是我们定时去问的
        await SyncAccountStreamsAsync(provider, configured, cancellationToken).ConfigureAwait(false);

        List<long> known = [.. configured.Select(f => f.Id)];

        foreach (long id in _followers.Keys.Where(i => !known.Contains(i)).ToArray())
        {
            IFollower removed = _followers[id];
            _followers.Remove(id);

            if (_leads.TryGetValue(removed.LeadAccountIndex.Value, out LeadTraderRuntime? owner))
            {
                // 只摘订阅者，不动带单员：池子该被观察这件事与有没有人跟无关
                owner.Unsubscribe(id);
            }

            logger.LogInformation("跟单员 {Id} 已删除，从带单员上摘除（池子的观察不受影响）", id);
        }

        foreach (Follower follower in configured)
        {
            if (!_leads.TryGetValue(follower.LeadAccountIndex.Value, out LeadTraderRuntime? runtime))
            {
                // 上面的补齐逻辑保证这里通常走不到；真到了也只是少挂一个订阅者
                logger.LogWarning(
                    "跟单员 {Id} 订阅的带单员 {Account} 不存在，跳过（应通过界面重新指定）",
                    follower.Id,
                    follower.LeadAccountIndex.Value);
                continue;
            }

            if (_followers.TryGetValue(follower.Id, out IFollower? existing))
            {
                bool changed = existing.Config != follower;
                existing.Reconfigure(follower);

                if (changed)
                {
                    // 配置变了就地重新对齐：恢复跟随要马上建仓、改了本金要马上按新规模重算目标，
                    // 而不是等下一次心跳。差额为 0 时它什么都不做。
                    await CatchUpAsync(runtime, existing, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            IFollower created = Create(follower);

            _followers[follower.Id] = created;
            runtime.Subscribe(created);

            logger.LogInformation(
                "跟单员 {Id} 已订阅带单员 {Account}（{Mode}·{State}）",
                follower.Id,
                follower.LeadAccountIndex.Value,
                FollowerMapper.Mode(follower.Mode),
                follower.IsActive ? "跟随中" : "已暂停");

            // 刚挂上的订阅者立刻按**当前状态**对齐一次：使用者点了"开始跟单"，
            // 期待的是当场按市价建仓，而不是十几秒后才看到仓位。
            await CatchUpAsync(runtime, created, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 让"在跑的流"与"实盘跟单员的需求"一致：需要而没跑的启动，不再需要的停掉。
    ///
    /// 由跟单员的同步驱动，而不是另起一个定时器：跟单员是唯一的需求来源，
    /// 两处各自判断"该不该有流"迟早会分叉（比如删掉跟单员后流还在跑，白占 listenKey 名额）。
    /// </summary>
    private async Task SyncAccountStreamsAsync(
        IServiceProvider provider,
        IReadOnlyList<Follower> configured,
        CancellationToken cancellationToken)
    {
        IExchangeConfigRepository exchanges = provider.GetRequiredService<IExchangeConfigRepository>();

        HashSet<long> needed = [.. configured
            .Where(f => f.Mode == CopyMode.Live && f.ExchangeConfigId is not null)
            .Select(f => f.ExchangeConfigId!.Value)];

        foreach (long configId in needed)
        {
            ExchangeConfig? config = await exchanges.GetAsync(configId, cancellationToken).ConfigureAwait(false);

            if (config is null)
            {
                logger.LogWarning(
                    "实盘跟单员引用的交易所配置 {ConfigId} 不存在，跳过订阅用户数据流",
                    configId);

                continue;
            }

            await accountStreams.EnsureAsync(config, cancellationToken).ConfigureAwait(false);
        }

        foreach (long running in accountStreams.Running().Keys.Where(id => !needed.Contains(id)).ToArray())
        {
            await accountStreams.StopAsync(running, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 按模式造跟单员。
    ///
    /// 实盘少了一个依赖是**故意的**：它不需要盯市价（真实盈亏以交易所的账户状态为准，
    /// 本地再算一份只会在两边不一致时制造"该信谁"的问题）。
    /// </summary>
    private IFollower Create(Follower follower) => follower.Mode == CopyMode.Live
        ? new LiveFollower(
            scopeFactory,
            brokers,
            catalogs,
            limits,
            loggerFactory.CreateLogger<LiveFollower>(),
            follower)
        : new PaperFollower(
            scopeFactory,
            market,
            options,
            loggerFactory.CreateLogger<PaperFollower>(),
            ledgerActivity,
            followerLive,
            follower);

    /// <summary>
    /// 让一个订阅者按带单员的最近快照对齐一次。没有基线的池跳过（等首次推送建立）。
    ///
    /// **异常在这里被拦住，不往上抛**：对齐一个跟单员失败（交易所读不到、key 失效、
    /// 网络抖动）只影响这一个跟单员，而放它上去会掀掉整张桌子——实测有一次币安回 401
    /// （key 的 IP 白名单不匹配），异常一路上抛到 BackgroundService，
    /// 而 .NET 对它的默认处理是 **StopHost**：**整个应用停机**，
    /// 连纸面跟单和池子观察都一起停了。一个账号的凭证问题不该有这种权力。
    /// </summary>
    private async Task CatchUpAsync(
        LeadTraderRuntime runtime,
        IFollower follower,
        CancellationToken cancellationToken)
    {
        if (runtime.LastSnapshot is not { } snapshot)
        {
            return;
        }

        try
        {
            await follower.CatchUpAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "跟单员 {Id} 对齐失败（不影响其他跟单员与池子观察）：{Message}",
                follower.Id,
                ex.Message);
        }
    }

    /// <summary>
    /// 首次运行时把配置里的初始名单播种成带单员行（"这个池我只想看着，不跟"）。
    ///
    /// 只补缺、不覆盖：库里已存在就不再动，否则每次启动都会把用户特意暂停过的带单员重新打开。
    /// 返回是否写入了新行——调用方据此决定要不要重读列表。
    /// </summary>
    private async Task<bool> SeedFromConfigAsync(
        ILeadTraderRepository leads,
        IReadOnlyList<LeadTrader> existing,
        CancellationToken cancellationToken)
    {
        if (options.Value.SeedAccountIndexes.Count == 0)
        {
            return false;
        }

        HashSet<long> present = [.. existing.Select(l => l.AccountIndex.Value)];
        bool added = false;

        foreach (long index in options.Value.SeedAccountIndexes.Where(i => !present.Contains(i)))
        {
            try
            {
                await CreateLeadAsync(leads, AccountIndex.From(index), "配置里的初始名单", cancellationToken)
                    .ConfigureAwait(false);

                added = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 播种失败不该拦住启动：那只是"少了一个默认观察对象"，界面上照样能创建
                logger.LogWarning(ex, "播种带单员 {Account} 失败，跳过", index);
            }
        }

        return added;
    }

    /// <summary>为每个跟单员的订阅目标补齐带单员——订阅关系成立就意味着观察者该存在。</summary>
    private async Task<bool> EnsureLeadsForFollowersAsync(
        ILeadTraderRepository leads,
        IReadOnlyList<LeadTrader> existing,
        IReadOnlyList<Follower> followers,
        CancellationToken cancellationToken)
    {
        HashSet<long> present = [.. existing.Select(l => l.AccountIndex.Value)];
        bool added = false;

        foreach (long index in followers
            .Select(f => f.LeadAccountIndex.Value)
            .Distinct()
            .Where(i => !present.Contains(i)))
        {
            try
            {
                await CreateLeadAsync(leads, AccountIndex.From(index), "跟单员的订阅目标", cancellationToken)
                    .ConfigureAwait(false);

                added = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "为跟单员补齐带单员 {Account} 失败，跳过", index);
            }
        }

        return added;
    }

    private async Task CreateLeadAsync(
        ILeadTraderRepository leads,
        AccountIndex pool,
        string reason,
        CancellationToken cancellationToken)
    {
        // 池名解析失败会退回"池 {索引}"，不抛异常——名字不可读总好过没有观察者
        string name = await market.PoolNameAsync(pool, cancellationToken).ConfigureAwait(false);

        await leads.UpsertAsync(
            new LeadTrader
            {
                AccountIndex = pool,
                Name = name,
                IsEnabled = true,
                CreatedAt = LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation("新增带单员（{Reason}）：{Name}（{Account}）", reason, name, pool.Value);
    }

    /// <summary>重连之后重取基线：断线期间的变化无从差分，只能以当下为新的起点。</summary>
    public async Task ResetBaselinesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (LeadTraderRuntime runtime in _leads.Values)
            {
                await CaptureBaselineAsync(runtime, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 收到某个池的持仓推送：取一次 REST 快照（WS 的持仓帧不含账户级总量），
    /// 差分出信号，落库、广播、分发给订阅者。
    /// </summary>
    public async Task ObserveAsync(AccountIndex pool, CancellationToken cancellationToken)
    {
        if (!_leads.TryGetValue(pool.Value, out LeadTraderRuntime? runtime) || !runtime.IsEnabled)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ObserveCoreAsync(runtime, pool, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ObserveCoreAsync(
        LeadTraderRuntime runtime,
        AccountIndex pool,
        CancellationToken cancellationToken)
    {

        PoolSnapshot current;

        await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
        {
            current = await scope.ServiceProvider
                .GetRequiredService<ILighterPoolsClient>()
                .FetchSnapshotAsync(pool, cancellationToken)
                .ConfigureAwait(false);
        }

        IReadOnlyList<PoolEvent> events = runtime.ApplySnapshot(current);

        if (events.Count == 0)
        {
            // 没有仓位变化，但池子权益可能变了（而规模比例依赖权益），
            // 所以仍然让订阅者按当前状态对齐一次——差额为 0 时它什么都不做。
            await runtime.CatchUpSubscribersAsync(current, cancellationToken).ConfigureAwait(false);

            // 池子的权益/仓位快照变了，公开页的卡片与详情要跟着刷新
            leadActivity.NotifyLeadChanged(pool.Value);
            await followerLive.BroadcastFollowerChangedAsync(0, cancellationToken).ConfigureAwait(false);
            return;
        }

        List<LeadSignal> signals = [.. events.Select(e => new LeadSignal(e, runtime.Lead.Name, current.TotalAssetValue))];

        await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
        {
            IServiceProvider provider = scope.ServiceProvider;

            await provider
                .GetRequiredService<IPoolEventRepository>()
                .AppendAsync(events, cancellationToken)
                .ConfigureAwait(false);

            foreach (LeadSignal signal in signals)
            {
                await provider
                    .GetRequiredService<IMonitorBroadcaster>()
                    .BroadcastEventAsync(PoolMapper.ToEvent(signal.Event, signal.PoolName), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        logger.LogInformation("带单员 {Name} 产出 {Count} 条信号", runtime.Lead.Name, signals.Count);

        foreach (LeadSignal signal in signals)
        {
            await runtime.NotifyAsync(signal, current, cancellationToken).ConfigureAwait(false);
        }

        // 跟单员落账后还会走 IFollowerLedgerActivity；这里覆盖"池动了但没有新流水"
        // （例如规模比例/池子权益变化）的情况。
        leadActivity.NotifyLeadChanged(pool.Value);
        await followerLive.BroadcastFollowerChangedAsync(0, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 心跳兜底：让每个带单员的订阅者按**最近一次快照**对齐一次。
    ///
    /// 存在的理由：仓位频道只在真正变化时推送，而"池子一直没动"与"我落后了"
    /// 在只有推送的模型里长得一模一样。心跳与上游活跃度无关，是唯一可靠的另一个驱动源。
    /// </summary>
    /// <summary>
    /// 心跳的体检：让每个订阅者算一遍偏差。**实盘不会因此下单**（见 IFollower.CheckDriftAsync）。
    /// </summary>
    public async Task CheckDriftAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (LeadTraderRuntime runtime in _leads.Values)
            {
                if (!runtime.IsEnabled || runtime.LastSnapshot is null)
                {
                    continue;
                }

                await runtime.CatchUpSubscribersAsync(runtime.LastSnapshot, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 连接断开期间的兜底：主动拉 REST 快照，让跟单员不至于"静止到重连为止"。
    ///
    /// 连接正常时永不触发——推送始终比 REST 快照新，叠加轮询只会让更新的推送
    /// 被更旧的快照覆盖，凭空造出"平了又开"的假动作。
    /// </summary>
    public async Task PollFallbackAsync(CancellationToken cancellationToken)
    {
        if (socket.IsLive)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await PollFallbackCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PollFallbackCoreAsync(CancellationToken cancellationToken)
    {

        foreach (LeadTraderRuntime runtime in _leads.Values)
        {
            if (!runtime.IsEnabled || !ShouldPollByRest(runtime.AccountIndex.Value))
            {
                continue;
            }

            try
            {
                PoolSnapshot snapshot;

                await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
                {
                    snapshot = await scope.ServiceProvider
                        .GetRequiredService<ILighterPoolsClient>()
                        .FetchSnapshotAsync(runtime.AccountIndex, cancellationToken)
                        .ConfigureAwait(false);
                }

                runtime.ResetBaseline(snapshot);
                await runtime.CatchUpSubscribersAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 取不到这个池的快照不该跳过其余池——下一个节流周期会自然重试
                logger.LogWarning(ex, "断线兜底取池 {Account} 的快照失败，本轮跳过", runtime.AccountIndex.Value);
            }
        }
    }

    private bool ShouldPollByRest(long accountIndex)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (_restFallbackAt.TryGetValue(accountIndex, out DateTimeOffset last)
            && now - last < TimeSpan.FromSeconds(Math.Max(1, options.Value.PaperRestReconcileSeconds)))
        {
            return false;
        }

        _restFallbackAt[accountIndex] = now;

        return true;
    }

    private async Task CaptureBaselineAsync(LeadTraderRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            PoolSnapshot snapshot;

            await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
            {
                snapshot = await scope.ServiceProvider
                    .GetRequiredService<ILighterPoolsClient>()
                    .FetchSnapshotAsync(runtime.AccountIndex, cancellationToken)
                    .ConfigureAwait(false);
            }

            runtime.ResetBaseline(snapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "池 {Account} 基线快照获取失败，将在首次推送时建立", runtime.AccountIndex.Value);
        }
    }

    /// <summary>释放串行锁。单例随容器关闭而释放，此时后台服务已经停下来了。</summary>
    /// <summary>
    /// 停机清理。
    ///
    /// **刻意不释放 <c>_gate</c>**：心跳线程可能正好停在它的 <c>WaitAsync</c> 里，
    /// 释放之后它会撞上 ObjectDisposedException（实测在停机时出现过一段"心跳兜底失败"）。
    /// SemaphoreSlim 只有在用过 <c>AvailableWaitHandle</c> 时才需要释放，这里没用过，
    /// 让它随进程消失是安全的——停机路径上少一个竞态。
    /// </summary>
    public void Dispose()
    {
    }
}
