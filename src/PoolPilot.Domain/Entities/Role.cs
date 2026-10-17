namespace PoolPilot.Domain.Entities;

/// <summary>
/// 一个角色。<see cref="Code"/> 是稳定标识，也是 Casbin 里 <c>role:{Code}</c> 的那一段——
/// 改它就等于换一套角色绑定，所以它是不可变的身份，不是可编辑的显示名。
///
/// <see cref="IsSystem"/> 为 <c>true</c> 的角色由系统维护（目前只有超级管理员）：
/// 它不可被删除，其策略由投影自动补齐。
///
/// <see cref="IsRegistrationDefault"/> 标记"新注册账号自动获得的角色"。全库最多一个；
/// 由「角色管理」显式指定，不会因为建角色而自动产生。它不改变已有账号的绑定。
/// </summary>
public sealed record Role
{
    public long Id { get; init; }

    public required string Code { get; init; }

    public required string Title { get; init; }

    /// <summary>系统角色（由种子建立、不可删）。普通角色为 <c>false</c>。</summary>
    public required bool IsSystem { get; init; }

    /// <summary>新账号注册时是否自动绑定这个角色。未指定默认角色时全库都是 <c>false</c>。</summary>
    public bool IsRegistrationDefault { get; init; }
}
