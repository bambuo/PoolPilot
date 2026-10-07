using Microsoft.AspNetCore.Mvc;

using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Api.Controllers;

/// <summary>回测：池子 / 跟单 / 直接入池三方净值对照。</summary>
[ApiController]
[Route("api/backtest")]
public sealed class BacktestController(RunBacktestUseCase runBacktest) : ControllerBase
{
    /// <param name="accountIndex">被跟的池。</param>
    /// <param name="roundTripCostBps">
    /// 假设的往返摩擦成本（基点）。默认 5 bps 来自深盘标的的实测量级；
    /// 更准确的值应当来自台账页测出的中位漏损 × 2。
    /// </param>
    /// <param name="tradesPerDay">假设的日均跟单笔数。</param>
    /// <param name="exposureRatio">假设的敞口比例（小数）。省略则用 5% 的保守默认值。</param>
    [HttpGet("{accountIndex:long}")]
    public async Task<ActionResult<BacktestResultDto>> Run(
        long accountIndex,
        [FromQuery] decimal roundTripCostBps = 5m,
        [FromQuery] decimal tradesPerDay = 2m,
        [FromQuery] decimal exposureRatio = 0m,
        CancellationToken cancellationToken = default)
    {
        if (roundTripCostBps < 0m || tradesPerDay < 0m || exposureRatio < 0m)
        {
            return BadRequest(new { error = "假设参数不能为负" });
        }

        BacktestResultDto? result = await runBacktest
            .ExecuteAsync(
                AccountIndex.From(accountIndex),
                new BacktestOptions
                {
                    AssumedRoundTripCostBps = roundTripCostBps,
                    AssumedTradesPerDay = tradesPerDay,
                    ExposureRatioOverride = exposureRatio,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return result is null
            ? NotFound(new { error = $"账户 {accountIndex} 无足够净值数据可供回测" })
            : Ok(result);
    }
}
