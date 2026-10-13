using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Exceptions;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Infrastructure.Lighter;

/// <summary>
/// **库未覆盖的那一半 Lighter 读法**——按 CryptoClients.Net 的兜底条款自己维护。
///
/// 依据（读 JKorf/Lighter.Net 源码得到，不是推测）：它的 socket 只封了三个频道——
/// <c>account_all</c>、<c>account_all_assets</c>、<c>user_stats</c>；
/// REST 也只有 <c>/api/v1/account</c>、<c>/api/v1/orderBookDetails</c>、<c>/api/v1/orderBooks</c>、
/// <c>/api/v1/systemConfig</c> 这几条。而本项目池分析要的三样它一条都没有：
/// <list type="bullet">
///   <item><b>池枚举</b>：上游 <c>publicPoolsMetadata</c>（<c>/api/v1/account</c> 不支持游标枚举，
///     实测传 <c>index/limit</c> 返回 <c>code 20001 invalid param</c>）；</item>
///   <item><b>净值序列</b>：WS <c>pool_info</c>——<c>operator_shares</c> 与 <c>min_operator_share_rate</c>
///     只有这个频道提供；</item>
///   <item><b>池的成交与挂单</b>：WS <c>account_all_trades</c> / <c>account_all_orders</c>
///     （REST 的挂单接口免鉴权取不到：实测 403。而池是**别人的账户**，我们没有它的凭据）。</item>
/// </list>
/// 所以这几条继续自己发——用已实测的地址与字段口径，解析仍走
/// <see cref="LighterEntityParser"/>（一份解析规则，REST 与 WS 共用）。
/// 上游一旦变，本文件的注释与 <c>docs/LIGHTER-API-NOTES.md</c> 是排查的第一手材料。
/// </summary>
public sealed class LighterPoolDataExtension
{
    private const int PageSize = 100;

    /// <summary>分页的保护上限：防止上游游标异常导致死循环。</summary>
    private const int MaxPages = 200;

    private readonly HttpClient _http;
    private readonly LighterWebSocketClient _socket;

    public LighterPoolDataExtension(HttpClient http, LighterWebSocketClient socket)
    {
        _http = http;
        _socket = socket;
    }

    public async Task<IReadOnlyList<Pool>> FetchPoolsAsync(
        PoolFilter filter,
        CancellationToken cancellationToken = default)
    {
        // 起始游标取流动性池索引（实测为 281474976710654，是最高位的池索引）
        long cursor = await FetchLiquidityPoolCursorAsync(cancellationToken).ConfigureAwait(false);
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
                foreach (PoolTrade trade in Each(marketEntry.Value).Select(LighterEntityParser.ParseTrade))
                {
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
                foreach (PoolOrder order in Each(marketEntry.Value).Select(LighterEntityParser.ParseOrder))
                {
                    orders[order.OrderIndex] = order;
                }
            }
        }

        return [.. orders.Values.OrderBy(o => o.MarketIndex).ThenBy(o => o.Price.Value)];
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

    /// <summary>
    /// 池枚举的起始游标。取自上游 <c>systemConfig.liquidity_pool_index</c>——
    /// 这一条**故意不用库**：库的 systemConfig 模型没有这个字段以外的问题，但也没映射
    /// <c>fee_collector_account_index</c>，与其两处各读一半，不如把 systemConfig 整个交给库
    /// （见 LighterPoolsClient），这里只为游标取一次。
    /// 实际上它由 <see cref="ILighterPoolsClient.FetchSystemConfigAsync"/> 的调用方传入更干净——
    /// 保留独立读取是为了让本扩展**不依赖库**，从而能单独测试与单独替换。
    /// </summary>
    private async Task<long> FetchLiquidityPoolCursorAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await GetAsync("systemConfig", cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        LighterJson.EnsureSuccess(root, "systemConfig");

        return LighterJson.RequireLong(root, "liquidity_pool_index", "systemConfig");
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

    /// <summary>「市场索引 → 数组」映射里的一个值：可能是数组，也可能只有一个对象。</summary>
    private static IEnumerable<JsonElement> Each(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray()] : [value];

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
