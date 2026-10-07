namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 配置写完之后，让运行期立刻跟上。
///
/// 存在理由：监控与配置是两条独立的时间线——配置写在数据库里，运行期由后台服务按心跳
/// （默认 15 秒）对齐。中间那段空窗里页面会显示出**令人误解**的状态：
/// 刚点了"恢复观察"，带单员那一栏却写着"未连接"（其实只是运行期还没读到新配置），
/// 刚创建了跟单员，账户却一直空着。
///
/// 所以每个改动配置的用例在写库之后都要喊一声。喊不喊得动不影响正确性（心跳最终会兜住），
/// 但不喊就会让使用者对着一个"其实在正常收敛"的界面怀疑系统坏了。
/// </summary>
public interface ILeadTraderSync
{
    /// <summary>立刻同步一次（带单员集合、订阅者挂载、新池基线）。</summary>
    Task RequestSyncAsync(CancellationToken cancellationToken = default);
}
