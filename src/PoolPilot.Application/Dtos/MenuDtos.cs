namespace PoolPilot.Application.Dtos;

/// <summary>
/// 菜单树的一个节点。前端按它注册路由、渲染导航、决定按钮是否可点。
///
/// <see cref="Actions"/> 的语义随接口而变：
/// <list type="bullet">
///   <item><c>GET /api/menus/tree</c>（我自己的树）：是**当前用户在这个菜单上拥有的动作**，
///     界面据此藏按钮。</item>
///   <item><c>GET /api/admin/menus</c>（管理视图）：是这个菜单**暴露的动作集合**，
///     编辑菜单时要能看见它有哪些动作。</item>
/// </list>
/// </summary>
public sealed record MenuNodeDto
{
    public required long Id { get; init; }

    public required string Code { get; init; }

    public required string Title { get; init; }

    public required string? Path { get; init; }

    public required string? Icon { get; init; }

    /// <summary><c>group</c> / <c>menu</c> / <c>hidden</c>。</summary>
    public required string Kind { get; init; }

    public required int SortOrder { get; init; }

    public required bool IsVisible { get; init; }

    public required IReadOnlyList<string> Actions { get; init; }

    public required IReadOnlyList<MenuNodeDto> Children { get; init; }
}

/// <summary>
/// 新建 / 修改菜单的请求体。
///
/// **刻意没有 PermissionKey**：权限键由服务端按 <c>"menu:" + code</c> 生成。
/// 让客户端传它就等于允许"code 与权限键对不上"这种状态存在，而那种错位不会被任何断言发现。
/// </summary>
public sealed record MenuUpsertRequestDto
{
    /// <summary>父节点主键；<c>null</c> = 根节点。</summary>
    public long? ParentId { get; init; }

    public required string Code { get; init; }

    public required string Title { get; init; }

    public string? Path { get; init; }

    public string? Icon { get; init; }

    public required string Kind { get; init; }

    public int SortOrder { get; init; }

    public bool IsVisible { get; init; }

    /// <summary>该菜单暴露的动作，元素取值域为 <c>view|edit|delete</c>。</summary>
    public required IReadOnlyList<string> Actions { get; init; }
}

/// <summary>某个菜单的角色绑定（读与写共用）。</summary>
public sealed record MenuRolesDto
{
    public required IReadOnlyList<string> RoleCodes { get; init; }
}

/// <summary>角色（读取表示）。</summary>
public sealed record RoleDto
{
    public required string Code { get; init; }

    public required string Title { get; init; }

    public required bool IsSystem { get; init; }
}

/// <summary>新建角色。<c>IsSystem</c> 由服务端定（新建的一律为 <c>false</c>），不接受客户端传。</summary>
public sealed record CreateRoleRequestDto
{
    public required string Code { get; init; }

    public required string Title { get; init; }
}

/// <summary><c>GET /api/auth/permissions</c> 的答复：当前用户的角色与逐权限点的动作。</summary>
public sealed record PermissionsDto
{
    public required IReadOnlyList<RoleDto> Roles { get; init; }

    public required IReadOnlyList<PermissionGrantDto> Permissions { get; init; }
}

/// <summary>一个权限点及其动作。</summary>
public sealed record PermissionGrantDto
{
    public required string PermissionKey { get; init; }

    public required IReadOnlyList<string> Actions { get; init; }
}
