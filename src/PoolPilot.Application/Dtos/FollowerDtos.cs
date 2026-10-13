namespace PoolPilot.Application.Dtos;

/// <summary>
/// 跟单员的完整状态。
///
/// 这一组 DTO 存在的意义就是回答一个问题：**跟着这个带单员做，我的钱会变成多少**。
/// 它和执行成本报告（<see cref="SlippageReportDto"/>）的分工是：
/// 后者量的是"每一腿差几个基点"，前者给的是完整账户会计——仓位、盯市、盈亏、权益曲线。
/// </summary>
public sealed record FollowerDto
{
    /// <summary>跟单员自身的标识（自增）。同一个带单员下可以有多个跟单员，各自一份本金。</summary>
    public required long Id { get; init; }

    /// <summary>订阅的带单员（池账户索引）。</summary>
    public required long LeadAccountIndex { get; init; }

    public required string LeadName { get; init; }

    /// <summary>带单员是否在观察。为 <c>false</c> 时这个跟单员不会再有新动作。</summary>
    public required bool LeadIsEnabled { get; init; }

    /// <summary><c>paper</c> / <c>live</c>。</summary>
    public required string Mode { get; init; }

    /// <summary>实盘用的交易所配置；纸面为 null。</summary>
    public required long? ExchangeConfigId { get; init; }

    /// <summary>交易所配置的显示名（"币安 主账号"），便于在列表上认人。</summary>
    public required string? ExchangeName { get; init; }

    public required decimal InitialCapital { get; init; }

    public required bool IsActive { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    // ── 估值 ──────────────────────────────────────────────

    /// <summary>权益 = 本金 + 已实现 + 未实现 − 手续费。</summary>
    public required decimal Equity { get; init; }

    public required decimal RealizedPnl { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    public required decimal Fees { get; init; }

    /// <summary>收益率（百分数），相对初始本金。</summary>
    public required decimal ReturnPercent { get; init; }

    public required decimal GrossNotional { get; init; }

    // ── 对照基准 ──────────────────────────────────────────

    /// <summary>池子权益，规模比例的分母。存下来才解释得清"为什么跟这么少"。</summary>
    public required decimal PoolEquity { get; init; }

    /// <summary>规模比例 = 本金 / 池子权益。</summary>
    public required decimal Scale { get; init; }

    /// <summary>已成交笔数。</summary>
    public required int FillCount { get; init; }

    /// <summary>因约束被跳过的信号数。**这个数字比盈亏更能说明问题**：本金不足时它会很大。</summary>
    public required int SkipCount { get; init; }

    /// <summary>
    /// 取不到盯市价的市场。这些市场的未实现盈亏**不可得**，不是 0。
    /// 界面必须显式区分这两者——0 看起来像"刚好打平"，而事实是"不知道"。
    /// </summary>
    public required IReadOnlyList<int> MarksUnavailable { get; init; }

    public required IReadOnlyList<PaperPositionDto> Positions { get; init; }

    public required IReadOnlyList<PaperFillDto> Fills { get; init; }

    public required IReadOnlyList<PaperEquityPointDto> Curve { get; init; }
}

/// <summary>纸面持仓，含与池子的跟踪偏差。</summary>
public sealed record PaperPositionDto
{
    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    /// <summary><c>long</c> / <c>short</c>；空仓时为 <c>null</c>。</summary>
    public required string? Side { get; init; }

    /// <summary>我方带符号数量。</summary>
    public required decimal Size { get; init; }

    public required decimal EntryVwap { get; init; }

    public required decimal MarkPrice { get; init; }

    /// <summary>是否取到了盯市价。为 <c>false</c> 时未实现盈亏按开仓均价估（即 0），必须标注出来。</summary>
    public required bool HasMark { get; init; }

    public required decimal Notional { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    public required decimal RealizedPnl { get; init; }

    // ── 跟踪偏差 ──────────────────────────────────────────

    /// <summary>池子在该市场的带符号仓位。</summary>
    public required decimal PoolSize { get; init; }

    /// <summary>按比例复制、并量化到交易所步长后，我方**应该**持有的数量。</summary>
    public required decimal TargetSize { get; init; }

    /// <summary>我方实际持仓 − 目标持仓。差额来自被跳过的信号累积，即颗粒度造成的跟踪误差。</summary>
    public required decimal TrackingDeviation { get; init; }

    /// <summary>跟踪偏差占目标的比例（百分数）。目标为 0 时为 0。</summary>
    public required decimal TrackingDeviationPercent { get; init; }
}

/// <summary>一条纸面动作（成交或被跳过）。</summary>
public sealed record PaperFillDto
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

    /// <summary>是否真的成交。</summary>
    public required bool IsFilled { get; init; }

    /// <summary>被跳过的原因（<c>below_minimum_size</c> / <c>below_minimum_notional</c> / <c>no_liquidity</c>）。</summary>
    public required string? SkipReason { get; init; }

    public required bool DepthSufficient { get; init; }

    /// <summary>池子触发本次动作后的仓位。</summary>
    public required decimal PoolPositionAfter { get; init; }

    public required decimal PositionBefore { get; init; }

    public required decimal PositionAfter { get; init; }

    public required decimal TargetPosition { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }
}

/// <summary>权益曲线上的一个点。</summary>
public sealed record PaperEquityPointDto
{
    public required DateTimeOffset At { get; init; }

    public required decimal Equity { get; init; }

    public required decimal RealizedPnl { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    public required decimal GrossNotional { get; init; }
}

/// <summary>开始跟单（新建一个跟单员）的请求。</summary>
public sealed record CreateFollowerRequest
{
    /// <summary>要订阅的带单员（池账户索引）。该池若还没有带单员，会自动补一个。</summary>
    public required long LeadAccountIndex { get; init; }

    /// <summary>投入的本金（USDC）。必须为正。</summary>
    public required decimal InitialCapital { get; init; }

    /// <summary><c>paper</c> / <c>live</c>；不传按纸面处理。</summary>
    public string? Mode { get; init; }

    /// <summary>
    /// 实盘要用的交易所配置 Id。<c>mode=live</c> 时必填——下单必须落到**某一个账号**上。
    ///
    /// 与标的映射刻意按平台存形成对照：映射是"哪个资产对应哪个合约"的知识，与账号无关；
    /// 而钱在哪本账上是账户事实，必须有具体的一条。
    /// </summary>
    public long? ExchangeConfigId { get; init; }
}

/// <summary>跟单员的可变字段。</summary>
public sealed record UpdateFollowerRequest
{
    /// <summary>投入的本金（USDC）。必须为正；改动会清空该跟单员的历史。</summary>
    public required decimal InitialCapital { get; init; }

    /// <summary>是否在跟随。</summary>
    public required bool IsActive { get; init; }
}

/// <summary>
/// 跟单员的卡片摘要。
///
/// 与 <see cref="FollowerDto"/> 的分工：本类型只带**一眼判断这个跟单好不好**所需的字段，
/// 刻意不含流水与权益曲线——那些是点进详情才要看的。卡片列表若把每个跟单员的完整流水
/// 都带上，跟单员一多响应就会失控增长，而卡片根本不显示它们。
/// </summary>
public sealed record FollowerSummaryDto
{
    public required long Id { get; init; }

    public required long LeadAccountIndex { get; init; }

    public required string LeadName { get; init; }

    /// <summary>带单员是否在观察。为 <c>false</c> 时卡片要标出"上游已暂停"。</summary>
    public required bool LeadIsEnabled { get; init; }

    /// <summary><c>paper</c> / <c>live</c>。</summary>
    public required string Mode { get; init; }

    /// <summary>实盘所在的交易所配置显示名；纸面为 null。</summary>
    public required string? ExchangeName { get; init; }

    /// <summary>
    /// 实盘所用的交易所配置 Id；纸面为 null。
    ///
    /// 交易所页靠它回答"这条配置现在有没有人在用"。**不能按显示名关联**：
    /// 名字是使用者起的，后端不保证唯一——同名两条会把关系对错，而错的后果是
    /// "停用一条正在被实盘使用的配置"看起来毫无影响。
    /// </summary>
    public required long? ExchangeConfigId { get; init; }

    /// <summary>
    /// 实盘的可用保证金（交易所给的）。纸面为 null。
    ///
    /// 与"本金"是两回事：本金是**规模基准**（跟多大仓），这个是**账户里能用的钱**。
    /// 混着显示会让人以为"我投了 1 块，账户里却有 181"是赚来的——
    /// 那个账号里可能还放着与这次跟单无关的资金。
    /// </summary>
    public required decimal? AvailableBalance { get; init; }

    /// <summary>
    /// 实盘账户当前持有的仓位，**交易所给的**。纸面跟单员为空
    /// （它的持仓在 <c>Positions</c> 里，那是按纸面账算出来的，两者口径不同不可混用）。
    ///
    /// 曾经省略这一段，于是卡片上写着「当前空仓」，而旁边就是「持仓名义 $17.75」——
    /// 一句明显的假话，而且正好出现在实盘上（真有钱的那一侧）。
    /// </summary>
    public required IReadOnlyList<LivePositionDto> LivePositions { get; init; }

    public required decimal InitialCapital { get; init; }

    public required bool IsActive { get; init; }

    // ── 卡片正面的关键信息 ────────────────────────────────

    /// <summary>
    /// 估值是否可得。
    ///
    /// 为 <c>false</c> 时下面所有估值字段（权益、盈亏、比例、持仓）**都没有意义**，
    /// 界面必须显示"估值不可得"而不是把它们当结果渲染出来——池子取不到数据时
    /// 显示 $0.00 会让人以为刚打平，而事实是"不知道"。配置本身仍然有效，操作照旧可用。
    /// </summary>
    public required bool IsValuationAvailable { get; init; }

    public required decimal Equity { get; init; }

    public required decimal ReturnPercent { get; init; }

    public required decimal RealizedPnl { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    /// <summary>规模比例 = 本金 / 池子权益。</summary>
    public required decimal Scale { get; init; }

    public required decimal PoolEquity { get; init; }

    public required decimal GrossNotional { get; init; }

    public required int FillCount { get; init; }

    /// <summary>因约束被跳过的信号数。不为 0 时卡片要显著标出——那是"没跟上"。</summary>
    public required int SkipCount { get; init; }

    /// <summary>持仓摘要，卡片上最多列前两项。</summary>
    public required IReadOnlyList<PaperPositionBriefDto> Positions { get; init; }

    /// <summary>取不到盯市价的市场数。&gt; 0 时未实现盈亏不完整，卡片要提示。</summary>
    public required int UnpricedMarketCount { get; init; }
}

/// <summary>卡片上的持仓摘要。</summary>
public sealed record PaperPositionBriefDto
{
    public required string Symbol { get; init; }

    /// <summary><c>long</c> / <c>short</c>；空仓时为 <c>null</c>。</summary>
    public required string? Side { get; init; }

    public required decimal Size { get; init; }

    public required decimal Notional { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    /// <summary>跟踪偏差占目标的比例（百分数）。绝对值大说明颗粒度损失明显。</summary>
    public required decimal TrackingDeviationPercent { get; init; }
}

/// <summary>
/// 实盘流水的一行。
///
/// 三件必读的事：**我发了什么**（意图、方向、数量）、**多少钱**（价格与名义额）、
/// **结果**（<c>placed</c> / <c>skipped</c> / <c>failed</c> 与原因）。
/// 真实持仓与盈亏不在这里——那以交易所的账户状态为准，不由本系统算第二份。
/// </summary>
public sealed record LiveOrderDto
{
    public required long Id { get; init; }

    public required long FollowerId { get; init; }

    public required long ExchangeConfigId { get; init; }

    /// <summary>交易所配置的显示名；配置已删时为 null。</summary>
    public required string? ExchangeName { get; init; }

    public required string Platform { get; init; }

    /// <summary>源标的（Lighter 的市场名）——回答"这笔是从哪个信号来的"。</summary>
    public required string SourceSymbol { get; init; }

    public required string TargetBase { get; init; }

    public required string TargetQuote { get; init; }

    /// <summary>目标交易对的系统标准形式（<c>BTC/USDT</c>）。</summary>
    public required string TargetPair { get; init; }

    /// <summary><c>open_long</c> / <c>close_long</c> / <c>open_short</c> / <c>close_short</c> / <c>none</c>。</summary>
    public required string Intent { get; init; }

    /// <summary><c>buy</c> / <c>sell</c> / <c>none</c>。</summary>
    public required string Side { get; init; }

    public required string? PositionSide { get; init; }

    public required decimal Quantity { get; init; }

    public required decimal Price { get; init; }

    /// <summary>这一单所涉仓位的开仓均价（开仓单=自己的成交均价；平仓单=被平仓位的成本线）。0 = 未知（旧行）。</summary>
    public required decimal EntryPrice { get; init; }

    /// <summary>平仓成交价；只有平仓单有。</summary>
    public required decimal? ClosePrice { get; init; }

    public required decimal NotionalUsd { get; init; }

    /// <summary>池子在该市场的持仓名义额（带符号）——信号本身有多大。</summary>
    public required decimal PoolNotional { get; init; }

    /// <summary>按比例复制后算出的目标名义额（带符号）——与实际下单名义的差别来自数量量化。</summary>
    public required decimal TargetNotionalUsd { get; init; }

    public required decimal PoolEquity { get; init; }

    public required decimal Capital { get; init; }

    /// <summary><c>signal</c>（信号驱动的自动跟单）/ <c>manual</c>（人在界面上手动下的）。</summary>
    public required string Trigger { get; init; }

    /// <summary><c>placed</c> / <c>skipped</c> / <c>failed</c>。</summary>
    public required string Status { get; init; }

    public required string? Reason { get; init; }

    public required string? VenueOrderId { get; init; }

    public required string? ClientOrderId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// 实盘账户的真实状态，**由交易所给出**。
///
/// 系统不另算一份权益：本地账与交易所账一旦不一致，就该以交易所为准，
/// 而"以谁为准"这件事写进字段里比写进注释里可靠。
/// </summary>
public sealed record LiveAccountDto
{
    public required long FollowerId { get; init; }

    public required long ExchangeConfigId { get; init; }

    public required string ExchangeName { get; init; }

    public required string Platform { get; init; }

    /// <summary>钱包余额（含未实现盈亏）。</summary>
    public required decimal WalletBalance { get; init; }

    /// <summary>可用保证金。</summary>
    public required decimal AvailableBalance { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    /// <summary>持仓名义总额（绝对值求和）。</summary>
    public required decimal GrossNotional { get; init; }

    /// <summary>是否双向持仓模式。它决定下单参数形状，也影响"多空能否同时存在"。</summary>
    public required bool IsHedgeMode { get; init; }

    /// <summary>投入本金——与钱包余额比较，就是"跟了这么久赚了多少"的粗口径。</summary>
    public required decimal InitialCapital { get; init; }

    public required IReadOnlyList<LivePositionDto> Positions { get; init; }
}

/// <summary>手动平掉实盘账户上的一笔仓位。</summary>
public sealed record CloseLivePositionRequest
{
    /// <summary>资产币（系统标准交易对的那一段）。</summary>
    public required string BaseAsset { get; init; }

    public required string QuoteAsset { get; init; }

    /// <summary>
    /// 平掉之后是否把这个跟单员一并暂停。
    ///
    /// 默认由界面上的确认框决定（那里勾着）：不平掉它，下一次对账就会按池子仓位
    /// 把刚平的仓位重新建起来——使用者点了"平仓"却看到仓位自己回来了。
    /// </summary>
    public bool PauseFollower { get; init; }
}

/// <summary>交易所账户里的一笔持仓。</summary>
public sealed record LivePositionDto
{
    /// <summary>交易所自己的合约写法（只用于在该交易所界面上找到它）。</summary>
    public required string VenueSymbol { get; init; }

    /// <summary>系统标准的资产币；该合约不在目录里时为 null。</summary>
    public required string? BaseAsset { get; init; }

    public required string? QuoteAsset { get; init; }

    /// <summary>系统标准交易对（<c>BTC/USDT</c>）；认不出时为 null。</summary>
    public required string? Pair { get; init; }

    /// <summary>交易所侧的原始字段（<c>LONG</c> / <c>SHORT</c> / <c>BOTH</c>）。</summary>
    public required string PositionSide { get; init; }

    /// <summary>
    /// 归一化后的方向（<c>long</c> / <c>short</c>）。
    ///
    /// 界面与"平仓"都用它，不用 <see cref="PositionSide"/> 自己推：
    /// 单向模式下那个字段恒为 <c>BOTH</c>，方向其实在数量的符号里。
    /// </summary>
    public required string Direction { get; init; }

    public required decimal Size { get; init; }

    public required decimal EntryPrice { get; init; }

    /// <summary>
    /// 交易所给的标记价（快照时刻的）。它是**未实现盈亏的自变量**：
    /// 每秒的实时值由标记价流推来，这里的值用于首次渲染与刷新兜底。
    /// </summary>
    public required decimal MarkPrice { get; init; }

    public required int Leverage { get; init; }

    public required decimal LiquidationPrice { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    public required decimal Notional { get; init; }
}
