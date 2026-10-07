using PoolPilot.Api.Hubs;
using PoolPilot.Api.Tests.Fakes;
using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 监控状态通道的契约测试。
///
/// 这里锁的是本项目最容易被误解的一条语义：**"没有事件"与"连接故障"必须能区分**。
/// 实测池的活动率只有市场成交的约 0.24%，长时间没有事件是正常的；
/// 如果界面分不清这两者，使用者会把正常运行误判成故障，或者反过来。
/// </summary>
[TestClass]
public sealed class MonitorStatusChannelTests
{
    private static MonitorStatusDto Status(string state, bool isConnected, DateTimeOffset? lastMessageAt = null) => new()
    {
        State = state,
        Detail = $"detail for {state}",
        IsConnected = isConnected,
        MonitoredAccounts = [281474976552443L],
        SubscribedChannels = ["account_all_positions/281474976552443"],
        LastMessageAt = lastMessageAt,
    };

    [TestMethod]
    public void Broadcast_WithStatus_RemembersItSoLateClientsCanReadIt()
    {
        // Arrange
        RecordingClientProxy proxy = new();
        SignalRMonitorBroadcaster broadcaster = new(new StubHubContext(new RecordingHubClients(proxy)));
        MonitorStatusDto status = Status("first_connect", isConnected: true);

        // Act
        broadcaster.BroadcastStatusAsync(status).GetAwaiter().GetResult();

        // Assert
        Assert.AreSame(status, broadcaster.CurrentStatus, "广播过的状态必须被记下，否则后连上来的页面读不到当前状态");
    }

    [TestMethod]
    public void Broadcast_WithStatus_PushesToClients()
    {
        // Arrange
        RecordingClientProxy proxy = new();
        SignalRMonitorBroadcaster broadcaster = new(new StubHubContext(new RecordingHubClients(proxy)));

        // Act
        broadcaster.BroadcastStatusAsync(Status("event", isConnected: true)).GetAwaiter().GetResult();

        // Assert
        Assert.HasCount(1, proxy.Sent);
        Assert.AreEqual("monitorStatus", proxy.Sent[0].Method);
    }

    [TestMethod]
    public void OnConnected_WhenStatusWasBroadcastBefore_SendsCurrentStatusToCaller()
    {
        // Arrange —— 模拟"浏览器在状态变化之后才打开页面"
        RecordingClientProxy proxy = new();
        SignalRMonitorBroadcaster broadcaster = new(new StubHubContext(new RecordingHubClients(proxy)));
        broadcaster.BroadcastStatusAsync(Status("first_connect", isConnected: true)).GetAwaiter().GetResult();
        proxy.Sent.Clear();

        MonitorHub hub = new(broadcaster)
        {
            Clients = new RecordingHubClients(proxy),
            Context = new StubHubCallerContext(),
        };

        // Act
        hub.OnConnectedAsync().GetAwaiter().GetResult();

        // Assert
        Assert.HasCount(1, proxy.Sent, "新连上的客户端必须补发一次当前状态");
        Assert.AreEqual("monitorStatus", proxy.Sent[0].Method);
        Assert.AreSame(broadcaster.CurrentStatus, proxy.Sent[0].Args[0]);
    }

    [TestMethod]
    public void OnConnected_WhenNoStatusYet_SendsNothing()
    {
        // Arrange —— 监控服务还没跑起来时不该编造状态
        RecordingClientProxy proxy = new();
        SignalRMonitorBroadcaster broadcaster = new(new StubHubContext(new RecordingHubClients(proxy)));
        MonitorHub hub = new(broadcaster)
        {
            Clients = new RecordingHubClients(proxy),
            Context = new StubHubCallerContext(),
        };

        // Act
        hub.OnConnectedAsync().GetAwaiter().GetResult();

        // Assert
        Assert.IsEmpty(proxy.Sent, "没有状态可报时应该什么都不发，而不是发一个假状态");
    }

    [TestMethod]
    public void Status_WithoutUpstreamMessageYet_ReportsNullNotCurrentTime()
    {
        // Arrange —— 这是关键区分：LastMessageAt 表示"收到过上游消息"，不是"状态生成于此刻"。
        // 如果填当前时间，界面的静默计时器每次广播都会被清零，计时器永远显示 0，
        // 正好掩盖了它本来要暴露的问题。
        MonitorStatusDto status = Status("first_connect", isConnected: true);

        // Act + Assert
        Assert.IsNull(status.LastMessageAt);
    }
}
