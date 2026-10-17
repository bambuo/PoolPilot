using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using PoolPilot.Api.Authorization;
using PoolPilot.Api.RateLimiting;
using PoolPilot.Api.Streaming;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 公开带单页（<c>/api/public/lead-traders</c>）：匿名浏览被观察的池，并能为一个池开启观察。
///
/// 与 <see cref="LeadTradersController"/> 的分工不是"同一组接口的匿名版"：
/// <list type="bullet">
///   <item>**读**的是同一份账（<see cref="ListLeadTradersUseCase"/>），出口换成窄 DTO
///     ——不输出 <c>isEnabled</c>：公开侧没有开关，给了只会被读成"我能点"</item>
///   <item>**写**只有"确保有人在观察"这一个动作（<c>Ensure</c>），
///     没有 PUT、没有 DELETE。暂停与停止观察是**交易决策**，
///     而且会连带影响正在跟这个池的人（见 docs/LEAD-FOLLOWERS.md 第 7 节），
///     陌生人不能替使用者做。</item>
///   <item>新增要过数量上限：带单员表是全站共享的，公开页多出来的每一个观察者都在花
///     上游的连接与订阅额度（<see cref="PublicLeadTraderQuota"/>）。</item>
/// </list>
///
/// 这三个 action 都是 <c>[AllowAnonymous]</c>：它们不在 Casbin 的讨论范围内，
/// **不进菜单表、不挂权限点**（见 docs/PERMISSIONS.md）。挡住它们的依次是
/// 功能开关（<see cref="PublicPageGateAttribute"/>，默认关）与
/// 按来源地址的限流（<see cref="PublicPageRateLimiting"/>）。
/// </summary>
[ApiController]
[Route("api/public/lead-traders")]
public sealed class PublicLeadTradersController(
    ListPublicLeadTradersUseCase list,
    AddPublicLeadTraderUseCase add,
    PublicPoolOptionsUseCase poolOptions,
    PublicPageFeed feed,
    PublicWriteSync writeSync) : ControllerBase
{
    /// <summary>G1：公开带单页的列表。</summary>
    [HttpGet]
    [AllowAnonymous]
    [PublicPageGate(PublicPage.Leads)]
    [EnableRateLimiting(PublicPageRateLimiting.ReadPolicy)]
    public async Task<ActionResult<IReadOnlyList<PublicLeadTraderDto>>> List(
        CancellationToken cancellationToken = default) =>
        Ok(await list.ExecuteAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// W1：观察这个池（无请求体，身份就是池索引）。
    ///
    /// 已存在时**原样返回现状**，<c>isEnabled</c> 一个字都不动；
    /// 池不存在或数量到上限时由用例抛 <c>ArgumentException</c> → 400。
    ///
    /// 成功后 fire-and-forget 推一帧：实时流上的人不用等下一次 15 秒 tick。
    /// 推送失败只记日志（feed 内），写本身已经成功。
    /// </summary>
    [HttpPost("{accountIndex:long}")]
    [AllowAnonymous]
    [PublicPageGate(PublicPage.Leads)]
    [EnableRateLimiting(PublicPageRateLimiting.WritePolicy)]
    public async Task<ActionResult<PublicLeadTraderDto>> Add(
        long accountIndex,
        CancellationToken cancellationToken = default)
    {
        PublicLeadTraderDto added = await add
            .ExecuteAsync(accountIndex, cancellationToken)
            .ConfigureAwait(false);

        _ = feed.PublishNowAsync(CancellationToken.None);

        // 新建的观察者要尽快订上频道；后台发起，匿名请求不等全量重同步
        writeSync.Request();

        return Ok(added);
    }

    /// <summary>
    /// G4：池名候选。<c>q</c> 按池名子串、大小写不敏感；<c>limit</c> 默认 20、上限 50（超出夹回）。
    ///
    /// 路由是绝对模板（开头的 <c>/</c>）：这一条属于公开面但不属于 lead-traders 这个资源，
    /// 为它单开一个控制器只会多一个只有 action 数的类。
    /// </summary>
    [HttpGet("/api/public/pool-options")]
    [AllowAnonymous]
    [PublicPageGate(PublicPage.Leads)]
    [EnableRateLimiting(PublicPageRateLimiting.ReadPolicy)]
    public async Task<ActionResult<IReadOnlyList<PublicPoolOptionDto>>> PoolOptions(
        [FromQuery] string? q,
        [FromQuery] int? limit,
        CancellationToken cancellationToken = default) =>
        Ok(await poolOptions.ExecuteAsync(q, limit, cancellationToken).ConfigureAwait(false));
}
