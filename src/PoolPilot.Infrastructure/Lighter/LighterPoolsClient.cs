using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Exceptions;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Infrastructure.Lighter;

/// <summary>
/// Lighter 公开数据客户端。
///
/// REST 与 WebSocket 的分工（实测决定，非设计偏好）：
/// <list type="bullet">
///   <item><b>REST</b>：系统配置、池清单、账户快照、合约规格。</item>
///   <item><b>WebSocket</b>：净值序列（<c>pool_info</c>）、成交流水（<c>account_all_trades</c>）、
///     盘口（<c>order_book</c>）——这三样**没有 REST 等价物**。</item>
/// </list>
///
/// 为什么不轮询 REST 拿成交：<c>trades</c> 端点权重 200，而 Standard 档总配额只有
/// 60 加权请求/分钟，**连每分钟一次都跑不满**。
/// </summary>
public sealed class LighterPoolsClient : ILighterPoolsClient
{
    private const int PageSize = 100;

    /// <summary>分页的保护上限：防止上游游标异常导致死循环。</summary>
    private const int MaxPages = 200;

    private readonly HttpClient _http;
    private readonly LighterWebSocketClient _socket;

    public LighterPoolsClient(HttpClient http, LighterWebSocketClient socket)
    {
        _http = http;
        _socket = socket;
    }

    public async Task<LighterSystemConfig> FetchSystemConfigAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument document = await GetAsync("systemConfig", cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        LighterJson.EnsureSuccess(root, "systemConfig");

        return new LighterSystemConfig
        {
            LiquidityPoolIndex = LighterJson.RequireLong(root, "liquidity_pool_index", "systemConfig"),
            StakingPoolIndex = LighterJson.OptionalLong(root, "staking_pool_index"),
            FeeCollectorAccountIndex = LighterJson.OptionalLong(root, "fee_collector_account_index"),
        };
    }

    public async Task<IReadOnlyList<Pool>> FetchPoolsAsync(
        PoolFilter filter,
        CancellationToken cancellationToken = default)
    {
        LighterSystemConfig system = await FetchSystemConfigAsync(cancellationToken).ConfigureAwait(false);

        // 起始游标取流动性池索引（实测为 281474976710654，是最高位的池索引）
        long cursor = system.LiquidityPoolIndex;
        string filterValue = ToFilterValue(filter);

        Dictionary<long, Pool> collected = [];

        for (int page = 0; page < MaxPages && cursor > 0; page++)
        {
            string url = $"publicPoolsMetadata?index={cursor}&limit={PageSize}&filter={filterValue}";
            using JsonDocument document = await GetAsync(url, cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            LighterJson.EnsureSuccess(root, "publicPoolsMetadata");

            IReadOnlyList<JsonElement> items = LighterJson.OptionalArray(root, "public_pools");

            if (items.Count == 0)
            {
                break;
            }

            long minIndex = long.MaxValue;

            foreach (JsonElement item in items)
            {
                Pool pool = LighterEntityParser.ParsePool(item);
                collected[pool.AccountIndex.Value] = pool;
                minIndex = Math.Min(minIndex, pool.AccountIndex.Value);
            }

            // 关键：下一页游标取「本页最小索引」本身，靠索引去重。
            // 用 min-1 会在页边界漏池——症状是 filter=user 与 filter=all 的差集双向不对称。
            if (minIndex >= cursor || minIndex <= 0)
            {
                break;
            }

            cursor = minIndex;
        }

        return [.. collected.Values.OrderByDescending(p => p.TotalAssetValue.Value)];
    }

    public async Task<PoolSnapshot> FetchSnapshotAsync(
        AccountIndex account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        string url = $"account?by=index&value={account.Value}";
        using JsonDocument document = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        LighterJson.EnsureSuccess(root, "account");

        IReadOnlyList<JsonElement> accounts = LighterJson.RequireArray(root, "accounts", "account");

        if (accounts.Count == 0)
        {
            throw new InvalidLighterFieldException("account.accounts", "(空数组)", $"账户 {account} 无数据");
        }

        JsonElement element = accounts[0];

        return new PoolSnapshot
        {
            AccountIndex = account,
            TakenAt = LighterTimestamp.FromMicroseconds(LighterJson.OptionalLong(element, "transaction_time")),
            TotalAssetValue = UsdAmount.From(LighterJson.RequireDecimalText(element, "total_asset_value", "account")),
            Collateral = UsdAmount.From(LighterJson.RequireDecimalText(element, "collateral", "account")),
            AvailableBalance = UsdAmount.From(LighterJson.RequireDecimalText(element, "available_balance", "account")),
            InitialMarginRequirement = UsdAmount.From(LighterJson.RequireDecimalText(element, "cross_initial_margin_requirement", "account")),
            MaintenanceMarginRequirement = UsdAmount.From(LighterJson.RequireDecimalText(element, "cross_maintenance_margin_requirement", "account")),
            Positions = [.. LighterJson.OptionalArray(element, "positions").Select(LighterEntityParser.ParsePosition)],

            // 挂单在 REST 需要鉴权（实测 HTTP 400），只能从 WS 的 account_all_orders 取。
            // 快照里留空，由上层按需补全——静默给空数组会让"无挂单"与"没查挂单"混为一谈。
            Orders = [],
        };
    }

    public async Task<PoolInfo> FetchPoolInfoAsync(
        AccountIndex account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        IReadOnlyList<JsonElement> messages = await _socket
            .CollectAsync(
                [$"pool_info/{account.Value}"],
                cancellationToken: cancellationToken,
                stopWhen: LighterJson.IsSubscriptionSnapshot)
            .ConfigureAwait(false);

        foreach (JsonElement message in messages)
        {
            JsonElement? info = LighterJson.Optional(message, "pool_info");

            if (info is null || info.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            List<NavPoint> points = [];

            foreach (JsonElement price in LighterJson.OptionalArray(info.Value, "share_prices"))
            {
                points.Add(new NavPoint
                {
                    Timestamp = LighterTimestamp.FromSeconds(LighterJson.RequireLong(price, "timestamp", "share_prices")),
                    SharePrice = LighterJson.RequireNumber(price, "share_price", "share_prices"),
                });
            }

            return new PoolInfo
            {
                AccountIndex = account,
                TotalShares = LighterJson.OptionalLong(info.Value, "total_shares"),
                OperatorShares = LighterJson.OptionalLong(info.Value, "operator_shares"),
                MinOperatorShareRate = PercentValue.FromPercent(
                    LighterJson.OptionalDecimalText(info.Value, "min_operator_share_rate")),
                OperatorFee = PercentValue.FromPercent(
                    LighterJson.OptionalDecimalText(info.Value, "operator_fee")),
                ReportedAnnualPercentageYield = PercentValue.FromPercent(
                    LighterJson.OptionalNumber(info.Value, "annual_percentage_yield")),
                ReportedSharpe = LighterJson.OptionalNumber(info.Value, "sharpe_ratio"),
                Nav = new NavSeries { AccountIndex = account, Points = points },
            };
        }

        throw new InvalidLighterFieldException("pool_info", "(无快照)", $"账户 {account} 未返回池信息");
    }

    public async Task<IReadOnlyList<PoolTrade>> FetchRecentTradesAsync(
        AccountIndex account,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        IReadOnlyList<JsonElement> messages = await _socket
            .CollectAsync(
                [$"account_all_trades/{account.Value}"],
                cancellationToken: cancellationToken,
                stopWhen: LighterJson.IsSubscriptionSnapshot)
            .ConfigureAwait(false);

        Dictionary<long, PoolTrade> trades = [];

        foreach (JsonElement message in messages)
        {
            JsonElement? tradesContainer = LighterJson.Optional(message, "trades");

            if (tradesContainer is null || tradesContainer.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty marketEntry in tradesContainer.Value.EnumerateObject())
            {
                // WS 的 trades 是「市场索引 → 数组」映射，与 REST 的平铺数组不同
                IReadOnlyList<JsonElement> items = marketEntry.Value.ValueKind == JsonValueKind.Array
                    ? [.. marketEntry.Value.EnumerateArray()]
                    : [marketEntry.Value];

                foreach (JsonElement item in items)
                {
                    PoolTrade trade = LighterEntityParser.ParseTrade(item);
                    trades[trade.TradeId] = trade;
                }
            }
        }

        return [.. trades.Values.OrderByDescending(t => t.Timestamp.Utc).Take(limit)];
    }

    public async Task<IReadOnlyList<PoolOrder>> FetchOrdersAsync(
        AccountIndex account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        IReadOnlyList<JsonElement> messages = await _socket
            .CollectAsync(
                [$"account_all_orders/{account.Value}"],
                cancellationToken: cancellationToken,
                stopWhen: LighterJson.IsSubscriptionSnapshot)
            .ConfigureAwait(false);

        Dictionary<long, PoolOrder> orders = [];

        foreach (JsonElement message in messages)
        {
            JsonElement? container = LighterJson.Optional(message, "orders");

            if (container is null || container.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // WS 的 orders 是「市场索引 → 数组」映射，与 REST 的平铺数组不同
            foreach (JsonProperty marketEntry in container.Value.EnumerateObject())
            {
                IReadOnlyList<JsonElement> items = marketEntry.Value.ValueKind == JsonValueKind.Array
                    ? [.. marketEntry.Value.EnumerateArray()]
                    : [marketEntry.Value];

                foreach (JsonElement item in items)
                {
                    PoolOrder order = LighterEntityParser.ParseOrder(item);
                    orders[order.OrderIndex] = order;
                }
            }
        }

        return [.. orders.Values.OrderBy(o => o.MarketIndex).ThenBy(o => o.Price.Value)];
    }


    public async Task<IReadOnlyList<InstrumentSpec>> FetchInstrumentsAsync(
        CancellationToken cancellationToken = default)
    {
        using JsonDocument document = await GetAsync("orderBookDetails", cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        LighterJson.EnsureSuccess(root, "orderBookDetails");

        return [.. LighterJson.OptionalArray(root, "order_book_details").Select(LighterEntityParser.ParseInstrument)];
    }

    public async Task<OrderBookSnapshot> FetchOrderBookAsync(
        int marketId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JsonElement> messages = await _socket
            .CollectAsync(
                [$"order_book/{marketId}"],
                cancellationToken: cancellationToken,
                stopWhen: LighterJson.IsSubscriptionSnapshot)
            .ConfigureAwait(false);

        foreach (JsonElement message in messages)
        {
            // 盘口嵌在 order_book 对象里，不是顶层——顶层只有 channel / offset / type。
            // 实测教训：按顶层 asks 读会一条都匹配不到，静默走到"无快照"分支。
            // 该频道订阅时推一次完整快照，之后只推增减量，故取第一个带 asks 的消息即可。
            JsonElement? book = LighterJson.Optional(message, "order_book");

            if (book is null || book.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            IReadOnlyList<JsonElement> asks = LighterJson.OptionalArray(book.Value, "asks");

            if (asks.Count == 0)
            {
                continue;
            }

            long lastUpdatedMicros = LighterJson.OptionalLong(book.Value, "last_updated_at");

            return new OrderBookSnapshot
            {
                MarketId = marketId,
                Asks = [.. asks.Select(LighterEntityParser.ParseBookLevel)],
                Bids = [.. LighterJson.OptionalArray(book.Value, "bids").Select(LighterEntityParser.ParseBookLevel)],

                // last_updated_at 是微秒刻度
                TakenAt = lastUpdatedMicros > 0L
                    ? LighterTimestamp.FromMicroseconds(lastUpdatedMicros)
                    : LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            };
        }

        throw new InvalidLighterFieldException("order_book", "(无快照)", $"市场 {marketId} 未返回盘口");
    }





    private async Task<JsonDocument> GetAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http
            .GetAsync(relativeUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.MethodNotAllowed)
        {
            throw new LighterRateLimitedException(relativeUrl, (int)response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new LighterHttpException(relativeUrl, (int)response.StatusCode);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string ToFilterValue(PoolFilter filter) => filter switch
    {
        PoolFilter.User => "user",
        PoolFilter.Protocol => "protocol",
        PoolFilter.Stake => "stake",
        _ => "all",
    };
}

/// <summary>上游返回了限速状态码（429 或 405）。调用方应当退避后重试，而不是当成数据缺失。</summary>
public sealed class LighterRateLimitedException : Exception
{
    public LighterRateLimitedException(string endpoint, int statusCode)
        : base($"Lighter 限速：{endpoint} 返回 HTTP {statusCode}")
    {
        Endpoint = endpoint;
        StatusCode = statusCode;
    }

    public string Endpoint { get; }

    public int StatusCode { get; }
}

/// <summary>上游返回非成功状态码。</summary>
public sealed class LighterHttpException : Exception
{
    public LighterHttpException(string endpoint, int statusCode)
        : base($"Lighter 请求失败：{endpoint} 返回 HTTP {statusCode}")
    {
        Endpoint = endpoint;
        StatusCode = statusCode;
    }

    public string Endpoint { get; }

    public int StatusCode { get; }
}
