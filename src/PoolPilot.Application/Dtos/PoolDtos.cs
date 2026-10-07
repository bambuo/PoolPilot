namespace PoolPilot.Application.Dtos;

/// <summary>净值曲线上的一个点。</summary>
public sealed record NavPointDto
{
    public required DateTimeOffset At { get; init; }

    public required decimal SharePrice { get; init; }
}

/// <summary>
/// 净值指标，**并列上游点估计与实算值**。
///
/// 这个"并排"是本工作台的核心呈现方式：实测见过某池 API 报年化 7.37%、
/// 实算 34.21%，而 API 根本不报最大回撤（实算 −26.32%）——点估计同时低估收益与风险。
/// 把两者放在一起，偏差自己会说话。
/// </summary>
public sealed record NavMetricsDto
{
    // ── 上游点估计 ──
    public required decimal ReportedApyPercent { get; init; }

    public required decimal ReportedSharpe { get; init; }

    // ── 从净值序列实算 ──
    public required decimal RealTotalReturnPercent { get; init; }

    public required decimal RealAnnualizedReturnPercent { get; init; }

    /// <summary>最大回撤（百分数，负值）。**上游完全不提供这一项。**</summary>
    public required decimal RealMaxDrawdownPercent { get; init; }

    public required decimal RealSharpe { get; init; }

    public required decimal PeriodVolatilityPercent { get; init; }

    public required int LossPeriods { get; init; }

    public required int TotalPeriods { get; init; }

    public required int SpanDays { get; init; }

    /// <summary>样本是否足够支撑结论。false 时界面上应给出提示。</summary>
    public required bool IsStatisticallyMeaningful { get; init; }
}

/// <summary>持仓项。</summary>
public sealed record PoolPositionDto
{
    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    public required string Side { get; init; }

    public required decimal Size { get; init; }

    public required decimal AvgEntryPrice { get; init; }

    public required decimal Notional { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    /// <summary>
    /// 浮动盈亏率（**对保证金**，与官方前端同口径）。
    /// <c>null</c> = 上游未给出保证金率，算不出 ROE，界面以 - 占位。
    /// </summary>
    public required decimal? UnrealizedPnlPercent { get; init; }

    /// <summary>标记价（由名义价值 ÷ 数量反推，上游不单独给该字段）。</summary>
    public required decimal MarkPrice { get; init; }

    /// <summary>
    /// 距清算的幅度（百分数，符号自带方向：空头为正、多头为负）。
    /// <c>null</c> = 不存在正清算价。**不含杠杆**——爆仓由维持保证金触发，与杠杆无关。
    /// </summary>
    public required decimal? LiquidationDistancePercent { get; init; }

    /// <summary>该仓位的最大杠杆倍数（1 ÷ 保证金率）。</summary>
    /// <summary>
    /// 最大杠杆倍数（由仓位保证金率换算）。
    /// <c>null</c> = 上游此刻未给出有效保证金率（仓位变动瞬间的 "0.00"），不是 0 倍。
    /// </summary>
    public required decimal? Leverage { get; init; }

    /// <summary>该市场的未成交挂单数。</summary>
    public required int OpenOrderCount { get; init; }

    public required decimal CumulativeFunding { get; init; }

    /// <summary>强平价。为 null 表示不适用（上游返回 0）。</summary>
    /// <summary>
    /// 全仓口径的清算价：该仓位**单独**亏掉整个账户清算缓冲所需的单价
    /// （多头靠跌、空头靠涨），不是它自己的爆仓价。<c>null</c> = 不存在正的清算价
    /// （多头要亏掉缓冲所需的跌幅会超过价格本身，上游即返回 0）。
    /// </summary>
    public required decimal? LiquidationPrice { get; init; }
}

/// <summary>挂单项。</summary>
public sealed record PoolOrderDto
{
    public required long OrderIndex { get; init; }

    public required int MarketIndex { get; init; }

    /// <summary>市场符号。由合约规格表解析——订单载荷里只有 market_index、没有 symbol。</summary>
    public required string Symbol { get; init; }

    public required string Side { get; init; }

    public required decimal Price { get; init; }

    public required decimal RemainingSize { get; init; }

    public required decimal InitialSize { get; init; }

    public required decimal RemainingNotional { get; init; }

    public required bool IsPartiallyFilled { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>单轮交易（从建仓到清仓）。</summary>
public sealed record RoundTripDto
{
    public required int MarketId { get; init; }

    /// <summary>
    /// 市场符号。由合约规格表解析，**覆盖池子已平掉的历史市场**——
    /// 只从当前持仓取符号时，历史行会退化成 "market-60" 这种占位符。
    /// </summary>
    public required string Symbol { get; init; }

    public required string Side { get; init; }

    public required decimal EntrySize { get; init; }

    public required decimal EntryVwap { get; init; }

    public required decimal ExitVwap { get; init; }

    public required decimal EntryNotional { get; init; }

    public required decimal GrossPnl { get; init; }

    public required decimal ReturnPercent { get; init; }

    public required DateTimeOffset OpenedAt { get; init; }

    public required DateTimeOffset ClosedAt { get; init; }

    public required string EntryRole { get; init; }

    public required string ExitRole { get; init; }

    public required bool IsClosed { get; init; }

    public required double HoldingMinutes { get; init; }
}

/// <summary>成交构成概要。判断"池子是挂单型还是吃单型"的依据。</summary>
public sealed record TradeCompositionDto
{
    public required int TradeCount { get; init; }

    public required int MakerCount { get; init; }

    public required int TakerCount { get; init; }

    public required decimal MakerSharePercent { get; init; }

    public required decimal BoughtNotional { get; init; }

    public required decimal SoldNotional { get; init; }

    public required decimal MedianTradeNotional { get; init; }
}

/// <summary>池快照概要。</summary>
public sealed record PoolSnapshotDto
{
    public required DateTimeOffset TakenAt { get; init; }

    public required decimal TotalAssetValue { get; init; }

    public required decimal Collateral { get; init; }

    public required decimal AvailableBalance { get; init; }

    public required decimal GrossExposure { get; init; }

    public required decimal NetExposure { get; init; }

    public required decimal ExposureRatioPercent { get; init; }

    /// <summary>多空失衡度。0 为市场中性，1 为完全单边。</summary>
    public required decimal ImbalanceRatio { get; init; }

    public required decimal InitialMarginRequirement { get; init; }

    public required decimal MaintenanceMarginRequirement { get; init; }

    public required decimal LiquidationBuffer { get; init; }

    public required int ActivePositionCount { get; init; }
}

/// <summary>池详情：一次请求给出观察该池所需的全部信息。</summary>
public sealed record PoolDetailDto
{
    public required PoolSummaryDto Summary { get; init; }

    /// <summary>运营者份额结构。来自 WS <c>pool_info</c>，池清单接口不提供。</summary>
    public required decimal OperatorSharePercent { get; init; }

    public required decimal MinOperatorSharePercent { get; init; }

    /// <summary>运营者份额是否仅略高于最低要求（利益绑定偏松）。</summary>
    /// <summary>
    /// 入金是否已被限制（运营者份额触到下限）。官方原文：
    /// "Deposits are limited because the operator share has reached its minimum limit."
    /// </summary>
    public required bool IsDepositLimited { get; init; }

    /// <summary>份额占比高出下限多少个百分点。0 表示正好压线（此时入金已受限）。</summary>
    public required decimal OperatorShareHeadroomPercent { get; init; }

    public required NavMetricsDto Metrics { get; init; }

    public required IReadOnlyList<NavPointDto> NavCurve { get; init; }

    public required PoolSnapshotDto Snapshot { get; init; }

    public required IReadOnlyList<PoolPositionDto> Positions { get; init; }

    /// <summary>
    /// 挂单列表。**REST 需要鉴权，此数据来自 WS 的 <c>account_all_orders</c>。**
    /// 取不到时为空数组，界面上应显示"未订阅"而不是"无挂单"。
    /// </summary>
    public required IReadOnlyList<PoolOrderDto> Orders { get; init; }

    public required IReadOnlyList<RoundTripDto> RoundTrips { get; init; }

    public required TradeCompositionDto TradeComposition { get; init; }

    /// <summary>本市场的主交易标的（用于默认展示盘口与规模门槛）。</summary>
    public required int? PrimaryMarketId { get; init; }
}
