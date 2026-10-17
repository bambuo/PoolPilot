using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using PoolPilot.Api.Authorization;
using PoolPilot.Api.RateLimiting;
using PoolPilot.Api.Streaming;
using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 公开跟单页（<c>/api/public/followers</c>）：浏览别人建的**纸面**跟单员、匿名建一份自己的纸面账，
/// 以及只有创建者能做的删除。
///
/// 三条边界写死在这里：
/// <list type="bullet">
///   <item>**只有纸面**。实盘要落到某个交易所账号、要真下单，那既不是匿名访客能负责的事，
///     也不是该对全世界输出的信息。模式传 <c>live</c> 或带上任何交易所配置都是明确的 400
///     ——不静默降级，因为"以为在开实盘、拿到的是一份模拟账"比建不出来危险。</item>
///   <item>**只有 <c>is_public == true</c> 的行**。工作台的行在这里一律 404
///     （包括详情与删除）——"存在但看不到"会把这个端点变成一根探针。</item>
///   <item>**只有创建者能删**。判据是领域层那一个函数（列表与详情上的
///     <c>isOwnedByCaller</c> 用的也是它），界面不会出现"按钮在、点了 403"。</item>
/// </list>
///
/// 这些 action 都 <c>[AllowAnonymous]</c>：不进菜单表、不挂权限点（见 docs/PERMISSIONS.md）。
/// 访客身份靠 <c>poolpilot_visitor</c> Cookie 承载，它**不参与认证**、不是 Casbin 主体，
/// 只表达"这一行是我建的"这一件事（见 <see cref="PublicCallerAccessor"/>）。
/// </summary>
[ApiController]
[Route("api/public/followers")]
public sealed class PublicFollowersController(
    ListPublicFollowersUseCase list,
    GetPublicFollowerUseCase get,
    ListOwnedPublicFollowerIdsUseCase owned,
    CreatePublicFollowerUseCase create,
    DeletePublicFollowerUseCase delete,
    PublicCallerAccessor callers,
    PublicPageFeed feed,
    PublicWriteSync writeSync) : ControllerBase
{
    /// <summary>
    /// G2b：当前调用者拥有的公开行 Id。
    ///
    /// 只服务"删除入口该不该出现"这一个判断，不返回任何卡片数据、也不装配估值。
    /// SSE 帧是共享的，无法携带按人裁剪的 isOwnedByCaller（见
    /// <see cref="PoolPilot.Api.Streaming.PublicPageFeedAssembler"/> 的说明），
    /// 前端因此用这条一次性读口补上归属；页面打开时取一次，建/删后本地增删。
    /// 它不是轮询：列表与详情的数据仍然只来自 SSE。
    /// </summary>
    [HttpGet("ownership")]
    [AllowAnonymous]
    [PublicPageGate(PublicPage.Followers)]
    [EnableRateLimiting(PublicPageRateLimiting.ReadPolicy)]
    public async Task<ActionResult<IReadOnlyList<long>>> Ownership(CancellationToken cancellationToken = default) =>
        Ok(await owned.ExecuteAsync(callers.Current(), cancellationToken).ConfigureAwait(false));

    /// <summary>G2：公开跟单页的卡片列表（只有公开行、只有纸面模式）。</summary>
    [HttpGet]
    [AllowAnonymous]
    [PublicPageGate(PublicPage.Followers)]
    [EnableRateLimiting(PublicPageRateLimiting.ReadPolicy)]
    public async Task<ActionResult<IReadOnlyList<PublicFollowerSummaryDto>>> List(
        CancellationToken cancellationToken = default) =>
        Ok(await list.ExecuteAsync(callers.Current(), cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// G3：公开跟单详情（完整持仓、最近若干条流水、权益曲线）。
    /// 不存在、不是公开行、是实盘行——都回同一句 404。
    /// </summary>
    [HttpGet("{id:long}")]
    [AllowAnonymous]
    [PublicPageGate(PublicPage.Followers)]
    [EnableRateLimiting(PublicPageRateLimiting.ReadPolicy)]
    public async Task<ActionResult<PublicFollowerDto>> Get(
        long id,
        CancellationToken cancellationToken = default)
    {
        PublicFollowerDto? follower = await get
            .ExecuteAsync(id, callers.Current(), cancellationToken)
            .ConfigureAwait(false);

        return follower is null
            ? NotFound(new { error = PublicNotFoundMessage(id) })
            : Ok(follower);
    }

    /// <summary>
    /// W2：建一份纸面跟单（201）。该池还没有带单员时回 400，不自动补（与工作台同一条规则）。
    ///
    /// 访客 Cookie 只在**这一次真的建成了、这一行靠访客密钥认创建者、而请求原本没有 Cookie**
    /// 时才签发：被上限或校验拒掉的请求不该在浏览器里留下一枚没有归属的 Cookie。
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [PublicPageGate(PublicPage.Followers)]
    [EnableRateLimiting(PublicPageRateLimiting.WritePolicy)]
    public async Task<ActionResult<PublicFollowerDto>> Create(
        [FromBody] CreatePublicFollowerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        PublicCaller caller = callers.ForCreate(out bool hadVisitorCookie);

        PublicFollowerDto follower = await create
            .ExecuteAsync(request, caller, cancellationToken)
            .ConfigureAwait(false);

        // 签发排在用例之后：被上限或校验拒掉的请求不该在浏览器里留下一枚没有归属的 Cookie
        if (callers.ShouldIssueVisitorCookie(caller, hadVisitorCookie))
        {
            callers.IssueVisitorCookie(caller);
        }

        // 写成功才推帧；fire-and-forget 是为了不把匿名请求绑在一次全量装配上
        _ = feed.PublishNowAsync(CancellationToken.None);

        // 运行期也要知道这一行（否则复制管线要等下一次心跳才接上）；后台发起，不等它
        writeSync.Request();

        return CreatedAtAction(nameof(Get), new { id = follower.Id }, follower);
    }

    /// <summary>
    /// W3：删掉自己建的公开跟单（204）。
    /// 不是创建者 → 403；不存在或不是公开行 → 404。删走工作台同一条清理路径
    /// （连带清流水与权益点），但**不在请求里等运行期全量同步**：
    /// 删除成功后控制器插一帧，并在后台发起一次同步——让运行期尽快摘掉这一行，
    /// 否则心跳前的几笔账会落在已删 Id 上。
    /// </summary>
    [HttpDelete("{id:long}")]
    [AllowAnonymous]
    [PublicPageGate(PublicPage.Followers)]
    [EnableRateLimiting(PublicPageRateLimiting.WritePolicy)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken = default)
    {
        PublicFollowerDeleteOutcome outcome = await delete
            .ExecuteAsync(id, callers.Current(), cancellationToken)
            .ConfigureAwait(false);

        switch (outcome)
        {
            case PublicFollowerDeleteOutcome.Deleted:
                _ = feed.PublishNowAsync(CancellationToken.None);

                // 已删的行必须让运行期尽快摘掉：只删库不通知，心跳前的几笔账会落在孤儿 Id 上
                writeSync.Request();

                return NoContent();

            // 403 不隐瞒"这一行存在"：列表本来就把它摊给所有人看过，
            // 藏起来换不来任何信息差，只会让当事人不知道自己那一条去哪了
            case PublicFollowerDeleteOutcome.Forbidden:
                return StatusCode(
                    StatusCodes.Status403Forbidden,
                    new { error = $"公开跟单 {id} 不是这个浏览器创建的，只有创建者能删除它。" });

            default:
                return NotFound(new { error = PublicNotFoundMessage(id) });
        }
    }

    /// <summary>
    /// 404 的统一文案。"不存在"与"存在但不是公开行"必须是同一句话——
    /// 两种写法让 {id} 逐个试一遍就能把哪些跟单员是私有的探测出来。
    ///
    /// <c>internal</c> 是给实时流（<see cref="PublicStreamController"/>）用的：
    /// 详情流在握手前 404 时必须说**同一句**，否则两条路径拼起来又成了探针。
    /// </summary>
    internal static string PublicNotFoundMessage(long id) => $"公开跟单 {id} 不存在。";
}
