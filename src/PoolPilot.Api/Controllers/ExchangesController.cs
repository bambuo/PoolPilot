using Microsoft.AspNetCore.Mvc;

using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 交易所配置：各平台的连接信息与凭据。
///
/// 两条必须守住的口径：
/// <list type="bullet">
///   <item>**凭据只进不出**：接口接受凭据，但**任何读取路径都只回尾号**——
///     这不是加密（存储就是明文，见 ExchangeConfig 的说明），而是减少屏幕上的暴露面。</item>
///   <item>**同一个交易所可以配多条**（两个币安账号是常态），身份是自增 Id。</item>
/// </list>
///
/// ⚠️ 本系统目前不下单，凭据暂时没有任何消费方；它是为将来接实盘准备的一步。
/// 这句话也写在界面上——否则使用者会以为配好就能自动交易。
/// </summary>
[ApiController]
[Route("api/exchanges")]
public sealed class ExchangesController(
    ListExchangesUseCase list,
    CreateExchangeUseCase create,
    UpdateExchangeUseCase update,
    DeleteExchangeUseCase delete,
    ProbeExchangeUseCase probe,
    PlanLiveOrderUseCase planOrder,
    MapPoolMarketsUseCase mapMarkets) : ControllerBase
{
    /// <summary>全部交易所配置（不含任何凭据字段）。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ExchangeConfigDto>>> List(
        CancellationToken cancellationToken = default) =>
        Ok(await list.ExecuteAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>新建。凭据可选；给了就按原样保存（明文）。</summary>
    [HttpPost]
    public async Task<ActionResult<ExchangeConfigDto>> Create(
        [FromBody] CreateExchangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ExchangeConfigDto created = await create.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        return CreatedAtAction(nameof(List), new { id = created.Id }, created);
    }

    /// <summary>修改。凭据是**三态**：不传 = 保持原样，空串 = 清除，有值 = 换成新的。</summary>
    [HttpPut("{id:long}")]
    public async Task<ActionResult<ExchangeConfigDto>> Update(
        long id,
        [FromBody] UpdateExchangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await update.ExecuteAsync(id, request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>删除（连同它的凭据密文）。</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken = default)
    {
        await delete.ExecuteAsync(id, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// 连通性自检：**只读**。用配置里的凭据调一次交易所，回账户余额、持仓与持仓模式。
    ///
    /// 失败不返回 5xx，而是 <c>ok=false</c> + 一条可读原因（币安的错误码原样带出来）——
    /// "钥匙不对"是使用者要看到的结果，不是服务器故障。
    /// </summary>
    [HttpPost("{id:long}/probe")]
    public async Task<ActionResult<ExchangeProbeDto>> Probe(
        long id,
        CancellationToken cancellationToken = default) =>
        Ok(await probe.ExecuteAsync(id, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// 某个池的各个市场**能不能在目标平台上跟**，以及为什么。
    ///
    /// 股票类标的走币安的传统金融命名空间；同名不同物（Lighter 的 SP500 与币安 SPX6900）
    /// 会被价格量级检查挡下并给出原因。
    /// </summary>
    [HttpGet("{id:long}/symbol-map")]
    public async Task<ActionResult<IReadOnlyList<MarketMappingDto>>> SymbolMap(
        long id,
        [FromQuery] long pool,
        CancellationToken cancellationToken = default) =>
        Ok(await mapMarkets
            .ExecuteAsync(id, Domain.ValueObjects.AccountIndex.From(pool), cancellationToken)
            .ConfigureAwait(false));

    /// <summary>
    /// 规划一笔实盘订单并**只校验不成交**（币安 <c>/order/test</c>）。
    ///
    /// 这条端点不会下单。它的用途是在不花钱的前提下把"名义额 → 数量 → 量化 → 交易所规格 →
    /// 参数形状"整条链验穿；实盘路径上任何与下单参数有关的改动都该先过它。
    /// </summary>
    [HttpPost("live-order/plan")]
    public async Task<ActionResult<LiveOrderPlanDto>> Plan(
        [FromBody] LiveOrderPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await planOrder.ExecuteAsync(request, cancellationToken).ConfigureAwait(false));
    }
}
