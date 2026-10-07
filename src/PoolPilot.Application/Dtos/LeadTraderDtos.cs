namespace PoolPilot.Application.Dtos;

/// <summary>
/// 带单员列表项。
///
/// 这一页要回答的是"哪些池正在被盯着、盯出了多少动静、有谁在跟"。
/// 其中 <see cref="IsWatching"/> 与 <see cref="IsEnabled"/> 是**两个不同的判断**：
/// 前者是此刻真的在收推送，后者是配置上开着。连接断了的时候两者会不一致，
/// 而"暂停"和"断了"必须能分开看——否则一次网络抖动会被读成"我把它关了"。
/// </summary>
public sealed record LeadTraderDto
{
    public required long AccountIndex { get; init; }

    public required string Name { get; init; }

    /// <summary>配置上是否在观察。</summary>
    public required bool IsEnabled { get; init; }

    /// <summary>此刻是否真的在接收该池的推送。</summary>
    public required bool IsWatching { get; init; }

    /// <summary>订阅它的跟单员数量。</summary>
    public required int FollowerCount { get; init; }

    /// <summary>其中处于跟随状态的数量。</summary>
    public required int ActiveFollowerCount { get; init; }

    /// <summary>累计信号数（落库的仓位变化条数）。</summary>
    public required int SignalCount { get; init; }

    /// <summary>最近一条信号的时刻；从未有过信号时为 <c>null</c>。</summary>
    public required DateTimeOffset? LastSignalAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>创建/启用一个带单员。请求体只带开关：身份就是池索引本身。</summary>
public sealed record ConfigureLeadTraderRequest
{
    /// <summary>是否在观察。创建时即为 <c>true</c> 就是"创建即开始观察"。</summary>
    public required bool IsEnabled { get; init; }
}
