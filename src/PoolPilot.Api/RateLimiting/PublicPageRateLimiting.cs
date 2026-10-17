using System.Globalization;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.RateLimiting;

namespace PoolPilot.Api.RateLimiting;

/// <summary>
/// 公开页的两条限流策略。用框架自带的 <c>Microsoft.AspNetCore.RateLimiting</c>
/// ——它在共享框架里，**不是 NuGet 包**，所以 Directory.Packages.props 不加任何条目。
///
/// 为什么必须有这两条：公开页匿名可达，而它后面每一次读都在花钱——G1/G2 要按带单员
/// 逐个拉池子快照与盯市价，G4 要枚举公共池。这里没有登录失败锁定、没有验证码，
/// 也没有 Casbin 主体可问；唯一还能挡住"一个脚本把这台服务当免费行情源"的东西就是这里。
///
/// ⚠️ 分区键是 <c>Connection.RemoteIpAddress</c>。**反向代理下那就是代理机的地址**，
/// 于是所有一次性访客共用同一格额度。本阶段按它限流是诚实的：这套服务的部署形态就是
/// 只监听本机/内网（docs/AUTH.md 里"IP 在本网段内没有区分度"是同款说明），
/// 而配额给的是 60 次/分钟这种"人手点不出、脚本够用"的量级，直连与代理两种形态都不会误伤正常使用。
/// 真要挂到公网代理后面，要改的是这一处分区键（换成可信的 X-Forwarded-For 解析），
/// 不是策略的形状。
/// </summary>
public static class PublicPageRateLimiting
{
    /// <summary>读侧策略名（G1、G2、G3、G4）。</summary>
    public const string ReadPolicy = "public-read";

    /// <summary>写侧策略名（W1、W2、W3）。</summary>
    public const string WritePolicy = "public-write";

    /// <summary>每个来源在一个窗口里允许多少次读。</summary>
    private const int ReadsPerWindow = 60;

    /// <summary>每个来源在一个窗口里允许多少次写。</summary>
    private const int WritesPerWindow = 10;

    private static readonly TimeSpan ReadWindow = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan WriteWindow = TimeSpan.FromHours(1);

    /// <summary>读不到来源地址时的分区名。刻意不与任何真实地址同格，免得互相顶掉额度。</summary>
    private const string UnknownPartition = "unknown";

    /// <summary>超额时的统一文案（形状沿用 <c>{"error": string}</c>）。</summary>
    internal const string RejectedMessage = "这个来源的请求太频繁，请等一会儿再试。";

    public static IServiceCollection AddPublicPageRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(ReadPolicy, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(httpContext),
                _ => NewWindow(ReadsPerWindow, ReadWindow)));

            options.AddPolicy(WritePolicy, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(httpContext),
                _ => NewWindow(WritesPerWindow, WriteWindow)));

            // 拒绝时也回 JSON：调用方判断的是状态码与 { error }，
            // 一个空体的 429 只会让界面剩下一句没有信息量的"请求失败"。
            // Retry-After 一并给出——"等一会儿再来"没有等多久，等于没说
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = RejectedMessage },
                    cancellationToken).ConfigureAwait(false);
            };
        });
    }

    private static string PartitionKey(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString() ?? UnknownPartition;

    private static FixedWindowRateLimiterOptions NewWindow(int permitLimit, TimeSpan window) => new()
    {
        PermitLimit = permitLimit,
        Window = window,

        // 排队数 0：超额请求排队只会让浏览器多转几圈，然后照样被拒。
        // 立刻 429 + Retry-After 才是调用方能照着做的答案（少发一点、晚点再来）
        QueueLimit = 0,
    };
}
