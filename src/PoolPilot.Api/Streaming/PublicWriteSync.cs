using PoolPilot.Application.Abstractions;

namespace PoolPilot.Api.Streaming;

/// <summary>
/// 公开写成功后的运行期同步——**在后台发起，不挡响应**。
///
/// 为什么不是完全不等：公开删除如果只删库、不告诉运行期，已经删掉的那一行在心跳到来前
/// 仍可能继续落流水与权益点（实测过 6 笔 fills + 1 个 equity 落在已删 Id 上）。公开新增
/// 也需要运行期尽快接上订阅，否则卡片上的"跟随中"要等下一次心跳才成真。
///
/// 为什么不是 await：公开面的写请求面向匿名访客，`RequestSyncAsync` 是一次全量重同步
/// （要连上游、重建订阅），实测能到 20 秒；把它挂在响应上等于让一个建单请求等一轮网络。
/// 所以这里只把工作交出去，响应立刻返回。失败只记日志——数据已经写成功，后台心跳会再试。
///
/// 这个类是 Scoped：它本身无状态，但与写请求的作用域同寿命，避免单例持有 Scoped 的同步器。
/// </summary>
public sealed class PublicWriteSync(ILeadTraderSync sync, ILogger<PublicWriteSync> logger)
{
    public void Request()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await sync.RequestSyncAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "公开写后的后台运行期同步失败；下一次心跳会重试。");
            }
        });
    }
}
