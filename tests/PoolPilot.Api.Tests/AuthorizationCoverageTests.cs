using System.Reflection;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

using PoolPilot.Api.Authorization;

namespace PoolPilot.Api.Tests;

/// <summary>
/// 覆盖门禁：**每个 MVC action 要么挂了权限门，要么在白名单里**。
///
/// 这条测试存在的唯一目的是"以后新加端点忘了加门 = CI 红灯"。它不测权限判定的对错
/// （那是 AuthApiTests / MenuApiTests / AdminUserRoleApiTests / BusinessEndpointAuthorizationTests 的事），
/// 只测**有没有门**：反射扫 <c>PoolPilot.Api</c> 程序集里的全部控制器 action，
/// 逐个检查 <see cref="RequirePermissionAttribute"/> / <see cref="RequireAnyPermissionAttribute"/>
/// （方法级或类级），命中不了就要求它在 <see cref="OpenEndpoints"/> 白名单里，且每一条白名单
/// 都附了"为什么它可以敞开"。
///
/// 白名单的 key 用「控制器类型名（含 Controller 后缀）.方法名」，与路由模板无关——
/// 路由可以改，方法名不会无声地漂移；而对不上真实 action 的白名单条目会被
/// <see cref="WhitelistEntries_AllMatchARealAction"/> 判为"烂掉的条目"。
/// </summary>
[TestClass]
public sealed class AuthorizationCoverageTests
{
    /// <summary>
    /// 匿名或"登录即可"的端点。**只在这两类下允许**：
    /// <list type="bullet">
    ///   <item>匿名：登录本身（注册 / 登录 / MFA / 登出）、首次初始化，以及健康检查（非 MVC 端点，不在此表）。</item>
    ///   <item>登录即可：回答"我是谁 / 我能做什么 / 我看到哪些菜单"的自省类端点——它们不承载业务数据。</item>
    /// </list>
    /// 任何**业务数据端点**都不该出现在这里；要放开，先想清楚"谁能读它"。
    /// </summary>
    private static readonly Dictionary<string, string> OpenEndpoints = new(StringComparer.Ordinal)
    {
        // ── 匿名：登录 / 注册 / 初始化 ─────────────────────────
        ["AuthController.SendRegistrationCode"] = "匿名：发送注册验证码——初始化前系统里没有任何账号，这是唯一的注册入口",
        ["AuthController.Register"] = "匿名：注册（注册保持开放，但注册完默认没有任何角色）",
        ["AuthController.Login"] = "匿名：登录第一步（验邮箱与口令）",
        ["AuthController.VerifyMfa"] = "匿名：登录第二步（已绑定账号用六位码换会话）",
        ["AuthController.BindMfa"] = "匿名：登录第二步（首次绑定，验过码即完成绑定）",
        ["AuthController.Logout"] = "匿名：登出幂等——没登录时调用也该回 204（服务端不存会话，清 Cookie 即完成）",
        ["SetupController.Status"] = "匿名：读取系统是否已初始化——前端据此决定进站先给初始化页还是登录页",
        ["SetupController.ConfigureNotifications"] = "匿名：首次初始化时配置通知通道（还没有账号时的唯一写入口，初始化完成后即回 400 失效）",
        ["SetupController.SendTestNotification"] = "匿名：用已保存的通道发测试邮件（同上，只在初始化窗口内有效）",

        // ── 登录即可：自省类端点 ───────────────────────────────
        ["AuthController.Me"] = "登录即可：会话自省（回答「当前是谁」），前端据此判断要不要显示登录页",
        ["AuthController.Permissions"] = "登录即可：权限清单——它本身就是「我能做什么」的答案",
        ["MenusController.Tree"] = "登录即可：当前用户自己的菜单树；没有角色的账号拿到空树正是它该有的样子",
        ["RolesController.List"] = "登录即可：角色名展示（界面上到处要用），读不承载业务数据",
    };

    [TestMethod]
    public void EveryControllerAction_IsGatedOrWhitelisted()
    {
        List<string> ungated = [];

        foreach (MethodInfo action in EnumerateActions())
        {
            if (IsGated(action) || OpenEndpoints.ContainsKey(Key(action)))
            {
                continue;
            }

            ungated.Add($"{Key(action)} [{HttpMethods(action)}]");
        }

        Assert.AreEqual(
            0,
            ungated.Count,
            "以下端点既没有 RequirePermission/RequireAnyPermission，也不在白名单里。"
            + "新加端点忘记加门会让这条测试变红——请给端点加门，或（确有理由地）加进白名单并写明原因：\n"
            + string.Join("\n", ungated));
    }

    [TestMethod]
    public void WhitelistEntries_AllMatchARealAction()
    {
        HashSet<string> realActions = [.. EnumerateActions().Select(Key)];

        List<string> stale = [.. OpenEndpoints.Keys.Where(key => !realActions.Contains(key))];

        Assert.AreEqual(
            0,
            stale.Count,
            "白名单里有对不上真实 action 的条目（端点被删或改名了）。"
            + "白名单会烂掉，必须与端点同步：\n"
            + string.Join("\n", stale));
    }

    /// <summary>扫程序集里的全部控制器 action：公开、实例、自身声明、且带 HTTP 方法特性。</summary>
    private static IEnumerable<MethodInfo> EnumerateActions() =>
        typeof(Program).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any()))
            .OrderBy(method => Key(method), StringComparer.Ordinal);

    /// <summary>方法级或所属类型级挂着任一权限属性即算有门。</summary>
    private static bool IsGated(MethodInfo action) =>
        HasGate(action)
        || (action.DeclaringType is { } controller && HasGate(controller));

    private static bool HasGate(MemberInfo member) =>
        member.GetCustomAttribute<RequirePermissionAttribute>(inherit: true) is not null
        || member.GetCustomAttribute<RequireAnyPermissionAttribute>(inherit: true) is not null;

    private static string Key(MethodInfo action) => $"{action.DeclaringType!.Name}.{action.Name}";

    private static string HttpMethods(MethodInfo action) =>
        string.Join(
            "/",
            action.GetCustomAttributes<HttpMethodAttribute>(inherit: true).SelectMany(attribute => attribute.HttpMethods));
}
