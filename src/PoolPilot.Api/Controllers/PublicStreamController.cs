using System.Text.Json;
using System.Threading.Channels;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PoolPilot.Api.Authorization;
using PoolPilot.Api.Streaming;
using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Constants;

namespace PoolPilot.Api.Controllers;

/// <summary>
/// 公开面的实时推送（SSE）：
/// <list type="bullet">
///   <item><c>GET /api/public/stream</c>：列表连接，只发 <c>snapshot</c> 帧
///     （<c>leads</c> + <c>followers</c> + <c>takenAt</c>，全量）。</item>
///   <item><c>GET /api/public/stream?detail={id}</c>：详情连接，只发 <c>detail</c> 帧，
///     内容与 G3 逐键一致。</item>
///   <item><c>GET /api/public/stream?lead={index}</c>：带单员详情连接，只发 <c>lead</c> 帧
///     （从池详情精选的匿名投影）。</item>
/// </list>
///
/// 为什么值得一条长连接：公开页是"挂着看"的页面，轮询既要按人头放大上游成本，
/// 又总是慢半拍。这里由 <see cref="PublicPageFeed"/> 在进程内把订阅者收成一份快照，
/// 有订阅者才刷新——在线人数与上游调用次数脱钩。
///
/// 三条与普通端点不同的地方：
/// <list type="number">
///   <item>**不挂 <c>public-read</c> 限流**：那是"每分钟 60 次"的短请求窗口，
///     一条合法长连接会在第二个窗口被误杀。额度改由并发连接上限兜住
///     （<see cref="PublicPageLimits.MaxStreamConnections"/>，超出在推流前 503）。</item>
///   <item>**握手前判定**：详情不存在 / 非公开 / 非纸面时先回 404（文案与 G3 同一句），
///     绝不先写 200 的 SSE 头再挂一条永远没有内容的流。</item>
///   <item>**开关是"或"**：流同时输出两页的数据，只有 <c>PublicPages</c> 两片都关时才 404。</item>
/// </list>
///
/// 帧的时间语义由 feed 保证：建连即发；之后由池事件/落账/行情事件推帧；
/// 无帧时每 15 秒一行 <c>: ping</c> 保活；公开写成功后立即插一帧。
/// </summary>
[ApiController]
[Route("api/public")]
public sealed class PublicStreamController(PublicPageFeed feed, PublicPageSettings settings) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>并发连接满时的文案（形状仍是 <c>{"error": string}</c>）。</summary>
    internal const string CapacityMessage = "公开页实时连接数已达上限，请稍后再试。";

    [HttpGet("stream")]
    [AllowAnonymous]
    [PublicPageGate(PublicPage.Leads, PublicPage.Followers)]
    public async Task Stream(
        [FromQuery] long? detail,
        [FromQuery] long? lead,
        CancellationToken cancellationToken)
    {
        // 先占名额再谈其他：超额请求不该先花一次装配，也不该看到半截 SSE 头
        if (!feed.TryAcquireConnection())
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await Response.WriteAsJsonAsync(new { error = CapacityMessage }, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            if (detail is long followerId)
            {
                await StreamDetailAsync(followerId, cancellationToken).ConfigureAwait(false);
            }
            else if (lead is long accountIndex)
            {
                await StreamLeadAsync(accountIndex, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await StreamSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 客户端在首帧之前就断开：不是错误
        }
        finally
        {
            feed.ReleaseConnection();
        }
    }

    private async Task StreamLeadAsync(long accountIndex, CancellationToken cancellationToken)
    {
        // 流同时服务两页，门是"或"；带单员详情这一段自己有开关判据
        if (!settings.Leads)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await Response.WriteAsJsonAsync(
                new { error = PublicPageGateAttribute.DisabledMessage },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        PublicLeadTraderDetailDto? detail = await feed.GetLeadDetailAsync(accountIndex, cancellationToken)
            .ConfigureAwait(false);

        if (detail is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await Response.WriteAsJsonAsync(
                new { error = $"公开带单 {accountIndex} 不存在。" },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using PublicPageFeedSubscription subscription = feed.SubscribeLead(accountIndex);
        feed.SeedLeadDetail(detail);

        StartEventStream();
        await WriteEventAsync(
            new PublicStreamFrame(PublicStreamEvents.Lead, JsonSerializer.Serialize(detail, JsonOptions)),
            cancellationToken).ConfigureAwait(false);

        await PumpAsync(subscription.Reader, cancellationToken).ConfigureAwait(false);
    }

    private async Task StreamSnapshotAsync(CancellationToken cancellationToken)
    {
        // 先订阅再装配：装配期间发生的写后即时帧会落进通道，不会丢
        using PublicPageFeedSubscription subscription = feed.Subscribe();

        PublicPageFeedSnapshot first = await feed.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);

        StartEventStream();
        await WriteEventAsync(
            new PublicStreamFrame(PublicStreamEvents.Snapshot, JsonSerializer.Serialize(first, JsonOptions)),
            cancellationToken).ConfigureAwait(false);

        await PumpAsync(subscription.Reader, cancellationToken).ConfigureAwait(false);
    }

    private async Task StreamDetailAsync(long followerId, CancellationToken cancellationToken)
    {
        // 404 必须发生在写 SSE 头之前，而且文案要与 G3 逐字一致
        PublicFollowerDto? detail = await feed.GetDetailAsync(followerId, cancellationToken)
            .ConfigureAwait(false);

        if (detail is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await Response.WriteAsJsonAsync(
                new { error = PublicFollowersController.PublicNotFoundMessage(followerId) },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using PublicPageFeedSubscription subscription = feed.SubscribeDetail(followerId);

        // 把首帧登记成这条连接的 live 视图：之后的 market_stats 事件直接在它上面补标记价。
        feed.SeedDetail(detail);

        StartEventStream();
        await WriteEventAsync(
            new PublicStreamFrame(PublicStreamEvents.Detail, JsonSerializer.Serialize(detail, JsonOptions)),
            cancellationToken).ConfigureAwait(false);

        await PumpAsync(subscription.Reader, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// SSE 的固定三件套（契约逐字）：<c>text/event-stream</c>、<c>no-cache</c>、
    /// <c>x-accel-buffering: no</c>。最后一条是给反向代理看的：不写它，Nginx 会把帧攒在缓冲里，
    /// 客户端看到的第一帧可能迟到几十秒——那正是这条流要消灭的东西。
    /// </summary>
    private void StartEventStream()
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
    }

    private async Task PumpAsync(ChannelReader<PublicStreamFrame> reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using CancellationTokenSource idle =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(TimeSpan.FromSeconds(PublicPageLimits.StreamIntervalSeconds));

            PublicStreamFrame frame;

            try
            {
                frame = await reader.ReadAsync(idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 15 秒没有新帧就发一行注释保活：SSE 的 ping 不触发客户端事件，
                // 只用来让代理与客户端都知道链路还活着
                await Response.WriteAsync(": ping\n\n", cancellationToken).ConfigureAwait(false);
                await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            await WriteEventAsync(frame, cancellationToken).ConfigureAwait(false);

            // 终止帧写完就收线：详情行已经不存在，继续挂着只会每次事件重复一条 gone。
            // 客户端收到 gone 也会主动 close，两条路径都让连接尽快释放。
            if (frame.Event == PublicStreamEvents.Gone)
            {
                return;
            }
        }
    }

    private async Task WriteEventAsync(PublicStreamFrame frame, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(
            $"event: {frame.Event}\ndata: {frame.Data}\n\n",
            cancellationToken).ConfigureAwait(false);
        await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
