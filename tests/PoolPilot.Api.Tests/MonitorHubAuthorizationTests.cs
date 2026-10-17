using Microsoft.Extensions.Logging.Abstractions;

using PoolPilot.Api.Hubs;
using PoolPilot.Api.Tests.Fakes;
using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Constants;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 监控 Hub 的准入。
///
/// 与 <see cref="MonitorStatusChannelTests"/> 分开：那一组测"状态补发"的语义，
/// 这一组只测"谁能连进来"。准入的关键在 <c>OnConnectedAsync</c> 的两个分支——
/// 没有 <c>menu:graph</c> 的 <c>view</c> 就 <c>Abort</c>，有则照常补发。
///
/// 内容是全量广播（不按收件人裁剪），所以"拒绝连接"是唯一能挡住内容的做法——
/// 与逐条过滤的接口不同，这里的准入一旦漏掉，就没有第二道防线。
/// </summary>
[TestClass]
public sealed class MonitorHubAuthorizationTests
{
    [TestMethod]
    public async Task OnConnected_WithoutGraphView_IsAbortedAndGetsNothing()
    {
        // 零权限：menu:graph 的 view 判不过
        RecordingPermissionEvaluator evaluator = new((_, _) => false);
        RecordingClientProxy proxy = new();
        SignalRMonitorBroadcaster broadcaster = new(new StubHubContext(new RecordingHubClients(proxy)), new NoopFollowerLiveBroadcaster());
        await broadcaster.BroadcastStatusAsync(Status());

        StubHubCallerContext context = new(userIdentifier: "7");
        MonitorHub hub = new(broadcaster, evaluator, NullLogger<MonitorHub>.Instance)
        {
            Clients = new RecordingHubClients(proxy),
            Context = context,
        };

        proxy.Sent.Clear();

        await hub.OnConnectedAsync();

        Assert.IsTrue(context.Aborted, "没有「总览」view 权限的连接必须被拒绝");
        Assert.IsEmpty(proxy.Sent, "被拒的连接不该收到任何推送（包括状态补发）");
        Assert.HasCount(1, evaluator.Queries, "准入应当只问一次");
        Assert.AreEqual(
            (PermissionKeys.Graph, MenuActions.View),
            evaluator.Queries[0],
            "准入问的必须是「总览」的 view");
    }

    [TestMethod]
    public async Task OnConnected_WithGraphView_IsNotAbortedAndReceivesStatus()
    {
        RecordingPermissionEvaluator evaluator = new(
            (key, action) => key == PermissionKeys.Graph && action == MenuActions.View);
        RecordingClientProxy proxy = new();
        SignalRMonitorBroadcaster broadcaster = new(new StubHubContext(new RecordingHubClients(proxy)), new NoopFollowerLiveBroadcaster());
        await broadcaster.BroadcastStatusAsync(Status());

        StubHubCallerContext context = new(userIdentifier: "7");
        MonitorHub hub = new(broadcaster, evaluator, NullLogger<MonitorHub>.Instance)
        {
            Clients = new RecordingHubClients(proxy),
            Context = context,
        };

        proxy.Sent.Clear();

        await hub.OnConnectedAsync();

        Assert.IsFalse(context.Aborted, "有「总览」view 权限的连接不该被拒");
        Assert.HasCount(1, proxy.Sent, "通过准入的连接照常收到状态补发");
        Assert.AreEqual("monitorStatus", proxy.Sent[0].Method);
    }

    private static MonitorStatusDto Status() => new()
    {
        State = "first_connect",
        Detail = "detail",
        IsConnected = true,
        MonitoredAccounts = [281474976552443L],
        SubscribedChannels = ["account_all_positions/281474976552443"],
        LastMessageAt = null,
    };
}

/// <summary>监控 Hub 测试不关心跟单实时通道；这里只提供空实现。</summary>
internal sealed class NoopFollowerLiveBroadcaster : IFollowerLiveBroadcaster
{
    public Task BroadcastFollowerChangedAsync(long followerId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task BroadcastMarketPriceAsync(int marketId, decimal price, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
