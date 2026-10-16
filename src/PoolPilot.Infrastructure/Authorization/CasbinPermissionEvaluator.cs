using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Constants;

namespace PoolPilot.Infrastructure.Authorization;

/// <summary>
/// 授权判定：把"当前用户"接到 Casbin 上。
///
/// 每次请求（Scoped）解析一次主体，并把该用户的全部授权**缓存到本次请求结束**：
/// 菜单树过滤、节点动作、按钮判定会在一次请求里反复问同一件事，逐次查 Enforcer 是纯浪费。
/// 缓存是实例字段，所以写操作后的下一次请求自然会读到重建过的策略——不存在跨请求的陈旧。
/// </summary>
public sealed class CasbinPermissionEvaluator(
    CasbinEnforcer enforcer,
    ICurrentUser currentUser) : IPermissionEvaluator
{
    private IReadOnlyList<PermissionGrant>? _granted;

    public bool Has(string permissionKey, string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        return Subject() is { } subject && enforcer.Enforce(subject, permissionKey, action);
    }

    public Task<IReadOnlyList<PermissionGrant>> GetGrantedAsync(
        IReadOnlyList<PermissionCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        _granted ??= Load(candidates);

        return Task.FromResult(_granted);
    }

    /// <summary>
    /// 逐个候选动作问 Enforcer——**判定与列举因此严格同源**：列出什么，就是判得过什么。
    ///
    /// 不穷举显式策略行（<c>GetImplicitPermissionsForUser</c> 那种做法）：
    /// 它只认识 p/g 行，超管在 matcher 里的短路不会被它反映，
    /// 结果是"判定通过、清单为空"，菜单树会跟着空掉。
    /// 成本 = 候选数 × 每候选动作数 次内存判定（当前几十次，微秒级），且结果按请求缓存。
    /// </summary>
    private List<PermissionGrant> Load(IReadOnlyList<PermissionCandidate> candidates)
    {
        if (Subject() is not { } subject)
        {
            return [];
        }

        List<PermissionGrant> granted = [];

        foreach (PermissionCandidate candidate in candidates.OrderBy(c => c.PermissionKey, StringComparer.Ordinal))
        {
            List<string> actions =
            [
                .. candidate.Actions
                    .Where(action => enforcer.Enforce(subject, candidate.PermissionKey, action))
                    .OrderBy(ActionRank),
            ];

            if (actions.Count > 0)
            {
                granted.Add(new PermissionGrant(candidate.PermissionKey, actions));
            }
        }

        return granted;
    }

    private string? Subject() => currentUser.UserId is { } id ? PermissionSubjects.ForUser(id) : null;

    /// <summary>动作按 <c>view → edit → delete</c> 的固定次序返回：界面与断言看到稳定的顺序。</summary>
    private static int ActionRank(string action)
    {
        for (int index = 0; index < MenuActions.All.Count; index++)
        {
            if (MenuActions.All[index] == action)
            {
                return index;
            }
        }

        return int.MaxValue;
    }
}
