using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Repositories;

namespace PoolPilot.Application.Services;

/// <summary>
/// 请求级的数据作用域。superadmin 的全量视图只查一次角色表并缓存在这个 scoped 实例上：
/// 同一个请求里列表过滤、详情守卫会反复问同一个问题。
/// </summary>
public sealed class UserDataScope(ICurrentUser currentUser, IUserRoleRepository userRoles) : IUserDataScope
{
    private bool? _canSeeAll;

    public long UserId =>
        currentUser.UserId
        ?? throw new InvalidOperationException("当前请求没有登录用户，私有数据作用域不可用。");

    public async Task<bool> CanSeeAllAsync(CancellationToken cancellationToken = default)
    {
        if (_canSeeAll is { } cached)
        {
            return cached;
        }

        IReadOnlyList<string> roles = await userRoles
            .ListRoleCodesForUserAsync(UserId, cancellationToken)
            .ConfigureAwait(false);

        _canSeeAll = roles.Contains(PermissionSubjects.SuperadminRoleCode, StringComparer.Ordinal);

        return _canSeeAll.Value;
    }

    public async Task<bool> CanAccessOwnerAsync(
        long? ownerUserId,
        CancellationToken cancellationToken = default) =>
        ownerUserId == UserId || await CanSeeAllAsync(cancellationToken).ConfigureAwait(false);

    public async Task<long?> OwnerFilterAsync(CancellationToken cancellationToken = default) =>
        await CanSeeAllAsync(cancellationToken).ConfigureAwait(false) ? null : UserId;
}
