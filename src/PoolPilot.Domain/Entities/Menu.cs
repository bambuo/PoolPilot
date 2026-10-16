using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 一个菜单节点。**菜单是数据，不是代码里的常量**：
/// 加一个页面、挪一次层级、改一次权限粒度，都是往这几张表里写一行，不是改代码重发。
///
/// 它同时是权限点的载体：<see cref="PermissionKey"/>（<c>menu:{Code}</c>）是 Casbin 里的对象，
/// <see cref="Actions"/> 是这个菜单暴露的动作集合（权限点粒度 = 菜单 + 动作）。
///
/// <see cref="Kind"/> 取 <see cref="Constants.MenuKinds"/> 的三个值之一；
/// <see cref="ParentId"/> 为 <c>null</c> 表示根节点。
/// </summary>
public sealed record Menu
{
    public long Id { get; init; }

    /// <summary>父节点主键；<c>null</c> = 根节点。</summary>
    public long? ParentId { get; init; }

    /// <summary>稳定标识（同一套部署里唯一）。权限键由它推导，改它等于换权限点。</summary>
    public required string Code { get; init; }

    public required string Title { get; init; }

    /// <summary>路由路径，允许 <c>:id</c> 之类的模板（如 <c>/pools/:accountIndex</c>）；分组为 <c>null</c>。</summary>
    public string? Path { get; init; }

    /// <summary>前端图标名（如 <c>IconMenu</c>）；<c>null</c> = 不显示图标。</summary>
    public string? Icon { get; init; }

    /// <summary><c>group</c> / <c>menu</c> / <c>hidden</c>，见 <see cref="Constants.MenuKinds"/>。</summary>
    public required string Kind { get; init; }

    public required int SortOrder { get; init; }

    /// <summary>是否在导航里显示。分组与普通菜单为 <c>true</c>，详情页那类为 <c>false</c>。</summary>
    public required bool IsVisible { get; init; }

    /// <summary>Casbin 里的对象键，恒等于 <c>"menu:" + Code</c>。</summary>
    public required string PermissionKey { get; init; }

    /// <summary>这个菜单暴露的动作，取值域见 <see cref="Constants.MenuActions"/>。</summary>
    public required IReadOnlyList<string> Actions { get; init; }

    public required LighterTimestamp CreatedAt { get; init; }
}
