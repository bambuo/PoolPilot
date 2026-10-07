using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;

namespace PoolPilot.Application.Mapping;

/// <summary>跟单员 DTO 的构造。</summary>
public static class FollowerMapper
{
    /// <summary>
    /// 刚写入的跟单员的"即时快照"：只回读**刚写进去了什么**。
    ///
    /// 权益、持仓、曲线一律留空/为零，而不是顺手去拉一次池子快照与盘口——
    /// 那会在一次写操作里塞进一整轮网络往返，而调用方（工作台）紧接着就会调读取接口。
    /// 这里的 0 表示"还没算过"，读取接口给的才是真实估值。
    /// </summary>
    public static FollowerDto Snapshot(Follower follower, LeadTrader? lead) => new()
    {
        Id = follower.Id,
        LeadAccountIndex = follower.LeadAccountIndex.Value,
        LeadName = lead?.Name ?? $"池 {follower.LeadAccountIndex.Value}",
        LeadIsEnabled = lead?.IsEnabled ?? false,
        Mode = Mode(follower.Mode),
        ExchangeConfigId = follower.ExchangeConfigId,

        // 显示名不在这里查：写入后的即时快照只回读"刚写进去了什么"，
        // 为它多打一次库是白费；真正的读路径（列出/详情）会把名字带上
        ExchangeName = null,
        InitialCapital = follower.InitialCapital.Value,
        IsActive = follower.IsActive,
        CreatedAt = follower.CreatedAt.Utc,
        Equity = follower.InitialCapital.Value,
        RealizedPnl = 0m,
        UnrealizedPnl = 0m,
        Fees = 0m,
        ReturnPercent = 0m,
        GrossNotional = 0m,
        PoolEquity = 0m,
        Scale = 0m,
        FillCount = 0,
        SkipCount = 0,
        MarksUnavailable = [],
        Positions = [],
        Fills = [],
        Curve = [],
    };

    /// <summary>模式的外部表示。</summary>
    public static string Mode(Domain.Enums.CopyMode mode) =>
        mode == Domain.Enums.CopyMode.Live ? "live" : "paper";
}
