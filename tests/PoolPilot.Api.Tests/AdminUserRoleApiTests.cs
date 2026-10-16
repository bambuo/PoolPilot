using System.Net;
using System.Net.Http.Json;

using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Constants;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 「系统 → 用户管理 / 角色管理」两个页面的数据面，跑在真实管线里
/// （路由、Cookie、授权中间件、Casbin、SQLite 全是真的）。
///
/// 这一组测三件事的接线：401 / 403 / 200 的三态确实由权限点区分；
/// 新接口的写操作会触发策略重建（不重新登录即生效）；守卫（最后一个超管、
/// 系统角色、有成员的角色、superadmin 的菜单替换）都在真实请求上回 400。
/// </summary>
[TestClass]
public sealed class AdminUserRoleApiTests
{
    private const string OwnerEmail = "owner@example.com";
    private const string PlainEmail = "plain@example.com";
    private const string OpsEmail = "ops@example.com";
    private const string AuditorEmail = "auditor@example.com";
    private const string MemberEmail = "member@example.com";
    private const string EditorEmail = "editor@example.com";
    private const string SecondEmail = "second@example.com";
    private const string Password = "correct horse";

    [TestMethod]
    public async Task AdminEndpoints_Are401WhenAnonymous()
    {
        using AuthTestApp app = new();
        using HttpClient client = app.CreateClient();

        foreach (string path in new[]
        {
            "/api/admin/users",
            "/api/admin/roles",
            "/api/admin/roles/superadmin/menus",
        })
        {
            HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, $"{path} 应当要求登录");
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "error");
        }
    }

    [TestMethod]
    public async Task AdminUsers_Is403WithoutThePermission_And200WithItBoundToTheRole()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await SignInAsync(app, OwnerEmail);
        using HttpClient plain = await SignInAsync(app, PlainEmail);
        using HttpClient ops = await SignInAsync(app, OpsEmail);

        await CreateRoleAsync(owner, "ops", "运营");
        await BindMenuToRoleAsync(owner, "system-users", "ops");

        // 数据面绑定走的就是被测的新接口：给 ops 账号绑上 ops 角色
        long opsId = await FindUserIdAsync(owner, OpsEmail);
        HttpResponseMessage bound = await owner.PutAsJsonAsync(
            new Uri($"/api/admin/users/{opsId}/roles", UriKind.Relative),
            new UserRolesDto { RoleCodes = ["ops"] });

        Assert.AreEqual(HttpStatusCode.NoContent, bound.StatusCode);

        HttpResponseMessage denied = await plain.GetAsync(new Uri("/api/admin/users", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        StringAssert.Contains(await denied.Content.ReadAsStringAsync(), "error");

        // ops 会话无需重新登录就能读——证明 PUT users/{id}/roles 触发了策略重建
        HttpResponseMessage allowed = await ops.GetAsync(new Uri("/api/admin/users", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);

        List<AdminUserDto> users = (await allowed.Content.ReadFromJsonAsync<List<AdminUserDto>>())!;

        CollectionAssert.AreEqual(
            users.Select(u => u.Id).Order().ToList(),
            users.Select(u => u.Id).ToList(),
            "列表按 id 升序");

        AdminUserDto ownerRow = users.Single(u => u.Email == OwnerEmail);
        Assert.IsTrue(Assert.ContainsSingle(ownerRow.Roles).IsSystem, "首个账号应当是系统角色 superadmin");

        AdminUserDto opsRow = users.Single(u => u.Email == OpsEmail);
        Assert.AreEqual("ops", Assert.ContainsSingle(opsRow.Roles).Code);
        Assert.IsFalse(opsRow.Roles[0].IsSystem);

        DateTimeOffset createdAt = DateTimeOffset.Parse(opsRow.CreatedAt, System.Globalization.CultureInfo.InvariantCulture);
        Assert.AreEqual(TimeSpan.Zero, createdAt.Offset, "createdAt 必须是 UTC 时刻");

        // edit 权限同样是通的：ops 把 plain 也绑上自己的角色，plain 随即能读
        long plainId = await FindUserIdAsync(owner, PlainEmail);
        HttpResponseMessage written = await ops.PutAsJsonAsync(
            new Uri($"/api/admin/users/{plainId}/roles", UriKind.Relative),
            new UserRolesDto { RoleCodes = ["ops"] });

        Assert.AreEqual(HttpStatusCode.NoContent, written.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await plain.GetAsync(new Uri("/api/admin/users", UriKind.Relative))).StatusCode);
    }

    [TestMethod]
    public async Task AdminRoles_Is403WithoutThePermission_And200WithItBoundToTheRole()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await SignInAsync(app, OwnerEmail);
        using HttpClient plain = await SignInAsync(app, PlainEmail);
        using HttpClient auditor = await SignInAsync(app, AuditorEmail);

        await CreateRoleAsync(owner, "auditor", "审计");
        await BindMenuToRoleAsync(owner, "system-roles", "auditor");

        long auditorId = await FindUserIdAsync(owner, AuditorEmail);
        HttpResponseMessage bound = await owner.PutAsJsonAsync(
            new Uri($"/api/admin/users/{auditorId}/roles", UriKind.Relative),
            new UserRolesDto { RoleCodes = ["auditor"] });

        Assert.AreEqual(HttpStatusCode.NoContent, bound.StatusCode);

        HttpResponseMessage denied = await plain.GetAsync(new Uri("/api/admin/roles", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);

        HttpResponseMessage allowed = await auditor.GetAsync(new Uri("/api/admin/roles", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);

        List<AdminRoleDto> roles = (await allowed.Content.ReadFromJsonAsync<List<AdminRoleDto>>())!;

        AdminRoleDto superadmin = roles.Single(r => r.Code == PermissionSubjects.SuperadminRoleCode);
        Assert.IsTrue(superadmin.IsSystem);
        Assert.AreEqual(1, superadmin.MemberCount, "此刻库里只有首个账号是超管");

        List<MenuNodeDto> tree = (await owner.GetFromJsonAsync<List<MenuNodeDto>>(
            new Uri("/api/admin/menus", UriKind.Relative)))!;
        Assert.AreEqual(
            Flatten(tree).Count(),
            superadmin.MenuCount,
            "超管的菜单数是全部菜单数（授权由 matcher 短路承担，不落绑定表）");

        AdminRoleDto auditorRole = roles.Single(r => r.Code == "auditor");
        Assert.AreEqual(1, auditorRole.MemberCount);
        Assert.AreEqual(1, auditorRole.MenuCount);
    }

    /// <summary>
    /// 最后一个超管守卫。判据是"库里还有没有别的超管用户"：两个超管时互相摘都放行，
    /// 只剩一个时连它自己也不能摘（否则系统失去唯一的管理员）。
    /// </summary>
    [TestMethod]
    public async Task SetUserRoles_AllowsRemovingSuperadminWhenAnotherExists_AndRejectsTheLastOne()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await SignInAsync(app, OwnerEmail);
        using HttpClient second = await SignInAsync(app, SecondEmail);

        long ownerId = await FindUserIdAsync(owner, OwnerEmail);
        long secondId = await FindUserIdAsync(owner, SecondEmail);

        // 先造出第二个超管
        HttpResponseMessage promoted = await owner.PutAsJsonAsync(
            new Uri($"/api/admin/users/{secondId}/roles", UriKind.Relative),
            new UserRolesDto { RoleCodes = [PermissionSubjects.SuperadminRoleCode] });

        Assert.AreEqual(HttpStatusCode.NoContent, promoted.StatusCode);

        // 还有别的超管（owner 自己）→ 摘掉 second 的超管角色放行
        HttpResponseMessage allowed = await owner.PutAsJsonAsync(
            new Uri($"/api/admin/users/{secondId}/roles", UriKind.Relative),
            new UserRolesDto { RoleCodes = [] });

        Assert.AreEqual(HttpStatusCode.NoContent, allowed.StatusCode);

        // 现在 owner 是唯一超管：摘自己的被 400 拒绝
        HttpResponseMessage rejected = await owner.PutAsJsonAsync(
            new Uri($"/api/admin/users/{ownerId}/roles", UriKind.Relative),
            new UserRolesDto { RoleCodes = [] });

        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
        StringAssert.Contains(await rejected.Content.ReadAsStringAsync(), "唯一的管理员");

        // 被拒绝之后状态原样：owner 仍是超管、仍是唯一一个
        List<AdminUserDto> users = (await owner.GetFromJsonAsync<List<AdminUserDto>>(
            new Uri("/api/admin/users", UriKind.Relative)))!;

        Assert.AreEqual(
            PermissionSubjects.SuperadminRoleCode,
            Assert.ContainsSingle(users.Single(u => u.Id == ownerId).Roles).Code);
        Assert.IsEmpty(users.Single(u => u.Id == secondId).Roles);
    }

    [TestMethod]
    public async Task DeleteRole_RejectsSystemRoleAndRoleWithMembers_AndDeletesEmptyRole()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await SignInAsync(app, OwnerEmail);
        using HttpClient member = await SignInAsync(app, MemberEmail);

        await CreateRoleAsync(owner, "editor", "编辑");

        long memberId = await FindUserIdAsync(owner, MemberEmail);
        HttpResponseMessage bound = await owner.PutAsJsonAsync(
            new Uri($"/api/admin/users/{memberId}/roles", UriKind.Relative),
            new UserRolesDto { RoleCodes = ["editor"] });

        Assert.AreEqual(HttpStatusCode.NoContent, bound.StatusCode);

        HttpResponseMessage hasMembers = await owner.DeleteAsync(
            new Uri("/api/admin/roles/editor", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.BadRequest, hasMembers.StatusCode);
        string memberMessage = await hasMembers.Content.ReadAsStringAsync();
        StringAssert.Contains(memberMessage, "1", "消息里要带人数");
        StringAssert.Contains(memberMessage, "先解绑用户");

        HttpResponseMessage systemRole = await owner.DeleteAsync(
            new Uri($"/api/admin/roles/{PermissionSubjects.SuperadminRoleCode}", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.BadRequest, systemRole.StatusCode);
        StringAssert.Contains(await systemRole.Content.ReadAsStringAsync(), "系统角色");

        HttpResponseMessage unknown = await owner.DeleteAsync(
            new Uri("/api/admin/roles/ghost", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);

        await CreateRoleAsync(owner, "temp", "临时");
        HttpResponseMessage deleted = await owner.DeleteAsync(
            new Uri("/api/admin/roles/temp", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);

        List<AdminRoleDto> remaining = (await owner.GetFromJsonAsync<List<AdminRoleDto>>(
            new Uri("/api/admin/roles", UriKind.Relative)))!;

        Assert.IsTrue(remaining.Any(r => r.Code == "editor"), "有成员的角色被拒绝后必须原样保留");
        Assert.IsFalse(remaining.Any(r => r.Code == "temp"));
    }

    [TestMethod]
    public async Task RoleMenus_ReplacesAndRoundTrips_AndRejectsSuperadminAndUnknownCodes()
    {
        using AuthTestApp app = new();
        using HttpClient owner = await SignInAsync(app, OwnerEmail);
        using HttpClient editor = await SignInAsync(app, EditorEmail);

        await CreateRoleAsync(owner, "editor", "编辑");

        RoleMenuCodesDto empty = (await owner.GetFromJsonAsync<RoleMenuCodesDto>(
            new Uri("/api/admin/roles/editor/menus", UriKind.Relative)))!;
        Assert.IsEmpty(empty.MenuCodes);

        // 故意乱序提交：读回时必须按 menus.sortOrder 排（system-menus 10 < graph 20）
        HttpResponseMessage set = await owner.PutAsJsonAsync(
            new Uri("/api/admin/roles/editor/menus", UriKind.Relative),
            new RoleMenuCodesDto { MenuCodes = ["graph", "system-menus"] });

        Assert.AreEqual(HttpStatusCode.NoContent, set.StatusCode);

        RoleMenuCodesDto roundTrip = (await owner.GetFromJsonAsync<RoleMenuCodesDto>(
            new Uri("/api/admin/roles/editor/menus", UriKind.Relative)))!;
        CollectionAssert.AreEqual(
            new List<string> { "system-menus", "graph" },
            roundTrip.MenuCodes.ToList());

        // 绑定立即生效：把 editor 角色给账号，它不重新登录就能看见这两个菜单
        long editorId = await FindUserIdAsync(owner, EditorEmail);
        HttpResponseMessage bound = await owner.PutAsJsonAsync(
            new Uri($"/api/admin/users/{editorId}/roles", UriKind.Relative),
            new UserRolesDto { RoleCodes = ["editor"] });

        Assert.AreEqual(HttpStatusCode.NoContent, bound.StatusCode);

        List<MenuNodeDto> editorTree = (await editor.GetFromJsonAsync<List<MenuNodeDto>>(
            new Uri("/api/menus/tree", UriKind.Relative)))!;

        Assert.IsTrue(Flatten(editorTree).Any(n => n.Code == "system-menus"), "PUT menus 之后策略应已重建");
        Assert.IsTrue(Flatten(editorTree).Any(n => n.Code == "graph"));

        // 守卫：superadmin 的授权由 matcher 短路承担，整表替换被拒
        HttpResponseMessage superadminRejected = await owner.PutAsJsonAsync(
            new Uri($"/api/admin/roles/{PermissionSubjects.SuperadminRoleCode}/menus", UriKind.Relative),
            new RoleMenuCodesDto { MenuCodes = ["graph"] });

        Assert.AreEqual(HttpStatusCode.BadRequest, superadminRejected.StatusCode);

        // 守卫：未知 code 要被点名
        HttpResponseMessage unknownRejected = await owner.PutAsJsonAsync(
            new Uri("/api/admin/roles/editor/menus", UriKind.Relative),
            new RoleMenuCodesDto { MenuCodes = ["nope"] });

        Assert.AreEqual(HttpStatusCode.BadRequest, unknownRejected.StatusCode);
        StringAssert.Contains(await unknownRejected.Content.ReadAsStringAsync(), "nope");

        // superadmin 的读是"全部菜单"的有效视图
        RoleMenuCodesDto superadminMenus = (await owner.GetFromJsonAsync<RoleMenuCodesDto>(
            new Uri($"/api/admin/roles/{PermissionSubjects.SuperadminRoleCode}/menus", UriKind.Relative)))!;

        List<MenuNodeDto> adminTree = (await owner.GetFromJsonAsync<List<MenuNodeDto>>(
            new Uri("/api/admin/menus", UriKind.Relative)))!;

        Assert.AreEqual(Flatten(adminTree).Count(), superadminMenus.MenuCodes.Count);
        Assert.IsTrue(superadminMenus.MenuCodes.Contains("system-users"));
        Assert.IsTrue(superadminMenus.MenuCodes.Contains("system-roles"));
    }

    private static async Task CreateRoleAsync(HttpClient owner, string code, string title)
    {
        HttpResponseMessage response = await owner.PostAsJsonAsync(
            new Uri("/api/roles", UriKind.Relative),
            new CreateRoleRequestDto { Code = code, Title = title });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task BindMenuToRoleAsync(HttpClient owner, string menuCode, string roleCode)
    {
        long menuId = await FindMenuIdAsync(owner, menuCode);

        HttpResponseMessage response = await owner.PutAsJsonAsync(
            new Uri($"/api/admin/menus/{menuId}/roles", UriKind.Relative),
            new MenuRolesDto { RoleCodes = [roleCode] });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<long> FindUserIdAsync(HttpClient owner, string email)
    {
        List<AdminUserDto> users = (await owner.GetFromJsonAsync<List<AdminUserDto>>(
            new Uri("/api/admin/users", UriKind.Relative)))!;

        return users.Single(u => u.Email == email).Id;
    }

    private static async Task<long> FindMenuIdAsync(HttpClient owner, string code)
    {
        List<MenuNodeDto> tree = (await owner.GetFromJsonAsync<List<MenuNodeDto>>(
            new Uri("/api/admin/menus", UriKind.Relative)))!;

        return Flatten(tree).Single(n => n.Code == code).Id;
    }

    private static IEnumerable<MenuNodeDto> Flatten(IEnumerable<MenuNodeDto> nodes) =>
        nodes.SelectMany(node => Flatten(node.Children).Prepend(node));

    /// <summary>注册 + 登录 + 绑定 MFA，返回带会话 Cookie 的客户端（实现收在 <see cref="TestSignIn"/>）。</summary>
    private static Task<HttpClient> SignInAsync(AuthTestApp app, string email) =>
        TestSignIn.Async(app, email, Password);
}
