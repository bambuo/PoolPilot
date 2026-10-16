using Casbin;
using Casbin.Model;

using PoolPilot.Domain.Services;

namespace PoolPilot.Infrastructure.Authorization;

/// <summary>
/// 进程内持有一个 Casbin <see cref="Enforcer"/>，并在策略变化时整体换掉它。
///
/// **只有单例**：Enforcer 的构建与持有不便宜，且它表达的是"全库此刻的授权状态"，
/// 每请求造一个既浪费又会让不同请求看到不同的策略快照。
///
/// **重建的并发安全靠"先建后换"**：先用投影**全新构造**一个 Enforcer，构造完成后
/// 在锁内一次引用交换。读者要么拿到旧的完整实例、要么拿到新的完整实例，
/// 绝不会看到"加了一半策略"的中间态——那种半加载状态会让权限忽有忽无，且极难复现。
/// </summary>
public sealed class CasbinEnforcer
{
    private readonly object _gate = new();

    private Enforcer _enforcer = Build(new PolicyProjection([], []));

    /// <summary>用一份完整投影替换当前策略集。</summary>
    public void Rebuild(PolicyProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        Enforcer next = Build(projection);

        lock (_gate)
        {
            Volatile.Write(ref _enforcer, next);
        }
    }

    /// <summary>判定 <paramref name="subject"/> 是否拥有该对象上的该动作。</summary>
    public bool Enforce(string subject, string objectKey, string action) =>
        Volatile.Read(ref _enforcer).Enforce(subject, objectKey, action);

    private static Enforcer Build(PolicyProjection projection)
    {
        Enforcer enforcer = new(DefaultModel.CreateFromText(PermissionModel.Text), null, null);

        foreach (GroupingRule grouping in projection.Grouping)
        {
            enforcer.AddGroupingPolicy(grouping.Subject, grouping.Role);
        }

        foreach (PolicyRule policy in projection.Policies)
        {
            enforcer.AddPolicy(policy.Subject, policy.ObjectKey, policy.Action);
        }

        return enforcer;
    }
}
