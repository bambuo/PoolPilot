using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 信号来了要跟哪个合约：先查例外表，表里没有就**同名直接用**。
///
/// 规则与扫描完全一致（都走 <see cref="SymbolMapper.Resolve"/>）——这一点是刻意的：
/// 扫描把结论写进表里、跟单读同一张表，如果两边的判据不同，
/// 表里显示的"能用"与运行时实际用的就会悄悄分叉。
///
/// 三档结论，缺一不可：
/// <list type="bullet">
///   <item>表里有且启用 → 按表走。人工登记的是判断，不再拿价格去质疑它。</item>
///   <item>表里有但停用 → **不跟**。停用是一个明确的决定，不是"没有意见"。</item>
///   <item>表里没有 → 同名直接用，但仍要过价格确认：同名不同物是实测存在的
///     （Lighter 的 <c>SPX</c> 与币安的 SPX6900），没有这一步就会照名字开错资产。</item>
/// </list>
/// </summary>
public sealed class LiveTargetResolver(
    ISymbolMappingRepository mappings,
    IExchangeSymbolCatalogFactory catalogs)
{
    public async Task<LiveTarget> ResolveAsync(
        string platform,
        string sourceSymbol,
        decimal sourcePrice,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSymbol);

        SymbolMapping? row = await mappings
            .FindAsync(platform, sourceSymbol, cancellationToken)
            .ConfigureAwait(false);

        if (row is not null)
        {
            return row.IsEnabled
                ? LiveTarget.FromMapping(row)
                : LiveTarget.Refuse($"例外表里这一条是停用的（{row.SourcePair} → {row.TargetPair}），按决定不跟");
        }

        IExchangeSymbolCatalog? catalog = catalogs.Create(ExchangeEnumText.ParseCode(platform));

        if (catalog is null)
        {
            return LiveTarget.Refuse($"{platform} 还没有合约目录，无法确认同名合约是否同物");
        }

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

        IReadOnlyDictionary<ExchangeSymbolInfo, decimal> prices = await catalog
            .PricesAsync(cancellationToken)
            .ConfigureAwait(false);

        SymbolMapper.Resolution resolution = SymbolMapper.Resolve(
            sourceSymbol,
            sourcePrice,
            SymbolMapper.CandidatesFor(sourceSymbol, byBase),
            prices.GetValueOrDefault,
            platform,
            catalog.Coverage);

        return resolution.IsMapped
            ? new LiveTarget
            {
                BaseAsset = resolution.Base!,
                QuoteAsset = resolution.Quote!,
                IsTradFi = resolution.IsTradFi,
                Reason = $"同名直接用（{resolution.Base}/{resolution.Quote}）",
            }
            : LiveTarget.Refuse(resolution.Reason ?? "解析不出目标合约");
    }

}

/// <summary>跟单目标：用系统标准交易对表示，平台写法留给对接实现。</summary>
public sealed record LiveTarget
{
    public string? BaseAsset { get; init; }

    public string? QuoteAsset { get; init; }

    public bool IsTradFi { get; init; }

    /// <summary>不跟的理由；能跟时为 null。</summary>
    public string? Refusal { get; init; }

    /// <summary>说明这一档是怎么定下来的（"按例外表" / "同名直接用"），进流水便于复盘。</summary>
    public required string Reason { get; init; }

    public bool IsUsable => Refusal is null && BaseAsset is not null && QuoteAsset is not null;

    internal static LiveTarget FromMapping(SymbolMapping row) => new()
    {
        BaseAsset = row.TargetBase,
        QuoteAsset = row.TargetQuote,
        IsTradFi = row.TargetIsTradFi,
        Reason = row.Origin == SymbolMappingOrigin.Manual
            ? "按人工登记的例外"
            : "按扫描登记的例外",
    };

    internal static LiveTarget Refuse(string reason) => new() { Refusal = reason, Reason = reason };
}
