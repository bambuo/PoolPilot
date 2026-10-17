using PoolPilot.Domain.Constants;

namespace PoolPilot.Api.Streaming;

/// <summary>
/// 公开页 SSE 的运行参数。目前只有一个：两类连接合计上限。
///
/// 为什么不直接把 <see cref="PublicPageLimits.MaxStreamConnections"/> 写进 feed：
/// 集成测试要能把它降到 2 来验证"第三条 503"这条分支，而真起 50 条连接再等第 51 条
/// 在测试里既慢又脆。生产仍从配置节 <c>PublicPages:Stream</c> 绑定，缺省值就是那个常量。
/// </summary>
public sealed class PublicStreamSettings
{
    /// <summary>两类连接（列表 + 详情）合计上限。</summary>
    public int MaxConnections { get; set; } = PublicPageLimits.MaxStreamConnections;
}
