namespace PoolPilot.Domain.Services;

/// <summary>
/// 计算账户级清算价时用到的一笔仓位。金额均为 USDT，费率为百分数。
/// </summary>
public sealed record PaperMarginPosition
{
    public required int MarketId { get; init; }

    /// <summary>带符号数量：多正空负。</summary>
    public required decimal SignedSize { get; init; }

    public required decimal EntryPrice { get; init; }

    public required decimal MarkPrice { get; init; }

    public required decimal Notional { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    /// <summary>维持保证金率（百分数）。</summary>
    public required decimal MaintenanceMarginPercent { get; init; }
}

/// <summary>
/// 纸面账户的保证金分析。
///
/// 纸面没有交易所的账户接口，但保证金/清算不是"接口数据"而是会计规则：
/// 保证金 = 名义价值 × 初始保证金率；ROI = 未实现盈亏 ÷ 保证金；
/// 清算价 = 账户权益跌到维持保证金要求时的价格。
///
/// 清算价采用全仓口径：只让**这一笔**价格变动，其他仓位的价格与维持保证金占用保持不变，
/// 解 <c>权益(P) = 维持保证金(P)</c>。这与上游池持仓的清算价语义一致，也与纸面账户的
/// 实际结构一致——纸面账没有逐仓隔离。
/// </summary>
public static class PaperMarginAnalyzer
{
    /// <summary>
    /// 计算指定仓位的账户级清算价。无法得到正数解、方向不合理或维持保证金数据缺失时返回 null。
    /// </summary>
    public static decimal? LiquidationPrice(
        PaperMarginPosition target,
        IReadOnlyList<PaperMarginPosition> positions,
        decimal equity)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(positions);

        if (target.SignedSize == 0m
            || target.MaintenanceMarginPercent <= 0m
            || target.MarkPrice <= 0m)
        {
            return null;
        }

        decimal maintenanceRate = target.MaintenanceMarginPercent / 100m;
        decimal otherMaintenance = 0m;

        foreach (PaperMarginPosition position in positions)
        {
            if (position.MarketId == target.MarketId || position.SignedSize == 0m)
            {
                continue;
            }

            if (position.MaintenanceMarginPercent <= 0m)
            {
                // 维持保证金不全时宁可不给清算价：少算一笔占用会把清算价推远，是危险的方向
                return null;
            }

            otherMaintenance += position.Notional * position.MaintenanceMarginPercent / 100m;
        }

        decimal q = target.SignedSize;
        decimal denominator = q - maintenanceRate * Math.Abs(q);

        if (denominator == 0m)
        {
            return null;
        }

        // 权益(P) = 当前权益 − 当前未实现盈亏 + (P − 开仓价) × q
        // 维持保证金(P) = 其他仓位占用 + 维持率 × |q| × P
        decimal numerator =
            otherMaintenance - equity + target.UnrealizedPnl + target.EntryPrice * q;
        decimal liquidation = numerator / denominator;

        if (liquidation <= 0m)
        {
            return null;
        }

        // 多头清算价只能低于现价，空头只能高于现价；另一侧的解没有经济含义
        return q > 0m
            ? liquidation < target.MarkPrice ? liquidation : null
            : liquidation > target.MarkPrice ? liquidation : null;
    }
}
