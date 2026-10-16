using Microsoft.AspNetCore.Mvc;

using PoolPilot.Api.Authorization;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Constants;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 角色的读与建。
///
/// 读是登录即可（界面上到处要用角色名做展示）；建需要**「角色管理」的 edit 权限**
/// （<c>menu:system-roles</c>）——角色是谁建的这件事属于角色管理，不挂在菜单管理上。
/// 菜单管理页里的"新建角色"只是同一接口的便捷入口，它同样要这一条权限。
/// </summary>
[ApiController]
[Route("api/roles")]
public sealed class RolesController(ListRolesUseCase list, CreateRoleUseCase create) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RoleDto>>> List(CancellationToken cancellationToken = default) =>
        Ok(await list.ExecuteAsync(cancellationToken).ConfigureAwait(false));

    [HttpPost]
    [RequirePermission(PermissionKeys.SystemRoles, MenuActions.Edit)]
    public async Task<ActionResult<RoleDto>> Create(
        CreateRoleRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await create.ExecuteAsync(request, cancellationToken).ConfigureAwait(false));
    }
}
