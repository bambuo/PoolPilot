using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 实时行情写入点：WS 的 market_stats 帧到达时把最新中间价推进 <see cref="IMarkPrices"/>。
///
/// 这是估值侧的"事件驱动"入口：订阅了 market_stats 的市场不再靠 REST 缓存过期来刷新，
/// 每来一帧就更新，下一次 ResolveAsync / 详情重装配直接用最新价。
/// 交易/成交路径仍然现取盘口，不受这里影响。
/// </summary>
public interface IMarkPriceSink
{
    void Update(int marketId, PriceAmount mid);
}
