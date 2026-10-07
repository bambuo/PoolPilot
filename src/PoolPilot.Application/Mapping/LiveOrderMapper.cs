using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;

namespace PoolPilot.Application.Mapping;

/// <summary>实盘流水实体的 DTO 化。**只有这一份**：写入路径（下单、手动平仓）与读取路径
/// 各写一份的话，同一笔记录在两个界面上的字段迟早会不一样。</summary>
public static class LiveOrderMapper
{
    /// <param name="exchangeName">交易所配置的显示名。调用方手上没有时传 null。</param>
    public static LiveOrderDto ToDto(LiveOrder order, string? exchangeName) => new()
    {
        Id = order.Id,
        FollowerId = order.FollowerId,
        ExchangeConfigId = order.ExchangeConfigId,
        ExchangeName = exchangeName,
        Platform = order.Platform,
        SourceSymbol = order.SourceSymbol,
        TargetBase = order.TargetBase,
        TargetQuote = order.TargetQuote,
        TargetPair = $"{order.TargetBase}/{order.TargetQuote}",
        Intent = order.Intent,
        Side = order.Side,
        PositionSide = order.PositionSide,
        Quantity = order.Quantity,
        Price = order.Price,
        EntryPrice = order.EntryPrice,
        ClosePrice = order.ClosePrice,
        NotionalUsd = order.NotionalUsd,
        PoolNotional = order.PoolNotional,
        TargetNotionalUsd = order.TargetNotionalUsd,
        PoolEquity = order.PoolEquity,
        Capital = order.Capital,
        Trigger = order.Trigger,
        Status = order.Status,
        Reason = order.Reason,
        VenueOrderId = order.VenueOrderId,
        ClientOrderId = order.ClientOrderId,
        CreatedAt = order.CreatedAt.Utc,
    };
}
