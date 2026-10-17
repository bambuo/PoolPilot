namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 确保某市场的实时行情频道已订阅（Lighter <c>market_stats/{market_id}</c>）。
///
/// 公开详情打开时，按它持仓里的市场逐个调用；实现方去重后追加到 WS 客户端的动态频道。
/// 协议没有验证过的退订帧，频道会保留到进程结束——这是"只为看过的市场付费"的边界：
/// 没打开过详情的市场不会被订阅。
/// </summary>
public interface IMarketStatsSubscription
{
    void EnsureSubscribed(int marketId);
}
