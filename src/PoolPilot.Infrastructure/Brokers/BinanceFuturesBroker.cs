using System.Globalization;
using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;

namespace PoolPilot.Infrastructure.Brokers;

/// <summary>币安返回的错误。**原样带上交易所有错误码与文案**。</summary>
public sealed class BinanceApiException(string endpoint, int code, string message, HttpStatusCode status)
    : Exception($"币安 {endpoint} 失败：HTTP {(int)status} code {code} — {message}")
{
    public string Endpoint { get; } = endpoint;

    /// <summary>币安的错误码（负数）。例如 -2019 保证金不足、-4164 名义额太小、-1021 时间戳不同步。</summary>
    public int Code { get; } = code;

    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// 币安 U 本位合约（fapi）客户端。
///
/// 已实测可用的端点（用真钥匙逐条验过）：
/// <list type="bullet">
///   <item><c>GET /fapi/v3/account</c> —— 钱包余额、可用、维持保证金、持仓模式</item>
///   <item><c>GET /fapi/v2/positionRisk</c> —— 仓位、均价、杠杆、强平价、未实现盈亏</item>
///   <item><c>GET /fapi/v1/leverageBracket</c> —— 各市杠杆档位（用于校验上限）</item>
///   <item><c>POST /fapi/v1/leverage</c> —— 设定杠杆（幂等）</item>
///   <item><c>POST /fapi/v1/order</c> / <c>/order/test</c> —— 下单 / 只校验不成交</item>
///   <item><c>GET /fapi/v1/exchangeInfo</c>（公开）—— 步长、最小量、最小名义额、精度</item>
/// </list>
///
/// 实测过的一条账户事实：**双向持仓模式（hedge mode）已开启**，因此下单必须带 `positionSide`，
/// 且不能用 `reduceOnly`——平仓靠"反方向 + positionSide 指向要减的那一侧"。
/// 参数形状由 <see cref="ValidateOrderAsync"/>（/order/test）在不花钱的前提下验证。
/// </summary>
public sealed class BinanceFuturesBroker(
    HttpClient http,
    ExchangeConfig config,
    ILogger logger) : IExchangeBroker
{
    private const string BasePath = "/fapi/v1";

    /// <summary>
    /// 服务器时间与本地时间的偏移（毫秒）。取一次缓存。
    /// **按接入地址建键**：主网与测试网是两台服务器，偏移不同，共用一个槽会让其中一边被拒。
    /// </summary>
    private static readonly Dictionary<string, long> Offsets = [];
    private static readonly SemaphoreSlim OffsetGate = new(1, 1);

    public string Code => "binance";

    public async Task<BrokerAccount> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        JsonElement account = await SendAsync(HttpMethod.Get, "/fapi/v3/account", null, signed: true, cancellationToken)
            .ConfigureAwait(false);

        bool hedge = false;

        try
        {
            JsonElement dual = await SendAsync(
                HttpMethod.Get, "/fapi/v1/positionSide/dual", null, signed: true, cancellationToken).ConfigureAwait(false);

            hedge = dual.TryGetProperty("dualSidePosition", out JsonElement flag) && flag.GetBoolean();
        }
        catch (BinanceApiException ex)
        {
            // 取不到持仓模式不该拦住"看账户"：它只影响下单参数形状，而这里是只读路径
            logger.LogWarning(ex, "读取持仓模式失败，按单向处理");
        }

        JsonElement positions = await SendAsync(
            HttpMethod.Get, "/fapi/v2/positionRisk", null, signed: true, cancellationToken).ConfigureAwait(false);

        List<BrokerPosition> open = [];

        foreach (JsonElement item in positions.EnumerateArray())
        {
            decimal size = ParseDecimal(item, "positionAmt");

            if (size == 0m)
            {
                continue;
            }

            decimal entry = ParseDecimal(item, "entryPrice");
            decimal mark = ParseDecimal(item, "markPrice");
            decimal liquidation = ParseDecimal(item, "liquidationPrice");

            open.Add(new BrokerPosition(
                Symbol: Text(item, "symbol"),
                PositionSide: Text(item, "positionSide"),
                Size: size,
                EntryPrice: entry,
                MarkPrice: mark,
                Leverage: (int)ParseDecimal(item, "leverage"),
                LiquidationPrice: liquidation,
                UnrealizedPnl: ParseDecimal(item, "unRealizedProfit"),
                Notional: mark > 0m ? size * mark : size * entry));
        }

        return new BrokerAccount(
            WalletBalance: ParseDecimal(account, "totalWalletBalance"),
            AvailableBalance: ParseDecimal(account, "availableBalance"),
            UnrealizedPnl: ParseDecimal(account, "totalUnrealizedProfit"),
            IsHedgeMode: hedge,
            Positions: open);
    }

    /// <summary>
    /// 系统标准交易对 → 币安的合约代码。**转换只发生在这里**：币安的写法是资产币紧跟交易币，
    /// 上层给 <c>(BTC, USDT)</c>，币安这边认 <c>BTCUSDT</c>。
    ///
    /// 之所以不放上层：那样全系统都得学会一家的方言，换一家要改一遍所有调用点，
    /// 而拼错的代价是"下单失败"或更糟——拼成了另一个合约。
    /// </summary>
    private static string ContractCodeOf(string baseAsset, string quoteAsset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAsset);
        ArgumentException.ThrowIfNullOrWhiteSpace(quoteAsset);

        return $"{baseAsset}{quoteAsset}".ToUpperInvariant();
    }

    public async Task<BrokerInstrument> GetInstrumentAsync(
        string baseAsset,
        string quoteAsset,
        CancellationToken cancellationToken = default)
    {
        string symbol = ContractCodeOf(baseAsset, quoteAsset);

        JsonElement info = await SendAsync(
            HttpMethod.Get, "/fapi/v1/exchangeInfo", null, signed: false, cancellationToken).ConfigureAwait(false);

        foreach (JsonElement item in info.GetProperty("symbols").EnumerateArray())
        {
            if (!string.Equals(Text(item, "symbol"), symbol, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            decimal step = 0m;
            decimal minQty = 0m;
            decimal minNotional = 0m;

            foreach (JsonElement filter in item.GetProperty("filters").EnumerateArray())
            {
                switch (Text(filter, "filterType"))
                {
                    case "LOT_SIZE":
                        step = FilterValue(filter, "stepSize");
                        minQty = FilterValue(filter, "minQty");
                        break;
                    case "MIN_NOTIONAL":
                        minNotional = FilterValue(filter, "notional");
                        break;
                }
            }

            return new BrokerInstrument(
                Symbol: Text(item, "symbol"),
                QuantityPrecision: (int)ParseDecimal(item, "quantityPrecision"),
                PricePrecision: (int)ParseDecimal(item, "pricePrecision"),
                StepSize: step,
                MinQuantity: minQty,
                MinNotional: minNotional);
        }

        throw new BinanceApiException("/fapi/v1/exchangeInfo", -1121, $"找不到合约 {symbol}", HttpStatusCode.NotFound);
    }

    public async Task<decimal> GetPriceAsync(
        string baseAsset,
        string quoteAsset,
        CancellationToken cancellationToken = default)
    {
        string symbol = ContractCodeOf(baseAsset, quoteAsset);

        JsonElement result = await SendAsync(
            HttpMethod.Get,
            "/fapi/v1/ticker/price",
            new Dictionary<string, string> { ["symbol"] = symbol },
            signed: false,
            cancellationToken).ConfigureAwait(false);

        return ParseDecimal(result, "price");
    }

    public async Task<int> EnsureLeverageAsync(
        string baseAsset,
        string quoteAsset,
        int leverage,
        CancellationToken cancellationToken = default)
    {
        string symbol = ContractCodeOf(baseAsset, quoteAsset);

        // 幂等：同一个值重复设置币安不会报错，所以不必先查当前值再决定
        JsonElement result = await SendAsync(
            HttpMethod.Post,
            "/fapi/v1/leverage",
            new Dictionary<string, string>
            {
                ["symbol"] = symbol,
                ["leverage"] = leverage.ToString(CultureInfo.InvariantCulture),
            },
            signed: true,
            cancellationToken).ConfigureAwait(false);

        return (int)ParseDecimal(result, "leverage");
    }

    public async Task<BrokerOrderResult> PlaceOrderAsync(
        BrokerOrderRequest order,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        JsonElement result = await SendAsync(
            HttpMethod.Post, "/fapi/v1/order", OrderParameters(order), signed: true, cancellationToken)
            .ConfigureAwait(false);

        return ToResult(order, result);
    }

    public async Task ValidateOrderAsync(
        BrokerOrderRequest order,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        // /order/test：走完签名、规格、权限、保证金的全套校验，但不产生订单
        await SendAsync(
            HttpMethod.Post, "/fapi/v1/order/test", OrderParameters(order), signed: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 下单参数。
    ///
    /// 双向持仓模式下的口径（本账户实测就是双向）：
    /// <list type="bullet">
    ///   <item>必须带 <c>positionSide</c>（LONG/SHORT），否则币安拒单。</item>
    ///   <item>**不能带 <c>reduceOnly</c>**——双向模式下它是被拒的参数；
    ///     减仓靠"方向相反 + positionSide 指向要减的那一侧"由币安自行识别，
    ///     实在要表达"只平不反手"就用 <c>closePosition</c>（全额平仓时）。</item>
    /// </list>
    /// 单向模式则相反：带 <c>reduceOnly</c>、不带 <c>positionSide</c>。
    /// 两条路都不能猜——所以形状先由 <c>/order/test</c> 验过再上真单。
    /// </summary>
    private Dictionary<string, string> OrderParameters(BrokerOrderRequest order)
    {
        Dictionary<string, string> parameters = new()
        {
            // 平台写法在这里出现：请求带的是系统标准交易对，合约代码由本实现拼
            ["symbol"] = ContractCodeOf(order.BaseAsset, order.QuoteAsset),
            ["side"] = order.Side == OrderSide.Buy ? "BUY" : "SELL",
            ["type"] = "MARKET",
            ["quantity"] = BinanceSignature.Text(order.Quantity),
            ["newClientOrderId"] = order.ClientOrderId,
        };

        if (config.Kind == ExchangeKind.Cex && order.PositionSide is { Length: > 0 } side)
        {
            parameters["positionSide"] = side;
        }
        else if (order.ReduceOnly)
        {
            parameters["reduceOnly"] = "true";
        }

        return parameters;
    }

    private static BrokerOrderResult ToResult(BrokerOrderRequest order, JsonElement result)
    {
        decimal executed = ParseDecimal(result, "executedQty");
        decimal average = ParseDecimal(result, "avgPrice");

        return new BrokerOrderResult(
            Symbol: Text(result, "symbol"),
            OrderId: (long)ParseDecimal(result, "orderId"),
            ClientOrderId: Text(result, "clientOrderId"),
            Status: Text(result, "status"),
            ExecutedQuantity: executed,
            AveragePrice: average);
    }

    /// <summary>
    /// 发一个带签名的请求。所有端点都走这里，于是时间同步、错误映射、密钥处理只有一份。
    /// **密钥不进日志**：出错时只带端点、错误码与币安原文。
    /// </summary>
    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        Dictionary<string, string>? parameters,
        bool signed,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> query = parameters is null ? [] : new(parameters);

        if (signed)
        {
            query["timestamp"] = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + await OffsetAsync(cancellationToken)
                .ConfigureAwait(false)).ToString(CultureInfo.InvariantCulture);
            query["recvWindow"] = "5000";
        }

        string queryString = string.Join('&', query.Select(pair => $"{pair.Key}={pair.Value}"));
        string requestPath = query.Count == 0 ? path : $"{path}?{queryString}";

        if (signed)
        {
            requestPath += $"&signature={BinanceSignature.Sign(RequireSecret(), queryString)}";
        }

        using HttpRequestMessage request = new(method, requestPath);

        if (signed)
        {
            request.Headers.Add("X-MBX-APIKEY", RequireKey());
        }

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            int code = -1;
            string message = body;

            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                code = document.RootElement.TryGetProperty("code", out JsonElement c) ? c.GetInt32() : -1;
                message = document.RootElement.TryGetProperty("msg", out JsonElement m) ? m.GetString() ?? body : body;
            }
            catch (JsonException)
            {
                // 非 JSON 的失败响应（网关错误页等）原样带出
            }

            throw new BinanceApiException(path, code, message, response.StatusCode);
        }

        using JsonDocument parsed = JsonDocument.Parse(body);

        return parsed.RootElement.Clone();
    }

    private string RequireKey() => config.ApiKey
        ?? throw new InvalidOperationException("这条交易所配置没有 API Key，无法调用需要签名的接口。");

    private string RequireSecret() => config.ApiSecret
        ?? throw new InvalidOperationException("这条交易所配置没有 API Secret，无法签名。");

    /// <summary>服务器时间偏移。取不到就用本地时间（多数情况够用，差得太多会被 -1021 拦下并提示）。</summary>
    private async Task<long> OffsetAsync(CancellationToken cancellationToken)
    {
        string key = http.BaseAddress?.AbsoluteUri ?? config.Code.ToString();

        if (Offsets.TryGetValue(key, out long cached))
        {
            return cached;
        }

        await OffsetGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Offsets.TryGetValue(key, out cached))
            {
                return cached;
            }

            using HttpResponseMessage response = await http
                .SendAsync(new HttpRequestMessage(HttpMethod.Get, "/fapi/v1/time"), cancellationToken)
                .ConfigureAwait(false);

            long offset = 0;

            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                using JsonDocument document = JsonDocument.Parse(body);
                long server = document.RootElement.GetProperty("serverTime").GetInt64();
                offset = server - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                logger.LogInformation("币安服务器时间偏移 {Offset} ms", offset);
            }

            Offsets[key] = offset;

            return offset;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "取币安服务器时间失败，本次用本地时间签名");
            Offsets[key] = 0;

            return 0;
        }
        finally
        {
            OffsetGate.Release();
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? string.Empty : string.Empty;

    private static decimal ParseDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return 0m;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String => decimal.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out decimal parsed)
                ? parsed
                : 0m,
            _ => 0m,
        };
    }

    /// <summary>过滤器里的数值也是字符串（"0.001"），口径与账户字段一致。</summary>
    private static decimal FilterValue(JsonElement filter, string name) =>
        filter.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed)
                ? parsed
                : 0m;
}

/// <summary>
/// 币安合约目录（公开接口）。带 5 分钟缓存：合约清单与规格变动极少，
/// 而跨场所跟单每次信号都要查映射，不缓存就是每次都拉一份 659+216 条的清单。
/// </summary>
public sealed class BinanceSymbolCatalog(
    HttpClient http,
    ILogger<BinanceSymbolCatalog> logger) : IExchangeSymbolCatalog
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>缓存**按接入地址**分开：主网与测试网的合约清单是两套，共用一个槽会互相污染。</summary>
    private static readonly Dictionary<string, (IReadOnlyList<ExchangeSymbolInfo> Rows, DateTimeOffset At)> Cache = [];

    public string Code => "binance";

    public string Coverage => "加密永续与传统金融永续（股票、商品、指数）";

    public async Task<IReadOnlyList<ExchangeSymbolInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        string key = http.BaseAddress?.AbsoluteUri ?? "binance";

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
                .SendAsync(new HttpRequestMessage(HttpMethod.Get, "/fapi/v1/exchangeInfo"), cancellationToken)
                .ConfigureAwait(false);

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(body);

            List<ExchangeSymbolInfo> rows = [];

            foreach (JsonElement item in document.RootElement.GetProperty("symbols").EnumerateArray())
            {
                string quote = item.TryGetProperty("quoteAsset", out JsonElement q) ? q.GetString() ?? "" : "";
                string status = item.TryGetProperty("status", out JsonElement st) ? st.GetString() ?? "" : "";

                // **不按计价币过滤**：同一个资产币在同一个交易所可以对应多个计价币
                // （实测币安就有 SPCXUSDT 与 SPCXUSD1），按 USDT 过滤会把它们全挡掉，
                // 于是"交易币"那一栏永远只有一个选项、看起来像个坏掉的控件。
                // 能不能交易由账户与交易所决定（下单时校验），不是这里先替使用者裁掉。
                if (status != "TRADING")
                {
                    continue;
                }

                string kind = item.TryGetProperty("contractType", out JsonElement kt) ? kt.GetString() ?? "" : "";

                // 但**交割合约不能收**：实测 4 个（BTC/ETH 的当季与次季），它们会到期结算，
                // 与源的永续不是同一种东西；而到期前价格只与现货差几个点，价格守卫认不出它。
                // 留着它的后果是"跟的是永续、开出来的是三个月后要交割的合约"。
                //
                // 注意这条口径**不是**按计价币、也不是按"是不是加密"筛——早先按 PERPETUAL 一个
                // 命名空间筛，把 216 个股票/商品合约全挡在门外，那才是错的。永续的那两个命名空间都收。
                if (kind is not ("PERPETUAL" or "TRADIFI_PERPETUAL"))
                {
                    continue;
                }

                decimal step = 0m;
                decimal minNotional = 0m;

                foreach (JsonElement filter in item.GetProperty("filters").EnumerateArray())
                {
                    string type = filter.TryGetProperty("filterType", out JsonElement t) ? t.GetString() ?? "" : "";

                    if (type == "LOT_SIZE")
                    {
                        step = ParseText(filter, "stepSize");
                    }
                    else if (type == "MIN_NOTIONAL")
                    {
                        minNotional = ParseText(filter, "notional");
                    }
                }

                string contractType = item.TryGetProperty("contractType", out JsonElement ct) ? ct.GetString() ?? "" : "";

                rows.Add(new ExchangeSymbolInfo(
                    Symbol: item.TryGetProperty("symbol", out JsonElement sym) ? sym.GetString() ?? "" : "",
                    BaseAsset: item.TryGetProperty("baseAsset", out JsonElement b) ? b.GetString() ?? "" : "",
                    QuoteAsset: quote,
                    ContractType: contractType,

                    // 币安的口径：传统金融走 TRADIFI_PERPETUAL 这个命名空间（加密是 PERPETUAL）
                    IsTradFi: string.Equals(contractType, "TRADIFI_PERPETUAL", StringComparison.Ordinal),
                    MinNotional: minNotional,
                    StepSize: step,
                    QuantityPrecision: item.TryGetProperty("quantityPrecision", out JsonElement qp) ? qp.GetInt32() : 0));
            }

            Cache[key] = (rows, DateTimeOffset.UtcNow);

            logger.LogInformation(
                "币安合约目录已刷新（{Endpoint}）：{Total} 个（其中传统金融 {TradFi} 个）",
                key,
                rows.Count,
                rows.Count(r => r.IsTradFi));

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
        // 不带 symbol 的行情端点一次返回全部合约价格（权重 2），比逐个查便宜得多
        using HttpResponseMessage response = await http
            .SendAsync(new HttpRequestMessage(HttpMethod.Get, "/fapi/v1/ticker/price"), cancellationToken)
            .ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);

        Dictionary<string, decimal> byCode = [];

        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("symbol", out JsonElement sym) && item.TryGetProperty("price", out JsonElement price)
                && decimal.TryParse(price.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal parsed))
            {
                byCode[sym.GetString() ?? ""] = parsed;
            }
        }

        // 对外按**合约对象**给价：上层不该拿字符串找合约（写法是一家之言，渗上去就会有人拼错）
        return (await ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => row, row => byCode.GetValueOrDefault(row.Symbol));
    }

    private static decimal ParseText(JsonElement filter, string name) =>
        filter.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out decimal parsed)
                ? parsed
                : 0m;
}
