using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Tests;

/// <summary>
/// 策略投影：从"菜单 + 动作 + 菜单-角色绑定 + 用户-角色绑定"算出 Casbin 的 p / g 行。
///
/// 这里锁的是两条最容易悄悄坏掉的约定：
/// <list type="number">
///   <item>**超级管理员的权限不落行**（由模型里的 matcher 短路承担）。投影只为真实授权出行；
///     若哪天有人"顺手"把超管的行补回来，或漏投影了某个角色的行，判定就悄悄变味——
///     而界面上只是某个按钮忽然点不动。</item>
///   <item>**键的格式**（<c>menu:</c> / <c>role:</c> / <c>user:</c>）。它同时是库里的列、接口里的
///     权限键、Casbin 的 sub/obj——格式一漂移，判定就再也命中不了。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class PermissionPolicyProjectionTests
{
    [TestMethod]
    public void Project_WithoutMenuRoleBindings_EmitsNoRows()
    {
        PolicyProjection projection = PermissionPolicyProjection.Project(
            [Menu(1, "pools", MenuActions.View, MenuActions.Edit)],
            [],
            []);

        // 超管不再落行（它的特权由 matcher 短路承担），没有绑定就是零行：
        // 策略集里只留真实授权，"每人一大片"的噪声从投影里就消失了。
        Assert.IsEmpty(projection.Policies, "没有任何菜单-角色绑定时不该出现策略行");
        Assert.IsEmpty(projection.Grouping, "没有用户-角色绑定时不该产生 g 行");
    }

    [TestMethod]
    public void Project_WithMenuRoleBinding_AddsRowsForThatRoleOnly()
    {
        Menu menu = Menu(7, "pools", MenuActions.View, MenuActions.Edit);

        PolicyProjection projection = PermissionPolicyProjection.Project(
            [menu],
            [new MenuRoleBinding(7, "editor")],
            []);

        // 只有 editor 的两条；超管的行不在这里（见上一测试）
        CollectionAssert.AreEquivalent(
            new List<PolicyRule>
            {
                new("role:editor", "menu:pools", "view"),
                new("role:editor", "menu:pools", "edit"),
            },
            projection.Policies.ToList());
    }

    [TestMethod]
    public void Project_WithSuperadminMenuRoleBinding_EmitsNoRows()
    {
        Menu menu = Menu(3, "graph", MenuActions.View);

        PolicyProjection projection = PermissionPolicyProjection.Project(
            [menu],
            [new MenuRoleBinding(3, PermissionSubjects.SuperadminRoleCode)],
            []);

        // 绑定表里出现超管也不落行：它的授权由 matcher 短路承担，落行只会制造与短路重复的噪声
        Assert.IsEmpty(projection.Policies, "超管的授权不落策略行");
    }

    [TestMethod]
    public void Project_WithUserRoleBinding_ProducesGroupingRule()
    {
        PolicyProjection projection = PermissionPolicyProjection.Project(
            [],
            [],
            [new UserRoleBinding(42, "editor")]);

        Assert.HasCount(1, projection.Grouping);
        Assert.AreEqual(new GroupingRule("user:42", "role:editor"), projection.Grouping[0]);
    }

    [TestMethod]
    public void Project_WithOrphanMenuRoleBinding_ProducesNoPolicyRow()
    {
        // 绑定指向一个不存在的菜单（菜单被删了但清理没跑到）：不该凭空造出策略
        PolicyProjection projection = PermissionPolicyProjection.Project(
            [Menu(1, "pools", MenuActions.View)],
            [new MenuRoleBinding(999, "editor")],
            []);

        Assert.IsEmpty(projection.Policies, "孤儿绑定不该产生任何策略行");
    }

    [TestMethod]
    public void PermissionSubjects_UseStableKeyFormats()
    {
        Assert.AreEqual("menu:system-menus", PermissionSubjects.ForMenu("system-menus"));
        Assert.AreEqual("role:superadmin", PermissionSubjects.ForRole(PermissionSubjects.SuperadminRoleCode));
        Assert.AreEqual("user:123", PermissionSubjects.ForUser(123));

        // 强制点用的那个常量要和"前缀 + 菜单 code"对得上；Api 测试会端到端地验证它确实命中策略
        Assert.AreEqual(PermissionKeys.SystemMenus, PermissionSubjects.ForMenu("system-menus"));
    }

    private static Menu Menu(long id, string code, params string[] actions) => new()
    {
        Id = id,
        ParentId = null,
        Code = code,
        Title = code,
        Path = $"/{code}",
        Icon = null,
        Kind = MenuKinds.Menu,
        SortOrder = 0,
        IsVisible = true,
        PermissionKey = PermissionSubjects.ForMenu(code),
        Actions = actions,
        CreatedAt = LighterTimestamp.FromMilliseconds(0),
    };
}
