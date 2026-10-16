using Microsoft.AspNetCore.Mvc;

using PoolPilot.Api.Authorization;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Constants;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 用户管理（「系统 → 用户管理」页的数据面）。
///
/// 读用 <c>view</c>、写用 <c>edit</c>；权限点固定是「系统 / 用户管理」
/// （<c>menu:system-users</c>，键由服务端按菜单 code 生成）。
/// </summary>
[ApiController]
[Route("api/admin/users")]
public sealed class AdminUsersController(
    ListAdminUsersUseCase list,
    SetUserRolesUseCase setRoles) : ControllerBase
{
    /// <summary>全部账号（含角色），按 id 升序。</summary>
    [HttpGet]
    [RequirePermission(PermissionKeys.SystemUsers, MenuActions.View)]
    public async Task<ActionResult<IReadOnlyList<AdminUserDto>>> List(CancellationToken cancellationToken = default) =>
        Ok(await list.ExecuteAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// 整体替换某个账号的角色绑定。
    /// 守卫（都在用例里，400 经异常中间件）：未知角色 code、摘掉最后一个超管。
    /// </summary>
    [HttpPut("{id:long}/roles")]
    [RequirePermission(PermissionKeys.SystemUsers, MenuActions.Edit)]
    public async Task<IActionResult> SetRoles(
        long id,
        UserRolesDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await setRoles.ExecuteAsync(id, request, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }
}
