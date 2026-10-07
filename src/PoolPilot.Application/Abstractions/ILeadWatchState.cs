using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 带单员的运行期观察状态。
///
/// 由后台监控服务实现——只有它知道 WebSocket 订阅此刻是在走还是断了。
/// 配置里只能表达"应该观察"，而"应该"与"实际"的差别恰恰是最需要被看见的东西：
/// 把它藏起来的话，连接断了会和"我自己暂停的"长得一模一样。
/// </summary>
public interface ILeadWatchState
{
    /// <summary>此刻是否正在接收该池的推送（连接活着且该池在观察）。</summary>
    bool IsWatching(AccountIndex account);
}
