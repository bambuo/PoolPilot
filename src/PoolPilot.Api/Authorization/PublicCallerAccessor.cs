using System.Security.Cryptography;

using Microsoft.AspNetCore.Http;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Services;

namespace PoolPilot.Api.Authorization;

/// <summary>
/// 公开页的"调用者是谁"：把会话用户与访客 Cookie 组装成应用层的
/// <see cref="PublicCaller"/>，并且只管这一件事。
///
/// 为什么在这一侧（与 <see cref="HttpContextCurrentUser"/> 同处）：只有表现层认识
/// <c>HttpContext</c> 与 Cookie。用例拿到的是两个值——会话用户 Id 与访客密钥——
/// 它不知道也不需要一个 Cookie 长什么样。
///
/// ⚠️ 这枚 Cookie **不是认证**：它不进 Casbin、不是主体、不证明"你是谁"，
/// 它承载的能力只有一件——"库里那一行的创建者凭证是我手里这个值派生出来的"。
/// 拿到它不能读别人的行、不能改别人的行，也拿不到任何私有端点。
/// </summary>
public sealed class PublicCallerAccessor(
    IHttpContextAccessor accessor,
    ICurrentUser currentUser)
{
    /// <summary>Cookie 名。它出现在文档与测试断言里，改动就是契约改动。</summary>
    public const string VisitorCookieName = "poolpilot_visitor";

    /// <summary>
    /// 作用路径。<c>/api/public</c> 而不是 <c>/</c>：这枚 Cookie 只服务于公开面，
    /// 让浏览器在工作台的每个请求上都带上它，除了扩大它的暴露面之外没有任何用处
    /// ——会话请求本来就有更可信的身份。
    /// </summary>
    private const string VisitorCookiePath = "/api/public";

    /// <summary>存活期。公开页的跟单是"挂着看几个月"的东西，一年比一个月诚实。</summary>
    private static readonly TimeSpan VisitorCookieLifetime = TimeSpan.FromDays(180);

    /// <summary>
    /// 32 字节随机数 → 64 位小写十六进制。
    /// 长度取自领域层的 <see cref="PublicFollowerOwnership.VisitorSecretHexLength"/>——
    /// 那里也是校验发生的地方，两边各自数一遍迟早会数出两个不同的数。
    /// 生成放在表现层：这是"这次请求该不该有一枚新 Cookie"的决定，与派生规则无关。
    /// </summary>
    private const int VisitorSecretByteLength = PublicFollowerOwnership.VisitorSecretHexLength / 2;

    /// <summary>读路径（列表、详情、删除）用的调用者：有 Cookie 就带上，没有就是 null。</summary>
    public PublicCaller Current() => new(currentUser.UserId, ReadVisitorSecret());

    /// <summary>
    /// 写路径（新建跟单）用的调用者。
    ///
    /// 没有 Cookie 时**先生成一枚候选密钥**而不是先签发：匿名行的创建者凭证要按它派生，
    /// 派生必须发生在建行之前后的回填里；而签发要等到这次写真的成功——
    /// 一个因为超上限被拒的请求不该在浏览器里留下一枚谁也没用的 Cookie。
    /// </summary>
    /// <param name="hadCookie">请求原本带着合法的访客 Cookie。为 <c>false</c> 时才需要签发。</param>
    public PublicCaller ForCreate(out bool hadCookie)
    {
        string? existing = ReadVisitorSecret();

        hadCookie = existing is not null;

        return new PublicCaller(currentUser.UserId, existing ?? NewVisitorSecret());
    }

    /// <summary>
    /// 按调用者手里的密钥签发访客 Cookie。
    /// 密钥为 <c>null</c>（读路径且没带 Cookie）时什么都不做——签发只属于"刚刚建了一行匿名跟单"之后。
    /// </summary>
    public void IssueVisitorCookie(PublicCaller caller)
    {
        if (caller.VisitorSecret is not { Length: > 0 } secret)
        {
            return;
        }

        accessor.HttpContext?.Response.Cookies.Append(VisitorCookieName, secret, BuildVisitorCookieOptions());
    }

    /// <summary>
    /// 这一发请求该不该带上一枚新 Cookie：<c>W2</c> 成功、调用者未登录（所以这一行的创建者能力
    /// 落在访客密钥上）、且它原本没有这枚 Cookie。三个条件缺一个都不签发。
    ///
    /// 判据里"未登录"这一条不是多余：登录建的行归 <c>owner_user_id</c>，那枚 Cookie 对它毫无用处，
    /// 发出去只是让浏览器多背一份没人读的东西。
    /// </summary>
    public bool ShouldIssueVisitorCookie(PublicCaller caller, bool hadVisitorCookie) =>
        !hadVisitorCookie && caller.UserId is null && caller.VisitorSecret is { Length: > 0 };

    private static CookieOptions BuildVisitorCookieOptions() => new()
    {
        // HttpOnly：脚本读不到它，前端因此没有"把这串值存在哪儿"这个决定要做，
        // XSS 也就不能把它抄进请求里发给别人
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Path = VisitorCookiePath,
        MaxAge = VisitorCookieLifetime,

        // 跟随会话 Cookie 的 Cookie.SecurePolicy（Program.cs 里是 None）：本机是 http://localhost，
        // 没有 TLS 可谈，这里若单独写 true，Cookie 会根本发不出去。
        // 两处必须一起改，所以它们挨着写在同一个组合根里
        Secure = false,
    };

    private string? ReadVisitorSecret()
    {
        string? raw = accessor.HttpContext?.Request.Cookies[VisitorCookieName];

        // 形状不对就当没有：那可能来自被截断的存储或被手工改过的值。
        // 拿它去派生只会得到一个谁也认不出的哈希，静默用下去反而更糟
        return PublicFollowerOwnership.IsVisitorSecret(raw) ? raw : null;
    }

    private static string NewVisitorSecret() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(VisitorSecretByteLength));
}
