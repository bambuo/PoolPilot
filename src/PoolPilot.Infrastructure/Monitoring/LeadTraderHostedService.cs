using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.ValueObjects;
using PoolPilot.Infrastructure.Lighter;

namespace PoolPilot.Infrastructure.Monitoring;

/// <summary>
/// 监控主机：一条 WebSocket 连接、一路帧分发、一个心跳。
///
/// 它**不知道跟单员的存在**——收到帧就交给注册表（带单员集合），
/// 由那边决定产出信号并分发给订阅者。所以"跟着的账户全删了"不会让监控停摆，
/// 而"某个池没人跟"也不会让它的信号少记一条。
///
/// 数据来源与实测依据：
/// <list type="bullet">
///   <item>持仓变化 —— 订阅 WS <c>account_all_positions</c>。该频道**会推增量**
///     （实测普通账户 72 秒推 261 条，真实公共池 300 秒推 4 条），
///     但**只在真正变化时推**，池子不动时一条都不推。</item>
///   <item>成交 —— 订阅 WS <c>account_all_trades</c>，用它驱动跟单漏损计算。</item>
///   <item>账户总量 —— 走 REST <c>account</c>。WS 的持仓频道不含账户级余额字段。</item>
/// </list>
///
/// 一条重要的设计纪律：**"没有事件"与"连接故障"必须区分开。**
/// 实测池的活动率只有市场成交的约 0.24%，长时间无事件是正常的；
/// 连接断开才是问题。所以每次状态变化都通过 <see cref="IMonitorBroadcaster"/> 广播状态，
/// 让界面能正确表达这两者的差别。
///
/// 生命周期说明：HostedService 是单例，而 <c>ILighterPoolsClient</c>、仓储与
/// EF Core 的 <c>DbContext</c> 都是 scoped —— 直接用构造器注入会在启动时被 DI 校验拦下。
/// 因此这里注入的依赖全部是单例（注册表、缓存、客户端、广播器），
/// scoped 的东西一律在注册表内部按需开作用域。
/// </summary>
public sealed class LeadTraderHostedService(
    LighterWebSocketClient socket,
    LeadTraderRegistry registry,
    CopySlippageRecorder slippage,
    IMonitorBroadcaster broadcaster,
    IMarkPriceSink markPrices,
    IMarketPriceActivity marketPrices,
    IOptions<LeadTraderOptions> options,
    ILogger<LeadTraderHostedService> logger) : BackgroundService
{
    /// <summary>最后一次广播出去的状态与连接性。心跳据此重播，从而只刷新遥测、不篡改状态。</summary>
    private string _state = "starting";
    private string _detail = "启动中";
    private bool _isConnected;
    private DateTimeOffset? _lastUpstreamMessageAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("监控未启用，服务退出");
            await PublishStatusAsync("disabled", "监控未启用", isConnected: false, stoppingToken).ConfigureAwait(false);
            return;
        }

        // 库里的带单员决定订阅集合。这里**不关心跟单员**：
        // "谁在被观察"与"谁在跟"从此是两个独立的问题。
        //
        // 启动这一次同步失败**不让服务退出**：失败通常是暂时的（交易所抖、key 的 IP 白名单
        // 不匹配、网络断），而心跳每 15 秒还会再同步一次，届时自己就恢复了。
        // 放它抛出去的后果是 StopHost——整套监控（含纸面跟单、池子观察）一起停摆，
        // 而原因可能只是某个实盘账号的凭证问题。
        try
        {
            await registry.SyncAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "启动同步失败，服务继续运行（心跳会每 {Seconds} 秒重试）：{Message}",
                options.Value.StatusHeartbeatSeconds,
                ex.Message);
        }

        List<string> channels = [];

        foreach (long index in registry.WatchedAccounts())
        {
            channels.Add($"account_all_positions/{index}");
            channels.Add($"account_all_trades/{index}");
        }

        await PublishStatusAsync(
            "subscribing",
            channels.Count == 0 ? "还没有带单员，等待创建" : $"订阅 {channels.Count} 个频道",
            false,
            stoppingToken).ConfigureAwait(false);

        using CancellationTokenSource loopCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task heartbeat = RunHeartbeatAsync(loopCts.Token);

        try
        {
            await foreach (JsonElement message in socket
                .StreamAsync(channels, stoppingToken)
                .ConfigureAwait(false))
            {
                try
                {
                    await HandleMessageAsync(message, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // **逐帧兜底**。一帧坏数据（例如上游在仓位变动瞬间给出的中间态）
                    // 只该丢掉这一帧，不该终止整个监控——否则上游的一次瞬时怪值
                    // 会让监控停摆到有人手动重启。帧与帧之间没有需要回滚的共享状态。
                    logger.LogError(ex, "处理上游消息失败，跳过该帧");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "监控循环异常终止");
            await PublishStatusAsync("error", $"监控异常：{ex.Message}", false, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            await loopCts.CancelAsync().ConfigureAwait(false);

            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 心跳随主循环一起停，属正常
            }
        }
    }

    /// <summary>
    /// 定期按当前状态重播一次（理由见 <see cref="LeadTraderOptions.StatusHeartbeatSeconds"/>），
    /// 同时驱动三件兜底的事：与库同步（新配置的带单员/跟单员上线）、
    /// 让跟单员按最近快照对齐、连接断了就退回 REST。
    ///
    /// 三件事都在同一把派发锁里，与帧处理互斥。
    /// </summary>
    private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(Math.Max(1, options.Value.StatusHeartbeatSeconds)));

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await PublishStatusAsync(_state, _detail, _isConnected, cancellationToken).ConfigureAwait(false);

            try
            {
                // 三个入口各自排队（锁在注册表里）——它们改的是同一批状态，
                // 而心跳没有"必须整批原子"的要求
                await registry.SyncAsync(cancellationToken).ConfigureAwait(false);

                // **体检而不是对齐**：对齐会下单，而心跳每 15 秒一次——那会变成
                // "池子没动系统也在动"（手动平掉的仓位被一次次买回来）。
                // 实盘只报偏差，纸面照旧对账（它是模拟，要贴着池子）
                await registry.CheckDriftAllAsync(cancellationToken).ConfigureAwait(false);
                await registry.PollFallbackAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 兜底失败不该拖垮监控主循环——上游推送与状态广播还要继续
                logger.LogError(ex, "心跳兜底失败");
            }
        }
    }

    private async Task HandleMessageAsync(JsonElement message, CancellationToken cancellationToken)
    {
        string type = message.TryGetProperty("type", out JsonElement typeElement)
            ? typeElement.GetString() ?? string.Empty
            : string.Empty;

        // 连接生命周期标记由 WS 客户端发出，用于区分"无事件"与"连接中断"
        if (type is "first_connect" or "reconnected" or "disconnected")
        {
            string detail = type switch
            {
                "first_connect" => "首次连接成功",
                "reconnected" => "已重连，此前订阅已重置，快照将重取",
                _ => "连接断开，正在退避重连",
            };

            await PublishStatusAsync(type, detail, type != "disconnected", cancellationToken).ConfigureAwait(false);

            if (type == "reconnected")
            {
                // 断线期间本地快照已经过期，重取基线以免产生错误的差分事件
                await registry.ResetBaselinesAsync(cancellationToken).ConfigureAwait(false);

                // 断线期间可能漏了池子的动作（基线一重置，那些差分就没了）。
                // 这里也**只体检不对齐**：对齐等于追高，而漏掉的会以偏差的形式被报出来
                await registry.CheckDriftAllAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // 真正的上游数据帧才算一条消息；生命周期标记与保活帧不算
        _lastUpstreamMessageAt = DateTimeOffset.UtcNow;

        // 实时行情（公开详情的标记价/未实现盈亏靠它，不再等 REST 缓存过期）
        if (message.TryGetProperty("market_stats", out JsonElement marketStats)
            && marketStats.ValueKind == JsonValueKind.Object)
        {
            HandleMarketStats(marketStats);
            return;
        }

        if (message.TryGetProperty("positions", out JsonElement positions) && positions.ValueKind == JsonValueKind.Object)
        {
            await HandlePositionUpdateAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message.TryGetProperty("trades", out JsonElement trades) && trades.ValueKind == JsonValueKind.Object)
        {
            await slippage.RecordAsync(trades, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandlePositionUpdateAsync(JsonElement message, CancellationToken cancellationToken)
    {
        long accountIndex = ResolveAccountIndex(message);

        // 没有带单员的池不谈——帧可能是退场频道留下的（协议里没有验证过退订帧）
        if (accountIndex == 0L || !registry.IsObserved(accountIndex))
        {
            return;
        }

        AccountIndex account;

        try
        {
            account = AccountIndex.From(accountIndex);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        await registry.ObserveAsync(account, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 一帧 <c>market_stats</c>：取中间价（缺失时退回标记价）写入估值缓存，并通知公开页。
    /// 估值口径与原来的 REST 盘口中间价一致；交易/成交路径不走这里。
    /// </summary>
    private void HandleMarketStats(JsonElement stats)
    {
        if (!stats.TryGetProperty("market_id", out JsonElement idElement)
            || idElement.ValueKind != JsonValueKind.Number
            || !idElement.TryGetInt32(out int marketId))
        {
            return;
        }

        decimal? mid = ReadDecimal(stats, "mid_price") ?? ReadDecimal(stats, "mark_price");

        if (mid is not > 0m)
        {
            return;
        }

        PriceAmount price = PriceAmount.From(mid.Value);
        markPrices.Update(marketId, price);
        marketPrices.NotifyMarketPriceChanged(marketId, price);
    }

    private static decimal? ReadDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal text)
                ? text
                : null;
    }

    /// <summary>发布状态：先记住，再广播。心跳要重播同一份状态，所以状态必须留痕。</summary>
    private async Task PublishStatusAsync(
        string state,
        string detail,
        bool isConnected,
        CancellationToken cancellationToken)
    {
        _state = state;
        _detail = detail;
        _isConnected = isConnected;

        await broadcaster.BroadcastStatusAsync(Status(state, detail, isConnected), cancellationToken)
            .ConfigureAwait(false);
    }

    private MonitorStatusDto Status(string state, string detail, bool isConnected) => new()
    {
        State = state,
        Detail = detail,
        IsConnected = isConnected,
        MonitoredAccounts = registry.WatchedAccounts(),
        SubscribedChannels =
        [
            .. registry.WatchedAccounts().SelectMany(i => new[] { $"account_all_positions/{i}", $"account_all_trades/{i}" }),
        ],
        // 如实上报数据管道的活性：没有收到过上游消息就是 null，不用"状态生成时间"顶替
        LastMessageAt = isConnected ? _lastUpstreamMessageAt : null,
    };

    private static long ResolveAccountIndex(JsonElement message)
    {
        if (message.TryGetProperty("account", out JsonElement accountElement) && accountElement.ValueKind == JsonValueKind.Number)
        {
            return accountElement.GetInt64();
        }

        if (message.TryGetProperty("channel", out JsonElement channelElement))
        {
            string channel = channelElement.GetString() ?? string.Empty;
            int separator = channel.IndexOf(':', StringComparison.Ordinal);

            if (separator >= 0 && long.TryParse(channel[(separator + 1)..], out long parsed))
            {
                return parsed;
            }
        }

        return 0L;
    }
}
