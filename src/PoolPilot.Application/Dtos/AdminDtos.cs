namespace PoolPilot.Application.Dtos;

/// <summary>
/// 「系统 → 用户管理」列表里的一条账号。
///
/// <see cref="CreatedAt"/> 是 ISO 8601 的 UTC 字符串（如 <c>2026-10-16T09:00:00.000Z</c>）：
/// 直接给前端一个不用猜时区的字面量，而不是让它去解释 <c>DateTimeOffset</c> 的偏移写法。
/// </summary>
public sealed record AdminUserDto
{
    public required long Id { get; init; }

    public required string Email { get; init; }

    public required string CreatedAt { get; init; }

    /// <summary>该账号拥有的角色（含显示名与是否系统角色），按 code 升序。</summary>
    public required IReadOnlyList<RoleDto> Roles { get; init; }
}

/// <summary>某个用户的角色绑定（读与写共用；写时未知 code 会被拒绝）。</summary>
public sealed record UserRolesDto
{
    public required IReadOnlyList<string> RoleCodes { get; init; }
}

/// <summary>
/// 「系统 → 角色管理」列表里的一条角色。
///
/// 两个计数都是**有效值**而不是原始绑定行数：<c>superadmin</c> 不落在 menu_roles 里，
/// 但它的授权由模型里的 matcher 短路承担（见 docs/PERMISSIONS.md 第四节），所以它的 <see cref="MenuCount"/>
/// 是全部菜单数——否则界面会把它显示成"一个菜单都没有"。
/// </summary>
public sealed record AdminRoleDto
{
    public required string Code { get; init; }

    public required string Title { get; init; }

    public required bool IsSystem { get; init; }

    /// <summary>持有该角色的账号数。</summary>
    public required int MemberCount { get; init; }

    /// <summary>该角色可访问的菜单数（superadmin 为全部菜单数）。</summary>
    public required int MenuCount { get; init; }
}

/// <summary>修改角色名。**刻意没有 code**：它是角色的身份（改它等于换掉全部绑定），不接受客户端传入。</summary>
public sealed record RenameRoleRequestDto
{
    public required string Title { get; init; }
}

/// <summary>某个角色绑定的菜单 code 列表（读与写共用）。</summary>
public sealed record RoleMenuCodesDto
{
    public required IReadOnlyList<string> MenuCodes { get; init; }
}
