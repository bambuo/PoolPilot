using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 纸面账户在某个市场的持仓。**由 <see cref="PaperFill"/> 流水重放推出，不单独持久化。**
///
/// 盈亏口径（永续合约，不建模资金费）：
/// <c>未实现盈亏 = (盯市价 − 开仓均价) × 带符号数量</c>。
/// 用带符号数量相乘是关键——空头的符号为负，价格下跌时该乘积自动为正，
/// 不需要为多空各写一套公式，也就不会出现"只测了多头"这类漏洞。
/// </summary>
public sealed record PaperPosition
{
    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    public required SignedSize Size { get; init; }

    /// <summary>开仓均价。平仓后归零。</summary>
    public required PriceAmount EntryVwap { get; init; }

    /// <summary>该市场累计已实现盈亏。</summary>
    public required UsdAmount RealizedPnl { get; init; }

    /// <summary>该市场累计手续费。</summary>
    public required UsdAmount Fees { get; init; }

    /// <summary>该市场累计成交笔数（含减仓）。</summary>
    public required int FillCount { get; init; }

    /// <summary>
    /// 建仓杠杆的初始保证金率（百分数，25 表示 25%）。加仓按成交名义额加权。
    /// <c>null</c> = 旧流水没有记录且源池当前也取不到保证金率；此时 ROI 不可得，不拿名义价值冒充。
    /// </summary>
    public decimal? InitialMarginPercent { get; init; }

    public bool IsFlat => Size.IsFlat;

    /// <summary>按给定盯市价算保证金占用额。取不到保证金率或空仓时为 null。</summary>
    public UsdAmount? MarginAmountAt(PriceAmount mark)
    {
        ArgumentNullException.ThrowIfNull(mark);

        if (InitialMarginPercent is not > 0m || IsFlat)
        {
            return null;
        }

        return UsdAmount.From(NotionalAt(mark).Value * InitialMarginPercent.Value / 100m);
    }

    /// <summary>
    /// 按给定盯市价算投资回报率（未实现盈亏 ÷ 保证金，百分数）。
    /// 保证金取不到时为 null——不退化成 ÷ 名义价值，否则同一列在不同行不是一个量。
    /// </summary>
    public PercentValue? UnrealizedPnlPercentAt(PriceAmount mark)
    {
        ArgumentNullException.ThrowIfNull(mark);

        return MarginAmountAt(mark) is not { Value: > 0m } margin
            ? null
            : PercentValue.FromPercent(UnrealizedPnlAt(mark).Value / margin.Value * 100m);
    }

    /// <summary>按给定盯市价算未实现盈亏。平仓或价格缺失时为 0。</summary>
    public UsdAmount UnrealizedPnlAt(PriceAmount mark)
    {
        ArgumentNullException.ThrowIfNull(mark);

        return Size.IsFlat || mark.Value <= 0m
            ? UsdAmount.Zero
            : UsdAmount.From((mark.Value - EntryVwap.Value) * Size.Value);
    }

    /// <summary>该市场的持仓名义价值（按盯市价）。</summary>
    public UsdAmount NotionalAt(PriceAmount mark)
    {
        ArgumentNullException.ThrowIfNull(mark);

        return Size.IsFlat || mark.Value <= 0m
            ? UsdAmount.Zero
            : UsdAmount.From(Size.Absolute * mark.Value);
    }

    public PositionSide? Side => Size.Side;
}
