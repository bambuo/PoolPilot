using Microsoft.AspNetCore.Mvc;

using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 当前用户自己的菜单树（导航用）。
///
/// **登录即可访问，不需要菜单权限**：它本来就是"我能看到哪些页面"的答案，
/// 一个还没被分配任何角色的账号访问它也只会拿到一棵空树——那正是它该有的样子。
/// </summary>
[ApiController]
[Route("api/menus")]
public sealed class MenusController(GetMenuTreeUseCase getMenuTree) : ControllerBase
{
    [HttpGet("tree")]
    public async Task<ActionResult<IReadOnlyList<MenuNodeDto>>> Tree(CancellationToken cancellationToken = default) =>
        Ok(await getMenuTree.ExecuteAsync(cancellationToken).ConfigureAwait(false));
}
