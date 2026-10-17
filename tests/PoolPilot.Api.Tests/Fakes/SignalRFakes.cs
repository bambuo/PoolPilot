using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace PoolPilot.Api.Tests.Fakes;

/// <summary>
/// SignalR 的手写替身。
///
/// 按 docs/CODING_STANDARDS.md 第 4 节，测试用 fake 而不是 mock 框架：
/// 这里只需要记录"调了哪个方法、带了什么参数"，手写一个 List 就够了，
/// 引入 mock 框架换来的只是更多配置面。
/// </summary>
internal sealed record SentMessage(string Method, object?[] Args);

/// <summary>记录所有发出去的消息。</summary>
internal sealed class RecordingClientProxy : IClientProxy
{
    public List<SentMessage> Sent { get; } = [];

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        lock (Sent)
        {
            Sent.Add(new SentMessage(method, args));
        }

        return Task.CompletedTask;
    }
}

/// <summary>所有调用目标都指向同一个记录器——测试关心的是"发了什么"，不是"发给谁"。</summary>
internal sealed class RecordingHubClients(RecordingClientProxy proxy) : IHubClients, IHubCallerClients
{
    /// <summary>按调用顺序记下选中的投递目标（<c>all</c> 或 <c>group:xxx</c>），
    /// 用来断言广播走的是全体还是某个分组。</summary>
    public List<string> Targets { get; } = [];

    public IClientProxy All
    {
        get
        {
            Targets.Add("all");

            return proxy;
        }
    }

    public IClientProxy Caller => proxy;

    public IClientProxy Others => proxy;

    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => proxy;

    public IClientProxy Client(string connectionId) => proxy;

    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;

    public IClientProxy Group(string groupName)
    {
        Targets.Add($"group:{groupName}");

        return proxy;
    }

    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => proxy;

    public IClientProxy Groups(IReadOnlyList<string> groupNames) => proxy;

    public IClientProxy OthersInGroup(string groupName) => proxy;

    public IClientProxy OthersInGroups(IReadOnlyList<string> groupNames) => proxy;

    public IClientProxy User(string userId) => proxy;

    public IClientProxy Users(IReadOnlyList<string> userIds) => proxy;
}

internal sealed class StubHubContext(RecordingHubClients clients) : IHubContext<Hubs.MonitorHub>
{
    public IHubClients Clients => clients;

    public IGroupManager Groups => new StubGroupManager();
}

internal sealed class StubFollowerHubContext(RecordingHubClients clients, IGroupManager groups)
    : IHubContext<Hubs.FollowerHub>
{
    public IHubClients Clients => clients;

    public IGroupManager Groups => groups;
}

internal sealed class StubGroupManager : IGroupManager
{
    public List<(string ConnectionId, string GroupName)> Added { get; } = [];

    public Task AddToGroupAsync(
        string connectionId,
        string groupName,
        CancellationToken cancellationToken = default)
    {
        Added.Add((connectionId, groupName));

        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(
        string connectionId,
        string groupName,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class StubHubCallerContext(string? userIdentifier = null) : HubCallerContext
{
    public override string ConnectionId { get; } = "test-connection";

    public override string? UserIdentifier { get; } = userIdentifier;

    public override ClaimsPrincipal? User => null;

    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    public override CancellationToken ConnectionAborted => CancellationToken.None;

    public override IFeatureCollection Features { get; } = new FeatureCollection();

    /// <summary>是否被拒绝连接。<c>OnConnectedAsync</c> 的准入分支靠它断言。</summary>
    public bool Aborted { get; private set; }

    public override void Abort() => Aborted = true;
}
