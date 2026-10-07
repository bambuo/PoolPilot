using Microsoft.AspNetCore.Mvc;

using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 跨交易所标的映射表。
///
/// 它存在的理由是**跟单路径上的那次查询不能打网络**：信号到达时要在毫秒级决定"这单能不能下"，
/// 所以映射必须先在本地表里备好。扫描是一次性的整理动作，查询是热路径上的本地读。
///
/// 另一条纪律：**扫描永不覆盖人工行**（<c>origin=manual</c>）。人把某个标的指到哪是一个判断，
/// 扫描只补充"表里还没有的"。
/// </summary>
[ApiController]
[Route("api/symbol-mappings")]
public sealed class SymbolMappingsController(
    ListSymbolMappingsUseCase list,
    ScanSymbolMappingsUseCase scan,
    SaveSymbolMappingUseCase save,
    DeleteSymbolMappingUseCase delete,
    IExchangeSymbolCatalogFactory catalogs,
    ILighterPoolsClient pools) : ControllerBase
{
    /// <summary>
    /// 可作为**映射目标**的平台（界面的选择器列的就是它）。
    ///
    /// 两条过滤，都不是"显示偏好"而是模型本身的约束：
    /// <list type="bullet">
    ///   <item>**去掉 Lighter**：它是映射的源（池都在它上面），"映射到它自己"没有意义。</item>
    ///   <item>**去掉 Custom**：那是"自己填"的占位，不是某个场地的身份，
    ///     也就没有合约目录可查——列出来只会是一个点了没反应的选项。</item>
    /// </list>
    ///
    /// 另外它**不是"你配了哪些交易所"**：映射是平台级的知识，与你有几个账号无关；
    /// 没配任何交易所配置时也该能整理、查看映射。
    /// </summary>
    [HttpGet("platforms")]
    public ActionResult<IReadOnlyList<object>> Platforms()
    {
        IReadOnlyList<object> targets =
        [
            .. Enum.GetValues<Domain.Enums.ExchangeCode>()
                .Where(code => code != Domain.Enums.ExchangeCode.Lighter
                    && code != Domain.Enums.ExchangeCode.Custom)
                .Select(code => (object)new
                {
                    platform = ExchangeEnumText.ToText(code),
                    supported = catalogs.Create(code) is not null,
                }),
        ];

        return Ok(targets);
    }

    /// <summary>
    /// 源标的清单：Lighter 的全部市场。
    ///
    /// 取**全部市场**而不是"当前被观察池持有过的那些"：映射是储备知识，
    /// 池子今天没持有某个标的，不代表明天不会——那时映射表里就该已经有一条等着它。
    /// </summary>
    [HttpGet("source-symbols")]
    public async Task<ActionResult<IReadOnlyList<object>>> SourceSymbols(
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Domain.Entities.InstrumentSpec> instruments = await pools
            .FetchInstrumentsAsync(cancellationToken)
            .ConfigureAwait(false);

        IEnumerable<Domain.Entities.InstrumentSpec> matched = instruments;

        if (!string.IsNullOrWhiteSpace(search))
        {
            matched = matched.Where(i => i.Symbol.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        return Ok(matched
            .OrderBy(i => i.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(i => new { symbol = i.Symbol, marketId = i.MarketId })
            .ToList());
    }

    /// <summary>该平台的合约清单（人工登记时选目标合约用；公开数据，不需要凭据）。</summary>
    [HttpGet("symbols")]
    public async Task<ActionResult<IReadOnlyList<ExchangeSymbolInfo>>> Symbols(
        [FromQuery] string platform,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        IExchangeSymbolCatalog catalog = catalogs.Create(ExchangeEnumText.ParseCode(platform))
            ?? throw new ArgumentException(
                $"{platform} 还没有合约清单可用。"
                + $"系统目前支持：{string.Join("、", catalogs.SupportedPlatforms.Select(ExchangeEnumText.ToText))}。",
                nameof(platform));

        IReadOnlyList<ExchangeSymbolInfo> symbols = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(search))
        {
            symbols = [.. symbols.Where(s =>
                s.Symbol.Contains(search, StringComparison.OrdinalIgnoreCase)
                || s.BaseAsset.Contains(search, StringComparison.OrdinalIgnoreCase))];
        }

        return Ok(symbols);
    }

    /// <summary>全部映射（可按平台过滤；不传就是全部平台）。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SymbolMappingDto>>> List(
        [FromQuery] string? platform = null,
        CancellationToken cancellationToken = default) =>
        Ok(await list.ExecuteAsync(platform, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// 扫描被观察的池，整理出"这些市场在这个平台上对应哪个合约"。
    /// 人工行会被跳过并在结果里计数。
    /// </summary>
    [HttpPost("scan")]
    public async Task<ActionResult<SymbolMappingScanResultDto>> Scan(
        [FromQuery] string platform,
        CancellationToken cancellationToken = default) =>
        Ok(await scan.ExecuteAsync(platform, cancellationToken).ConfigureAwait(false));

    /// <summary>人工登记或修正一条映射（登记后不再被扫描覆盖）。</summary>
    [HttpPost]
    public async Task<ActionResult<SymbolMappingDto>> Save(
        [FromBody] SaveSymbolMappingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await save.ExecuteAsync(request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>删除一条映射。</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken = default)
    {
        await delete.ExecuteAsync(id, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }
}
