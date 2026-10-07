using Microsoft.AspNetCore.SignalR;

using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;

namespace PoolPilot.Api.Hubs;

/// <summary>
/// 监控推送 Hub。
///
/// 推送三类消息，前端据此区分"没信号"与"没动作"：
/// <list type="bullet">
///   <item><c>monitorEvent</c> —— 池的持仓变化（开仓 / 加仓 / 减仓 / 平仓 / 反手）</item>
///   <item><c>copyFill</c> —— 跟单对照记录（含实时漏损）</item>
///   <item><c>monitorStatus</c> —— 连接状态。**这条是必需的**：实测池的活动率只有市场成交的
///     约 0.24%，长时间没有事件是正常的；而连接断了才是故障。没有状态推送，
///     使用者无法区分这两者。</item>
/// </list>
///
/// <see cref="OnConnectedAsync"/> 会向新连上来的客户端补发一次当前状态：
/// 状态只在**变化**时广播，而浏览器可能在所有变化之后才打开页面，
/// 不补发的话新页面会一直停在"未知"——那和"监控断了"长得一模一样。
/// </summary>
public sealed class MonitorHub(IMonitorBroadcaster broadcaster) : Hub
{
    /// <summary>供前端查询当前订阅的池（用于首次加载时展示"正在监控哪些池"）。</summary>
    public Task<string> Ping() => Task.FromResult("pong");

    public override async Task OnConnectedAsync()
    {
        MonitorStatusDto? current = broadcaster.CurrentStatus;

        if (current is not null)
        {
            await Clients.Caller.SendAsync("monitorStatus", current).ConfigureAwait(false);
        }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 基于 SignalR 的监控广播实现。
///
/// 同时持有最近一次状态：状态只在变化时推送，而客户端可能晚于所有变化才连接，
/// 那时需要一个地方回答"此刻状态是什么"。这个字段就是那个地方——由推送本身体写入，
/// 因此不会和实际推送出去的内容不一致。
/// </summary>
public sealed class SignalRMonitorBroadcaster(IHubContext<MonitorHub> hub) : IMonitorBroadcaster
{
    public MonitorStatusDto? CurrentStatus { get; private set; }

    public Task BroadcastEventAsync(PoolEventDto poolEvent, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync("monitorEvent", poolEvent, cancellationToken);

    public Task BroadcastCopyFillAsync(CopyFillDto fill, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync("copyFill", fill, cancellationToken);

    public Task BroadcastLiveAccountAsync(
        long followerId,
        LiveAccountDto account,
        CancellationToken cancellationToken = default) =>
        // 事件名带上跟单员 Id：同一个账号下可能挂着多个跟单员，页面只认自己那一个
        hub.Clients.All.SendAsync("liveAccount", followerId, account, cancellationToken);

    public Task BroadcastLivePnlAsync(
        long followerId,
        IReadOnlyDictionary<string, decimal> byPair,
        decimal total,
        IReadOnlyDictionary<string, decimal> marksByPair,
        CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync("livePnl", followerId, byPair, total, marksByPair, cancellationToken);

    public Task BroadcastAccountStreamHealthAsync(
        long followerId,
        bool healthy,
        CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync("accountStreamHealth", followerId, healthy, cancellationToken);

    public async Task BroadcastStatusAsync(
        MonitorStatusDto status,
        CancellationToken cancellationToken = default)
    {
        // 先记后推：这样补发拿到的状态不会落后于已经广播出去的状态
        CurrentStatus = status;

        await hub.Clients.All.SendAsync("monitorStatus", status, cancellationToken).ConfigureAwait(false);
    }
}