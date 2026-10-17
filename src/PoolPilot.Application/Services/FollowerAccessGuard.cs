using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;

namespace PoolPilot.Application.Services;

/// <summary>
/// 工作台跟单员的归属守卫。
///
/// 公开行一律返回 <c>null</c>：它们属于公开广场，不从工作台私有端点读写。
/// 这样既满足"公开行不进入工作台私有列表"，也避免拿私有详情接口去探测公开行。
/// </summary>
public sealed class FollowerAccessGuard(IFollowerRepository followers, IUserDataScope scope)
{
    public async Task<Follower?> FindAsync(long id, CancellationToken cancellationToken = default)
    {
        Follower? follower = await followers.GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (follower is null || follower.IsPublic)
        {
            return null;
        }

        return await scope
            .CanAccessOwnerAsync(follower.OwnerUserId, cancellationToken)
            .ConfigureAwait(false)
                ? follower
                : null;
    }
}
