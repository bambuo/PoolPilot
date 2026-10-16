using Microsoft.AspNetCore.Mvc;

using PoolPilot.Api.Authorization;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Constants;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 带单员：被观察的池。
///
/// 它存在的意义是把"观察"与"跟单"分开：这一层管的是**谁在被盯着**，
/// 与有没有人跟它完全无关。停止一个带单员之前必须先把订阅它的跟单员处理掉，
/// 否则会留下"看着在跟随、其实永远收不到信号"的账户。
/// </summary>
[ApiController]
[Route("api/lead-traders")]
public sealed class LeadTradersController(
    ListLeadTradersUseCase list,
    ConfigureLeadTraderUseCase configure) : ControllerBase
{
    /// <summary>全部带单员：配置状态、此刻是否在收推送、订阅数与信号产量。</summary>
    [HttpGet]
    [RequirePermission(PermissionKeys.Leads, MenuActions.View)]
    public async Task<ActionResult<IReadOnlyList<LeadTraderDto>>> List(
        CancellationToken cancellationToken = default) =>
        Ok(await list.ExecuteAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>读取单个带单员。</summary>
    [HttpGet("{accountIndex:long}")]
    [RequirePermission(PermissionKeys.Leads, MenuActions.View)]
    public async Task<ActionResult<LeadTraderDto>> Get(
        long accountIndex,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await list
                .ExecuteAsync(Domain.ValueObjects.AccountIndex.From(accountIndex), cancellationToken)
                .ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// 创建或启停。**创建即观察**——没有"建了但先不观察"的中间态。
    /// 该池若已有带单员则只改开关。
    /// </summary>
    [HttpPut("{accountIndex:long}")]
    [RequirePermission(PermissionKeys.Leads, MenuActions.Edit)]
    public async Task<ActionResult<LeadTraderDto>> Configure(
        long accountIndex,
        [FromBody] ConfigureLeadTraderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await configure
            .ExecuteAsync(Domain.ValueObjects.AccountIndex.From(accountIndex), request, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>停止观察并删除。还有跟单员订阅时返回 400。</summary>
    [HttpDelete("{accountIndex:long}")]
    [RequirePermission(PermissionKeys.Leads, MenuActions.Delete)]
    public async Task<IActionResult> Delete(
        long accountIndex,
        CancellationToken cancellationToken = default)
    {
        await configure
            .DeleteAsync(Domain.ValueObjects.AccountIndex.From(accountIndex), cancellationToken)
            .ConfigureAwait(false);

        return NoContent();
    }
}
