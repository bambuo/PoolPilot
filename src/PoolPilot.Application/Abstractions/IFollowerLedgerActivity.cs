namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 纸面跟单员落账后的变更通知。
///
/// 为什么需要它：公开页的详情/列表是 SSE，但默认每 15 秒才由 tick 重装配一次。
/// 一笔成交或一个权益点写进库之后，等下一次 tick 才出现，用户看到的就是"详情不实时"。
/// 这个通知让复制管线在写成功后立即告诉公开面"这一行变了"，由公开面强制重装配并推帧；
/// 15 秒 tick 仍保留，负责价格/盯市这类没有落账事件的变化。
///
/// 实现方必须**非阻塞**：复制管线在信号处理路径上调用它，不能因为推帧慢而拖住对账。
/// 目前唯一实现是 Api 层的 <c>PublicPageFeed</c>（fire-and-forget）；没有公开订阅者时它会立刻返回。
/// </summary>
public interface IFollowerLedgerActivity
{
    /// <summary>某个纸面跟单员的流水或权益点发生了变化。</summary>
    void NotifyFollowerChanged(long followerId);
}
