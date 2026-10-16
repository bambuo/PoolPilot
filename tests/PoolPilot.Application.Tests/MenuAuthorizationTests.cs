using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Repositories;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 菜单管理用例：建 / 改 / 删 / 角色绑定 / 菜单树过滤。
///
/// 断言集中在会**静默变坏**的几条：删父节点该拒绝（而不是连坐删子树）、
/// 父节点不能落到自己的子树里（否则树成环）、权限键由服务端生成（而不是信客户端）、
/// 以及每一次写之后都触发了一次策略重建。
/// </summary>
[TestClass]
public sealed class MenuAuthorizationTests
{
    [TestMethod]
    public async Task CreateMenu_GeneratesPermissionKeyFromCodeAndRebuilds()
    {
        Harness h = new();

        MenuNodeDto created = await h.Create.ExecuteAsync(Request("reports", MenuKinds.Menu, ["view", "edit"]));

        Assert.AreEqual("reports", created.Code);
        Assert.AreEqual("menu:reports", h.Menus.Menus.Single().PermissionKey, "权限键必须由服务端按 menu:{code} 生成");
        Assert.AreEqual(1, h.Policies.RebuildCount, "写操作之后必须重建一次策略");
    }

    [TestMethod]
    public async Task CreateMenu_WithDuplicateCode_Throws()
    {
        Harness h = new();
        h.Menus.Seed("reports");

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Create.ExecuteAsync(Request("reports", MenuKinds.Menu, ["view"])));

        Assert.AreEqual(0, h.Policies.RebuildCount, "没写成功就不该重建");
    }

    [TestMethod]
    public async Task CreateMenu_GroupWithPath_IsRejected()
    {
        Harness h = new();

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Create.ExecuteAsync(Request("grp", MenuKinds.Group, ["view"]) with { Path = "/grp" }));

        StringAssert.Contains(error.Message, "分组");
    }

    [TestMethod]
    public async Task UpdateMenu_WithItselfAsParent_Throws()
    {
        Harness h = new();
        var menu = h.Menus.Seed("reports");

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Update.ExecuteAsync(menu.Id, Request("reports", MenuKinds.Menu, ["view"]) with { ParentId = menu.Id }));
    }

    [TestMethod]
    public async Task UpdateMenu_WithOwnDescendantAsParent_Throws()
    {
        Harness h = new();
        var parent = h.Menus.Seed("pools", parentId: null);
        var child = h.Menus.Seed("pool-detail", parentId: parent.Id);

        // 把父节点挂到它自己的子节点下面 → 成环
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Update.ExecuteAsync(parent.Id, Request("pools", MenuKinds.Menu, ["view"]) with { ParentId = child.Id }));
    }

    [TestMethod]
    public async Task DeleteMenu_WithChildren_ThrowsAndDoesNotRebuild()
    {
        Harness h = new();
        // 用自定义 code：内置 code 会被"内置菜单不可删"那道守卫先拦下，就测不到"有子节点"这条规则了
        var parent = h.Menus.Seed("reports");
        h.Menus.Seed("reports-detail", parentId: parent.Id);

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Delete.ExecuteAsync(parent.Id));

        StringAssert.Contains(error.Message, "子节点");
        Assert.HasCount(2, h.Menus.Menus, "拒绝删除时不该动任何数据");
        Assert.AreEqual(0, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task UpdateMenu_RenamingBuiltInCode_IsRejected()
    {
        Harness h = new();
        var menu = h.Menus.Seed("graph");

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Update.ExecuteAsync(menu.Id, Request("dashboard", MenuKinds.Menu, [MenuActions.View])));

        StringAssert.Contains(error.Message, "编码不可修改");
        Assert.AreEqual(0, h.Policies.RebuildCount, "拒绝时不该重建策略");
    }

    [TestMethod]
    public async Task UpdateMenu_EditingBuiltInWithoutRenaming_IsAllowed()
    {
        Harness h = new();
        var menu = h.Menus.Seed("graph");

        MenuNodeDto updated = await h.Update.ExecuteAsync(
            menu.Id,
            Request("graph", MenuKinds.Menu, [MenuActions.View]) with { Title = "换个标题" });

        Assert.AreEqual("换个标题", updated.Title);
        Assert.AreEqual(1, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task DeleteMenu_ForBuiltIn_IsRejected()
    {
        Harness h = new();
        var menu = h.Menus.Seed("backtest");

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.Delete.ExecuteAsync(menu.Id));

        StringAssert.Contains(error.Message, "不可删除");
        Assert.HasCount(1, h.Menus.Menus, "拒绝删除时那份数据还在");
        Assert.AreEqual(0, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task DeleteMenu_WithoutChildren_Rebuilds()
    {
        Harness h = new();
        var menu = h.Menus.Seed("reports");

        await h.Delete.ExecuteAsync(menu.Id);

        Assert.IsEmpty(h.Menus.Menus);
        Assert.AreEqual(1, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task SetMenuRoles_KeepsSuperadminAndStoresOnlyAssignedRoles()
    {
        Harness h = new();
        var menu = h.Menus.Seed("pools");
        h.Roles.Seed("editor", "编辑");

        MenuRolesDto result = await h.SetRoles.ExecuteAsync(menu.Id, new MenuRolesDto { RoleCodes = ["editor"] });

        // 超管常驻在最前，且不可被移除
        CollectionAssert.AreEqual(new List<string> { "superadmin", "editor" }, result.RoleCodes.ToList());

        // 但落到 menu_roles 的只有 editor —— 超管的 p 行是投影常驻补的，不落这张表
        MenuRoleBinding stored = Assert.ContainsSingle(h.MenuRoles.Bindings);
        Assert.AreEqual("editor", stored.RoleCode);
        Assert.AreEqual(menu.Id, stored.MenuId);
        Assert.AreEqual(1, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task SetMenuRoles_WithUnknownRole_Throws()
    {
        Harness h = new();
        var menu = h.Menus.Seed("pools");

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.SetRoles.ExecuteAsync(menu.Id, new MenuRolesDto { RoleCodes = ["nobody"] }));

        Assert.AreEqual(0, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task GetMenuTree_OnlyContainsNodesWithViewPermission()
    {
        Harness h = new();
        h.Menus.Seed("graph", sortOrder: 0);
        // 菜单的"动作"是它自己声明的：授予的动作必须落在菜单声明的集合里才成立
        // （列举按候选逐项判定，候选来自菜单表——见 FakePermissionEvaluator 的注释）
        h.Menus.Seed("pools", sortOrder: 1, actions: [MenuActions.View, MenuActions.Edit]);
        h.Permissions.Grant("menu:pools", MenuActions.View, MenuActions.Edit);

        IReadOnlyList<MenuNodeDto> tree = await h.Tree.ExecuteAsync();

        MenuNodeDto only = Assert.ContainsSingle(tree);
        Assert.AreEqual("pools", only.Code);
        CollectionAssert.AreEqual(
            new List<string> { MenuActions.View, MenuActions.Edit },
            only.Actions.ToList(),
            "节点的动作是当前用户拥有的那些");
    }

    [TestMethod]
    public async Task GetMenuTree_WithNoPermissions_IsEmpty()
    {
        Harness h = new();
        h.Menus.Seed("graph");

        Assert.IsEmpty(await h.Tree.ExecuteAsync());
    }

    [TestMethod]
    public async Task GetMenuTree_PromotesOrphanWhenParentIsNotPermitted()
    {
        Harness h = new();
        var group = h.Menus.Seed("system", kind: MenuKinds.Group);
        h.Menus.Seed("system-menus", parentId: group.Id);

        // 只给了子节点的权限：它不该因为父节点不可见而消失
        h.Permissions.Grant("menu:system-menus", MenuActions.View);

        MenuNodeDto node = Assert.ContainsSingle(await h.Tree.ExecuteAsync());
        Assert.AreEqual("system-menus", node.Code);
    }

    [TestMethod]
    public async Task GetPermissions_ReturnsRolesAndGrantedActions()
    {
        Harness h = new();
        h.Current.UserId = 5;
        h.Roles.Seed(PermissionSubjects.SuperadminRoleCode, "超级管理员", isSystem: true);
        h.UserRoles.AddAsync(5, PermissionSubjects.SuperadminRoleCode).GetAwaiter().GetResult();
        // 权限清单按"候选 = 菜单表里的权限点"逐项判定，所以权限点必须真的存在、
        // 且要授予的动作必须在该菜单声明的动作集合里（与真实现同口径）
        h.Menus.Seed("system-menus", actions: [MenuActions.View, MenuActions.Edit, MenuActions.Delete]);
        h.Permissions.Grant("menu:system-menus", MenuActions.View, MenuActions.Edit, MenuActions.Delete);

        PermissionsDto result = await h.GetPermissions.ExecuteAsync();

        Assert.AreEqual("superadmin", Assert.ContainsSingle(result.Roles).Code);
        PermissionGrantDto grant = Assert.ContainsSingle(result.Permissions);
        Assert.AreEqual("menu:system-menus", grant.PermissionKey);
        CollectionAssert.AreEqual(
            new List<string> { MenuActions.View, MenuActions.Edit, MenuActions.Delete },
            grant.Actions.ToList());
    }

    private static MenuUpsertRequestDto Request(string code, string kind, IReadOnlyList<string> actions) => new()
    {
        ParentId = null,
        Code = code,
        Title = code,
        Path = kind == MenuKinds.Group ? null : $"/{code}",
        Icon = null,
        Kind = kind,
        SortOrder = 0,
        IsVisible = true,
        Actions = actions,
    };

    private sealed class Harness
    {
        public FakeMenuRepository Menus { get; } = new();

        public FakeRoleRepository Roles { get; } = new();

        public FakeUserRoleRepository UserRoles { get; } = new();

        public FakeMenuRoleRepository MenuRoles { get; } = new();

        public FakePermissionEvaluator Permissions { get; } = new();

        public FakeCurrentUser Current { get; } = new();

        public FakePolicyWriter Policies { get; } = new();

        public CreateMenuUseCase Create { get; }

        public UpdateMenuUseCase Update { get; }

        public DeleteMenuUseCase Delete { get; }

        public SetMenuRolesUseCase SetRoles { get; }

        public GetMenuTreeUseCase Tree { get; }

        public GetPermissionsUseCase GetPermissions { get; }

        public Harness()
        {
            FakeClock clock = new(new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero));

            Create = new CreateMenuUseCase(Menus, Policies, clock);
            Update = new UpdateMenuUseCase(Menus, Policies);
            Delete = new DeleteMenuUseCase(Menus, Policies);
            SetRoles = new SetMenuRolesUseCase(Menus, Roles, MenuRoles, Policies);
            Tree = new GetMenuTreeUseCase(Menus, Permissions);
            GetPermissions = new GetPermissionsUseCase(Permissions, Menus, UserRoles, Roles, Current);
        }
    }
}
