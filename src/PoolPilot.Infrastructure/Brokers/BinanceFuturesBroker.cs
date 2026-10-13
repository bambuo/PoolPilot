using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;

using BinancePositionSide = Binance.Net.Enums.PositionSide;
using BinanceOrderSide = Binance.Net.Enums.OrderSide;
using BinanceContractType = Binance.Net.Enums.ContractType;
using BinanceOrderStatus = Binance.Net.Enums.OrderStatus;
using BinanceOrderType = Binance.Net.Enums.FuturesOrderType;
using BinanceRestClient = Binance.Net.Interfaces.Clients.IBinanceRestClient;
using BinanceSymbolStatus = Binance.Net.Enums.SymbolStatus;
using DomainOrderSide = PoolPilot.Domain.Enums.OrderSide;

namespace PoolPilot.Infrastructure.Brokers;

/// <summary>币安返回的错误。**原样带上交易所的错误码与文案**。</summary>
public sealed class BinanceApiException(string endpoint, string code, string message, int status)
    : Exception($"币安 {endpoint} 失败：{(status > 0 ? $"HTTP {status} " : string.Empty)}code {code} — {message}")
{
    public string Endpoint { get; } = endpoint;

    /// <summary>
    /// 币安的错误码（负数，字符串形式，如 "-2019" 保证金不足、"-4164" 名义太小、"-1021" 时间戳不同步）。
    /// **保持字符串**：上游给的就是这个形状，转成数字只会在遇到非数字码时丢信息。
    /// </summary>
    public string Code { get; } = code;

    /// <summary>HTTP 状态码；取不到时为 0（库在某些失败路径上不给状态码）。</summary>
    public int Status { get; } = status;
}

/// <summary>
/// 币安 U 本位合约（fapi）券商——**建立在 CryptoClients.Net 的 Binance.Net 之上**。
///
/// 迁移前这里是自研的 HTTP + HMAC 签名 + 时间同步；那些现在都归库管。本类只保留三件事：
/// <list type="number">
///   <item>**系统标准交易对与平台合约代码的转换**（只在这一处发生）；</item>
///   <item>**业务口径**：只把有量的仓位算进来、持仓模式取不到时不拦住看账户、
///     名义额按标记价算（没有标记价才退回开仓价）；</item>
///   <item>**错误原样透传**（见 <see cref="BinanceClients.OrThrow"/>）。</item>
/// </list>
///
/// 一条**已实测**的账户事实仍然成立：账户是双向持仓模式（hedge mode），
/// 下单必须带 <c>positionSide</c> 且不能用 <c>reduceOnly</c>——平仓靠"反方向 + positionSide
/// 指向要减的那一侧"。形状由 <see cref="ValidateOrderAsync"/>（/order/test）在不花钱的前提下验证。
/// </summary>
public sealed class BinanceFuturesBroker(
    BinanceRestClient client,
    BinanceOrderTestExtension testOrder,
    ILogger logger) : IExchangeBroker
{
    public string Code => "binance";

    public async Task<BrokerAccount> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        Binance.Net.Objects.Models.Futures.BinanceFuturesAccountInfoV3 account = BinanceClients.OrThrow(
            await client.UsdFuturesApi.Account
                .GetAccountInfoV3Async(ct: cancellationToken)
                .ConfigureAwait(false),
            "读取账户");

        bool hedge = false;

        try
        {
            Binance.Net.Objects.Models.Futures.BinanceFuturesPositionMode mode = BinanceClients.OrThrow(
                await client.UsdFuturesApi.Account
                    .GetPositionModeAsync(ct: cancellationToken)
                    .ConfigureAwait(false),
                "读取持仓模式");

            hedge = mode.IsHedgeMode;
        }
        catch (BinanceApiException ex)
        {
            // 取不到持仓模式不该拦住"看账户"：它只影响下单参数形状，而这里是只读路径
            logger.LogWarning(ex, "读取持仓模式失败，按单向处理");
        }

        IReadOnlyList<Binance.Net.Objects.Models.Futures.BinancePositionDetailsUsdt> positions =
            BinanceClients.OrThrow(
                await client.UsdFuturesApi.Account
                    .GetPositionInformationAsync(ct: cancellationToken)
                    .ConfigureAwait(false),
                "读取持仓");

        List<BrokerPosition> open = [];

        foreach (Binance.Net.Objects.Models.Futures.BinancePositionDetailsUsdt item in positions)
        {
            if (item.Quantity == 0m)
            {
                continue;
            }

            open.Add(new BrokerPosition(
                Symbol: item.Symbol,
                PositionSide: item.PositionSide.ToString().ToUpperInvariant(),
                Size: item.Quantity,
                EntryPrice: item.EntryPrice,
                MarkPrice: item.MarkPrice,
                Leverage: item.Leverage,
                LiquidationPrice: item.LiquidationPrice,
                UnrealizedPnl: item.UnrealizedPnl,
                Notional: item.MarkPrice > 0m ? item.Quantity * item.MarkPrice : item.Quantity * item.EntryPrice));
        }

        return new BrokerAccount(
            WalletBalance: account.TotalWalletBalance,
            AvailableBalance: account.AvailableBalance,
            UnrealizedPnl: account.TotalUnrealizedProfit,
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

        Binance.Net.Objects.Models.Futures.BinanceFuturesUsdtExchangeInfo info = BinanceClients.OrThrow(
            await client.UsdFuturesApi.ExchangeData
                .GetExchangeInfoAsync(cancellationToken)
                .ConfigureAwait(false),
            "读取合约规格");

        foreach (Binance.Net.Objects.Models.Futures.BinanceFuturesUsdtSymbol item in info.Symbols)
        {
            if (!string.Equals(item.Name, symbol, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new BrokerInstrument(
                Symbol: item.Name,
                QuantityPrecision: item.QuantityPrecision,
                PricePrecision: item.PricePrecision,
                StepSize: item.LotSizeFilter?.StepSize ?? 0m,
                MinQuantity: item.LotSizeFilter?.MinQuantity ?? 0m,
                MinNotional: item.MinNotionalFilter?.MinNotional ?? 0m);
        }

        throw new BinanceApiException(
            "fapi/v1/exchangeInfo",
            "-1121",
            $"找不到合约 {symbol}",
            0);
    }

    public async Task<decimal> GetPriceAsync(
        string baseAsset,
        string quoteAsset,
        CancellationToken cancellationToken = default)
    {
        string symbol = ContractCodeOf(baseAsset, quoteAsset);

        Binance.Net.Objects.Models.Spot.BinancePrice price = BinanceClients.OrThrow(
            await client.UsdFuturesApi.ExchangeData
                .GetPriceAsync(symbol, cancellationToken)
                .ConfigureAwait(false),
            "读取最新价");

        return price.Price;
    }

    public async Task<int> EnsureLeverageAsync(
        string baseAsset,
        string quoteAsset,
        int leverage,
        CancellationToken cancellationToken = default)
    {
        string symbol = ContractCodeOf(baseAsset, quoteAsset);

        // 幂等：同一个值重复设置币安不会报错，所以不必先查当前值再决定
        Binance.Net.Objects.Models.Futures.BinanceFuturesInitialLeverageChangeResult result = BinanceClients.OrThrow(
            await client.UsdFuturesApi.Account
                .ChangeInitialLeverageAsync(symbol, leverage, ct: cancellationToken)
                .ConfigureAwait(false),
            "设置杠杆");

        return result.Leverage;
    }

    public async Task<BrokerOrderResult> PlaceOrderAsync(
        BrokerOrderRequest order,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        Binance.Net.Objects.Models.Futures.BinanceUsdFuturesOrder result = BinanceClients.OrThrow(
            await client.UsdFuturesApi.Trading
                .PlaceOrderAsync(
                    symbol: ContractCodeOf(order.BaseAsset, order.QuoteAsset),
                    side: order.Side == DomainOrderSide.Buy ? BinanceOrderSide.Buy : BinanceOrderSide.Sell,
                    type: BinanceOrderType.Market,
                    quantity: order.Quantity,
                    positionSide: PositionSideOf(order),
                    reduceOnly: ReduceOnlyOf(order),

                    // 客户端订单号：与本地记录对上的唯一线索，库会原样带给币安
                    newClientOrderId: order.ClientOrderId,
                    ct: cancellationToken)
                .ConfigureAwait(false),
            "下单");

        return new BrokerOrderResult(
            Symbol: result.Symbol,
            OrderId: result.Id,
            ClientOrderId: result.ClientOrderId,
            Status: result.Status.ToString().ToUpperInvariant(),
            ExecutedQuantity: result.QuantityFilled,
            AveragePrice: result.AveragePrice);
    }

    public Task ValidateOrderAsync(BrokerOrderRequest order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        return testOrder.ValidateAsync(order, cancellationToken);
    }

    /// <summary>
    /// 要影响哪一侧的持仓（双向模式）。给不出答案就返回 null——那时由 <see cref="ReduceOnlyOf"/>
    /// 表达"只减不增"，这是单向模式的形状。
    /// </summary>
    private static BinancePositionSide? PositionSideOf(BrokerOrderRequest order) =>
        order.PositionSide switch
        {
            "LONG" => BinancePositionSide.Long,
            "SHORT" => BinancePositionSide.Short,
            _ => null,
        };

    /// <summary>
    /// 只减仓。**只在与 positionSide 互斥时使用**：双向模式下带它会直接被币安拒单
    /// （减仓靠"方向相反 + positionSide 指向要减的那一侧"表达）。
    /// </summary>
    private static bool? ReduceOnlyOf(BrokerOrderRequest order) =>
        PositionSideOf(order) is not null ? null
        : order.ReduceOnly ? true
        : null;
}

/// <summary>
/// **库未覆盖的唯一一处**：币安 U 本位合约的 <c>POST /fapi/v1/order/test</c>（只校验不成交）。
///
/// 整个 Binance.Net 里只有**现货**有 <c>PlaceTestOrderAsync</c>，U 本位合约没有；
/// 库的 <c>RestApiClient.SendAsync</c> 是 protected，也没有"自定义端点"入口，
/// 所以这一条只能自己发——但它不是"重新造一套对接"：
/// <list type="bullet">
///   <item>地址取自库的环境对象（<see cref="Binance.Net.BinanceEnvironment.UsdFuturesRestAddress"/>），
///     主网/测试网由库那份定义决定，不在这里再抄一遍；</item>
///   <item>参数形状与下单一致（同一个 <see cref="Parameters"/> 生成，两处不会漂移）；</item>
///   <item>签名是已实测、有独立参考值锁住的那一段（<see cref="BinanceSignature"/>）。</item>
/// </list>
///
/// 它是**唯一能在不花钱的前提下证明"这单发得出去"**的办法（签名、规格、权限、保证金全套校验），
/// 所以留着它，而不是因为"库没有"就把它删掉。
/// </summary>
public sealed class BinanceOrderTestExtension(
    HttpClient http,
    ExchangeConfig config)
{
    public async Task ValidateAsync(BrokerOrderRequest order, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> query = Parameters(order);

        query["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        query["recvWindow"] = "5000";

        string queryString = string.Join('&', query.Select(pair => $"{pair.Key}={pair.Value}"));
        string signature = BinanceSignature.Sign(
            config.ApiSecret ?? throw new InvalidOperationException(
                $"「{config.DisplayName}」没有 API Secret，无法签名。"),
            queryString);

        string baseAddress = BinanceClients.EnvironmentOf(config).UsdFuturesRestAddress
            ?? throw new InvalidOperationException("库没有给出该环境的 U 本位合约 REST 地址。");
        Uri url = new($"{baseAddress.TrimEnd('/')}/fapi/v1/order/test?{queryString}&signature={signature}");

        using HttpRequestMessage request = new(HttpMethod.Post, url);
        request.Headers.Add(
            "X-MBX-APIKEY",
            config.ApiKey ?? throw new InvalidOperationException(
                $"「{config.DisplayName}」没有 API Key，无法调用需要签名的接口。"));

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string code = "-1";
        string message = body;

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            code = document.RootElement.TryGetProperty("code", out JsonElement c)
                ? c.ToString()
                : code;
            message = document.RootElement.TryGetProperty("msg", out JsonElement m)
                ? m.GetString() ?? body
                : body;
        }
        catch (JsonException)
        {
            // 非 JSON 的失败响应（网关错误页等）原样带出
        }

        throw new BinanceApiException("fapi/v1/order/test", code, message, (int)response.StatusCode);
    }

    /// <summary>
    /// 下单参数。
    ///
    /// 双向持仓模式下的口径（本账户实测就是双向）：
    /// <list type="bullet">
    ///   <item>必须带 <c>positionSide</c>（LONG/SHORT），否则币安拒单。</item>
    ///   <item>**不能带 <c>reduceOnly</c>**——双向模式下它是被拒的参数。</item>
    /// </list>
    /// 单向模式则相反：带 <c>reduceOnly</c>、不带 <c>positionSide</c>。
    /// 两条路都不能猜——所以形状先由 <c>/order/test</c> 验过再上真单。
    /// </summary>
    internal static Dictionary<string, string> Parameters(BrokerOrderRequest order)
    {
        ArgumentNullException.ThrowIfNull(order);

        Dictionary<string, string> parameters = new()
        {
            // 平台写法在这里出现：请求带的是系统标准交易对，合约代码由本实现拼
            ["symbol"] = $"{order.BaseAsset}{order.QuoteAsset}".ToUpperInvariant(),
            ["side"] = order.Side == DomainOrderSide.Buy ? "BUY" : "SELL",
            ["type"] = "MARKET",
            ["quantity"] = BinanceSignature.Text(order.Quantity),
            ["newClientOrderId"] = order.ClientOrderId,
        };

        if (order.PositionSide is "LONG" or "SHORT")
        {
            parameters["positionSide"] = order.PositionSide;
        }
        else if (order.ReduceOnly)
        {
            parameters["reduceOnly"] = "true";
        }

        return parameters;
    }
}

/// <summary>
/// 币安合约目录——**建立在 CryptoClients.Net 之上**。带 5 分钟缓存：合约清单与规格变动极少，
/// 而跨场所跟单每次信号都要查映射，不缓存就是每次都拉一份 659+216 条的清单。
/// </summary>
public sealed class BinanceSymbolCatalog(
    BinanceRestClient client,
    Binance.Net.BinanceEnvironment environment,
    ILogger<BinanceSymbolCatalog> logger) : IExchangeSymbolCatalog
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>缓存**按环境**分开：主网与测试网的合约清单是两套，共用一个槽会互相污染。</summary>
    private static readonly ConcurrentDictionary<string, (IReadOnlyList<ExchangeSymbolInfo> Rows, DateTimeOffset At)> Cache = new();

    public string Code => "binance";

    public string Coverage => "加密永续与传统金融永续（股票、商品、指数）";

    public async Task<IReadOnlyList<ExchangeSymbolInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        string key = environment.Name;

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

            Binance.Net.Objects.Models.Futures.BinanceFuturesUsdtExchangeInfo info = BinanceClients.OrThrow(
                await client.UsdFuturesApi.ExchangeData
                    .GetExchangeInfoAsync(cancellationToken)
                    .ConfigureAwait(false),
                "读取合约目录");

            List<ExchangeSymbolInfo> rows = [];

            foreach (Binance.Net.Objects.Models.Futures.BinanceFuturesUsdtSymbol item in info.Symbols)
            {
                // **不按计价币过滤**：同一个资产币在同一个交易所可以对应多个计价币
                // （实测币安就有 SPCXUSDT 与 SPCXUSD1），按 USDT 过滤会把它们全挡掉，
                // 于是"交易币"那一栏永远只有一个选项、看起来像个坏掉的控件。
                // 能不能交易由账户与交易所决定（下单时校验），不是这里先替使用者裁掉。
                if (item.Status != BinanceSymbolStatus.Trading)
                {
                    continue;
                }

                // 但**交割合约不能收**：实测 4 个（BTC/ETH 的当季与次季），它们会到期结算，
                // 与源的永续不是同一种东西；而到期前价格只与现货差几个点，价格守卫认不出它。
                // 留着它的后果是"跟的是永续、开出来的是三个月后要交割的合约"。
                //
                // 注意这条口径**不是**按计价币、也不是按"是不是加密"筛——早先按 PERPETUAL 一个
                // 命名空间筛，把 216 个股票/商品合约全挡在门外，那才是错的。永续的那两个命名空间都收。
                if (item.ContractType is not (BinanceContractType.Perpetual or BinanceContractType.PerpetualTradFi))
                {
                    continue;
                }

                rows.Add(new ExchangeSymbolInfo(
                    Symbol: item.Name,
                    BaseAsset: item.BaseAsset,
                    QuoteAsset: item.QuoteAsset,
                    ContractType: ContractTypeText(item.ContractType),

                    // 币安的口径：传统金融走 TRADIFI_PERPETUAL 这个命名空间（加密是 PERPETUAL）
                    IsTradFi: item.ContractType == BinanceContractType.PerpetualTradFi,
                    MinNotional: item.MinNotionalFilter?.MinNotional ?? 0m,
                    StepSize: item.LotSizeFilter?.StepSize ?? 0m,
                    QuantityPrecision: item.QuantityPrecision));
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

    /// <summary>
    /// 合约类型的**上游写法**。库给的是枚举（Perpetual / PerpetualTradFi），
    /// 但落进流水与界面的是上游原文——"PERPETUAL"/"TRADIFI_PERPETUAL" 这两个词
    /// 在 docs 与既有数据里已经用了一路，换一种写法等于悄悄改了对外口径。
    /// </summary>
    private static string ContractTypeText(BinanceContractType? type) => type switch
    {
        BinanceContractType.Perpetual => "PERPETUAL",
        BinanceContractType.PerpetualTradFi => "TRADIFI_PERPETUAL",
        null => string.Empty,
        _ => type.Value.ToString().ToUpperInvariant(),
    };

    public async Task<IReadOnlyDictionary<ExchangeSymbolInfo, decimal>> PricesAsync(
        CancellationToken cancellationToken = default)
    {
        // 不带 symbol 的行情端点一次返回全部合约价格，比逐个查便宜得多
        IReadOnlyList<Binance.Net.Objects.Models.Spot.BinancePrice> prices = BinanceClients.OrThrow(
            await client.UsdFuturesApi.ExchangeData
                .GetPricesAsync(cancellationToken)
                .ConfigureAwait(false),
            "读取合约价格");

        Dictionary<string, decimal> byCode = prices
            .GroupBy(p => p.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Price, StringComparer.Ordinal);

        // 对外按**合约对象**给价：上层不该拿字符串找合约（写法是一家之言，渗上去就会有人拼错）
        return (await ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => row, row => byCode.GetValueOrDefault(row.Symbol));
    }
}
