using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 评估"给定本金能否跟得动这个池"。
///
/// 这条路回答的是跟单的**物理约束**，而不是成本约束：池子的单笔敞口通常只占其资产的
/// 百分之几，按同比例跟单，小资金会算出低于交易所最小下单额的名义金额。
/// </summary>
public sealed class EvaluateSizeFeasibilityUseCase(ILighterPoolsClient client)
{
    /// <summary>
    /// 对指定池与指定本金，逐个标的评估可行性。
    /// </summary>
    /// <param name="account">被跟的池。</param>
    /// <param name="capitalUsd">跟单本金。</param>
    /// <param name="marketIds">要评估的市场；传空则用池当前持仓的市场。</param>
    public async Task<IReadOnlyList<SizeFeasibilityDto>> ExecuteAsync(
        AccountIndex account,
        decimal capitalUsd,
        IReadOnlyList<int>? marketIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        Task<PoolSnapshot> snapshotTask = client.FetchSnapshotAsync(account, cancellationToken);
        Task<IReadOnlyList<InstrumentSpec>> instrumentsTask = client.FetchInstrumentsAsync(cancellationToken);

        await Task.WhenAll(snapshotTask, instrumentsTask).ConfigureAwait(false);

        PoolSnapshot snapshot = snapshotTask.Result;
        IReadOnlyList<InstrumentSpec> instruments = instrumentsTask.Result;

        // 池的敞口比例是"跟单按同比例"的基准
        decimal exposureRatio = snapshot.ExposureRatio.Fraction;

        // 默认评估池当前持仓的市场；无持仓时退化为评估全部市场（此时敞口比例为 0，结论会是"不可行"）
        HashSet<int> targets = marketIds is { Count: > 0 }
            ? [.. marketIds]
            : [.. snapshot.ActivePositions.Select(p => p.MarketId)];

        IReadOnlyList<InstrumentSpec> selected = targets.Count > 0
            ? [.. instruments.Where(i => targets.Contains(i.MarketId))]
            : instruments;

        Dictionary<int, decimal> prices = snapshot.ActivePositions
            .GroupBy(p => p.MarketId)
            .ToDictionary(g => g.Key, g => g.First().AvgEntryPrice.Value);

        return
        [
            .. SizeFeasibilityEvaluator
                .EvaluateAll(capitalUsd, exposureRatio, selected, id => prices.GetValueOrDefault(id, 0m))
                .Select(ToDto),
        ];
    }

    private static SizeFeasibilityDto ToDto(SizeFeasibility result) => new()
    {
        CapitalUsd = result.CapitalUsd,
        MarketId = result.MarketId,
        Symbol = result.Symbol,
        PoolExposureRatioPercent = result.PoolExposureRatio * 100m,
        IdealFollowNotional = result.IdealFollowNotional,
        MinOrderNotional = result.MinOrderNotional,
        CanPlaceMinimumOrder = result.CanPlaceMinimumOrder,
        RequiredCapitalForMinOrder = result.RequiredCapitalForMinOrder,
        AmplificationFactor = result.AmplificationFactor,
    };
}
