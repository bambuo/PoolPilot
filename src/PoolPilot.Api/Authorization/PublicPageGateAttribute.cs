using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

using PoolPilot.Application.Abstractions;

namespace PoolPilot.Api.Authorization;

/// <summary>
/// 公开页的功能开关：<c>[PublicPageGate(PublicPage.Followers)]</c>。
///
/// 传多片时是**或**：<c>[PublicPageGate(PublicPage.Leads, PublicPage.Followers)]</c>
/// 表示"任意一片开着就放行"。实时流用这条——它同时输出带单页与跟单页的数据，
/// 只有两片都关的时候，这个地址才该和不存在一样。
///
/// 关掉时回 **404** 而不是 403——对一个没有对外开放的功能，"存在但拒绝你"和
/// "这里什么都没有"对匿名请求不该有区别；回 403 等于让探测者一遍就数出这台服务开了哪些口子。
/// 文案形状沿用 <c>ExceptionHandlingMiddleware</c> 的 <c>{"error": string}</c>。
///
/// 为什么是过滤器而不是写在每个 action 里：这批端点共享同一句"这一页没开"，
/// 写在 action 里迟早漏掉最后一个。它也**不是权限门**（<c>RequirePermission</c> 那一套）：
/// 权限回答"你能不能"，这里回答"这个部署有没有这一页"。
///
/// 用 <see cref="IAuthorizationFilter"/> 是为了跑在模型绑定之前：开关关掉时一个用例都不会
/// 被叫起来，也就不存在"写请求被拦下来之前已经动了库"这种状态。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class PublicPageGateAttribute(params PublicPage[] pages) : Attribute, IAuthorizationFilter
{
    /// <summary>关闭时的统一文案。</summary>
    public const string DisabledMessage = "公开页未启用";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        PublicPageSettings settings = context.HttpContext.RequestServices
            .GetRequiredService<PublicPageSettings>();

        if (!IsOpen(settings))
        {
            context.Result = new NotFoundObjectResult(new { error = DisabledMessage });
        }
    }

    private bool IsOpen(PublicPageSettings settings) => pages.Any(page => page switch
    {
        PublicPage.Leads => settings.Leads,
        PublicPage.Followers => settings.Followers,
        _ => false,
    });
}
