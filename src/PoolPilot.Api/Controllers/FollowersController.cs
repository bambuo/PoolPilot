using Microsoft.AspNetCore.Mvc;

using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 跟单员：带单员信号的订阅者。
///
/// 与 <c>/api/lead-traders</c> 的分工必须分清，否则两个接口会被当成重复的：
/// <list type="bullet">
///   <item><c>/api/lead-traders</c> —— **被观察的池**。谁在被盯着、盯着产出多少信号。</item>
///   <item><c>/api/followers</c> —— **跟单的账户**。每个有自己的本金、模式与账，
///     同一个池可以挂多个。身份是自增 Id，不是池索引。</item>
/// </list>
///
/// 与 <c>/api/monitor/ledger</c> 的分工同理：那边量的是"每一腿差几个基点"（池子级），
/// 这边给的是完整账户会计（跟单员级）。
/// </summary>
[ApiController]
[Route("api/followers")]
public sealed class FollowersController(
    ListFollowersUseCase list,
    GetFollowerUseCase get,
    CreateFollowerUseCase create,
    UpdateFollowerUseCase update,
    DeleteFollowerUseCase delete,
    GetLiveOrdersUseCase liveOrders,
    GetLiveAccountUseCase liveAccount,
    CloseLivePositionUseCase closePosition) : ControllerBase
{
    /// <summary>全部跟单员的卡片摘要（不带流水与曲线）。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FollowerSummaryDto>>> List(
        CancellationToken cancellationToken = default) =>
        Ok(await list.ExecuteAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// 实盘账户的真实状态（余额、持仓、未实现盈亏）——**现场读交易所**。
    ///
    /// 与 <c>/api/followers/{id}</c> 的关系：那边的权益是纸面账算出来的模拟结果，
    /// 实盘跟单员没有那份账；这一份才是"钱现在是什么样"。
    /// </summary>
    [HttpGet("{id:long}/live-account")]
    public async Task<ActionResult<LiveAccountDto>> LiveAccount(
        long id,
        CancellationToken cancellationToken = default) =>
        Ok(await liveAccount.ExecuteAsync(id, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// **手动平掉**实盘账户上的一笔仓位（真实下单）。
    ///
    /// 请求只说"平哪个交易对"，数量以**现场读到的交易所持仓**为准——
    /// 界面上的数字可能已经过期几秒，照着它下单会平不干净或反向开仓。
    /// 这一单会记进实盘流水并标明 `manual`。
    /// </summary>
    [HttpPost("{id:long}/live-positions/close")]
    public async Task<ActionResult<LiveOrderDto>> ClosePosition(
        long id,
        [FromBody] CloseLivePositionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await closePosition.ExecuteAsync(id, request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 实盘流水：我发过什么、交易所怎么回的。
    ///
    /// 与 <c>/api/followers/{id}</c> 的分工：那边是**纸面账**（跟它会变成多少，
    /// 由流水重放算出）；这边是**实盘日志**（真发出去的单与结果）。
    /// 实盘的真实持仓与盈亏不在这里——那以交易所的账户状态为准，本系统不另算一份。
    /// </summary>
    [HttpGet("{id:long}/live-orders")]
    public async Task<ActionResult<IReadOnlyList<LiveOrderDto>>> LiveOrders(
        long id,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default) =>
        Ok(await liveOrders.ExecuteAsync(id, limit, cancellationToken).ConfigureAwait(false));

    /// <summary>读取跟单员状态。不存在时返回 404，让界面区分"没有这个跟单员"与"有但没成交"。</summary>
    [HttpGet("{id:long}")]
    public async Task<ActionResult<FollowerDto>> Get(long id, CancellationToken cancellationToken = default)
    {
        FollowerDto? follower = await get.ExecuteAsync(id, cancellationToken).ConfigureAwait(false);

        return follower is null
            ? NotFound(new { error = $"跟单员 {id} 不存在。" })
            : Ok(follower);
    }

    /// <summary>开始跟单：新建一个跟单员订阅某个带单员。该池没有带单员时会自动补一个。</summary>
    [HttpPost]
    public async Task<ActionResult<FollowerDto>> Create(
        [FromBody] CreateFollowerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        FollowerDto follower = await create.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        return CreatedAtAction(nameof(Get), new { id = follower.Id }, follower);
    }

    /// <summary>改本金或启停。**改动本金会清空该跟单员的历史流水**，理由见用例说明。</summary>
    [HttpPut("{id:long}")]
    public async Task<ActionResult<FollowerDto>> Update(
        long id,
        [FromBody] UpdateFollowerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await update.ExecuteAsync(id, request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>删除跟单员及其流水。**不影响带单员是否继续观察**——那正是这次拆分的目的。</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken = default)
    {
        await delete.ExecuteAsync(id, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }
}
