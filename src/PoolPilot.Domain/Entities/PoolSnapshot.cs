using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 池在某一时刻的完整状态快照。
///
/// 这是差分判定的输入：两次快照相减即得到"池子做了什么"。
/// 净值的计算刻意不放在这里——净值要用份额净值（<see cref="NavSeries"/>），
/// 而不是本类型里的 <see cref="TotalAssetValue"/>（后者会被申赎扰动）。
/// </summary>
public sealed record PoolSnapshot
{
    public required AccountIndex AccountIndex { get; init; }

    public required LighterTimestamp TakenAt { get; init; }

    public required UsdAmount TotalAssetValue { get; init; }

    public required UsdAmount Collateral { get; init; }

    public required UsdAmount AvailableBalance { get; init; }

    /// <summary>初始保证金占用。<c>÷ Collateral</c> 即保证金使用率。</summary>
    public required UsdAmount InitialMarginRequirement { get; init; }

    /// <summary>维持保证金占用。<c>Collateral − 该值</c> 即距强平缓冲。</summary>
    public required UsdAmount MaintenanceMarginRequirement { get; init; }

    public required IReadOnlyList<PoolPosition> Positions { get; init; }

    public required IReadOnlyList<PoolOrder> Orders { get; init; }

    /// <summary>非零持仓。</summary>
    public IReadOnlyList<PoolPosition> ActivePositions =>
        [.. Positions.Where(p => p.Size != 0m)];

    /// <summary>总敞口（毛，多空名义绝对值之和）。</summary>
    public UsdAmount GrossExposure =>
        UsdAmount.From(ActivePositions.Sum(p => Math.Abs(p.Notional.Value)));

    /// <summary>净敞口（多减空，带符号）。接近 0 说明是市场中性组合。</summary>
    public UsdAmount NetExposure =>
        UsdAmount.From(ActivePositions.Sum(p => p.Side == PositionSide.Long ? p.Notional.Value : -p.Notional.Value));

    /// <summary>敞口占资产比例。判断池子风险状态的核心指标。</summary>
    public PercentValue ExposureRatio =>
        TotalAssetValue.Value == 0m
            ? PercentValue.FromPercent(0m)
            : PercentValue.FromPercent(GrossExposure.Value / TotalAssetValue.Value * 100m);

    /// <summary>多空失衡度。0 为完全中性，1 为完全单边。</summary>
    public decimal ImbalanceRatio
    {
        get
        {
            decimal gross = GrossExposure.Value;
            return gross == 0m ? 0m : Math.Abs(NetExposure.Value) / gross;
        }
    }

    /// <summary>距强平缓冲（抵押品减去维持保证金要求）。</summary>
    public UsdAmount LiquidationBuffer =>
        UsdAmount.From(Collateral.Value - MaintenanceMarginRequirement.Value);
}
