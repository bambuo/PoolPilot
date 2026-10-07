using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

using static PoolPilot.Application.UseCases.SourcePairDefaults;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 标的映射的用例组。
///
/// 这张表是**例外表**，不是标的清单。
///
/// 跟单要用的是"源标的在目标平台对应哪个合约"。绝大多数标的在两边**同名**——
/// 那就直接用，不必占一行：`BTC` 在币安就是 `BTC/USDT`，为它登记一条只是把常识抄一遍，
/// 抄出来的每一行都要人来看、来核对、来跟着改。
///
/// 所以表里只放**不一样**的那些：改了名的（WTI → CL）、以及人工作出的判断。
/// <list type="bullet">
///   <item>**扫描**：把被观察池里的市场逐个解析一遍，只把"与源不同"的写进表；
///     同名的不写（还会顺手收掉早先为同名标的留下的扫描行）。</item>
///   <item>**查询**：信号来了先查表。表里有 → 按表走；表里没有 → 同名直接用，不打网络。</item>
/// </list>
/// </summary>
/// <summary>源侧交易币。<b>实测 Lighter 的 orderBookDetails 里 base_asset_id/quote_asset_id 恒为 0</b>，
/// 拿不到逐市场的交易币，所以按系统口径统一取 USDC（Lighter 的合约以 USDC 计价与结算）。</summary>
internal static class SourcePairDefaults
{
    public const string Quote = "USDC";
}

public sealed class ListSymbolMappingsUseCase(ISymbolMappingRepository mappings)
{
    public async Task<IReadOnlyList<SymbolMappingDto>> ExecuteAsync(
        string? platform = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SymbolMapping> rows = await mappings.ListAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. rows
                .Where(r => platform is null || string.Equals(r.TargetPlatform, platform, StringComparison.OrdinalIgnoreCase))
                .Select(ToDto),
        ];
    }

    internal static SymbolMappingDto ToDto(SymbolMapping mapping) => new()
    {
        Id = mapping.Id,
        SourcePlatform = mapping.SourcePlatform,
        SourceBase = mapping.SourceBase,
        SourceQuote = mapping.SourceQuote,
        SourcePair = mapping.SourcePair,
        TargetPlatform = mapping.TargetPlatform,
        TargetBase = mapping.TargetBase,
        TargetQuote = mapping.TargetQuote,
        TargetIsTradFi = mapping.TargetIsTradFi,
        TargetPair = mapping.TargetPair,
        TargetContractType = mapping.TargetContractType,
        Origin = mapping.Origin == SymbolMappingOrigin.Manual ? "manual" : "scanned",
        IsEnabled = mapping.IsEnabled,
        SourcePrice = mapping.SourcePrice,
        TargetPrice = mapping.TargetPrice,
        PriceRatio = mapping.PriceRatio,
        VerifiedAt = mapping.VerifiedAt?.Utc,
        Note = mapping.Note,
        CreatedAt = mapping.CreatedAt.Utc,
    };
}

/// <summary>
/// 扫描被观察的池，把每个市场在目标交易所上的映射整理进表里。
///
/// 三条纪律：
/// <list type="number">
///   <item>**永不覆盖人工行**。人把 US500 指到 SPY 是一个判断，扫描没有资格推翻它；
///     扫描只会补充"表里还没有的"与刷新"扫描出来的那些"的验证信息。</item>
///   <item>**映射不上的不写进表**，只在结果里带上原因。表是"信号来了查目标"的查阅表，
///     里面放一行没有目标合约的记录，查到它的人还得再判一次"这行能不能用"——
///     不如让表只装能用的，把"谁没映射上、为什么"放在扫描结果里当场说清。</item>
///   <item>**刷新不推翻已有结论**：这一轮解析失败（多为临时取不到价）时保留上一轮的目标，
///     只把验证时间往后挪。否则价格抖动一次，映射就随机失效。</item>
/// </list>
/// </summary>
public sealed class ScanSymbolMappingsUseCase(
    ISymbolMappingRepository mappings,
    ILeadTraderRepository leads,
    ILighterPoolsClient pools,
    IExchangeSymbolCatalogFactory catalogs)
{
    /// <param name="platform">目标平台符号（<c>binance</c> 等）。**不是配置 Id**：
    /// 映射是平台级的知识，与使用者在那个平台上有几个账号无关。</param>
    public async Task<SymbolMappingScanResultDto> ExecuteAsync(
        string platform,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            throw new ArgumentException("必须指定目标平台。", nameof(platform));
        }

        // Lighter 是源，不是目标：挡住"把 Lighter 映射到 Lighter"这种没有意义的行，
        // 而不是让它悄悄写进表里再在界面上占一行看不懂的记录
        if (string.Equals(platform, SymbolMapping.SourcePlatformLighter, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Lighter 是映射的**源**（池都在它上面），不需要映射到它自己。"
                + "这里要填的是目标交易所，比如 binance。",
                nameof(platform));
        }

        IExchangeSymbolCatalog catalog = catalogs.Create(ExchangeEnumText.ParseCode(platform))
            ?? throw new ArgumentException(
                $"{platform} 还没有合约目录实现，扫描不了这个平台。"
                + $"系统目前支持：{string.Join("、", catalogs.SupportedPlatforms.Select(ExchangeEnumText.ToText))}。",
                nameof(platform));

        // 源侧：被观察的池（带单员盯着的那些）里出现过的市场，加上池子持仓的价格
        IReadOnlyList<LeadTrader> observed = await leads.ListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, decimal> sourcePrices = [];
        List<int> marketIds = [];

        foreach (LeadTrader lead in observed)
        {
            try
            {
                PoolSnapshot snapshot = await pools
                    .FetchSnapshotAsync(lead.AccountIndex, cancellationToken)
                    .ConfigureAwait(false);

                foreach (PoolPosition position in snapshot.ActivePositions)
                {
                    sourcePrices[position.Symbol] = position.MarkPrice.Value;

                    if (!marketIds.Contains(position.MarketId))
                    {
                        marketIds.Add(position.MarketId);
                    }
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // 单个池取不到快照不该让整次扫描失败：它只是少几个源标的
                continue;
            }
        }

        if (sourcePrices.Count == 0)
        {
            return new SymbolMappingScanResultDto
            {
                TargetPlatform = platform,
                SourceMarkets = 0,
                Created = 0,
                Updated = 0,
                Deleted = 0,
                Direct = 0,
                Kept = 0,
                SkippedManual = 0,
                Failures = [],
            };
        }

        IReadOnlyList<ExchangeSymbolInfo> symbols = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<ExchangeSymbolInfo, decimal> targetPrices = await catalog
            .PricesAsync(cancellationToken)
            .ConfigureAwait(false);

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

        IReadOnlyList<SymbolMapping> existing = await mappings.ListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, SymbolMapping> bySource = existing
            .Where(m => m.TargetPlatform == platform)
            .ToDictionary(m => m.SourceBase);

        int created = 0;
        int updated = 0;
        int deleted = 0;
        int direct = 0;
        int kept = 0;
        int skippedManual = 0;
        List<SymbolMappingFailureDto> failures = [];
        string venue = ExchangeEnumText.ToDisplayName(ExchangeEnumText.ParseCode(platform));
        LighterTimestamp now = LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        foreach ((string sourceSymbol, decimal sourcePrice) in sourcePrices)
        {
            SymbolMapper.Resolution resolution = SymbolMapper.Resolve(
                sourceSymbol,
                sourcePrice,
                SymbolMapper.CandidatesFor(sourceSymbol, byBase),
                targetPrices.GetValueOrDefault,
                venue,
                catalog.Coverage);

            bool sameName = resolution.IsMapped
                && string.Equals(resolution.Base, sourceSymbol, StringComparison.OrdinalIgnoreCase);

            bySource.TryGetValue(sourceSymbol, out SymbolMapping? row);

            // 人工行不动：那是人的判断，扫描没有资格推翻
            if (row?.Origin == SymbolMappingOrigin.Manual)
            {
                skippedManual++;
                continue;
            }

            // **同名直接用**——这一条与"表里有没有行"无关：
            // 没行，本来就该直接用；有行（扫描早先写下的），那一行是冗余的，收掉。
            // 只有停用的那种要留：里面有一个"不要跟它"的决定，删掉就等于把决定翻过来
            if (sameName)
            {
                if (row is null)
                {
                    direct++;
                }
                else if (row.IsEnabled)
                {
                    await mappings.DeleteAsync(row.Id, cancellationToken).ConfigureAwait(false);
                    deleted++;
                    direct++;
                }
                else
                {
                    kept++;
                }

                continue;
            }

            if (row is not null)
            {

                SymbolMapping refreshed = row with
                {
                    TargetBase = resolution.Base ?? row.TargetBase,
                    TargetQuote = resolution.Quote ?? row.TargetQuote,
                    TargetContractType = resolution.ContractType ?? row.TargetContractType,
                    TargetIsTradFi = resolution.IsMapped ? resolution.IsTradFi : row.TargetIsTradFi,
                    SourcePrice = sourcePrice,

                    // 这一轮没解析出来（例如临时取不到价）时保留上一次的结论，只把验证时间往后挪，
                    // 而不是把一条可用的映射改成不可用——那会让跟单在价格抖动时随机停摆
                    TargetPrice = resolution.IsMapped
                        ? resolution.Price
                        : row.TargetPrice,
                    VerifiedAt = now,

                    // 启用与否是人的选择，扫描只刷新"是什么"，不改"要不要用"
                    IsEnabled = row.IsEnabled,
                    Note = resolution.IsMapped ? resolution.Reason : row.Note ?? resolution.Reason,
                };

                await mappings.UpdateAsync(refreshed, cancellationToken).ConfigureAwait(false);
                updated++;
                continue;
            }

            if (!resolution.IsMapped)
            {
                failures.Add(new SymbolMappingFailureDto
                {
                    SourceBase = sourceSymbol,
                    SourceQuote = SourcePairDefaults.Quote,
                    Reason = resolution.Reason ?? "解析不出目标合约",
                });

                continue;
            }

            // 走到这里说明**与源不同名**（同名在上面就 continue 了）：这才值得占一行

            await mappings.CreateAsync(
                new SymbolMapping
                {
                    SourcePlatform = SymbolMapping.SourcePlatformLighter,
                    SourceBase = sourceSymbol,
                    SourceQuote = SourcePairDefaults.Quote,
                    TargetPlatform = platform,
                    TargetBase = resolution.Base!,
                    TargetQuote = resolution.Quote!,
                    TargetContractType = resolution.ContractType ?? "PERPETUAL",
                    TargetIsTradFi = resolution.IsTradFi,
                    Origin = SymbolMappingOrigin.Scanned,
                    IsEnabled = true,
                    SourcePrice = sourcePrice,
                    TargetPrice = resolution.Price,
                    VerifiedAt = now,
                    Note = resolution.Reason,
                    CreatedAt = now,
                },
                cancellationToken).ConfigureAwait(false);

            created++;
        }

        return new SymbolMappingScanResultDto
        {
            TargetPlatform = platform,
            SourceMarkets = sourcePrices.Count,
            Created = created,
            Updated = updated,
            Deleted = deleted,
            Direct = direct,
            Kept = kept,
            SkippedManual = skippedManual,
            Failures = failures,
        };
    }
}

/// <summary>人工登记或修正一条映射。**登记后它就不再被扫描覆盖**。</summary>
public sealed class SaveSymbolMappingUseCase(
    ISymbolMappingRepository mappings,
    IExchangeSymbolCatalogFactory catalogs)
{
    public async Task<SymbolMappingDto> ExecuteAsync(
        SaveSymbolMappingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string platform = request.TargetPlatform.Trim().ToLowerInvariant();

        if (platform == SymbolMapping.SourcePlatformLighter)
        {
            throw new ArgumentException(
                "Lighter 是映射的源，不能作为目标。目标填目标交易所的符号，比如 binance。",
                nameof(request));
        }

        IExchangeSymbolCatalog catalog = catalogs.Create(ExchangeEnumText.ParseCode(platform))
            ?? throw new ArgumentException(
                $"{platform} 还没有合约目录实现，登记不了映射。",
                nameof(request));

        IReadOnlyList<ExchangeSymbolInfo> symbols = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        // 按 (资产币, 交易币) 两段找：交易对的身份就是这两段，拼成一个字符串再拆回来
        // 只会多一层出错的地方
        ExchangeSymbolInfo target = symbols.FirstOrDefault(s =>
                string.Equals(s.BaseAsset, request.TargetBase, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.QuoteAsset, request.TargetQuote, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"{platform} 上没有 {request.TargetBase}/{request.TargetQuote} 这个交易对。"
                + "目标交易对要从平台的合约清单里选——手输一个不存在的组合只会在下单时才失败。",
                nameof(request));

        LighterTimestamp now = LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        SymbolMapping? existing = await mappings
            .FindAsync(platform, request.SourceBase, cancellationToken)
            .ConfigureAwait(false);

        SymbolMapping saved;

        if (existing is null)
        {
            saved = await mappings.CreateAsync(
                new SymbolMapping
                {
                    SourcePlatform = SymbolMapping.SourcePlatformLighter,
                    SourceBase = request.SourceBase.Trim().ToUpperInvariant(),
                    SourceQuote = SourcePairDefaults.Quote,
                    TargetPlatform = platform,
                    TargetBase = target.BaseAsset,
                    TargetQuote = target.QuoteAsset,
                    TargetContractType = target.ContractType,
                    TargetIsTradFi = target.IsTradFi,
                    Origin = SymbolMappingOrigin.Manual,
                    IsEnabled = true,
                    SourcePrice = request.SourcePrice,
                    TargetPrice = null,
                    VerifiedAt = null,
                    Note = request.Note,
                    CreatedAt = now,
                },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            saved = existing with
            {
                TargetBase = target.BaseAsset,
                TargetQuote = target.QuoteAsset,
                TargetContractType = target.ContractType,
                Origin = SymbolMappingOrigin.Manual,
                IsEnabled = request.IsEnabled,
                SourcePrice = request.SourcePrice ?? existing.SourcePrice,
                Note = request.Note,

                // 人工改过目标合约，上一次的验证结论就不再适用——把它清掉，
                // 免得界面上显示一个"已验证"的旧比值让人以为这条还有人验过
                TargetPrice = null,
                VerifiedAt = null,
            };

            await mappings.UpdateAsync(saved, cancellationToken).ConfigureAwait(false);
        }

        return ListSymbolMappingsUseCase.ToDto(saved);
    }
}

/// <summary>删除一条映射。</summary>
public sealed class DeleteSymbolMappingUseCase(ISymbolMappingRepository mappings)
{
    public Task ExecuteAsync(long id, CancellationToken cancellationToken = default) =>
        mappings.DeleteAsync(id, cancellationToken);
}
