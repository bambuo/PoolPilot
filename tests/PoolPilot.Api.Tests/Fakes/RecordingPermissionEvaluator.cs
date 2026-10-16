using PoolPilot.Application.Abstractions;

namespace PoolPilot.Api.Tests.Fakes;

/// <summary>
/// 假判定器：判定由一个委托决定，并把每次被问到的 (权限键, 动作) 记下来。
///
/// Api.Tests 里它服务两类地方：MonitorHub 的准入分支（既验证"拒了"也验证"问的是
/// <c>menu:graph</c> 的 <c>view</c>"），以及 <see cref="MonitorStatusChannelTests"/> 里
/// "准入总是通过"的基线。
///
/// 记录查询这一点是刻意的：只回一个 <c>bool</c> 的替身测不出"hub 问错了权限键"，
/// 那正是准入最容易被写歪的地方。
/// </summary>
internal sealed class RecordingPermissionEvaluator(Func<string, string, bool> decide) : IPermissionEvaluator
{
    public List<(string PermissionKey, string Action)> Queries { get; } = [];

    public bool Has(string permissionKey, string action)
    {
        Queries.Add((permissionKey, action));

        return decide(permissionKey, action);
    }

    /// <summary>hub 的准入路径不走列举；如实抛，避免"没实现却看起来通过"。</summary>
    public Task<IReadOnlyList<PermissionGrant>> GetGrantedAsync(
        IReadOnlyList<PermissionCandidate> candidates,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("本替身只服务 Has 判定。");
}
