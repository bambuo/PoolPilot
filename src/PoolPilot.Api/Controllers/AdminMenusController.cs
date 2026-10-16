using Microsoft.AspNetCore.Mvc;

using PoolPilot.Api.Authorization;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Constants;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 菜单管理（「系统 → 菜单管理」页的数据面）。
///
/// 本批后端强制（403）就落在这里：每个动作都标了权限属性，权限点固定是「系统 / 菜单管理」。
/// 读用 <c>view</c>、写用 <c>edit</c>、删用 <c>delete</c>——只有读整棵树那个动作例外，
/// 它用「菜单管理 view **或** 角色管理 view」（见 <see cref="Tree"/> 的说明）。
/// </summary>
[ApiController]
[Route("api/admin/menus")]
public sealed class AdminMenusController(
    GetAdminMenuTreeUseCase getTree,
    CreateMenuUseCase create,
    UpdateMenuUseCase update,
    DeleteMenuUseCase delete,
    GetMenuRolesUseCase getRoles,
    SetMenuRolesUseCase setRoles) : ControllerBase
{
    /// <summary>
    /// 全量菜单树（管理视图，含 group / hidden，不按权限过滤）。
    ///
    /// **这里是唯一一处"或"权限**：它既服务「菜单管理」页（<c>menu:system-menus</c>），
    /// 也服务「角色管理」页的"配置菜单"弹窗（<c>menu:system-roles</c>）——两个页面的读写面
    /// 各自独立，却要读同一份菜单清单。只挂其中任意一个都会让另一个页面开不了这个弹窗，
    /// 所以用 <see cref="RequireAnyPermissionAttribute"/> 明确表达"两者之一即可"。
    /// </summary>
    [HttpGet]
    [RequireAnyPermission(
        PermissionKeys.SystemMenus, MenuActions.View,
        PermissionKeys.SystemRoles, MenuActions.View)]
    public async Task<ActionResult<IReadOnlyList<MenuNodeDto>>> Tree(CancellationToken cancellationToken = default) =>
        Ok(await getTree.ExecuteAsync(cancellationToken).ConfigureAwait(false));

    [HttpPost]
    [RequirePermission(PermissionKeys.SystemMenus, MenuActions.Edit)]
    public async Task<ActionResult<MenuNodeDto>> Create(
        MenuUpsertRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await create.ExecuteAsync(request, cancellationToken).ConfigureAwait(false));
    }

    [HttpPut("{id:long}")]
    [RequirePermission(PermissionKeys.SystemMenus, MenuActions.Edit)]
    public async Task<ActionResult<MenuNodeDto>> Update(
        long id,
        MenuUpsertRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await update.ExecuteAsync(id, request, cancellationToken).ConfigureAwait(false));
    }

    [HttpDelete("{id:long}")]
    [RequirePermission(PermissionKeys.SystemMenus, MenuActions.Delete)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken = default)
    {
        await delete.ExecuteAsync(id, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>这个菜单绑定了哪些角色（超级管理员常驻在最前，不可取消）。</summary>
    [HttpGet("{id:long}/roles")]
    [RequirePermission(PermissionKeys.SystemMenus, MenuActions.View)]
    public async Task<ActionResult<MenuRolesDto>> Roles(long id, CancellationToken cancellationToken = default) =>
        Ok(await getRoles.ExecuteAsync(id, cancellationToken).ConfigureAwait(false));

    /// <summary>整体替换这个菜单的角色绑定。</summary>
    [HttpPut("{id:long}/roles")]
    [RequirePermission(PermissionKeys.SystemMenus, MenuActions.Edit)]
    public async Task<ActionResult<MenuRolesDto>> SetRoles(
        long id,
        MenuRolesDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await setRoles.ExecuteAsync(id, request, cancellationToken).ConfigureAwait(false));
    }
}
