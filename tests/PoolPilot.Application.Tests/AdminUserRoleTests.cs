using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 「用户管理」「角色管理」两组用例。
///
/// 断言集中在会**静默变坏**的几条：未知 code 必须点名回绝、最后一个超管不能被摘掉
/// （判据是"库里还有没有别的超管"，不是"能不能改自己"）、系统角色与有成员的角色不可删、
/// 以及每次写之后都触发了一次策略重建。
/// </summary>
[TestClass]
public sealed class AdminUserRoleTests
{
    [TestMethod]
    public async Task ListAdminUsers_OrdersByIdAttachesRolesAndFormatsIsoUtc()
    {
        Harness h = new();

        User first = await h.Users.CreateAsync(new User
        {
            Email = EmailAddress.Parse("first@example.com"),
            PasswordHash = PasswordHash.FromPassword("correct horse"),
            CreatedAt = LighterTimestamp.FromMilliseconds(1_700_000_000_123),
        });

        User second = h.Users.Seed("second@example.com", "correct horse");

        h.Roles.Seed(PermissionSubjects.SuperadminRoleCode, "超级管理员", isSystem: true);
        h.Roles.Seed("editor", "编辑");

        await h.UserRoles.AddAsync(first.Id, "editor");
        await h.UserRoles.AddAsync(second.Id, PermissionSubjects.SuperadminRoleCode);

        IReadOnlyList<AdminUserDto> list = await h.ListUsers.ExecuteAsync();

        Assert.HasCount(2, list);
        Assert.AreEqual(first.Id, list[0].Id, "按 id 升序");
        Assert.AreEqual("first@example.com", list[0].Email);
        Assert.AreEqual("2023-11-14T22:13:20.123Z", list[0].CreatedAt, "createdAt 必须是 ISO 8601 的 UTC 字面量");

        RoleDto editor = Assert.ContainsSingle(list[0].Roles);
        Assert.AreEqual("editor", editor.Code);
        Assert.AreEqual("编辑", editor.Title);
        Assert.IsFalse(editor.IsSystem);

        RoleDto superadmin = Assert.ContainsSingle(list[1].Roles);
        Assert.AreEqual(PermissionSubjects.SuperadminRoleCode, superadmin.Code);
        Assert.IsTrue(superadmin.IsSystem);
    }

    [TestMethod]
    public async Task SetUserRoles_WithUnknownCode_NamesItAndDoesNotTouchAnything()
    {
        Harness h = new();
        User user = h.Users.Seed("a@example.com", "correct horse");
        h.Roles.Seed("editor", "编辑");
        await h.UserRoles.AddAsync(user.Id, "editor");

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.SetUserRoles.ExecuteAsync(user.Id, new UserRolesDto { RoleCodes = ["nobody"] }));

        StringAssert.Contains(error.Message, "nobody", "错误必须点名是哪个 code");
        Assert.AreEqual(0, h.Policies.RebuildCount, "没写成功就不该重建");
        Assert.AreEqual("editor", Assert.ContainsSingle(h.UserRoles.Bindings).RoleCode, "拒绝时不该动绑定");
    }

    [TestMethod]
    public async Task SetUserRoles_ForMissingUser_Throws()
    {
        Harness h = new();

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.SetUserRoles.ExecuteAsync(42, new UserRolesDto { RoleCodes = [] }));

        StringAssert.Contains(error.Message, "42");
        Assert.AreEqual(0, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task SetUserRoles_RemovingTheLastSuperadmin_IsRejected()
    {
        Harness h = new();
        User only = h.Users.Seed("admin@example.com", "correct horse");
        h.Roles.Seed(PermissionSubjects.SuperadminRoleCode, "超级管理员", isSystem: true);
        await h.UserRoles.AddAsync(only.Id, PermissionSubjects.SuperadminRoleCode);

        // 判据是"库里还有没有别的超管"，不是"能不能改自己"：这里改的就是自己，且是唯一一个
        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.SetUserRoles.ExecuteAsync(only.Id, new UserRolesDto { RoleCodes = [] }));

        StringAssert.Contains(error.Message, "唯一的管理员");
        Assert.AreEqual(0, h.Policies.RebuildCount);
        Assert.AreEqual(
            PermissionSubjects.SuperadminRoleCode,
            Assert.ContainsSingle(h.UserRoles.Bindings).RoleCode,
            "拒绝之后超管绑定必须原样保留");
    }

    [TestMethod]
    public async Task SetUserRoles_RemovingSuperadminWhenAnotherExists_IsAllowed()
    {
        Harness h = new();
        User first = h.Users.Seed("first@example.com", "correct horse");
        User second = h.Users.Seed("second@example.com", "correct horse");
        h.Roles.Seed(PermissionSubjects.SuperadminRoleCode, "超级管理员", isSystem: true);
        await h.UserRoles.AddAsync(first.Id, PermissionSubjects.SuperadminRoleCode);
        await h.UserRoles.AddAsync(second.Id, PermissionSubjects.SuperadminRoleCode);

        await h.SetUserRoles.ExecuteAsync(first.Id, new UserRolesDto { RoleCodes = [] });

        Assert.IsEmpty(
            h.UserRoles.Bindings.Where(b => b.UserId == first.Id),
            "还有别的超管时，摘掉自己的超管角色必须放行");
        Assert.AreEqual(
            PermissionSubjects.SuperadminRoleCode,
            Assert.ContainsSingle(h.UserRoles.Bindings).RoleCode,
            "另一个超管不受影响");
        Assert.AreEqual(1, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task ListAdminRoles_CountsMembersAndMenusPerRole()
    {
        Harness h = new();
        h.Roles.Seed(PermissionSubjects.SuperadminRoleCode, "超级管理员", isSystem: true);
        h.Roles.Seed("editor", "编辑");

        Menu graph = h.Menus.Seed("graph", sortOrder: 10);
        h.Menus.Seed("pools", sortOrder: 20);

        await h.UserRoles.AddAsync(1, "editor");
        await h.UserRoles.AddAsync(2, "editor");
        await h.UserRoles.AddAsync(1, PermissionSubjects.SuperadminRoleCode);

        // 超管永不落 menu_roles；editor 只绑到 graph 一个菜单
        await h.MenuRoles.ReplaceForRoleAsync("editor", [graph.Id]);

        IReadOnlyList<AdminRoleDto> roles = await h.ListRoles.ExecuteAsync();

        AdminRoleDto superadmin = roles.Single(r => r.Code == PermissionSubjects.SuperadminRoleCode);
        Assert.AreEqual(1, superadmin.MemberCount);
        Assert.AreEqual(2, superadmin.MenuCount, "超管的菜单数是全部菜单数（它的授权由 matcher 短路承担）");

        AdminRoleDto editor = roles.Single(r => r.Code == "editor");
        Assert.AreEqual(2, editor.MemberCount, "成员数数的是人，不是绑定行");
        Assert.AreEqual(1, editor.MenuCount, "菜单数数的是绑定的菜单");
        Assert.IsFalse(editor.IsRegistrationDefault, "没指定过默认角色，列表必须如实反映");
    }

    [TestMethod]
    public async Task RegistrationDefault_SetMovesTheFlagAndRebuilds()
    {
        Harness h = new();
        h.Roles.Seed("editor", "编辑");
        h.Roles.Seed("viewer", "只读");

        await h.RegistrationDefault.SetAsync("editor");

        Assert.IsTrue(h.Roles.Roles.Single(r => r.Code == "editor").IsRegistrationDefault);
        Assert.IsFalse(h.Roles.Roles.Single(r => r.Code == "viewer").IsRegistrationDefault);
        Assert.AreEqual(1, h.Policies.RebuildCount);

        // 换一个角色：旧的必须自动清掉，库里永远只有一个默认
        await h.RegistrationDefault.SetAsync("viewer");

        Assert.IsFalse(h.Roles.Roles.Single(r => r.Code == "editor").IsRegistrationDefault);
        Assert.IsTrue(h.Roles.Roles.Single(r => r.Code == "viewer").IsRegistrationDefault);
        Assert.AreEqual(2, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task RegistrationDefault_ForSystemRole_IsRejected()
    {
        Harness h = new();
        h.Roles.Seed(PermissionSubjects.SuperadminRoleCode, "超级管理员", isSystem: true);

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.RegistrationDefault.SetAsync(PermissionSubjects.SuperadminRoleCode));

        StringAssert.Contains(error.Message, "系统角色");
        Assert.IsFalse(h.Roles.Roles.Single().IsRegistrationDefault);
        Assert.AreEqual(0, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task RegistrationDefault_ForMissingRole_Throws()
    {
        Harness h = new();

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.RegistrationDefault.SetAsync("ghost"));

        StringAssert.Contains(error.Message, "ghost");
        Assert.AreEqual(0, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task RegistrationDefault_ClearRemovesTheFlagAndRebuilds()
    {
        Harness h = new();
        h.Roles.Seed("editor", "编辑", isRegistrationDefault: true);

        await h.RegistrationDefault.ClearAsync("editor");

        Assert.IsFalse(h.Roles.Roles.Single().IsRegistrationDefault);
        Assert.AreEqual(1, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task DeleteRole_ForRegistrationDefault_IsRejected()
    {
        Harness h = new();
        h.Roles.Seed("editor", "编辑", isRegistrationDefault: true);

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.DeleteRole.ExecuteAsync("editor"));

        StringAssert.Contains(error.Message, "注册默认");
        Assert.HasCount(1, h.Roles.Roles, "拒绝删除时不该动任何数据");
        Assert.AreEqual(0, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task RenameRole_UpdatesTitleAndRebuilds()
    {
        Harness h = new();
        h.Roles.Seed("editor", "旧名");

        await h.RenameRole.ExecuteAsync("editor", new RenameRoleRequestDto { Title = "  新名  " });

        Assert.AreEqual("新名", h.Roles.Roles.Single().Title, "标题应当去空白后落库");
        Assert.AreEqual(1, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task RenameRole_WithEmptyTitle_Throws()
    {
        Harness h = new();
        h.Roles.Seed("editor", "旧名");

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.RenameRole.ExecuteAsync("editor", new RenameRoleRequestDto { Title = "   " }));

        Assert.AreEqual("旧名", h.Roles.Roles.Single().Title);
        Assert.AreEqual(0, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task RenameRole_ForMissingRole_Throws()
    {
        Harness h = new();

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.RenameRole.ExecuteAsync("ghost", new RenameRoleRequestDto { Title = "新名" }));

        StringAssert.Contains(error.Message, "ghost");
    }

    [TestMethod]
    public async Task DeleteRole_ForSystemRole_IsRejected()
    {
        Harness h = new();
        h.Roles.Seed(PermissionSubjects.SuperadminRoleCode, "超级管理员", isSystem: true);

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.DeleteRole.ExecuteAsync(PermissionSubjects.SuperadminRoleCode));

        StringAssert.Contains(error.Message, "系统角色");
        Assert.HasCount(1, h.Roles.Roles, "拒绝删除时不该动任何数据");
        Assert.AreEqual(0, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task DeleteRole_WithMembers_NamesTheCountAndTellsToUnbindFirst()
    {
        Harness h = new();
        h.Roles.Seed("editor", "编辑");
        await h.UserRoles.AddAsync(1, "editor");
        await h.UserRoles.AddAsync(2, "editor");

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.DeleteRole.ExecuteAsync("editor"));

        StringAssert.Contains(error.Message, "2", "消息里要带人数");
        StringAssert.Contains(error.Message, "先解绑用户");
        Assert.HasCount(1, h.Roles.Roles);
        Assert.AreEqual(0, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task DeleteRole_ForMissingRole_Throws()
    {
        Harness h = new();

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.DeleteRole.ExecuteAsync("ghost"));

        StringAssert.Contains(error.Message, "ghost");
    }

    [TestMethod]
    public async Task DeleteRole_WithoutMembers_DeletesAndRebuilds()
    {
        Harness h = new();
        h.Roles.Seed("editor", "编辑");

        await h.DeleteRole.ExecuteAsync("editor");

        Assert.IsEmpty(h.Roles.Roles);
        Assert.AreEqual(1, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task GetRoleMenus_ReturnsBoundMenuCodesOrderedBySortOrder()
    {
        Harness h = new();
        h.Roles.Seed("editor", "编辑");
        Menu graph = h.Menus.Seed("graph", sortOrder: 10);
        Menu pools = h.Menus.Seed("pools", sortOrder: 20);
        Menu leads = h.Menus.Seed("leads", sortOrder: 30);

        // 故意乱序输入：输出必须按 menus.sortOrder 排
        await h.MenuRoles.ReplaceForRoleAsync("editor", [leads.Id, graph.Id]);

        RoleMenuCodesDto result = await h.GetRoleMenus.ExecuteAsync("editor");

        CollectionAssert.AreEqual(new List<string> { "graph", "leads" }, result.MenuCodes.ToList());
    }

    [TestMethod]
    public async Task GetRoleMenus_ForSuperadmin_ReturnsAllMenus()
    {
        Harness h = new();
        h.Roles.Seed(PermissionSubjects.SuperadminRoleCode, "超级管理员", isSystem: true);
        h.Menus.Seed("graph", sortOrder: 10);
        h.Menus.Seed("pools", sortOrder: 20);

        RoleMenuCodesDto result = await h.GetRoleMenus.ExecuteAsync(PermissionSubjects.SuperadminRoleCode);

        CollectionAssert.AreEqual(new List<string> { "graph", "pools" }, result.MenuCodes.ToList());
    }

    [TestMethod]
    public async Task GetRoleMenus_ForMissingRole_Throws()
    {
        Harness h = new();

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.GetRoleMenus.ExecuteAsync("ghost"));

        StringAssert.Contains(error.Message, "ghost");
    }

    [TestMethod]
    public async Task SetRoleMenus_ReplacesBindingAndRebuilds()
    {
        Harness h = new();
        h.Roles.Seed("editor", "编辑");
        Menu graph = h.Menus.Seed("graph");
        h.Menus.Seed("pools");
        await h.MenuRoles.ReplaceForRoleAsync("editor", [graph.Id]);

        await h.SetRoleMenus.ExecuteAsync("editor", new RoleMenuCodesDto { MenuCodes = ["pools"] });

        MenuRoleBinding stored = Assert.ContainsSingle(h.MenuRoles.Bindings);
        Assert.AreEqual("editor", stored.RoleCode);
        Assert.AreEqual("pools", h.Menus.Menus.Single(m => m.Id == stored.MenuId).Code);
        Assert.AreEqual(1, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task SetRoleMenus_ForSuperadmin_IsRejected()
    {
        Harness h = new();
        h.Roles.Seed(PermissionSubjects.SuperadminRoleCode, "超级管理员", isSystem: true);
        Menu graph = h.Menus.Seed("graph");

        // 超管的授权由 matcher 短路承担、不落绑定表：整表替换在这里结构上说不通
        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.SetRoleMenus.ExecuteAsync(
                PermissionSubjects.SuperadminRoleCode,
                new RoleMenuCodesDto { MenuCodes = ["graph"] }));

        StringAssert.Contains(error.Message, "投影");
        Assert.IsEmpty(h.MenuRoles.Bindings);
        Assert.AreEqual(0, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task SetRoleMenus_WithUnknownMenuCode_NamesIt()
    {
        Harness h = new();
        h.Roles.Seed("editor", "编辑");
        h.Menus.Seed("graph");

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.SetRoleMenus.ExecuteAsync("editor", new RoleMenuCodesDto { MenuCodes = ["graph", "nope"] }));

        StringAssert.Contains(error.Message, "nope");
        Assert.IsEmpty(h.MenuRoles.Bindings);
        Assert.AreEqual(0, h.Policies.RebuildCount);
    }

    [TestMethod]
    public async Task SetRoleMenus_ForMissingRole_Throws()
    {
        Harness h = new();

        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => h.SetRoleMenus.ExecuteAsync("ghost", new RoleMenuCodesDto { MenuCodes = [] }));

        StringAssert.Contains(error.Message, "ghost");
    }

    private sealed class Harness
    {
        public FakeUserRepository Users { get; } = new();

        public FakeMenuRepository Menus { get; } = new();

        public FakeRoleRepository Roles { get; } = new();

        public FakeUserRoleRepository UserRoles { get; } = new();

        public FakeMenuRoleRepository MenuRoles { get; } = new();

        public FakePolicyWriter Policies { get; } = new();

        public ListAdminUsersUseCase ListUsers { get; }

        public SetUserRolesUseCase SetUserRoles { get; }

        public ListAdminRolesUseCase ListRoles { get; }

        public RenameRoleUseCase RenameRole { get; }

        public DeleteRoleUseCase DeleteRole { get; }

        public RegistrationDefaultRoleUseCase RegistrationDefault { get; }

        public GetRoleMenusUseCase GetRoleMenus { get; }

        public SetRoleMenusUseCase SetRoleMenus { get; }

        public Harness()
        {
            ListUsers = new ListAdminUsersUseCase(Users, UserRoles, Roles);
            SetUserRoles = new SetUserRolesUseCase(Users, Roles, UserRoles, Policies);
            ListRoles = new ListAdminRolesUseCase(Roles, UserRoles, MenuRoles, Menus);
            RenameRole = new RenameRoleUseCase(Roles, Policies);
            DeleteRole = new DeleteRoleUseCase(Roles, UserRoles, Policies);
            RegistrationDefault = new RegistrationDefaultRoleUseCase(Roles, Policies);
            GetRoleMenus = new GetRoleMenusUseCase(Roles, Menus, MenuRoles);
            SetRoleMenus = new SetRoleMenusUseCase(Roles, Menus, MenuRoles, Policies);
        }
    }
}
