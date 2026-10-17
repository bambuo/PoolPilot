using PoolPilot.Domain.Entities;

namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 公共池目录的读取口，**带服务端缓存**。
///
/// 与 <see cref="ILighterPoolsClient.FetchPoolsAsync"/> 的分工：那边是"要最新的清单"
/// （工作台翻页、创建带单员时校验池子存在），这边只服务公开页的池名候选框——
/// 每敲一个字问一次，且问的是同一个全量清单。
///
/// 为什么要单独开一个口而不是让公开用例直接打客户端：上游按**权重**限速，
/// 匿名流量没有登录失败锁定那一层挡着，每次穿透会把工作台自己的取数挤到限速之后。
/// 实现方的义务只有一条：**允许返回若干秒之前的清单**（存活期见
/// <c>PublicPageLimits.PoolCatalogCacheSeconds</c>），且这份陈旧只用于"给人挑一个池名"。
/// </summary>
public interface IPoolCatalog
{
    /// <summary>枚举全部公共池（含已冻结；筛选与截断由调用方的用例做）。</summary>
    Task<IReadOnlyList<Pool>> ListAllAsync(CancellationToken cancellationToken = default);
}
