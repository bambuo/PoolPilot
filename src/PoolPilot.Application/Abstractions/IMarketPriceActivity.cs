using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 实时行情事件：某市场的中间价变了。公开页据此立即更新正在看的详情，
/// 不靠定时器重装配。
/// </summary>
public interface IMarketPriceActivity
{
    void NotifyMarketPriceChanged(int marketId, PriceAmount mid);
}
