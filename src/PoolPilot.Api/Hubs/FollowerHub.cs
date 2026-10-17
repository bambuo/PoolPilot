using Microsoft.AspNetCore.SignalR;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Constants;

namespace PoolPilot.Api.Hubs;

/// <summary>
/// 工作台跟单模块的实时 Hub。
///
/// 与 <see cref="MonitorHub"/> 分开：那个 Hub 的门是「总览」页权限，内容是全量广播；
/// 跟单模块需要自己的门（<c>menu:copy-list</c>）与按市场订阅的行情通道，
/// 混在一个 Hub 里会让"能看总览的人"顺带收到跟单数据。
///
/// 连接建立时校验「跟单」的 view 权限；行情按 <see cref="SubscribeMarkets"/> 加入
/// 市场分组，后续价格帧只发给真正需要它的连接。账本事件仍发全体（内容只是"该刷新了"，
/// 真正的数据由 REST 端点按同一权限点返回）。
///
/// 入组的同时还要确保上游订了 <c>market_stats/{marketId}</c>：分组只决定"谁收得到"，
/// 上游没订就根本没有帧可发。订阅是进程级幂等的（协议没有退订帧），
/// 所以一次 EnsureSubscribed 之后所有连接共用。
/// </summary>
public sealed class FollowerHub(
    IPermissionEvaluator permissions,
    IMarketStatsSubscription marketStats,
    ILogger<FollowerHub> logger) : Hub
{
    public Task<string> Ping() => Task.FromResult("pong");

    /// <summary>订阅这些市场的行情帧。重复调用是幂等的。</summary>
    public async Task SubscribeMarkets(int[] marketIds)
    {
        foreach (int marketId in marketIds.Distinct())
        {
            marketStats.EnsureSubscribed(marketId);
            await Groups.AddToGroupAsync(Context.ConnectionId, MarketGroup(marketId)).ConfigureAwait(false);
        }
    }

    public override async Task OnConnectedAsync()
    {
        if (!permissions.Has(PermissionKeys.CopyList, MenuActions.View))
        {
            logger.LogWarning(
                "拒绝跟单 Hub 连接：用户 {UserIdentifier} 没有 {Permission} 的 {Action} 权限",
                Context.UserIdentifier ?? "匿名",
                PermissionKeys.CopyList,
                MenuActions.View);

            Context.Abort();
            return;
        }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    internal static string MarketGroup(int marketId) => $"market:{marketId}";
}

/// <summary>基于 SignalR 的跟单实时广播实现。</summary>
public sealed class SignalRFollowerLiveBroadcaster(IHubContext<FollowerHub> hub) : IFollowerLiveBroadcaster
{
    public Task BroadcastFollowerChangedAsync(long followerId, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync("followerChanged", followerId, cancellationToken);

    public Task BroadcastMarketPriceAsync(
        int marketId,
        decimal price,
        CancellationToken cancellationToken = default) =>
        hub.Clients
            .Group(FollowerHub.MarketGroup(marketId))
            .SendAsync("marketPrice", marketId, price, cancellationToken);
}
