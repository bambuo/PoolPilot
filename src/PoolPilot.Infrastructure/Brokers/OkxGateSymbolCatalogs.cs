using System.Collections.Concurrent;
using System.Globalization;

using Microsoft.Extensions.Logging;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;

using GateContractTarget = GateIo.Net.Enums.ContractTargetType;
using GateRestClient = GateIo.Net.Interfaces.Clients.IGateIoRestClient;
using OkxContractType = OKX.Net.Enums.ContractType;
using OkxInstrumentState = OKX.Net.Enums.InstrumentState;
using OkxInstrumentType = OKX.Net.Enums.InstrumentType;
using OkxRestClient = OKX.Net.Interfaces.Clients.IOKXRestClient;

namespace PoolPilot.Infrastructure.Brokers;

/// <summary>
/// OKX 合约目录——**建立在 CryptoClients.Net 的 OKX.Net 之上**。
///
/// 口径与迁移前一致（都是实测定的），只是数据来源换成了库：
/// <list type="bullet">
///   <item>只要 <c>live</c> 状态的 SWAP；</item>
///   <item>**反向合约（inverse）不收**：它的保证金与结算都是币本身，稳定币账户开不出来。
///     实测 15 个，全在这个家族里；留着它们的代价是扫描可能选中一个永远下不出去单的合约——
///     "映射登记成功、下单必失败"比缺一行更糟。</item>
///   <item>基础资产与计价币从标的（<c>uly</c>，如 BTC-USDT）拆出来，不走 baseCcy/quoteCcy——
///     那两个字段在永续里可能为空；计价币取 settleCcy（真正的保证金币种），
///     "这个合约能不能用我的钱开"看的就是它；</item>
///   <item>传统金融的判据是平台自己给的分类（<c>instCategory</c>：1 = 加密，3 = 股票类，
///     4 = 商品/指数），**不是**"合约类型等于某个字符串"。</item>
/// </list>
/// </summary>
public sealed class OkxSymbolCatalog(
    OkxRestClient client,
    ILogger<OkxSymbolCatalog> logger) : IExchangeSymbolCatalog
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<string, (IReadOnlyList<ExchangeSymbolInfo> Rows, DateTimeOffset At)> Cache = new();

    public string Code => "okx";

    public string Coverage => "该平台全部正向（linear）永续，含股票代币类";

    public async Task<IReadOnlyList<ExchangeSymbolInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        const string Key = "okx-live";

        if (Cache.TryGetValue(Key, out (IReadOnlyList<ExchangeSymbolInfo> Rows, DateTimeOffset At) hit)
            && DateTimeOffset.UtcNow - hit.At < CacheFor)
        {
            return hit.Rows;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Cache.TryGetValue(Key, out hit) && DateTimeOffset.UtcNow - hit.At < CacheFor)
            {
                return hit.Rows;
            }

            IReadOnlyList<OKX.Net.Objects.Public.OKXInstrument> instruments = BinanceClients.OrThrow(
                await client.UnifiedApi.ExchangeData
                    .GetSymbolsAsync(OkxInstrumentType.Swap, null, null, null, cancellationToken)
                    .ConfigureAwait(false),
                "读取 OKX 合约目录");

            List<ExchangeSymbolInfo> rows = [];

            foreach (OKX.Net.Objects.Public.OKXInstrument item in instruments)
            {
                if (item.Symbol.Length == 0 || item.State != OkxInstrumentState.Live)
                {
                    continue;
                }

                if (item.ContractType == OkxContractType.Inverse)
                {
                    continue;
                }

                string[] parts = (item.Underlying ?? string.Empty).Split('-');
                string baseAsset = parts.Length > 0 ? parts[0] : string.Empty;

                string quote = item.SettlementAsset ?? string.Empty;

                if (quote.Length == 0 && parts.Length > 1)
                {
                    quote = parts[1];
                }

                if (baseAsset.Length == 0 || quote.Length == 0)
                {
                    continue;
                }

                decimal lotSize = item.LotSize ?? 0m;

                rows.Add(new ExchangeSymbolInfo(
                    Symbol: item.Symbol,
                    BaseAsset: baseAsset,
                    QuoteAsset: quote,
                    ContractType: "SWAP",

                    // 传统金融的判据是平台自己给的分类，不是"合约类型等于某个字符串"。
                    //
                    // ⚠️ 判据取的是 instCategory（库里的 SymbolCategory），**不是 category**——
                    // 后者是另一个字段，实测 500 个 SWAP 全是 "1"，拿它判会得出"一个传统金融合约都没有"
                    // （实际有 190 个股票类 + 9 个商品/指数类）。这条错被 Live 契约测试逮到过。
                    IsTradFi: item.SymbolCategory is { } category && category != OKX.Net.Enums.SymbolCategory.Crypto,
                    MinNotional: 0m,
                    StepSize: lotSize,
                    QuantityPrecision: DecimalsOf(lotSize)));
            }

            Cache[Key] = (rows, DateTimeOffset.UtcNow);
            logger.LogInformation("OKX 合约目录已刷新：{Total} 个", rows.Count);

            return rows;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<ExchangeSymbolInfo, decimal>> PricesAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OKX.Net.Objects.Market.OKXTicker> tickers = BinanceClients.OrThrow(
            await client.UnifiedApi.ExchangeData
                .GetTickersAsync(OkxInstrumentType.Swap, null, null, cancellationToken)
                .ConfigureAwait(false),
            "读取 OKX 合约价格");

        Dictionary<string, decimal> byCode = tickers
            .Where(t => t.LastPrice is not null)
            .GroupBy(t => t.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().LastPrice!.Value, StringComparer.Ordinal);

        // 对外按**合约对象**给价：上层不该拿字符串找合约（写法是一家之言，渗上去就会有人拼错）
        return (await ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => row, row => byCode.GetValueOrDefault(row.Symbol));
    }

    /// <summary>小数位数（0.001 → 3）。数量精度的口径与它一致。</summary>
    internal static int DecimalsOf(decimal value)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);

        int dot = text.IndexOf('.');

        return dot < 0 ? 0 : text.Length - dot - 1;
    }
}

/// <summary>
/// Gate 合约目录——**建立在 CryptoClients.Net 的 GateIo.Net 之上**。口径与迁移前一致：
/// <list type="bullet">
///   <item>不收正在下架（<c>in_delisting</c>）的合约；</item>
///   <item>只在 USDT 本位永续里找（<c>settlementAsset = usdt</c> 就是那个家族）；</item>
///   <item>资产币与计价币从合约名（<c>BTC_USDT</c>）拆出来；</item>
///   <item>Gate 自己用 <c>contract_type</c> 区分家族：空 = 加密，其余是 stocks/indices/metals/…，
///     这就是传统金融的判据。</item>
/// </list>
/// </summary>
public sealed class GateSymbolCatalog(
    GateRestClient client,
    ILogger<GateSymbolCatalog> logger) : IExchangeSymbolCatalog
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<string, (IReadOnlyList<ExchangeSymbolInfo> Rows, DateTimeOffset At)> Cache = new();

    public string Code => "gate";

    public string Coverage => "该平台全部 USDT 本位永续，含股票代币类";

    public async Task<IReadOnlyList<ExchangeSymbolInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        const string Key = "gate-live";

        if (Cache.TryGetValue(Key, out (IReadOnlyList<ExchangeSymbolInfo> Rows, DateTimeOffset At) hit)
            && DateTimeOffset.UtcNow - hit.At < CacheFor)
        {
            return hit.Rows;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Cache.TryGetValue(Key, out hit) && DateTimeOffset.UtcNow - hit.At < CacheFor)
            {
                return hit.Rows;
            }

            IReadOnlyList<GateIo.Net.Objects.Models.GateIoPerpFuturesContract> contracts = BinanceClients.OrThrow(
                await client.PerpetualFuturesApi.ExchangeData
                    .GetContractsAsync("usdt", cancellationToken)
                    .ConfigureAwait(false),
                "读取 Gate 合约目录");

            List<ExchangeSymbolInfo> rows = [];

            foreach (GateIo.Net.Objects.Models.GateIoPerpFuturesContract item in contracts)
            {
                if (item.Name.Length == 0 || item.Delisting)
                {
                    continue;
                }

                string[] parts = item.Name.Split('_');
                string baseAsset = parts.Length > 0 ? parts[0] : string.Empty;
                string quote = parts.Length > 1 ? parts[1] : string.Empty;

                if (baseAsset.Length == 0 || quote.Length == 0)
                {
                    continue;
                }

                string family = FamilyText(item.ContractType);

                rows.Add(new ExchangeSymbolInfo(
                    Symbol: item.Name,
                    BaseAsset: baseAsset,
                    QuoteAsset: quote,

                    // Gate 自己用 contract_type 区分家族：空 = 加密，其余是 stocks/indices/metals/…
                    ContractType: family.Length > 0 ? family : "PERPETUAL",
                    IsTradFi: family.Length > 0,
                    MinNotional: 0m,
                    StepSize: 0m,

                    // Gate 不给数量精度，只给价格精度；数量精度由最小下单量的位数推
                    QuantityPrecision: OkxSymbolCatalog.DecimalsOf(item.MinOrderQuantity)));
            }

            Cache[Key] = (rows, DateTimeOffset.UtcNow);
            logger.LogInformation("Gate 合约目录已刷新：{Total} 个", rows.Count);

            return rows;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<ExchangeSymbolInfo, decimal>> PricesAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<GateIo.Net.Objects.Models.GateIoPerpTicker> tickers = BinanceClients.OrThrow(
            await client.PerpetualFuturesApi.ExchangeData
                .GetTickersAsync("usdt", null, cancellationToken)
                .ConfigureAwait(false),
            "读取 Gate 合约价格");

        Dictionary<string, decimal> byCode = tickers
            .GroupBy(t => t.Contract, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().LastPrice, StringComparer.Ordinal);

        return (await ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => row, row => byCode.GetValueOrDefault(row.Symbol));
    }

    /// <summary>
    /// 家族名（<c>stocks</c>/<c>indices</c>/<c>metals</c>/…）。上游给的是小写字符串，库把它做成了枚举，
    /// 这里转回**上游的写法**——它已经落进流水与界面，换一种写法等于悄悄改了对外口径。
    /// 没有家族（加密永续）返回空串。
    /// </summary>
    internal static string FamilyText(GateContractTarget? target) => target switch
    {
        null => string.Empty,
        GateContractTarget.Stocks => "stocks",
        GateContractTarget.Metals => "metals",
        GateContractTarget.Indices => "indices",
        GateContractTarget.Forex => "forex",
        GateContractTarget.Commodities => "commodities",
        _ => target.Value.ToString().ToLowerInvariant(),
    };
}
