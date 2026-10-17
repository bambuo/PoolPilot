using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 确保某个池有一个带单员，返回它。
///
/// 两个入口共用：工作台带单员页的「创建带单员」（经由
/// <see cref="ConfigureLeadTraderUseCase"/>），以及公开带单页的「观察这个池」。
/// 「开始跟单」**不是**入口：跟单员订阅的是带单员，池上没有观察者时那条请求直接被拒绝，
/// 不由这里顺手补——建观察者是带单页的一个独立决定，它占着上游订阅与持续落库。
///
/// 已存在时**原样返回**，绝不顺手改动它：调用方可能只是想看看这个池，
/// 而"把它打开"会覆盖别人特意做的暂停决定。上游是暂停状态就让调用方自己去看。
/// </summary>
public sealed class EnsureLeadTraderUseCase(
    ILeadTraderRepository leads,
    ILighterPoolsClient client)
{
    public async Task<LeadTrader> ExecuteAsync(
        AccountIndex pool,
        bool enableOnCreate = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pool);

        LeadTrader? existing = await leads.GetAsync(pool, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        IReadOnlyList<Pool> pools = await client
            .FetchPoolsAsync(PoolFilter.All, cancellationToken)
            .ConfigureAwait(false);

        Pool found = pools.FirstOrDefault(p => p.AccountIndex == pool)
            ?? throw new ArgumentException(
                $"找不到账户索引为 {pool.Value} 的池，不能为它创建带单员。",
                nameof(pool));

        LeadTrader created = new()
        {
            AccountIndex = pool,

            // 池名在这里落库一次快照：池子改名或下线后，历史信号仍然说得出当时跟的是谁
            Name = found.Name,
            IsEnabled = enableOnCreate,
            CreatedAt = LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        };

        await leads.UpsertAsync(created, cancellationToken).ConfigureAwait(false);

        return created;
    }
}
