using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 公开面两页的开关状态（匿名可读）。
///
/// 存在的唯一理由：EventSource 看不到 404 的响应体，"这一页没开放"与"实时流正在连接"
/// 在浏览器里长得一模一样。没有这个读口时，生产默认（两片都关）下匿名访客会永远停在
/// "实时数据连接中…"；有了它，页面可以先问一句再决定是建连还是显示"未启用"。
///
/// 它**不挂功能开关**（两片都关也必须能回答），不读库、不打上游，只回两个布尔；
/// 也没有限流——它不产生数据，不值得占读额度。
/// </summary>
[ApiController]
[Route("api/public")]
public sealed class PublicStatusController(PublicPageSettings settings) : ControllerBase
{
    [HttpGet("status")]
    [AllowAnonymous]
    public ActionResult<PublicPageStatusDto> Status() => Ok(new PublicPageStatusDto
    {
        Leads = settings.Leads,
        Followers = settings.Followers,
    });
}
