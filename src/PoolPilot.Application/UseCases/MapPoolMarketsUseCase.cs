using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 列出某个池的每个市场**能不能在指定的那个交易所上跟**，以及为什么。
/// （早先目标只有币安，措辞沿用至今；目标平台现在由交易所配置决定。）
///
/// 这是跨场所跟单最需要先看清的一件事：一个池可能持有十几个市场，
/// 其中哪些在币安有对应合约、哪些没有、哪些同名但不是同一个资产、哪些名义额太小跟不动。
/// 没有这层透视，"实盘跟单"上线后表现就是"有些动作没跟上"，而原因不可见。
///
/// 判据全部来自实测事实：
/// <list type="bullet">
///   <item>币安的两个命名空间（<c>PERPETUAL</c> / <c>TRADIFI_PERPETUAL</c>）互不相交，
///     所以要两个都查——股票合约只在后者里。</item>
///   <item>同名不等于同物（Lighter 的 SPX ↔ 币安 SPX6900 meme 币），所以还要价格量级确认。</item>
///   <item>能映射上也未必跟得动：币安各合约有最小名义额（实测 5 / 20 / 50 USDT 不等），
///     小资金按比例复制出来的名义额常常低于它。</item>
/// </list>
/// </summary>
public sealed class MapPoolMarketsUseCase(
    ILighterPoolsClient pools,
    IExchangeSymbolCatalogFactory catalogs,
    IExchangeConfigRepository exchanges)
{
    public async Task<IReadOnlyList<MarketMappingDto>> ExecuteAsync(
        long exchangeId,
        AccountIndex poolAccount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(poolAccount);

        Domain.Entities.ExchangeConfig config = await exchanges.GetAsync(exchangeId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ArgumentException($"交易所配置 {exchangeId} 不存在。", nameof(exchangeId));

        IExchangeSymbolCatalog catalog = catalogs.Create(config.Code)
            ?? throw new ArgumentException(
                $"{config.DisplayName}（{ExchangeEnumText.ToText(config.Code)}）还没有合约目录可用，映射不了。",
                nameof(exchangeId));

        // 池快照给出 Lighter 侧的标记价（用它做"同物"确认）
        PoolSnapshot snapshot = await pools.FetchSnapshotAsync(poolAccount, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<ExchangeSymbolInfo, decimal> prices = await catalog
            .PricesAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ExchangeSymbolInfo> symbols = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, List<ExchangeSymbolInfo>> byBase = [];
        foreach (ExchangeSymbolInfo symbol in symbols)
        {
            if (!byBase.TryGetValue(symbol.BaseAsset, out List<ExchangeSymbolInfo>? list))
            {
                list = [];
                byBase[symbol.BaseAsset] = list;
            }

            list.Add(symbol);
        }

        List<MarketMappingDto> result = [];

        // 池子当前持有的市场 + 它挂单里的市场一起看：能跟的集合不只在持仓里
        foreach (PoolPosition position in snapshot.ActivePositions.OrderByDescending(p => p.Notional.Value))
        {
            decimal lighterPrice = position.MarkPrice.Value;
            SymbolMapper.Resolution resolution = SymbolMapper.Resolve(
                position.Symbol,
                lighterPrice,
                SymbolMapper.CandidatesFor(position.Symbol, byBase),
                prices.GetValueOrDefault,
                ExchangeEnumText.ToDisplayName(config.Code),
                catalog.Coverage);

            result.Add(new MarketMappingDto
            {
                LighterSymbol = position.Symbol,
                MarketId = position.MarketId,
                PoolNotional = position.Notional.Value,
                LighterPrice = lighterPrice,
                TargetPair = resolution.IsMapped ? $"{resolution.Base}/{resolution.Quote}" : null,
                ContractType = resolution.ContractType,
                IsTradFi = resolution.IsTradFi,
                MinNotional = resolution.MinNotional,
                IsMapped = resolution.IsMapped,
                Note = resolution.Reason,
            });
        }

        return result;
    }
}
