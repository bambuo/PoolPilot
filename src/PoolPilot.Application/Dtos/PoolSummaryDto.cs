namespace PoolPilot.Application.Dtos;

/// <summary>
/// 池列表项。
///
/// 只包含**廉价可得**的字段——都来自一次 <c>publicPoolsMetadata</c> 枚举。
/// 刻意不放真实指标（回撤、真实夏普）与运营者份额：前者要逐池拉净值序列，
/// 后者只在 WS <c>pool_info</c> 里，对 336 个池逐个拉是不可接受的。
/// 那些字段属于池详情页。
/// </summary>
public sealed record PoolSummaryDto
{
    public required long AccountIndex { get; init; }

    public required string Name { get; init; }

    public required decimal TotalAssetValue { get; init; }

    /// <summary>上游报的年化收益率（百分数，点估计）。实测可达数千万百分点。</summary>
    public required decimal ReportedApyPercent { get; init; }

    /// <summary>上游报的夏普比率（点估计）。</summary>
    public required decimal ReportedSharpe { get; init; }

    /// <summary>运营费（百分数）。实测区间 1%~30%。</summary>
    public required decimal OperatorFeePercent { get; init; }

    public required long TotalShares { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required int StateCode { get; init; }

    /// <summary>是否在公共池索引段内。非池账户（如机构账户）为 false。</summary>
    public required bool IsPublicPoolRange { get; init; }

    /// <summary>运营方归属：<c>lighter</c>（官方）/ <c>community</c>（社区第三方）。</summary>
    public required string Origin { get; init; }

    /// <summary>运营者钱包地址。官方池（杠杆代币线）也用的是同一个官方地址。</summary>
    public required string OperatorAddress { get; init; }

    /// <summary>是否已冻结（停止接受新入金）。实测全量池中约 43% 处于冻结态。</summary>
    public required bool IsFrozen { get; init; }
}
