using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 启动播种：内置菜单按 code **缺一补一**（存在即跳过、缺失才插入，幂等）。
///
/// 为什么不是"只在空库播种"：内置菜单现在不可删（见 DeleteMenuUseCase 的守卫），
/// 于是"库里没有这条"只有一个原因——这个库比代码老或刚建出来。一套"缺就补"就够，
/// 新加内置菜单时也不必再维护第二份"后加清单"。
/// </summary>
[TestClass]
public sealed class AuthorizationSeedTests
{
    [TestMethod]
    public async Task Seed_OnEmptyDatabase_AddsEveryBuiltInMenu()
    {
        Harness h = new();

        AuthorizationSeedResult result = await h.Seed.ExecuteAsync();

        Assert.AreEqual(h.Menus.Menus.Count, result.AddedMenuCodes.Count, "空库应当把内置菜单全部建出来");
        Assert.IsGreaterThanOrEqualTo(15, result.AddedMenuCodes.Count);

        Menu users = h.Menus.Menus.Single(m => m.Code == "system-users");
        Menu roles = h.Menus.Menus.Single(m => m.Code == "system-roles");
        Menu system = h.Menus.Menus.Single(m => m.Code == "system");

        Assert.AreEqual(system.Id, users.ParentId, "挂在「系统」分组下");
        Assert.AreEqual(system.Id, roles.ParentId);
        CollectionAssert.AreEqual(
            new List<string> { MenuActions.View, MenuActions.Edit },
            users.Actions.ToList());
        CollectionAssert.AreEqual(
            new List<string> { MenuActions.View, MenuActions.Edit, MenuActions.Delete },
            roles.Actions.ToList());
        Assert.AreEqual("menu:system-users", users.PermissionKey);
        Assert.AreEqual("menu:system-roles", roles.PermissionKey);
    }

    [TestMethod]
    public async Task Seed_OnNonEmptyDatabase_AddsOnlyTheMissingMenusAndIsIdempotent()
    {
        Harness h = new();
        Menu system = h.Menus.Seed("system", kind: MenuKinds.Group, sortOrder: 0);
        h.Menus.Seed("system-menus", parentId: system.Id, sortOrder: 10);
        h.Menus.Seed("graph", sortOrder: 20);

        AuthorizationSeedResult first = await h.Seed.ExecuteAsync();

        Assert.DoesNotContain("system-menus", first.AddedMenuCodes, "已存在的行不动");
        Assert.DoesNotContain("graph", first.AddedMenuCodes);
        Assert.Contains("system-users", first.AddedMenuCodes, "缺的补上");

        Menu users = h.Menus.Menus.Single(m => m.Code == "system-users");
        Assert.AreEqual(system.Id, users.ParentId, "补的节点挂在**现有的**「系统」分组下");
        Assert.AreEqual("/system/users", users.Path);
        Assert.AreEqual("IconUser", users.Icon);
        Assert.AreEqual(11, users.SortOrder);

        Menu roles = h.Menus.Menus.Single(m => m.Code == "system-roles");
        Assert.AreEqual(system.Id, roles.ParentId);
        Assert.AreEqual(12, roles.SortOrder);

        // 再跑一次：存在即跳过，不重复插入
        AuthorizationSeedResult second = await h.Seed.ExecuteAsync();

        Assert.IsEmpty(second.AddedMenuCodes, "第二次启动必须无事可做（幂等）");
        Assert.HasCount(15, h.Menus.Menus);
    }

    [TestMethod]
    public async Task Seed_RestoresBuiltInMenuMissingFromTheDatabase()
    {
        Harness h = new();
        h.Menus.Seed("system", kind: MenuKinds.Group);
        h.Menus.Seed("system-menus");
        h.Menus.Seed("graph");
        // 库里少了一个内置菜单（如直改库删掉了）：启动时补回来。
        // 界面上删不掉它——内置菜单有"不可删"的守卫，所以"缺"只可能是库比代码老。
        await h.Seed.ExecuteAsync();

        Assert.IsTrue(h.Menus.Menus.Any(m => m.Code == "notifications"), "缺失的内置菜单应被补回");
    }

    private sealed class Harness
    {
        public FakeMenuRepository Menus { get; } = new();

        public FakeRoleRepository Roles { get; } = new();

        public FakeUserRepository Users { get; } = new();

        public FakeUserRoleRepository UserRoles { get; } = new();

        public FakePolicyWriter Policies { get; } = new();

        public SeedAuthorizationUseCase Seed { get; }

        public Harness()
        {
            FakeClock clock = new(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero));

            Seed = new SeedAuthorizationUseCase(Roles, Menus, Users, UserRoles, Policies, clock);
        }
    }
}
