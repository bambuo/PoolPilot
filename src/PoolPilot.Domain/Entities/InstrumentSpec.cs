using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 合约规格（来自 <c>orderBookDetails</c> 与 <c>assetDetails</c>）。
///
/// 最小下单量是跟单的**物理约束**，而不是成本约束：实测 ENA 的
/// <c>min_quote_amount</c> 为 10 USDT，而某池单笔敞口占其资产的 1.2%——
/// 200 USDT 本金按同比例只有 2.39 USDT，**根本下不出单**。
/// </summary>
public sealed record InstrumentSpec
{
    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    /// <summary>最小下单金额（计价币）。</summary>
    public required decimal MinOrderNotional { get; init; }

    /// <summary>最小下单数量（基础币）。</summary>
    public required decimal MinOrderSize { get; init; }

    public required int SizeDecimals { get; init; }

    public required int PriceDecimals { get; init; }

    /// <summary>吃单费率（百分数，0.05 表示 0.05%）。实测 Standard 档为 0。</summary>
    public required decimal TakerFeePercent { get; init; }

    /// <summary>挂单费率（百分数）。</summary>
    public required decimal MakerFeePercent { get; init; }

    /// <summary>
    /// 维持保证金率（百分数）。上游 <c>maintenance_margin_fraction</c> 的整数刻度 ÷ 100。
    /// 纸面清算价要用它：爆仓由维持保证金触发，与初始保证金率（杠杆）不是同一个数。
    /// </summary>
    public required decimal MaintenanceMarginPercent { get; init; }

    /// <summary>往返吃单费率（百分数）。</summary>
    public decimal RoundTripTakerFeePercent => TakerFeePercent * 2m;
}
