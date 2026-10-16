namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 授权策略的写侧。**单一写入口**：菜单 / 角色 / 绑定发生任何变化之后，
/// 用例都必须经过它把 Enforcer 从表重建一次——而不是增量改几条策略。
///
/// 为什么是"整体重建"而不是"增量维护"：表才是事实源，重建永远得到与之一致的结果；
/// 增量维护则要在十几处写点上各自记得改对，漏一处就会留下一条谁也看不见的过期策略。
/// 重建带并发保护，读者永远看不到"加载了一半"的策略集。
/// </summary>
public interface IPolicyWriter
{
    Task RebuildAsync(CancellationToken cancellationToken = default);
}
