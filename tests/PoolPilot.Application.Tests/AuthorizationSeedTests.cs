using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 启动播种：空库全量播，老库（menus 非空）只走"后加的内置菜单"幂等补插。
///
/// 补插的关键性质有两条：**只补缺的**（存在即跳过）与**只补这几条**（不是内置菜单全量重播——
/// 那会让用户主动删掉的内置菜单每次重启复活）。
/// </summary>
[TestClass]
public sealed class AuthorizationSeedTests
{
    [TestMethod]
    public async Task Seed_OnEmptyDatabase_SeedsAllMenusWithoutBackfill()
    {
        Harness h = new();

        AuthorizationSeedResult result = await h.Seed.ExecuteAsync();

        Assert.IsGreaterThan(0, result.MenusSeeded, "空库应当播种内置菜单");
        Assert.IsEmpty(result.BackfilledMenuCodes, "空库已经拿到了那两个菜单，补插应无事可做");
        Assert.AreEqual(result.MenusSeeded, h.Menus.Menus.Count);

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
    public async Task Seed_OnNonEmptyDatabase_BackfillsOnlyTheLateAddedMenusAndIsIdempotent()
    {
        Harness h = new();
        Menu system = h.Menus.Seed("system", kind: MenuKinds.Group, sortOrder: 0);
        h.Menus.Seed("system-menus", parentId: system.Id, sortOrder: 10);
        h.Menus.Seed("graph", sortOrder: 20);

        AuthorizationSeedResult first = await h.Seed.ExecuteAsync();

        Assert.AreEqual(0, first.MenusSeeded, "menus 非空时不做全量播种");
        CollectionAssert.AreEqual(
            new List<string> { "system-users", "system-roles" },
            first.BackfilledMenuCodes.ToList());

        Menu users = h.Menus.Menus.Single(m => m.Code == "system-users");
        Assert.AreEqual(system.Id, users.ParentId, "补插的节点挂在现有的「系统」分组下");
        Assert.AreEqual("/system/users", users.Path);
        Assert.AreEqual("IconUser", users.Icon);
        Assert.AreEqual(MenuKinds.Menu, users.Kind);
        Assert.AreEqual(11, users.SortOrder);

        Menu roles = h.Menus.Menus.Single(m => m.Code == "system-roles");
        Assert.AreEqual(system.Id, roles.ParentId);
        Assert.AreEqual("/system/roles", roles.Path);
        Assert.AreEqual("IconUserGroup", roles.Icon);
        Assert.AreEqual(12, roles.SortOrder);

        // 再跑一次：存在即跳过，不重复插入
        AuthorizationSeedResult second = await h.Seed.ExecuteAsync();

        Assert.IsEmpty(second.BackfilledMenuCodes, "第二次启动必须无事可做（幂等）");
        Assert.HasCount(5, h.Menus.Menus);
    }

    [TestMethod]
    public async Task Seed_DoesNotResurrectDeletedBuiltInMenus()
    {
        Harness h = new();
        h.Menus.Seed("system", kind: MenuKinds.Group);
        h.Menus.Seed("system-menus");
        h.Menus.Seed("system-users");
        h.Menus.Seed("system-roles");
        // 用户主动删掉了一个别的内置菜单（如 graph）：重启时它不能复活
        await h.Seed.ExecuteAsync();

        Assert.IsFalse(h.Menus.Menus.Any(m => m.Code == "graph"), "补插只认后加的那几条 code");
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
