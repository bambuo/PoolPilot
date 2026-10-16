namespace PoolPilot.Application.Abstractions;

/// <summary>
/// "当前用户对某个权限点拥有哪些动作"。它是菜单/权限展示的读取表示——
/// 界面据此决定显示哪些节点、藏起哪些按钮。
/// </summary>
public sealed record PermissionGrant(string PermissionKey, IReadOnlyList<string> Actions);

/// <summary>
/// 一个待判定的候选：权限点 + 它的全部动作。列举"当前用户有哪些授权"时，
/// 候选集合由调用方给出（菜单树与权限清单的调用方本来就在读菜单表）。
/// </summary>
public sealed record PermissionCandidate(string PermissionKey, IReadOnlyList<string> Actions);

/// <summary>
/// 授权判定。**Casbin 只出现在 Infrastructure**，应用层只认这个抽象，
/// 因此用例与接口都不依赖 Casbin 的类型。
///
/// 它绑定"当前请求的用户"（见 <see cref="ICurrentUser"/>）：权限判定的主语永远是当下这个人，
/// 把 userId 作为参数传进来反而给了"替别人判定"的机会。
/// </summary>
public interface IPermissionEvaluator
{
    /// <summary>当前用户是否拥有该权限点的该动作。未登录时为 <c>false</c>。</summary>
    bool Has(string permissionKey, string action);

    /// <summary>
    /// 在**给定候选**上问出当前用户的全部授权（按权限点归并动作）。**带进程内缓存**——
    /// 一次请求里树过滤与按钮判定会反复问同一个问题。
    ///
    /// 候选必须由调用方传入，而不是让实现去"穷举策略行"：判定与列举要走同一条 matcher
    /// （超管的短路就是这样被如实展开的），穷举显式行会漏掉 matcher 里的口径。
    /// </summary>
    Task<IReadOnlyList<PermissionGrant>> GetGrantedAsync(
        IReadOnlyList<PermissionCandidate> candidates,
        CancellationToken cancellationToken = default);
}
