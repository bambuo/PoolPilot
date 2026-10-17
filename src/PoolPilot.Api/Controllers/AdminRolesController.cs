using Microsoft.AspNetCore.Mvc;

using PoolPilot.Api.Authorization;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Constants;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 角色管理（「系统 → 角色管理」页的数据面）。
///
/// 读用 <c>view</c>、改写用 <c>edit</c>、删用 <c>delete</c>；权限点固定是「系统 / 角色管理」
/// （<c>menu:system-roles</c>，键由服务端按菜单 code 生成）。
///
/// 与 <see cref="RolesController"/> 的关系：那边是"登录即可读 + 用「角色管理」的 edit 新建"的面，
/// 这里是角色管理页自己的只读列表与编辑／删除面。两者共用同一套权限点。
/// </summary>
[ApiController]
[Route("api/admin/roles")]
public sealed class AdminRolesController(
    ListAdminRolesUseCase list,
    RenameRoleUseCase rename,
    DeleteRoleUseCase delete,
    GetRoleMenusUseCase getMenus,
    SetRoleMenusUseCase setMenus,
    RegistrationDefaultRoleUseCase registrationDefault) : ControllerBase
{
    /// <summary>全部角色（含成员数与菜单数）。</summary>
    [HttpGet]
    [RequirePermission(PermissionKeys.SystemRoles, MenuActions.View)]
    public async Task<ActionResult<IReadOnlyList<AdminRoleDto>>> List(CancellationToken cancellationToken = default) =>
        Ok(await list.ExecuteAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>改显示名。code 是身份，请求体里没有它。</summary>
    [HttpPut("{code}")]
    [RequirePermission(PermissionKeys.SystemRoles, MenuActions.Edit)]
    public async Task<IActionResult> Rename(
        string code,
        RenameRoleRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await rename.ExecuteAsync(code, request, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>删除角色。系统角色、注册默认角色与"还有成员"的角色会被 400 拒绝（都在用例里）。</summary>
    [HttpDelete("{code}")]
    [RequirePermission(PermissionKeys.SystemRoles, MenuActions.Delete)]
    public async Task<IActionResult> Delete(string code, CancellationToken cancellationToken = default)
    {
        await delete.ExecuteAsync(code, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>指定该角色为注册默认角色（之后注册的账号自动获得它）。系统角色会被 400 拒绝。</summary>
    [HttpPut("{code}/registration-default")]
    [RequirePermission(PermissionKeys.SystemRoles, MenuActions.Edit)]
    public async Task<IActionResult> SetRegistrationDefault(
        string code,
        CancellationToken cancellationToken = default)
    {
        await registrationDefault.SetAsync(code, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>取消该角色的注册默认身份；它不再是默认角色时幂等返回 204。</summary>
    [HttpDelete("{code}/registration-default")]
    [RequirePermission(PermissionKeys.SystemRoles, MenuActions.Edit)]
    public async Task<IActionResult> ClearRegistrationDefault(
        string code,
        CancellationToken cancellationToken = default)
    {
        await registrationDefault.ClearAsync(code, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>该角色绑定的菜单 code 列表（superadmin 恒为全部菜单）。</summary>
    [HttpGet("{code}/menus")]
    [RequirePermission(PermissionKeys.SystemRoles, MenuActions.View)]
    public async Task<ActionResult<RoleMenuCodesDto>> Menus(string code, CancellationToken cancellationToken = default) =>
        Ok(await getMenus.ExecuteAsync(code, cancellationToken).ConfigureAwait(false));

    /// <summary>整体替换该角色的菜单绑定（superadmin 会被 400 拒绝，见用例）。</summary>
    [HttpPut("{code}/menus")]
    [RequirePermission(PermissionKeys.SystemRoles, MenuActions.Edit)]
    public async Task<IActionResult> SetMenus(
        string code,
        RoleMenuCodesDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await setMenus.ExecuteAsync(code, request, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }
}
