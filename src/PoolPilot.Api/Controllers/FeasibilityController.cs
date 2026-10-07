using Microsoft.AspNetCore.Mvc;

using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Api.Controllers;

/// <summary>规模门槛：给定本金，能否跟得动这个池。</summary>
[ApiController]
[Route("api/feasibility")]
public sealed class FeasibilityController(EvaluateSizeFeasibilityUseCase evaluate) : ControllerBase
{
    /// <param name="accountIndex">被跟的池。</param>
    /// <param name="capital">跟单本金（USDT）。</param>
    /// <param name="markets">可选：只评估这些市场（逗号分隔）。省略则用池当前持仓的市场。</param>
    [HttpGet("{accountIndex:long}")]
    public async Task<ActionResult<IReadOnlyList<SizeFeasibilityDto>>> Evaluate(
        long accountIndex,
        [FromQuery] decimal capital = 1000m,
        [FromQuery] string? markets = null,
        CancellationToken cancellationToken = default)
    {
        if (capital <= 0m)
        {
            return BadRequest(new { error = "本金必须为正" });
        }

        IReadOnlyList<int>? marketIds = null;

        if (!string.IsNullOrWhiteSpace(markets))
        {
            List<int> parsed = [];

            foreach (string part in markets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, out int marketId))
                {
                    return BadRequest(new { error = $"无法解析市场 ID：{part}" });
                }

                parsed.Add(marketId);
            }

            marketIds = parsed;
        }

        return Ok(await evaluate
            .ExecuteAsync(AccountIndex.From(accountIndex), capital, marketIds, cancellationToken)
            .ConfigureAwait(false));
    }
}

/// <summary>市场行情：盘口与合约规格，供规模门槛页与漏损展示使用。</summary>
[ApiController]
[Route("api/markets")]
public sealed class MarketsController(ILighterPoolsClient client) : ControllerBase
{
    /// <summary>读取指定市场的盘口与规格。</summary>
    [HttpGet("{marketId:int}/quote")]
    public async Task<ActionResult<MarketQuoteDto>> Quote(
        int marketId,
        CancellationToken cancellationToken = default)
    {
        Task<Domain.Entities.OrderBookSnapshot> bookTask = client.FetchOrderBookAsync(marketId, cancellationToken);
        Task<IReadOnlyList<Domain.Entities.InstrumentSpec>> instrumentsTask = client.FetchInstrumentsAsync(cancellationToken);

        await Task.WhenAll(bookTask, instrumentsTask).ConfigureAwait(false);

        Domain.Entities.OrderBookSnapshot book = bookTask.Result;
        Domain.Entities.InstrumentSpec? instrument = instrumentsTask.Result.FirstOrDefault(i => i.MarketId == marketId);

        if (book.BestBid is null || book.BestAsk is null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"市场 {marketId} 盘口不完整" });
        }

        decimal mid = (book.BestBid.Value + book.BestAsk.Value) / 2m;

        return Ok(new MarketQuoteDto
        {
            MarketId = marketId,
            Symbol = instrument?.Symbol ?? $"market-{marketId}",
            BestBid = book.BestBid.Value,
            BestAsk = book.BestAsk.Value,
            MidPrice = mid,
            SpreadBps = book.SpreadBps,
            MinOrderNotional = instrument?.MinOrderNotional ?? 0m,
            MinOrderSize = instrument?.MinOrderSize ?? 0m,
            TakerFeePercent = instrument?.TakerFeePercent ?? 0m,
        });
    }
}
