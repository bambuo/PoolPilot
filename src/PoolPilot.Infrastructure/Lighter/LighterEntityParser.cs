using System.Globalization;
using System.Text.Json;

using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Infrastructure.Lighter;

/// <summary>
/// 上游 JSON → 领域实体的共享解析器。
///
/// 单独成类的原因：REST 与 WebSocket 两条路径都要解析同样的实体（持仓、挂单、成交）。
/// 若各写一份，字段口径会出现两个实现——而本项目的全部风险恰恰集中在字段口径上
/// （四种时间刻度、两种保证金率刻度、字符串金额）。**只允许存在一份解析规则。**
/// </summary>
internal static class LighterEntityParser
{
    internal static Pool ParsePool(JsonElement element)
    {
        const string owner = "publicPools";

        return new Pool
        {
            AccountIndex = AccountIndex.From(LighterJson.RequireLong(element, "account_index", owner)),
            Name = LighterJson.OptionalString(element, "name"),
            L1Address = LighterJson.OptionalString(element, "l1_address"),

            // 账户类型码原样保留，不在解析层做归属判断——那是领域规则（Pool.Origin）
            AccountTypeCode = LighterJson.OptionalInt(element, "account_type"),
            TotalAssetValue = UsdAmount.From(LighterJson.RequireDecimalText(element, "total_asset_value", owner)),

            // 这三个字段本身就是百分数，不做任何缩放
            ReportedAnnualPercentageYield = PercentValue.FromPercent(
                LighterJson.OptionalNumber(element, "annual_percentage_yield")),
            ReportedSharpe = LighterJson.OptionalNumber(element, "sharpe_ratio"),
            OperatorFee = PercentValue.FromPercent(LighterJson.OptionalDecimalText(element, "operator_fee")),

            TotalShares = LighterJson.OptionalLong(element, "total_shares"),

            // 刻意不解析 operator_shares 与 min_operator_share_rate：
            // 实测 publicPoolsMetadata 的字段清单里没有它们，只存在于 WS 的 pool_info。
            // 在这里硬读会永远得到 0，让"读不到"与"真的是 0"无法区分。

            // 池的 created_at 是秒刻度（与 orderBookDetails 的毫秒字符串不同）
            CreatedAt = LighterTimestamp.FromSeconds(LighterJson.OptionalLong(element, "created_at")),
            StateCode = LighterJson.OptionalInt(element, "status"),
        };
    }

    internal static PoolOrder ParseOrder(JsonElement element)
    {
        const string owner = "orders";

        return new PoolOrder
        {
            OrderIndex = LighterJson.RequireLong(element, "order_index", owner),
            ClientOrderIndex = LighterJson.OptionalLong(element, "client_order_index"),
            MarketIndex = LighterJson.RequireInt(element, "market_index", owner),
            Side = LighterJson.OptionalBool(element, "is_ask") ? OrderSide.Sell : OrderSide.Buy,
            Price = PriceAmount.Parse(
                LighterJson.RequireDecimalText(element, "price", owner).ToString(CultureInfo.InvariantCulture)),
            RemainingBaseAmount = LighterJson.RequireDecimalText(element, "remaining_base_amount", owner),
            InitialBaseAmount = LighterJson.OptionalDecimalText(element, "initial_base_amount"),

            // 挂单的时间戳是秒刻度（与成交的毫秒 / 微秒都不同）
            CreatedAt = LighterTimestamp.FromSeconds(LighterJson.OptionalLong(element, "created_at")),

            // order_expiry 是毫秒
            ExpiresAt = LighterTimestamp.FromMilliseconds(LighterJson.OptionalLong(element, "order_expiry")),
            Status = LighterJson.OptionalString(element, "status", "open"),
        };
    }

    internal static PoolTrade ParseTrade(JsonElement element)
    {
        const string owner = "trades";

        return new PoolTrade
        {
            TradeId = LighterJson.RequireLong(element, "trade_id", owner),
            MarketId = LighterJson.RequireInt(element, "market_id", owner),
            Size = LighterJson.RequireDecimalText(element, "size", owner),
            Price = PriceAmount.Parse(
                LighterJson.RequireDecimalText(element, "price", owner).ToString(CultureInfo.InvariantCulture)),
            Usd = UsdAmount.From(LighterJson.RequireDecimalText(element, "usd_amount", owner)),
            IsMakerAsk = LighterJson.OptionalBool(element, "is_maker_ask"),
            AskAccountIndex = AccountIndex.From(LighterJson.RequireLong(element, "ask_account_id", owner)),
            BidAccountIndex = AccountIndex.From(LighterJson.RequireLong(element, "bid_account_id", owner)),

            // 两个时间字段刻度不同：timestamp 是毫秒，transaction_time 是微秒
            Timestamp = LighterTimestamp.FromMilliseconds(LighterJson.RequireLong(element, "timestamp", owner)),
            TransactionTime = LighterTimestamp.FromMicroseconds(LighterJson.RequireLong(element, "transaction_time", owner)),

            ExecutionType = LighterJson.OptionalString(element, "type", "trade"),
            MakerPositionSizeBefore = LighterJson.Optional(element, "maker_position_size_before") is null
                ? null
                : LighterJson.OptionalDecimalText(element, "maker_position_size_before"),
            TakerPositionSizeBefore = LighterJson.Optional(element, "taker_position_size_before") is null
                ? null
                : LighterJson.OptionalDecimalText(element, "taker_position_size_before"),
        };
    }

    internal static OrderBookLevel ParseBookLevel(JsonElement element) => new()
    {
        Price = PriceAmount.Parse(LighterJson.RequireString(element, "price", "order_book")),
        Size = LighterJson.RequireDecimalText(element, "size", "order_book"),
    };

}
