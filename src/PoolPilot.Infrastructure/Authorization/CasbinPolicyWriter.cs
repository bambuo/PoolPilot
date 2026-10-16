using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.Services;

namespace PoolPilot.Infrastructure.Authorization;

/// <summary>
/// 策略写侧的唯一实现：从四张表读出事实、投影、然后整体替换 Enforcer。
///
/// 读的是**四张表本身**而不是"某次改动的增量"——所以任何一个写用例调用它之后，
/// Enforcer 与表必然一致，不必去猜"这次改动影响了哪几条策略"。
/// </summary>
public sealed class CasbinPolicyWriter(
    IMenuRepository menus,
    IMenuRoleRepository menuRoles,
    IUserRoleRepository userRoles,
    CasbinEnforcer enforcer) : IPolicyWriter
{
    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Domain.Entities.Menu> menuList = await menus
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<MenuRoleBinding> menuRoleList = await menuRoles
            .ListAllAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<UserRoleBinding> userRoleList = await userRoles
            .ListAllAsync(cancellationToken)
            .ConfigureAwait(false);

        PolicyProjection projection = PermissionPolicyProjection.Project(menuList, menuRoleList, userRoleList);

        enforcer.Rebuild(projection);
    }
}
