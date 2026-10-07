using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using PoolPilot.Application.Abstractions;

namespace PoolPilot.Infrastructure.Brokers;

/// <summary>
/// OKX 与 Gate 的合约目录（公开接口，不需要凭据）。
///
/// 两家都只需一次 GET 就能拿全永续清单，于是写成同一形状的两个类：
/// 清单按接入地址做 5 分钟缓存（**按地址**而不是按平台：主网与测试网是两套数据）。
///
/// 合约代码用各家自己的写法（OKX 是 <c>BTC-USDT-SWAP</c>、Gate 是 <c>BTC_USDT</c>）——
/// 下单时用的就是它，界面上显示的也是它，中间不做任何"翻译"。
///
/// ⚠️ 最小名义额给不出确切值：这两家把最小下单量表达为**合约张数**
/// （OKX 的 ctVal、Gate 的 order_size_min），换算成名义额要乘合约面值与当时价格，
/// 而面值又因合约家族而异。所以这里的 <see cref="ExchangeSymbolInfo.MinNotional"/> 填 0，
/// 由界面显示为"—"（不知道，不是 0）。真正下单前要按各自规则换算——那是下单路径的事。
/// </summary>
public sealed class OkxSymbolCatalog(
    HttpClient http,
    ILogger<OkxSymbolCatalog> logger) : IExchangeSymbolCatalog
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, (IReadOnlyList<ExchangeSymbolInfo> Rows, DateTimeOffset At)> Cache = [];

    public string Code => "okx";

    /// <remarks>反向合约不收（稳定币账户开不出来），所以"找过"的范围里没有它们——
    /// 这句话要如实，不然它会变成一句用来安慰人的假话。</remarks>
    public string Coverage => "该平台全部正向（linear）永续，含股票代币类";

    public async Task<IReadOnlyList<ExchangeSymbolInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        string key = Key();

        if (Fresh(key, out IReadOnlyList<ExchangeSymbolInfo> cached))
        {
            return cached;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Fresh(key, out cached))
            {
                return cached;
            }

            using JsonDocument document = await GetAsync(
                "/api/v5/public/instruments?instType=SWAP", cancellationToken).ConfigureAwait(false);

            List<ExchangeSymbolInfo> rows = [];

            if (document.RootElement.TryGetProperty("data", out JsonElement data))
            {
                foreach (JsonElement item in data.EnumerateArray())
                {
                    string instId = Text(item, "instId");
                    string state = Text(item, "state");
                    string contractKind = Text(item, "ctType");

                    if (instId.Length == 0 || state != "live")
                    {
                        continue;
                    }

                    // 反向合约（inverse，如 BTC-USD-SWAP）不收：它的保证金与结算都是**币本身**
                    // ，稳定币账户开不出来。实测 15 个，全在这个家族里。
                    // 留着它们的代价是扫描可能选中一个永远下不出去单的合约——
                    // 那种"映射登记成功、下单必失败"的组合，比缺一行更糟。
                    if (contractKind == "inverse")
                    {
                        continue;
                    }

                    // 基础资产与计价币从标的（uly，如 BTC-USDT）拆出来。
                    // 不走 baseCcy/quoteCcy：那两个字段在永续里可能为空。
                    string[] parts = Text(item, "uly").Split('-');
                    string baseAsset = parts.Length > 0 ? parts[0] : string.Empty;

                    // 计价币取 settleCcy（真正的保证金币种）而不是 uly 的第二段：
                    // 判断"这个合约能不能用我的钱开"，看的是保证金币种。
                    string quote = Text(item, "settleCcy");

                    if (quote.Length == 0 && parts.Length > 1)
                    {
                        quote = parts[1];
                    }

                    if (baseAsset.Length == 0 || quote.Length == 0)
                    {
                        continue;
                    }

                    // OKX 的 instCategory：1 = 加密，3 = 股票类，4 = 商品/指数（金属、原油、铜…）
                    string category = Text(item, "instCategory");

                    rows.Add(new ExchangeSymbolInfo(
                        Symbol: instId,
                        BaseAsset: baseAsset,
                        QuoteAsset: quote,
                        ContractType: "SWAP",

                        // 传统金融的判据是平台自己给的分类，不是"合约类型等于某个字符串"
                        IsTradFi: category.Length > 0 && category != "1",
                        MinNotional: 0m,
                        StepSize: Number(item, "lotSz"),
                        QuantityPrecision: Decimals(item, "lotSz")));
                }
            }

            Cache[key] = (rows, DateTimeOffset.UtcNow);
            logger.LogInformation("OKX 合约目录已刷新（{Endpoint}）：{Total} 个", key, rows.Count);

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
        using JsonDocument document = await GetAsync("/api/v5/market/tickers?instType=SWAP", cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, decimal> byCode = [];

        if (document.RootElement.TryGetProperty("data", out JsonElement data))
        {
            foreach (JsonElement item in data.EnumerateArray())
            {
                string instId = Text(item, "instId");
                decimal price = Number(item, "last");

                if (instId.Length > 0 && price > 0m)
                {
                    byCode[instId] = price;
                }
            }
        }

        // 对外按**合约对象**给价：合约代码是 OKX 的写法，不外传
        return (await ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => row, row => byCode.GetValueOrDefault(row.Symbol));
    }

    private string Key() => http.BaseAddress?.AbsoluteUri ?? "okx";

    private static bool Fresh(string key, out IReadOnlyList<ExchangeSymbolInfo> rows)
    {
        if (Cache.TryGetValue(key, out (IReadOnlyList<ExchangeSymbolInfo> Rows, DateTimeOffset At) hit)
            && DateTimeOffset.UtcNow - hit.At < CacheFor)
        {
            rows = hit.Rows;

            return true;
        }

        rows = [];

        return false;
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http
            .SendAsync(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return JsonDocument.Parse(body);
    }

    internal static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? string.Empty : string.Empty;

    internal static decimal Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed)
                ? parsed
                : 0m;

    /// <summary>由步长推小数位（"0.1" → 1、"1" → 0）。这是数量精度最可靠的来源。</summary>
    internal static int Decimals(JsonElement element, string name)
    {
        decimal step = Number(element, name);

        for (int digits = 0; digits <= 8; digits++)
        {
            if (decimal.Round(step, digits) == step)
            {
                return digits;
            }
        }

        return 8;
    }
}

/// <summary>Gate 的合约目录。合约代码用 Gate 自己的写法（<c>BTC_USDT</c>）。</summary>
public sealed class GateSymbolCatalog(
    HttpClient http,
    ILogger<GateSymbolCatalog> logger) : IExchangeSymbolCatalog
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, (IReadOnlyList<ExchangeSymbolInfo> Rows, DateTimeOffset At)> Cache = [];

    public string Code => "gate";

    public string Coverage => "该平台全部 USDT 本位永续，含股票代币类";

    public async Task<IReadOnlyList<ExchangeSymbolInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        string key = http.BaseAddress?.AbsoluteUri ?? "gate";

        if (Cache.TryGetValue(key, out (IReadOnlyList<ExchangeSymbolInfo> Rows, DateTimeOffset At) hit)
            && DateTimeOffset.UtcNow - hit.At < CacheFor)
        {
            return hit.Rows;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Cache.TryGetValue(key, out hit) && DateTimeOffset.UtcNow - hit.At < CacheFor)
            {
                return hit.Rows;
            }

            using HttpResponseMessage response = await http
                .SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v4/futures/usdt/contracts"), cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(body);

            List<ExchangeSymbolInfo> rows = [];

            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                string name = OkxSymbolCatalog.Text(item, "name");
                bool delisting = item.TryGetProperty("in_delisting", out JsonElement d) && d.GetBoolean();

                if (name.Length == 0 || delisting)
                {
                    continue;
                }

                string[] parts = name.Split('_');
                string baseAsset = parts.Length > 0 ? parts[0] : string.Empty;
                string quote = parts.Length > 1 ? parts[1] : string.Empty;

                if (baseAsset.Length == 0 || quote.Length == 0)
                {
                    continue;
                }

                string gateFamily = OkxSymbolCatalog.Text(item, "contract_type");
                string round = OkxSymbolCatalog.Text(item, "order_price_round");

                rows.Add(new ExchangeSymbolInfo(
                    Symbol: name,
                    BaseAsset: baseAsset,
                    QuoteAsset: quote,
                    // Gate 自己用 contract_type 区分家族：空 = 加密，其余是 stocks/indices/metals/…
                    ContractType: gateFamily.Length > 0 ? gateFamily : "PERPETUAL",
                    IsTradFi: gateFamily.Length > 0,
                    MinNotional: 0m,
                    StepSize: 0m,

                    // Gate 不给数量精度，只给价格精度；数量精度由 order_size_min 的位数推
                    QuantityPrecision: DecimalsOf(OkxSymbolCatalog.Number(item, "order_size_min"))));
            }

            Cache[key] = (rows, DateTimeOffset.UtcNow);
            logger.LogInformation("Gate 合约目录已刷新（{Endpoint}）：{Total} 个", key, rows.Count);

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
        using HttpResponseMessage response = await http
            .SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v4/futures/usdt/tickers"), cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);

        Dictionary<string, decimal> byCode = [];

        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            string contract = OkxSymbolCatalog.Text(item, "contract");
            decimal price = OkxSymbolCatalog.Number(item, "mark_price");

            if (price <= 0m)
            {
                price = OkxSymbolCatalog.Number(item, "last");
            }

            if (contract.Length > 0 && price > 0m)
            {
                byCode[contract] = price;
            }
        }

        // 对外按**合约对象**给价：合约代码是 Gate 的写法，不外传
        return (await ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => row, row => byCode.GetValueOrDefault(row.Symbol));
    }

    private static int DecimalsOf(decimal value)
    {
        for (int digits = 0; digits <= 8; digits++)
        {
            if (decimal.Round(value, digits) == value)
            {
                return digits;
            }
        }

        return 8;
    }
}
