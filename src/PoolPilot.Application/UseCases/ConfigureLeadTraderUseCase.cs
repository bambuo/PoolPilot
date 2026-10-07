using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 创建、启停与停止带单员。
///
/// 三条规则：
/// <list type="bullet">
///   <item>**创建即观察**：没有"建了但先不观察"的中间态，那只会让页面出现一个什么都不做的条目。</item>
///   <item>**暂停不动作**：暂停只停止产生新信号，跟单员的仓位一律不动。
///     平仓是交易决策——替使用者平掉是拿他的钱做一个他没做的决定。</item>
///   <item>**有订阅者不许停止**：删掉观察者而把订阅者留在原地，会造出
///     "看着在跟随、其实永远收不到信号"的状态，而界面上它跟正常运行一模一样。</item>
/// </list>
/// </summary>
public sealed class ConfigureLeadTraderUseCase(
    ILeadTraderRepository leads,
    IFollowerRepository followers,
    EnsureLeadTraderUseCase ensure,
    ListLeadTradersUseCase list,
    ILeadTraderSync sync)
{
    public async Task<LeadTraderDto> ExecuteAsync(
        AccountIndex pool,
        ConfigureLeadTraderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(request);

        LeadTrader lead = await ensure
            .ExecuteAsync(pool, request.IsEnabled, cancellationToken)
            .ConfigureAwait(false);

        if (lead.IsEnabled != request.IsEnabled)
        {
            await leads.UpsertAsync(lead with { IsEnabled = request.IsEnabled }, cancellationToken)
                .ConfigureAwait(false);
        }

        // 让运行期立刻跟上：否则页面会显示"已启用但未连接"，看起来像连接出了问题
        await sync.RequestSyncAsync(cancellationToken).ConfigureAwait(false);

        return await list.ExecuteAsync(pool, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(AccountIndex pool, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pool);

        IReadOnlyList<Follower> subscribers = await followers
            .ListByLeadAsync(pool, cancellationToken)
            .ConfigureAwait(false);

        if (subscribers.Count > 0)
        {
            throw new ArgumentException(
                $"这个带单员还有 {subscribers.Count} 个跟单员在订阅，不能停止。" +
                "先删除或改投这些跟单员——否则它们会留在原地等待永远不会再来的信号。",
                nameof(pool));
        }

        await leads.DeleteAsync(pool, cancellationToken).ConfigureAwait(false);
        await sync.RequestSyncAsync(cancellationToken).ConfigureAwait(false);
    }
}
