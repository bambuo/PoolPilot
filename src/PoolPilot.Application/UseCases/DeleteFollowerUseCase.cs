using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 删除一个跟单员。
///
/// **连带删掉它的流水与权益点**：留下孤儿行会让"某个跟单员的历史还在，但跟单员没了"，
/// 之后新建的跟单员会拿到一个新的自增 Id，两者不会相撞——但库会随着反复试用无限增长，
/// 而删掉跟单员本来就是为了重新开始。
///
/// 与带单员的关系：删跟单员**不影响**带单员是否继续观察。
/// 这正是这次拆分的意义——池子的观察不该因为没人跟而停。
/// </summary>
public sealed class DeleteFollowerUseCase(
    IFollowerRepository followers,
    IFollowerLedgerRepository ledger,
    ILeadTraderSync sync)
{
    public async Task ExecuteAsync(long followerId, CancellationToken cancellationToken = default)
    {
        Follower? existing = await followers.GetAsync(followerId, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            return;
        }

        await ledger.ClearHistoryAsync(followerId, cancellationToken).ConfigureAwait(false);
        await followers.DeleteAsync(followerId, cancellationToken).ConfigureAwait(false);

        // 立刻摘掉订阅者（带单员本身继续观察——那条线不看跟单员的存在与否）
        await sync.RequestSyncAsync(cancellationToken).ConfigureAwait(false);
    }
}
