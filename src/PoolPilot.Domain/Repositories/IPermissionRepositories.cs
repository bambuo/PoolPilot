using PoolPilot.Domain.Entities;

namespace PoolPilot.Domain.Repositories;

/// <summary>一条"某菜单绑定了某角色"（菜单 → 角色的 p 策略来源）。</summary>
public sealed record MenuRoleBinding(long MenuId, string RoleCode);

/// <summary>一条"某用户拥有某角色"（g 策略来源）。</summary>
public sealed record UserRoleBinding(long UserId, string RoleCode);

/// <summary>
/// 菜单的存档。它是授权的事实源之一：菜单 + 动作（<see cref="Menu.Actions"/>）决定
/// Casbin 的对象与动作集合。
/// </summary>
public interface IMenuRepository
{
    /// <summary>全部菜单（含各自的动作），按 (SortOrder, Id) 排序。树由调用方按 ParentId 组装。</summary>
    Task<IReadOnlyList<Menu>> ListAsync(CancellationToken cancellationToken = default);

    Task<Menu?> GetAsync(long id, CancellationToken cancellationToken = default);

    Task<Menu?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>是否存在以该菜单为父的节点（删除前检查用）。</summary>
    Task<bool> HasChildrenAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>新建并返回带库分配主键的菜单（动作一并写入）。</summary>
    Task<Menu> CreateAsync(Menu menu, CancellationToken cancellationToken = default);

    /// <summary>更新可变字段，并**整体替换**该菜单的动作集合。code 变化时 PermissionKey 由调用方一并给出。</summary>
    Task UpdateAsync(Menu menu, CancellationToken cancellationToken = default);

    /// <summary>删除菜单，连同它的动作与角色绑定。</summary>
    Task DeleteAsync(long id, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);
}

/// <summary>角色的存档。</summary>
public interface IRoleRepository
{
    Task<IReadOnlyList<Role>> ListAsync(CancellationToken cancellationToken = default);

    Task<Role?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>当前注册默认角色；没有指定时为 <c>null</c>。</summary>
    Task<Role?> FindRegistrationDefaultAsync(CancellationToken cancellationToken = default);

    /// <summary>新建并返回带库分配主键的角色。</summary>
    Task<Role> CreateAsync(Role role, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新可变字段。**只有 Title 可写**：Code 是身份（改它等于换掉全部绑定），
    /// IsSystem 由系统维护，两者都不是"编辑角色"能碰的。
    /// </summary>
    Task UpdateAsync(Role role, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把注册默认角色切换为 <paramref name="code"/>；<c>null</c> 表示取消默认。
    /// **一次写操作把其余角色一并清掉**，所以库里的默认角色恒定最多一个。
    /// </summary>
    Task SetRegistrationDefaultAsync(string? code, CancellationToken cancellationToken = default);

    /// <summary>按 code 删除角色，连同它的用户绑定与菜单绑定（没有外键级联，子表在这里清）。</summary>
    Task DeleteAsync(string code, CancellationToken cancellationToken = default);
}

/// <summary>
/// 用户-角色绑定的存档。它把用户接到角色上，是 Casbin <c>g</c> 策略的事实源。
/// </summary>
public interface IUserRoleRepository
{
    /// <summary>某个用户拥有的角色标识。</summary>
    Task<IReadOnlyList<string>> ListRoleCodesForUserAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>某个用户绑定了几个角色（首个账号判据用）。</summary>
    Task<int> CountForUserAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>绑定一个角色；已绑定则幂等无操作。</summary>
    Task AddAsync(long userId, string roleCode, CancellationToken cancellationToken = default);

    /// <summary>整体替换某个用户的角色绑定（提交什么就是什么）。未知角色 code 抛异常。</summary>
    Task ReplaceAsync(long userId, IReadOnlyList<string> roleCodes, CancellationToken cancellationToken = default);

    /// <summary>全部绑定（重建 Enforcer 时一次性读出）。</summary>
    Task<IReadOnlyList<UserRoleBinding>> ListAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 菜单-角色绑定的存档。它是每个菜单的 <c>p</c> 策略（除超级管理员那条常驻的以外）的事实源。
/// </summary>
public interface IMenuRoleRepository
{
    /// <summary>某个菜单绑定的角色标识。</summary>
    Task<IReadOnlyList<string>> ListRoleCodesAsync(long menuId, CancellationToken cancellationToken = default);

    /// <summary>整体替换某个菜单的角色绑定。</summary>
    Task ReplaceAsync(long menuId, IReadOnlyList<string> roleCodes, CancellationToken cancellationToken = default);

    /// <summary>
    /// 整体替换某个角色的菜单绑定（「角色管理」页写的就是这个方向）。
    /// 收的是菜单主键：用例为校验未知 code 已经读过整张菜单表，把解析好的 Id 传进来，
    /// 这里再按 code 解析一次只是同一份数据查两遍。
    /// </summary>
    Task ReplaceForRoleAsync(string roleCode, IReadOnlyList<long> menuIds, CancellationToken cancellationToken = default);

    /// <summary>全部绑定（重建 Enforcer 时一次性读出）。</summary>
    Task<IReadOnlyList<MenuRoleBinding>> ListAllAsync(CancellationToken cancellationToken = default);
}
