using PoolPilot.Domain.Entities;

namespace PoolPilot.Application.Dtos;

/// <summary>持仓变化事件（监控台的事件流）。</summary>
public sealed record PoolEventDto
{
    public required long AccountIndex { get; init; }

    public required string PoolName { get; init; }

    public required string Kind { get; init; }

    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    public required string Side { get; init; }

    public required decimal SizeBefore { get; init; }

    public required decimal SizeAfter { get; init; }

    public required decimal NotionalDelta { get; init; }

    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>是否在增加敞口（开仓或加仓）。</summary>
    public required bool IsExposureIncreasing { get; init; }
}

/// <summary>
/// 跟单成交对照记录。
///
/// 这是本工作台最有价值的一张表：左列是池子的真实成交价，右列是我方按真实盘口
/// 走盘算出的模拟成交价，差额（基点）就是跟单的真实摩擦成本。
/// 它把"跟单值不值得做"从推测变成可测量的数字。
/// </summary>
public sealed record CopyFillDto
{
    public required long Id { get; init; }

    public required long AccountIndex { get; init; }

    public required string PoolName { get; init; }

    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    public required string Side { get; init; }

    public required decimal Size { get; init; }

    /// <summary>池子的实际成交价。</summary>
    public required decimal PoolPrice { get; init; }

    /// <summary>我方模拟成交的加权均价（走真实盘口）。</summary>
    public required decimal OurVwap { get; init; }

    /// <summary>单腿漏损（基点）。**正数表示我方更差。**</summary>
    public required decimal SlippageBps { get; init; }

    /// <summary>盘口深度是否吃得下该规模。</summary>
    public required bool DepthSufficient { get; init; }

    /// <summary>从池子成交到我们观察到的延迟（毫秒）。</summary>
    public required double DetectionLatencyMs { get; init; }

    /// <summary>
    /// 该样本是否落在有效测量窗口内。
    ///
    /// 落窗外的样本，其盘口已不是信号时刻的市场，算出来的漏损甚至会是负的
    /// （实测出现过 −107 bps）。这类样本**进入原始记录但被排除在报告统计之外**——
    /// 保留是为了可追溯，排除是因为它不度量任何真实的东西。
    /// 界面据此把两者区分开，否则"报告说 0 样本"和"表格里有负数"会互相矛盾。
    /// </summary>
    public required bool IsWithinMeasurementWindow { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }
}

/// <summary>
/// 跟单漏损汇总。
///
/// 把离散的对照记录聚合成可以支撑决策的数字：按标的与按市场分组，
/// 给出往返总成本，并对照池子自身的单轮收益幅度——若摩擦成本吃掉收益的显著比例，
/// 跟单在经济上就不成立。
/// </summary>
public sealed record SlippageReportDto
{
    public required long AccountIndex { get; init; }

    public required string PoolName { get; init; }

    public required int SampleCount { get; init; }

    public required decimal MedianEntrySlippageBps { get; init; }

    public required decimal P90EntrySlippageBps { get; init; }

    /// <summary>按"往返 = 两条腿"估算的总摩擦成本（基点）。</summary>
    public required decimal EstimatedRoundTripBps { get; init; }

    /// <summary>池子的平均单轮收益率（百分数），用于与摩擦成本对照。</summary>
    public required decimal AveragePoolRoundTripReturnPercent { get; init; }

    public required IReadOnlyList<MarketSlippageDto> ByMarket { get; init; }
}

/// <summary>按标的细分的漏损。</summary>
public sealed record MarketSlippageDto
{
    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    public required int SampleCount { get; init; }

    public required decimal MedianSlippageBps { get; init; }

    public required decimal SpreadBps { get; init; }

    public required bool DepthSufficient { get; init; }
}

/// <summary>规模门槛评估结果。</summary>
public sealed record SizeFeasibilityDto
{
    public required decimal CapitalUsd { get; init; }

    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    /// <summary>池的敞口占其资产比例（百分数）。</summary>
    public required decimal PoolExposureRatioPercent { get; init; }

    /// <summary>按同比例跟单应下的名义金额。</summary>
    public required decimal IdealFollowNotional { get; init; }

    public required decimal MinOrderNotional { get; init; }

    public required bool CanPlaceMinimumOrder { get; init; }

    /// <summary>能下最小单所需的最低本金。</summary>
    public required decimal RequiredCapitalForMinOrder { get; init; }

    /// <summary>若硬凑到最小单，相当于把池的敞口放大了多少倍。</summary>
    public required decimal AmplificationFactor { get; init; }
}

/// <summary>净值曲线对照的一条。</summary>
public sealed record BacktestCurvePointDto
{
    public required DateTimeOffset At { get; init; }

    /// <summary>池子自身的净值（归一化到 1.0 起）。</summary>
    public required decimal PoolNav { get; init; }

    /// <summary>跟单策略的净值（扣除实测摩擦成本）。</summary>
    public required decimal CopyNav { get; init; }

    /// <summary>直接入池的净值（扣除运营费）。</summary>
    public required decimal DepositNav { get; init; }
}

/// <summary>
/// 回测结果：三方净值对照。
///
/// 三条曲线回答的是同一个问题的三个答案：
/// 池子自己赚了多少、扣掉跟单摩擦后还剩多少、以及什么都不做把资金放进池子里能拿多少。
/// 第三条是零工程量方案，跟单必须显著跑赢它才值得做。
/// </summary>
public sealed record BacktestResultDto
{
    public required long AccountIndex { get; init; }

    public required string PoolName { get; init; }

    public required decimal PoolTotalReturnPercent { get; init; }

    public required decimal CopyTotalReturnPercent { get; init; }

    public required decimal DepositTotalReturnPercent { get; init; }

    /// <summary>跟单采用的往返摩擦成本（基点），来自实测或显式指定。</summary>
    public required decimal AssumedRoundTripCostBps { get; init; }

    /// <summary>运营费（百分数），用于扣减入池方案。</summary>
    public required decimal OperatorFeePercent { get; init; }

    public required int SampleDays { get; init; }

    public required IReadOnlyList<BacktestCurvePointDto> Curve { get; init; }
}

/// <summary>跟单台账汇总。</summary>
public sealed record CopyLedgerDto
{
    public required long AccountIndex { get; init; }

    public required string PoolName { get; init; }

    public required IReadOnlyList<CopyFillDto> Entries { get; init; }

    public required SlippageReportDto Report { get; init; }
}

/// <summary>可用的盘口信息（规模门槛页需要参考价）。</summary>
public sealed record MarketQuoteDto
{
    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    public required decimal BestBid { get; init; }

    public required decimal BestAsk { get; init; }

    public required decimal MidPrice { get; init; }

    public required decimal SpreadBps { get; init; }

    public required decimal MinOrderNotional { get; init; }

    public required decimal MinOrderSize { get; init; }

    public required decimal TakerFeePercent { get; init; }
}
