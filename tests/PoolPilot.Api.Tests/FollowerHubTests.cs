using Microsoft.Extensions.Logging.Abstractions;

using PoolPilot.Api.Hubs;
using PoolPilot.Api.Tests.Fakes;
using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Constants;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 跟单实时 Hub 的准入、市场订阅与广播路由。
///
/// 这个 Hub 的门是「跟单」页的 view：漏掉它，能登录但没有跟单权限的人
/// 也能收到账本变化帧。行情帧按市场分组发，路由错了的后果是"页面上一半标的
/// 的价格不动"，而那看起来像行情源坏了。
/// </summary>
[TestClass]
public sealed class FollowerHubTests
{
    private static readonly string[] ExpectedTargets = ["all", "group:market:7"];

    private static readonly int[] ExpectedMarkets = [7, 9];

    [TestMethod]
    public async Task OnConnected_WithoutCopyListView_IsAborted()
    {
        RecordingPermissionEvaluator evaluator = new((_, _) => false);
        StubHubCallerContext context = new(userIdentifier: "7");
        FollowerHub hub = new(evaluator, new RecordingMarketStats(), NullLogger<FollowerHub>.Instance)
        {
            Context = context,
            Groups = new StubGroupManager(),
        };

        await hub.OnConnectedAsync();

        Assert.IsTrue(context.Aborted, "没有「跟单」view 权限的连接必须被拒绝");
        Assert.HasCount(1, evaluator.Queries, "准入应当只问一次");
        Assert.AreEqual(
            (PermissionKeys.CopyList, MenuActions.View),
            evaluator.Queries[0],
            "准入问的必须是「跟单」的 view");
    }

    [TestMethod]
    public async Task OnConnected_WithCopyListView_IsNotAborted()
    {
        RecordingPermissionEvaluator evaluator = new(
            (key, action) => key == PermissionKeys.CopyList && action == MenuActions.View);
        StubHubCallerContext context = new(userIdentifier: "7");
        FollowerHub hub = new(evaluator, new RecordingMarketStats(), NullLogger<FollowerHub>.Instance)
        {
            Context = context,
            Groups = new StubGroupManager(),
        };

        await hub.OnConnectedAsync();

        Assert.IsFalse(context.Aborted, "有「跟单」view 权限的连接不该被拒");
    }

    [TestMethod]
    public async Task SubscribeMarkets_JoinsEachDistinctMarketGroup()
    {
        StubGroupManager groups = new();
        RecordingMarketStats marketStats = new();
        FollowerHub hub = new(
            new RecordingPermissionEvaluator((_, _) => true),
            marketStats,
            NullLogger<FollowerHub>.Instance)
        {
            Context = new StubHubCallerContext(userIdentifier: "7"),
            Groups = groups,
        };

        await hub.SubscribeMarkets([7, 7, 9]);

        Assert.HasCount(2, groups.Added, "重复的市场只该入组一次");
        Assert.AreEqual(("test-connection", "market:7"), groups.Added[0]);
        Assert.AreEqual(("test-connection", "market:9"), groups.Added[1]);

        CollectionAssert.AreEqual(
            ExpectedMarkets,
            marketStats.Markets,
            "入组的同时必须确保上游订了这两个市场的行情——只入组是没有帧可发的");
    }

    [TestMethod]
    public async Task Broadcaster_SendsChangesToEveryoneAndPricesToTheMarketGroup()
    {
        RecordingClientProxy proxy = new();
        RecordingHubClients clients = new(proxy);
        SignalRFollowerLiveBroadcaster broadcaster = new(
            new StubFollowerHubContext(clients, new StubGroupManager()));

        await broadcaster.BroadcastFollowerChangedAsync(42);
        await broadcaster.BroadcastMarketPriceAsync(7, 123.5m);

        Assert.HasCount(2, proxy.Sent);

        Assert.AreEqual("followerChanged", proxy.Sent[0].Method);
        Assert.AreEqual(42L, proxy.Sent[0].Args[0]);

        Assert.AreEqual("marketPrice", proxy.Sent[1].Method);
        Assert.AreEqual(7, proxy.Sent[1].Args[0]);
        Assert.AreEqual(123.5m, proxy.Sent[1].Args[1]);

        CollectionAssert.AreEqual(
            ExpectedTargets,
            clients.Targets,
            "账本变化发全体；行情只发该市场的分组");
    }
}

/// <summary>记录要求上游订阅的市场；真实实现还会去发 WS 订阅帧（测试不连交易所）。</summary>
internal sealed class RecordingMarketStats : IMarketStatsSubscription
{
    public List<int> Markets { get; } = [];

    public void EnsureSubscribed(int marketId) => Markets.Add(marketId);
}
