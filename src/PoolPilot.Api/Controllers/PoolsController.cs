using Microsoft.AspNetCore.Mvc;

using PoolPilot.Api.Authorization;
using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 池列表与详情。Api 层是薄壳：绑定参数 → 调用用例 → 返回 DTO。
///
/// 整组挂「池列表」的 <c>view</c>：详情（<c>/api/pools/{accountIndex}</c>）不另立权限点，
/// 一律挂父菜单（已定口径，见 docs/PERMISSIONS.md）。
/// </summary>
[ApiController]
[Route("api/pools")]
[RequirePermission(PermissionKeys.Pools, MenuActions.View)]
public sealed class PoolsController(
    GetPoolListUseCase listPools,
    GetPoolDetailUseCase getDetail) : ControllerBase
{
    /// <summary>
    /// 枚举公共池（全量，含已冻结）。<paramref name="filter"/> 取 user / protocol / stake / all。
    /// 冻结状态在 DTO 的 <c>IsFrozen</c> 上如实返回，由界面标记而非服务端隐藏。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PoolSummaryDto>>> List(
        [FromQuery] string filter = "user",
        CancellationToken cancellationToken = default)
    {
        PoolFilter parsed = filter.ToLowerInvariant() switch
        {
            "protocol" => PoolFilter.Protocol,
            "stake" => PoolFilter.Stake,
            "all" => PoolFilter.All,
            _ => PoolFilter.User,
        };

        return Ok(await listPools.ExecuteAsync(parsed, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>读取单个池的完整观察视图。</summary>
    [HttpGet("{accountIndex:long}")]
    public async Task<ActionResult<PoolDetailDto>> Detail(
        long accountIndex,
        CancellationToken cancellationToken = default)
    {
        PoolDetailDto? detail = await getDetail
            .ExecuteAsync(AccountIndex.From(accountIndex), cancellationToken)
            .ConfigureAwait(false);

        return detail is null
            ? NotFound(new { error = $"未找到账户 {accountIndex} 对应的池" })
            : Ok(detail);
    }
}
