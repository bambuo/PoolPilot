namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 工作台跟单模块的实时通知。
///
/// 两类事件刻意分开：
/// <list type="bullet">
///   <item><see cref="BroadcastFollowerChangedAsync"/>：账本/池状态变了，**数据本身要重读**
///     （流水、权益曲线只能从库里重放出来）。<c>followerId = 0</c> 表示影响所有跟单员
///     （池快照变化），页面据此刷新列表与当前打开的详情。</item>
///   <item><see cref="BroadcastMarketPriceAsync"/>：只是价格变了，页面按已有持仓本地重算
///     未实现盈亏/权益，不重读库、不打上游。</item>
/// </list>
/// </summary>
public interface IFollowerLiveBroadcaster
{
    /// <summary>跟单员状态变化；0 表示影响全部跟单员。</summary>
    Task BroadcastFollowerChangedAsync(long followerId, CancellationToken cancellationToken = default);

    /// <summary>某个市场的最新中间价。只有订阅了该市场的连接会收到。</summary>
    Task BroadcastMarketPriceAsync(int marketId, decimal price, CancellationToken cancellationToken = default);
}
