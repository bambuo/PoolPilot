using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Constants;
using PoolPilot.Infrastructure.Persistence;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 「系统 → 菜单管理」的数据面，跑在真实管线里（路由、Cookie、授权中间件、异常中间件、
/// Casbin、SQLite 全是真的）。
///
/// 这一组测的是**接线**：全局"登录才可访问"与逐接口的权限点是否真的叠出了
/// 401 / 403 / 200 三种结果，以及"写操作之后策略真的被重建了"——
/// 后者只能端到端才看得见（单测里重建是假的）。
/// </summary>
[TestClass]
public sealed class MenuApiTests
{
    private const string OwnerEmail = "owner@example.com";
    private const string PlainEmail = "plain@example.com";
    private const string Password = "correct horse";

    [TestMethod]
    public async Task AdminMenus_Is401WhenAnonymous()
    {
        using AuthTestApp app = new();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync(new Uri("/api/admin/menus", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "error");
    }

    [TestMethod]
    public async Task AdminMenus_Is200ForSuperadminAndListsSeededMenus()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await SignInAsync(app, OwnerEmail);

        HttpResponseMessage response = await owner.GetAsync(new Uri("/api/admin/menus", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        IReadOnlyList<MenuNodeDto> tree = (await response.Content.ReadFromJsonAsync<List<MenuNodeDto>>())!;
        Assert.IsTrue(Flatten(tree).Any(n => n.Code == "system-menus"), "管理视图应含播种出来的菜单");
        Assert.IsTrue(Flatten(tree).Any(n => n.Kind == MenuKinds.Group), "管理视图应含分组节点");
    }

    [TestMethod]
    public async Task AdminMenus_Is403ForAccountWithoutThePermission()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await SignInAsync(app, OwnerEmail);
        using HttpClient plain = await SignInAsync(app, PlainEmail);

        // 先确认这个账号是"登录着但没权限"，而不是没登录
        Assert.AreEqual(HttpStatusCode.OK, (await plain.GetAsync(new Uri("/api/menus/tree", UriKind.Relative))).StatusCode);

        HttpResponseMessage denied = await plain.GetAsync(new Uri("/api/admin/menus", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        StringAssert.Contains(await denied.Content.ReadAsStringAsync(), "error");

        // 业主自己当然是通的（否则这条断言就只是"路径写错了"）
        Assert.AreEqual(HttpStatusCode.OK, (await owner.GetAsync(new Uri("/api/admin/menus", UriKind.Relative))).StatusCode);
    }

    [TestMethod]
    public async Task MenuTree_IsFilteredByRole()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await SignInAsync(app, OwnerEmail);
        using HttpClient plain = await SignInAsync(app, PlainEmail);

        IReadOnlyList<MenuNodeDto> ownerTree = await GetTreeAsync(owner);
        Assert.IsTrue(Flatten(ownerTree).Any(n => n.Code == "system-menus"), "超管应看到全部菜单");

        IReadOnlyList<MenuNodeDto> plainTree = await GetTreeAsync(plain);
        Assert.IsEmpty(plainTree, "没有任何角色的账号看到的应是空树");
    }

    [TestMethod]
    public async Task CreateMenu_MakesItImmediatelyVisibleToSuperadmin()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await SignInAsync(app, OwnerEmail);

        Assert.IsFalse((await GetTreeAsync(owner)).Any(n => n.Code == "reports"));

        HttpResponseMessage created = await owner.PostAsJsonAsync(
            new Uri("/api/admin/menus", UriKind.Relative),
            new MenuUpsertRequestDto
            {
                ParentId = null,
                Code = "reports",
                Title = "报表",
                Path = "/reports",
                Icon = null,
                Kind = MenuKinds.Menu,
                SortOrder = 999,
                IsVisible = true,
                Actions = [MenuActions.View],
            });

        Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);

        // 新建菜单之后超管立刻能看见它：这条 p 策略是投影在重建时补出来的，
        // 若写操作没有触发重建，这里就会看不到
        Assert.IsTrue((await GetTreeAsync(owner)).Any(n => n.Code == "reports"),
            "新建菜单后，超管自己的菜单树应立刻出现它（证明写操作触发了策略重建）");
    }

    /// <summary>
    /// 端到端证明"重建有效"：给另一个账号的角色开放一个菜单之后，
    /// **无需重新登录**，同一个会话再请求就能看见它。
    /// </summary>
    [TestMethod]
    public async Task SetMenuRoles_MakesAnotherAccountSeeTheMenuImmediately()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await SignInAsync(app, OwnerEmail);
        using HttpClient plain = await SignInAsync(app, PlainEmail);

        Assert.IsEmpty(await GetTreeAsync(plain));

        HttpResponseMessage role = await owner.PostAsJsonAsync(
            new Uri("/api/roles", UriKind.Relative),
            new CreateRoleRequestDto { Code = "editor", Title = "编辑" });

        Assert.AreEqual(HttpStatusCode.OK, role.StatusCode);

        // 本批没有"用户-角色绑定"接口，直接按数据面把账号绑到角色上
        await BindUserToRoleAsync(app, PlainEmail, "editor");

        // 此刻 Enforcer 还不知道这条绑定（没有谁触发重建）：plain 依旧什么都看不到
        Assert.IsEmpty(await GetTreeAsync(plain), "绑定的效果要等一次重建才生效");

        long graphId = await FindMenuIdAsync(owner, "graph");

        HttpResponseMessage set = await owner.PutAsJsonAsync(
            new Uri($"/api/admin/menus/{graphId}/roles", UriKind.Relative),
            new MenuRolesDto { RoleCodes = ["editor"] });

        Assert.AreEqual(HttpStatusCode.OK, set.StatusCode);

        Assert.IsTrue((await GetTreeAsync(plain)).Any(n => n.Code == "graph"),
            "PUT roles 之后另一个账号的可见性应即时生效（重建把新策略换上了）");
    }

    [TestMethod]
    public async Task Permissions_Endpoint_ReturnsRolesAndMenuGrants()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await SignInAsync(app, OwnerEmail);

        PermissionsDto permissions = (await owner.GetFromJsonAsync<PermissionsDto>(
            new Uri("/api/auth/permissions", UriKind.Relative)))!;

        Assert.AreEqual(PermissionSubjects.SuperadminRoleCode, Assert.ContainsSingle(permissions.Roles).Code);

        PermissionGrantDto systemMenus = permissions.Permissions.Single(p => p.PermissionKey == PermissionKeys.SystemMenus);
        CollectionAssert.AreEqual(
            new List<string> { MenuActions.View, MenuActions.Edit, MenuActions.Delete },
            systemMenus.Actions.ToList(),
            "超管对「菜单管理」应拥有全部动作");
    }

    [TestMethod]
    public async Task Roles_IsReadableByAnySignedInAccount()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await SignInAsync(app, OwnerEmail);
        using HttpClient plain = await SignInAsync(app, PlainEmail);

        HttpResponseMessage readable = await plain.GetAsync(new Uri("/api/roles", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.OK, readable.StatusCode);

        List<RoleDto> roles = (await readable.Content.ReadFromJsonAsync<List<RoleDto>>())!;
        Assert.IsTrue(roles.Any(r => r.Code == PermissionSubjects.SuperadminRoleCode && r.IsSystem));

        // 但新建角色需要「角色管理」的 edit：无权限的账号拿不到
        HttpResponseMessage denied = await plain.PostAsJsonAsync(
            new Uri("/api/roles", UriKind.Relative),
            new CreateRoleRequestDto { Code = "nope", Title = "不该建成" });

        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);

        HttpResponseMessage allowed = await owner.PostAsJsonAsync(
            new Uri("/api/roles", UriKind.Relative),
            new CreateRoleRequestDto { Code = "editor", Title = "编辑" });

        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
    }

    [TestMethod]
    public async Task SetMenuRoles_KeepsSuperadminInTheResponse()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await SignInAsync(app, OwnerEmail);

        long graphId = await FindMenuIdAsync(owner, "graph");

        HttpResponseMessage set = await owner.PutAsJsonAsync(
            new Uri($"/api/admin/menus/{graphId}/roles", UriKind.Relative),
            new MenuRolesDto { RoleCodes = [] });

        Assert.AreEqual(HttpStatusCode.OK, set.StatusCode);

        MenuRolesDto roles = (await set.Content.ReadFromJsonAsync<MenuRolesDto>())!;
        CollectionAssert.Contains(roles.RoleCodes.ToList(), PermissionSubjects.SuperadminRoleCode);
    }

    [TestMethod]
    public async Task DeleteMenu_WithChildren_Is400()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await SignInAsync(app, OwnerEmail);

        // 用自定义父/子：内置菜单会被"不可删除"的守卫先挡下（那条规则另有用例），
        // 这里要测的是"有子节点"这一条
        long parentId = await CreateMenuAsync(owner, "reports", parentId: null);
        await CreateMenuAsync(owner, "reports-detail", parentId);

        HttpResponseMessage response = await owner.DeleteAsync(
            new Uri($"/api/admin/menus/{parentId}", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task BuiltInMenu_CannotBeDeletedOrRenamed_Is400()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await SignInAsync(app, OwnerEmail);

        long graphId = await FindMenuIdAsync(owner, "graph");

        HttpResponseMessage deleted = await owner.DeleteAsync(
            new Uri($"/api/admin/menus/{graphId}", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.BadRequest, deleted.StatusCode);
        StringAssert.Contains(
            (await deleted.Content.ReadAsStringAsync()) ?? string.Empty,
            "不可删除");

        HttpResponseMessage renamed = await owner.PutAsJsonAsync(
            new Uri($"/api/admin/menus/{graphId}", UriKind.Relative),
            new MenuUpsertRequestDto
            {
                ParentId = null,
                Code = "dashboard",
                Title = "总览",
                Path = "/graph",
                Icon = null,
                Kind = MenuKinds.Menu,
                SortOrder = 20,
                IsVisible = true,
                Actions = [MenuActions.View],
            });

        Assert.AreEqual(HttpStatusCode.BadRequest, renamed.StatusCode);

        // 拒绝之后那一页还在（没有半改状态）
        Assert.IsTrue((await GetTreeAsync(owner)).Any(n => n.Code == "graph"));
    }

    /// <summary>建一个自定义菜单并返回它的 Id（用例里的"非内置"夹具）。</summary>
    private static async Task<long> CreateMenuAsync(HttpClient owner, string code, long? parentId)
    {
        HttpResponseMessage created = await owner.PostAsJsonAsync(
            new Uri("/api/admin/menus", UriKind.Relative),
            new MenuUpsertRequestDto
            {
                ParentId = parentId,
                Code = code,
                Title = code,
                Path = $"/{code}",
                Icon = null,
                Kind = MenuKinds.Menu,
                SortOrder = 999,
                IsVisible = true,
                Actions = [MenuActions.View],
            });

        created.EnsureSuccessStatusCode();

        return await FindMenuIdAsync(owner, code);
    }

    private static async Task<IReadOnlyList<MenuNodeDto>> GetTreeAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<MenuNodeDto>>(new Uri("/api/menus/tree", UriKind.Relative)))!;

    private static async Task<long> FindMenuIdAsync(HttpClient owner, string code)
    {
        List<MenuNodeDto> tree = (await owner.GetFromJsonAsync<List<MenuNodeDto>>(
            new Uri("/api/admin/menus", UriKind.Relative)))!;

        return Flatten(tree).Single(n => n.Code == code).Id;
    }

    private static IEnumerable<MenuNodeDto> Flatten(IEnumerable<MenuNodeDto> nodes) =>
        nodes.SelectMany(node => Flatten(node.Children).Prepend(node));

    /// <summary>本批没有用户-角色绑定接口：按数据面直接写一行，用于端到端验证重建。</summary>
    private static async Task BindUserToRoleAsync(AuthTestApp app, string email, string roleCode)
    {
        await using PoolPilotDbContext db = app.OpenDatabase();

        long userId = (await db.Users.AsNoTracking().SingleAsync(u => u.Email == email)).Id;
        long roleId = (await db.Roles.AsNoTracking().SingleAsync(r => r.Code == roleCode)).Id;

        db.UserRoles.Add(new UserRoleRecord { UserId = userId, RoleId = roleId });
        await db.SaveChangesAsync();
    }

    /// <summary>注册 + 登录 + 绑定 MFA，返回带会话 Cookie 的客户端（实现收在 <see cref="TestSignIn"/>）。</summary>
    private static Task<HttpClient> SignInAsync(AuthTestApp app, string email) =>
        TestSignIn.Async(app, email, Password);
}
