namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 带单员的池快照发生了变化（仓位、权益或首次基线）。公开页据此刷新
/// 该池的带单员卡片与订阅它的跟单员详情，不靠定时器重装配。
/// </summary>
public interface ILeadActivity
{
    void NotifyLeadChanged(long accountIndex);
}
