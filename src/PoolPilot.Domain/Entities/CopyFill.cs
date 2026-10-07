using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 一条跟单对照记录（纸面跟单，不真实下单）。
///
/// 这是本系统最有价值的产出：左列是池子的真实成交价与规模，右列是我方按**真实盘口**
/// 走盘算出的模拟成交价，差额（基点）就是跟单的真实摩擦成本。
///
/// 它把"跟单值不值得做"从推测变成可测量的数字——池子赚价差、跟单者付价差这个
/// 方向反转，只有用真实盘口算才能得到有决策价值的量级。
/// </summary>
public sealed record CopyFill
{
    /// <summary>持久化主键；未入库时为 0。</summary>
    public long Id { get; init; }

    public required AccountIndex AccountIndex { get; init; }

    /// <summary>触发该记录的池成交 ID，用于幂等去重。</summary>
    public required long SourceTradeId { get; init; }

    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    /// <summary>跟随的方向：池子买入则我们买入。</summary>
    public required OrderSide Side { get; init; }

    public required decimal Size { get; init; }

    /// <summary>池子的实际成交价。</summary>
    public required PriceAmount PoolPrice { get; init; }

    /// <summary>我方模拟成交的加权均价（走真实盘口，含预期滑点）。</summary>
    public required PriceAmount OurVwap { get; init; }

    /// <summary>单腿漏损（基点）。**正数表示我方更差。**</summary>
    public required decimal SlippageBps { get; init; }

    /// <summary>盘口深度是否吃得下该规模。为 false 时该样本的漏损是下界。</summary>
    public required bool DepthSufficient { get; init; }

    /// <summary>从池子成交到我们观察到的延迟（毫秒）。</summary>
    public required double DetectionLatencyMs { get; init; }

    public required LighterTimestamp ObservedAt { get; init; }
}
