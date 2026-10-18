namespace PoolPilot.Application.Dtos;

// ── 公开面（/api/public/*）的窄 DTO ────────────────────────────
//
// **为什么不复用工作台的 FollowerSummaryDto / FollowerDto**（这两个类型带着
// exchangeName / exchangeConfigId / availableBalance / livePositions）：
// 公开端点面向匿名访客，那些是"某个实盘账号里有什么"的信息，一行都不该出去。
// 复用一次就等于把"以后往私有 DTO 上加字段"变成"顺手对全世界公开"——
// 窄 DTO 让这件事必须经过一次显形的映射改动才可能发生。
// 账的算法仍然只有一份：这些类型由 PublicPageMapper 从既有的装配结果裁剪而来。

/// <summary>
/// 公开带单员的详情（来自池详情，但**只保留匿名观察者需要的部分**）。
///
/// 不下发挂单、逐笔成交、清算价、运营者钱包地址与逐仓入场价：那些要么是交易执行细节，
/// 要么会把池子的即时意图暴露给所有人。留下的是"这个池值不值得跟"的四个面：
/// 收益、风险、敞口、前几大持仓。
/// </summary>
public sealed record PublicLeadTraderDetailDto
{
    // ── 带单员（观察关系）────────────────────────────────
    public required long AccountIndex { get; init; }

    public required string Name { get; init; }

    /// <summary><c>lighter</c> / <c>community</c>。</summary>
    public required string Origin { get; init; }

    public required bool IsFrozen { get; init; }

    public required bool IsWatching { get; init; }

    public required int FollowerCount { get; init; }

    public required int SignalCount { get; init; }

    public required DateTimeOffset? LastSignalAt { get; init; }

    /// <summary>带单员（观察关系）建立的时刻。</summary>
    public required DateTimeOffset ObservedSince { get; init; }

    /// <summary>池本身在上游创建的时刻。</summary>
    public required DateTimeOffset PoolCreatedAt { get; init; }

    // ── 规模与运营 ───────────────────────────────────────
    public required decimal TotalAssetValue { get; init; }

    public required long TotalShares { get; init; }

    /// <summary>最新份额净值；净值序列为空时为 null。</summary>
    public required decimal? SharePrice { get; init; }

    public required DateTimeOffset? NavAt { get; init; }

    public required decimal OperatorFeePercent { get; init; }

    public required decimal OperatorSharePercent { get; init; }

    public required decimal MinOperatorSharePercent { get; init; }

    public required bool IsDepositLimited { get; init; }

    public required PublicLeadMetricsDto Metrics { get; init; }

    public required PublicLeadExposureDto Exposure { get; init; }

    /// <summary>按名义额降序的前几大持仓。</summary>
    public required IReadOnlyList<PublicLeadPositionDto> Positions { get; init; }

    /// <summary>这些持仓市场的未成交挂单；只用于持仓行展开区，不单列成行情阶梯。</summary>
    public required IReadOnlyList<PublicLeadOrderDto> Orders { get; init; }

    /// <summary>最近一段净值曲线（按时间升序，已截断）。</summary>
    public required IReadOnlyList<PublicLeadNavPointDto> NavCurve { get; init; }
}

/// <summary>池的收益与风险指标；上报值与实算值并列，口径与工作台池详情一致。</summary>
public sealed record PublicLeadMetricsDto
{
    public required decimal ReportedApyPercent { get; init; }

    public required decimal ReportedSharpe { get; init; }

    public required decimal RealTotalReturnPercent { get; init; }

    public required decimal RealAnnualizedReturnPercent { get; init; }

    public required decimal RealMaxDrawdownPercent { get; init; }

    public required decimal RealSharpe { get; init; }

    public required decimal PeriodVolatilityPercent { get; init; }

    public required int LossPeriods { get; init; }

    public required int TotalPeriods { get; init; }

    public required int SpanDays { get; init; }

    public required bool IsStatisticallyMeaningful { get; init; }
}

/// <summary>池的当前敞口；清算缓冲是账户级风险，不含任何仓位级清算价。</summary>
public sealed record PublicLeadExposureDto
{
    public required decimal GrossExposure { get; init; }

    public required decimal NetExposure { get; init; }

    public required decimal ExposureRatioPercent { get; init; }

    public required decimal ImbalanceRatio { get; init; }

    public required decimal LiquidationBuffer { get; init; }

    public required int ActivePositionCount { get; init; }
}

/// <summary>
/// 公开详情里的前几大持仓。
///
/// 带单广场与工作台共用同一套展示列：开仓价、标记价、清算价、已实现/未实现盈亏
/// 与投资回报率。公开侧仍只取前几大，不把整本账摊开。
/// </summary>
public sealed record PublicLeadPositionDto
{
    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    /// <summary><c>long</c> / <c>short</c>。</summary>
    public required string Side { get; init; }

    public required decimal AvgEntryPrice { get; init; }

    public required decimal MarkPrice { get; init; }

    /// <summary>没有正清算价时为 null（全仓保证金下的结构性结果，不是缺失）。</summary>
    public required decimal? LiquidationPrice { get; init; }

    public required decimal Notional { get; init; }

    /// <summary>占池子总敞口的比例（百分数）。</summary>
    public required decimal WeightPercent { get; init; }

    /// <summary>近端成交窗口内已平仓轮次的毛利合计；上游持仓不提供逐仓已实现盈亏。</summary>
    public required decimal RealizedPnl { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    /// <summary>未实现盈亏 ÷ 保证金；取不到保证金率时为 null。</summary>
    public required decimal? UnrealizedPnlPercent { get; init; }
}

/// <summary>公开详情里当前持仓市场的挂单（只服务持仓行的展开区）。</summary>
public sealed record PublicLeadOrderDto
{
    public required int MarketId { get; init; }

    public required long OrderIndex { get; init; }

    /// <summary><c>buy</c> / <c>sell</c>。</summary>
    public required string Side { get; init; }

    public required decimal Price { get; init; }

    public required decimal RemainingSize { get; init; }

    public required decimal RemainingNotional { get; init; }

    public required bool IsPartiallyFilled { get; init; }
}

/// <summary>公开详情里的净值点。</summary>
public sealed record PublicLeadNavPointDto
{
    public required DateTimeOffset At { get; init; }

    public required decimal SharePrice { get; init; }
}

/// <summary>
/// 公开面两页各自的开关状态（匿名可读，即使两页都关也回 200）。
///
/// 前端需要它来区分"这一页没开放"与"实时流正在连接"：EventSource 看不到 404 的响应体，
/// 没有这个读口时，默认关闭的部署里匿名访客会永远停在"实时数据连接中…"。
/// </summary>
public sealed record PublicPageStatusDto
{
    public required bool Leads { get; init; }

    public required bool Followers { get; init; }

    /// <summary>
    /// 是否允许登录/注册。公开-only 部署会把它设为 false，前端据此隐藏入口并拦下认证页。
    /// </summary>
    public required bool AccountsEnabled { get; init; }
}

/// <summary>公开带单页的一行（G1）。</summary>
public sealed record PublicLeadTraderDto
{
    /// <summary>被观察的池的账户索引。</summary>
    public required long AccountIndex { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// 此刻是否真的在收这个池的推送。
    ///
    /// 公开侧只给这一个判断，**不给 isEnabled**：那个是"有人把它关了"的记录，
    /// 而公开页既不能关也不能开，把它放出去只会引来"怎么点了没反应"。
    /// </summary>
    public required bool IsWatching { get; init; }

    /// <summary>订阅它的跟单员数量（纸面 + 实盘，工作台建的也算——那是这个池的热度）。</summary>
    public required int FollowerCount { get; init; }

    /// <summary>累计信号数。</summary>
    public required int SignalCount { get; init; }

    /// <summary>最近一条信号的时刻；从未有过信号时为 <c>null</c>。</summary>
    public required DateTimeOffset? LastSignalAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// 池名候选的一项（G4）。
///
/// 带 <see cref="TotalAssetValue"/> 是为了与工作台的创建弹窗保持一致：选池时
/// "这个池多大"与"已经有多少人在跟"是决定值不值得观察的两个参照。
/// </summary>
public sealed record PublicPoolOptionDto
{
    public required long AccountIndex { get; init; }

    public required string Name { get; init; }

    /// <summary>池子总资产（USDC）。与工作台池列表同源。</summary>
    public required decimal TotalAssetValue { get; init; }
}

/// <summary>
/// 公开跟单页的一行（G2）。
///
/// 与工作台卡片的差别只在**出去的字段**：没有交易所、没有实盘余额与持仓，
/// 多一个 <c>label</c>（创建者自己起的名字）与 <c>isOwnedByCaller</c>
/// （决定"删除"按钮给不给，判据与删除端点同源，界面因此不会点了才 403）。
/// </summary>
public sealed record PublicFollowerSummaryDto
{
    public required long Id { get; init; }

    /// <summary>创建者起的名字；没起时为 <c>null</c>。</summary>
    public required string? Label { get; init; }

    public required long LeadAccountIndex { get; init; }

    public required string LeadName { get; init; }

    /// <summary>带单员是否在观察。为 <c>false</c> 时卡片要标出"上游已暂停"。</summary>
    public required bool LeadIsEnabled { get; init; }

    /// <summary>这个跟单员是否在跟随。</summary>
    public required bool IsActive { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>这一行是否属于当前调用者（会话用户相等，或访客密钥派生的哈希对得上）。</summary>
    public required bool IsOwnedByCaller { get; init; }

    /// <summary>
    /// 估值是否可得。为 <c>false</c> 时下面所有估值字段都没有意义，
    /// 界面必须显示"估值不可得"而不是把 0 渲染成"刚好打平"。
    /// </summary>
    public required bool IsValuationAvailable { get; init; }

    public required decimal InitialCapital { get; init; }

    public required decimal Equity { get; init; }

    public required decimal ReturnPercent { get; init; }

    public required decimal RealizedPnl { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    /// <summary>累计手续费。卡片不显示，行情事件补丁用它重算权益。</summary>
    public required decimal Fees { get; init; }

    public required decimal GrossNotional { get; init; }

    /// <summary>规模比例 = 本金 / 池子权益。</summary>
    public required decimal Scale { get; init; }

    public required decimal PoolEquity { get; init; }

    public required int FillCount { get; init; }

    /// <summary>因约束被跳过的信号数。不为 0 时卡片要显著标出——那是"没跟上"。</summary>
    public required int SkipCount { get; init; }

    public required IReadOnlyList<PublicPositionBriefDto> Positions { get; init; }
}

/// <summary>卡片上的持仓摘要（G2）。</summary>
public sealed record PublicPositionBriefDto
{
    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    /// <summary><c>long</c> / <c>short</c>；空仓时为 <c>null</c>。</summary>
    public required string? Side { get; init; }

    public required decimal Size { get; init; }

    /// <summary>开仓均价。卡片不显示，但行情事件补丁按它重算未实现盈亏。</summary>
    public required decimal EntryVwap { get; init; }

    public required decimal Notional { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    /// <summary>
    /// 源池的初始保证金率（百分数）。公开卡片据它换算标的杠杆（100 ÷ 保证金率）；
    /// 取不到时为 null，卡片显示 — 而不是编一个杠杆。
    /// </summary>
    public required decimal? InitialMarginPercent { get; init; }
}

/// <summary>
/// 公开跟单详情页（G3）= G2 的全部字段 + 模式、完整持仓、最近若干条流水与权益曲线。
///
/// <see cref="Positions"/> 在这里换成完整形状（卡片上只有摘要），
/// <see cref="Fills"/> 只给最近 <c>PublicPageLimits.FillsShownOnDetail</c> 条：
/// 详情页要回答"这个纸面账现在长什么样、最近跟了哪几笔"，
/// 而全量流水是工作台的复盘材料，没有理由对匿名访客敞开。
/// </summary>
public sealed record PublicFollowerDto
{
    public required long Id { get; init; }

    public required string? Label { get; init; }

    public required long LeadAccountIndex { get; init; }

    public required string LeadName { get; init; }

    public required bool LeadIsEnabled { get; init; }

    public required bool IsActive { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required bool IsOwnedByCaller { get; init; }

    /// <summary>恒为 <c>paper</c>：公开面只有纸面跟单（实盘一律在建行前就被拒）。</summary>
    public required string Mode { get; init; }

    public required bool IsValuationAvailable { get; init; }

    public required decimal InitialCapital { get; init; }

    public required decimal Equity { get; init; }

    public required decimal ReturnPercent { get; init; }

    public required decimal RealizedPnl { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    /// <summary>累计手续费（纸面账重放出来的）。与工作台详情的「累计手续费」同一口径。</summary>
    public required decimal Fees { get; init; }

    public required decimal GrossNotional { get; init; }

    public required decimal Scale { get; init; }

    public required decimal PoolEquity { get; init; }

    public required int FillCount { get; init; }

    public required int SkipCount { get; init; }

    /// <summary>取不到盯市价的市场。这些市场的未实现盈亏**不可得**，不是 0。</summary>
    public required IReadOnlyList<int> MarksUnavailable { get; init; }

    public required IReadOnlyList<PublicPositionDto> Positions { get; init; }

    public required IReadOnlyList<PublicFillDto> Fills { get; init; }

    public required IReadOnlyList<PublicEquityPointDto> Curve { get; init; }
}

/// <summary>详情页的持仓（G3）：带开仓均价、盯市价与跟踪偏差，但不带池子侧的对照量。</summary>
public sealed record PublicPositionDto
{
    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    /// <summary><c>long</c> / <c>short</c>；空仓时为 <c>null</c>。</summary>
    public required string? Side { get; init; }

    public required decimal Size { get; init; }

    public required decimal EntryVwap { get; init; }

    public required decimal MarkPrice { get; init; }

    /// <summary>是否取到了盯市价。为 <c>false</c> 时未实现盈亏按开仓均价估（即 0），必须标注出来。</summary>
    public required bool HasMark { get; init; }

    public required decimal Notional { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    public required decimal RealizedPnl { get; init; }

    /// <summary>源池初始保证金率（百分数）；取不到时为 null。</summary>
    public required decimal? InitialMarginPercent { get; init; }

    /// <summary>保证金占用额；取不到时为 null。</summary>
    public required decimal? MarginAmount { get; init; }

    /// <summary>维持保证金率（百分数）；行情补丁重算清算价用。</summary>
    public required decimal MaintenanceMarginPercent { get; init; }

    /// <summary>未实现盈亏 ÷ 保证金（百分数）；不可得时为 null。</summary>
    public required decimal? UnrealizedPnlPercent { get; init; }

    /// <summary>账户级清算价；不存在正数解时为 null。</summary>
    public required decimal? LiquidationPrice { get; init; }

    /// <summary>距清算幅度（百分数，空头为正、多头为负）。</summary>
    public required decimal? LiquidationDistancePercent { get; init; }

    /// <summary>跟踪偏差占目标的比例（百分数）。目标为 0 时为 0。</summary>
    public required decimal TrackingDeviationPercent { get; init; }
}

/// <summary>详情页的一条纸面动作（成交或被跳过）。</summary>
public sealed record PublicFillDto
{
    public required long Id { get; init; }

    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    /// <summary><c>buy</c> / <c>sell</c>；被跳过时为 <c>null</c>。</summary>
    public required string? Side { get; init; }

    public required decimal Size { get; init; }

    public required decimal Price { get; init; }

    public required decimal Notional { get; init; }

    public required decimal Fee { get; init; }

    public required decimal RealizedPnl { get; init; }

    /// <summary>这笔动作对应的保证金率（百分数）；旧流水没有记录且源池无该仓时为 null。</summary>
    public required decimal? InitialMarginPercent { get; init; }

    /// <summary>是否缩小了敞口（平仓/减仓）。</summary>
    public required bool IsReducing { get; init; }

    /// <summary>是否真的成交。</summary>
    public required bool IsFilled { get; init; }

    /// <summary>被跳过的原因（<c>below_minimum_size</c> / <c>below_minimum_notional</c> / <c>no_liquidity</c>）。</summary>
    public required string? SkipReason { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }
}

/// <summary>权益曲线上的一个点（G3）。独立类型：私有曲线以后加字段不该顺带公开出去。</summary>
public sealed record PublicEquityPointDto
{
    public required DateTimeOffset At { get; init; }

    public required decimal Equity { get; init; }

    public required decimal RealizedPnl { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    public required decimal GrossNotional { get; init; }
}

/// <summary>
/// 公开页新建跟单员（W2）的请求。
///
/// 比工作台的 <see cref="CreateFollowerRequest"/> 多 <see cref="Label"/>，
/// 而 <see cref="ExchangeConfigId"/> 只为"把拒绝的理由说清楚"留在契约上：
/// 前端误传时应当收到一句明确的 400，而不是被静默改写成纸面。
/// </summary>
public sealed record CreatePublicFollowerRequest
{
    /// <summary>要订阅的带单员（池账户索引）。该池没有带单员时回 400——先到公开带单页建立观察。</summary>
    public required long LeadAccountIndex { get; init; }

    /// <summary>本金（USDT）。必须落在公开页允许的区间内。</summary>
    public required decimal InitialCapital { get; init; }

    /// <summary>给这张卡片起的名字。可空；超长或含控制字符会被拒。</summary>
    public string? Label { get; init; }

    /// <summary>只接受不传或 <c>paper</c>；<c>live</c> 明确拒绝。</summary>
    public string? Mode { get; init; }

    /// <summary>公开面没有实盘：非 <c>null</c> 一律 400。</summary>
    public long? ExchangeConfigId { get; init; }
}

/// <summary>W3 的三档结论。404 与 403 的区别要留给调用方（Api）去映射成状态码。</summary>
public enum PublicFollowerDeleteOutcome
{
    /// <summary>是公开行、创建者就是调用者，已删除。</summary>
    Deleted,

    /// <summary>行不存在，或存在但不是公开行（一律 404：不给"存在但看不到"的探针）。</summary>
    NotFound,

    /// <summary>存在且公开，但创建者不是调用者。</summary>
    Forbidden,
}
