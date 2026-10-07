using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 池的详细信息（来自 WS 频道 <c>pool_info/{account_index}</c>）。
///
/// 与 <see cref="Pool"/> 的分工是被实测逼出来的：<c>publicPoolsMetadata</c>
/// **不提供** <c>operator_shares</c> 与 <c>min_operator_share_rate</c> 两个字段
/// （实测字段清单里没有），它们只存在于 <c>pool_info</c>。
/// 硬把它们塞进 <see cref="Pool"/> 会让"读不到"与"真的是 0"混为一谈。
///
/// 净值序列也在这里——它是算真实回撤与真实夏普的唯一来源。
/// </summary>
public sealed record PoolInfo
{
    public required AccountIndex AccountIndex { get; init; }

    public required long TotalShares { get; init; }

    public required long OperatorShares { get; init; }

    /// <summary>运营者必须维持的最低份额占比。</summary>
    public required PercentValue MinOperatorShareRate { get; init; }

    public required PercentValue OperatorFee { get; init; }

    public required PercentValue ReportedAnnualPercentageYield { get; init; }

    public required decimal ReportedSharpe { get; init; }

    /// <summary>净值序列（份额净值），由同一频道的 <c>share_prices</c> 提供。</summary>
    public required NavSeries Nav { get; init; }

    /// <summary>运营者实际份额占比。</summary>
    public PercentValue OperatorShareRatio =>
        TotalShares == 0L
            ? PercentValue.FromPercent(0m)
            : PercentValue.FromPercent((decimal)OperatorShares / TotalShares * 100m);

    /// <summary>
    /// **入金是否已被限制**：运营者份额触到（或跌破）下限，平台不再接受新入金。
    ///
    /// 判定与官方前端**同源**（从其生产产物逆向，官方原文：
    /// "Deposits are limited because the operator share has reached its minimum limit."）：
    /// <c>round(实际占比, 2) &lt;= 下限</c>。官方用的正是 <c>&lt;=</c> 而非 <c>&lt;</c>——
    /// 恰好等于下限也算触线。
    ///
    /// 机制：新入金会摊薄运营者的份额占比，若其已在下限，再入金就会把运营者压到下限之下，
    /// 违反平台规则，因此平台直接限制入金。所以"池子满了"的实际含义是**运营者不再追加自有资金**，
    /// 而不是容量上限——上游没有任何容量字段。
    ///
    /// 取舍说明：这里用**平台自己的阈值**（下限本身），而不是自定义的"几倍下限"。
    /// 曾经用过自定义的 1.5 倍阈值来表达"利益绑定偏松"，但那既是我拍的数、
    /// 又答不了"我能不能入金"这个操作性问题，还因为命名歧义把文案渲染反了。
    /// 平台规则是可验证的，自定义阈值不是。
    /// </summary>
    public bool IsDepositLimited =>
        MinOperatorShareRate.Percent > 0m
        && decimal.Round(OperatorShareRatio.Percent, 2) <= MinOperatorShareRate.Percent;

    /// <summary>份额占比高出下限多少个百分点。为 0 表示正好压在线上（此时入金已受限）。</summary>
    public PercentValue OperatorShareHeadroom =>
        PercentValue.FromPercent(OperatorShareRatio.Percent - MinOperatorShareRate.Percent);
}
