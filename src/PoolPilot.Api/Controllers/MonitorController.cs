using Microsoft.AspNetCore.Mvc;

using PoolPilot.Api.Authorization;
using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;
using PoolPilot.Infrastructure.Monitoring;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 池子级的观测接口：持仓变化事件与跟单对照（执行成本）台账。
///
/// 注意这里的一切都是**池子级**的：事件属于池子，漏损对照也属于池子。
/// 跟单员自己的账户会计在 <c>/api/followers</c>——两者不要互相替代。
///
/// 权限不整组统一：事件流与运行期连接状态是「总览」页的面（<c>menu:graph</c> 的 <c>view</c>），
/// 而台账是跟单详情页的面（<c>menu:copy-list</c> 的 <c>view</c>，已按前端调用点核实只被它用）。
/// </summary>
[ApiController]
[Route("api/monitor")]
public sealed class MonitorController(
    IPoolEventRepository events,
    GetCopyLedgerUseCase getLedger,
    ILeadWatchState watchState) : ControllerBase
{
    /// <summary>最近的持仓变化事件。<paramref name="accountIndex"/> 省略则返回全部池的。</summary>
    [HttpGet("events")]
    [RequirePermission(PermissionKeys.Graph, MenuActions.View)]
    public async Task<ActionResult<IReadOnlyList<PoolEventDto>>> Events(
        [FromQuery] long? accountIndex = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Domain.Entities.PoolEvent> rows = accountIndex is null
            ? await events.GetRecentAcrossPoolsAsync(limit, cancellationToken).ConfigureAwait(false)
            : await events.GetRecentAsync(AccountIndex.From(accountIndex.Value), limit, cancellationToken).ConfigureAwait(false);

        return Ok(rows.Select(e => new PoolEventDto
        {
            AccountIndex = e.AccountIndex.Value,
            PoolName = string.Empty,
            Kind = e.Kind.ToString().ToLowerInvariant(),
            MarketId = e.MarketId,
            Symbol = e.Symbol,
            Side = e.Side == Domain.Enums.PositionSide.Long ? "long" : "short",
            SizeBefore = e.SizeBefore,
            SizeAfter = e.SizeAfter,
            NotionalDelta = e.NotionalDelta.Value,
            DetectedAt = e.DetectedAt.Utc,
            IsExposureIncreasing = e.IsExposureIncreasing,
        }));
    }

    /// <summary>跟单台账与漏损报告。</summary>
    [HttpGet("ledger/{accountIndex:long}")]
    [RequirePermission(PermissionKeys.CopyList, MenuActions.View)]
    public async Task<ActionResult<CopyLedgerDto>> Ledger(
        long accountIndex,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        CopyLedgerDto? ledger = await getLedger
            .ExecuteAsync(AccountIndex.From(accountIndex), limit, cancellationToken)
            .ConfigureAwait(false);

        return ledger is null
            ? NotFound(new { error = $"未找到账户 {accountIndex} 对应的池" })
            : Ok(ledger);
    }

    /// <summary>
    /// 此刻是否正在接收某几个池的推送。
    ///
    /// 不问"配置里写了哪些池"——那由带单员表决定，看 <c>/api/lead-traders</c>。
    /// 这里给的是**运行期事实**：连接是否活着。两者不一致正是需要被看见的情况。
    /// </summary>
    [HttpGet("watch-state")]
    [RequirePermission(PermissionKeys.Graph, MenuActions.View)]
    public ActionResult<object> WatchState([FromQuery] long[]? accountIndex = null) => Ok(new
    {
        watching = (accountIndex ?? [])
            .Where(i => i > 0)
            .Select(i => new { accountIndex = i, watching = watchState.IsWatching(AccountIndex.From(i)) })
            .ToList(),
    });
}
