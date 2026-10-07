using PoolPilot.Domain.Entities;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Services;

/// <summary>规模可行性结论。</summary>
public sealed record SizeFeasibility
{
    public required decimal CapitalUsd { get; init; }

    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    /// <summary>池的敞口占其资产比例（小数，0.012 表示 1.2%）。</summary>
    public required decimal PoolExposureRatio { get; init; }

    /// <summary>按同比例跟单应下的名义金额（可能低于最小下单额）。</summary>
    public required decimal IdealFollowNotional { get; init; }

    public required decimal MinOrderNotional { get; init; }

    /// <summary>按该池的敞口比例，本金是否足以下一张最小单。</summary>
    public required bool CanPlaceMinimumOrder { get; init; }

    /// <summary>能下最小单所需的最低本金。<see cref="CanPlaceMinimumOrder"/> 为真时等于实际本金需求。</summary>
    public required decimal RequiredCapitalForMinOrder { get; init; }

    /// <summary>
    /// 若硬凑到最小单，相当于把池的敞口放大了多少倍。
    /// 大于 1 意味着你的单笔风险高于池子——这是"小资金硬跟"的真实代价。
    /// </summary>
    public required decimal AmplificationFactor { get; init; }
}

/// <summary>
/// 规模可行性评估。
///
/// 回答"这个本金跟得动吗"。它存在的理由是一个容易被忽略的事实：
/// **跟单的硬约束往往不是成本，而是最小下单量。**
///
/// 池子的单笔敞口通常只占其资产的百分之几（实测某池为 1.2%），
/// 按同比例跟单，小资金会算出低于交易所最小下单额的名义金额——
/// 此时要么跟不了，要么必须放大敞口，而后者会让你的单笔风险高于被跟的池子。
/// </summary>
public static class SizeFeasibilityEvaluator
{
    /// <param name="capitalUsd">跟单本金。</param>
    /// <param name="poolExposureRatio">池的敞口占其资产比例（小数）。</param>
    /// <param name="instrument">合约规格。</param>
    /// <param name="referencePrice">
    /// 参考价。用于把「最小下单数量」换算成金额——上游同时给了金额下限与数量下限，
    /// 两者取更严的。传 0 时只按金额下限判断。
    /// </param>
    public static SizeFeasibility Evaluate(
        decimal capitalUsd,
        decimal poolExposureRatio,
        InstrumentSpec instrument,
        decimal referencePrice = 0m)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentOutOfRangeException.ThrowIfNegative(capitalUsd);
        ArgumentOutOfRangeException.ThrowIfNegative(poolExposureRatio);

        decimal idealNotional = capitalUsd * poolExposureRatio;

        // 最小单约束来自两个下限：金额下限，以及数量下限折算成的金额。取更严的。
        decimal minBySize = referencePrice > 0m ? instrument.MinOrderSize * referencePrice : 0m;
        decimal minOrderNotional = Math.Max(instrument.MinOrderNotional, minBySize);

        bool canPlace = idealNotional >= minOrderNotional && capitalUsd > 0m;

        // 需要的本金 = 最小下单额 ÷ 敞口比例
        decimal requiredCapital = poolExposureRatio > 0m
            ? minOrderNotional / poolExposureRatio
            : decimal.MaxValue;

        // 硬凑到最小单时的放大倍数
        decimal amplification = idealNotional > 0m
            ? minOrderNotional / idealNotional
            : 0m;

        return new SizeFeasibility
        {
            CapitalUsd = capitalUsd,
            MarketId = instrument.MarketId,
            Symbol = instrument.Symbol,
            PoolExposureRatio = poolExposureRatio,
            IdealFollowNotional = idealNotional,
            MinOrderNotional = minOrderNotional,
            CanPlaceMinimumOrder = canPlace,
            RequiredCapitalForMinOrder = requiredCapital,
            AmplificationFactor = amplification,
        };
    }

    /// <summary>
    /// 对多个标的批量评估，返回按"所需本金"升序排列的结果——
    /// 排在前面的就是这个小资金实际能跟的标的。
    /// </summary>
    public static IReadOnlyList<SizeFeasibility> EvaluateAll(
        decimal capitalUsd,
        decimal poolExposureRatio,
        IEnumerable<InstrumentSpec> instruments,
        Func<int, decimal>? priceResolver = null)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        return [.. instruments
            .Select(i => Evaluate(capitalUsd, poolExposureRatio, i, priceResolver?.Invoke(i.MarketId) ?? 0m))
            .OrderBy(r => r.RequiredCapitalForMinOrder)];
    }
}
