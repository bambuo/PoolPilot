using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 带单员列表。
///
/// 一次把三类信息合起来：配置（谁应该被观察）、运行期（此刻在不在收推送）、
/// 产出与订阅（积累了多少信号、有几个人在跟）。分开看会得到三种都不完整的判断——
/// 只有配置会显示"在监控"而其实连接是断的，只有产出则不知道有没有人在跟。
/// </summary>
public sealed class ListLeadTradersUseCase(
    ILeadTraderRepository leads,
    IFollowerRepository followers,
    IPoolEventRepository events,
    ILeadWatchState watchState)
{
    public async Task<IReadOnlyList<LeadTraderDto>> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        Task<IReadOnlyList<LeadTrader>> leadsTask = leads.ListAsync(cancellationToken);
        Task<IReadOnlyList<Follower>> followersTask = followers.ListAsync(cancellationToken);
        Task<IReadOnlyList<PoolSignalStats>> statsTask = events.GetSignalStatsAsync(cancellationToken);

        await Task.WhenAll(leadsTask, followersTask, statsTask).ConfigureAwait(false);

        IReadOnlyList<LeadTrader> all = await leadsTask.ConfigureAwait(false);
        IReadOnlyList<Follower> subscribers = await followersTask.ConfigureAwait(false);
        Dictionary<long, PoolSignalStats> stats = (await statsTask.ConfigureAwait(false))
            .ToDictionary(s => s.Pool.Value);

        return
        [
            .. all.Select(lead => ToDto(
                lead,
                [.. subscribers.Where(f => f.LeadAccountIndex == lead.AccountIndex)],
                stats.GetValueOrDefault(lead.AccountIndex.Value))),
        ];
    }

    /// <summary>单个带单员的 DTO。创建/启停之后直接复用，避免再写一套映射。</summary>
    public async Task<LeadTraderDto> ExecuteAsync(
        AccountIndex pool,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pool);

        LeadTrader lead = await leads.GetAsync(pool, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"账户索引 {pool.Value} 上没有带单员。", nameof(pool));

        IReadOnlyList<Follower> subscribers = await followers
            .ListByLeadAsync(pool, cancellationToken)
            .ConfigureAwait(false);

        PoolSignalStats? stats = (await events.GetSignalStatsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(s => s.Pool == pool);

        return ToDto(lead, subscribers, stats);
    }

    private LeadTraderDto ToDto(
        LeadTrader lead,
        IReadOnlyList<Follower> subscribers,
        PoolSignalStats? stats) => new()
        {
            AccountIndex = lead.AccountIndex.Value,
            Name = lead.Name,
            IsEnabled = lead.IsEnabled,

            // 暂停的带单员不算在观察——"在收推送"与"配置开着"是两个判断
            IsWatching = lead.IsEnabled && watchState.IsWatching(lead.AccountIndex),
            FollowerCount = subscribers.Count,
            ActiveFollowerCount = subscribers.Count(f => f.IsActive),
            SignalCount = stats?.Count ?? 0,
            LastSignalAt = stats?.LastSignalAt?.Utc,
            CreatedAt = lead.CreatedAt.Utc,
        };
}
