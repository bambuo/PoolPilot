using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Abstractions;

/// <summary>盯市结果：取到的中间价 + 没取到的市场。</summary>
public sealed record MarkPrices(
    IReadOnlyDictionary<int, PriceAmount> Prices,
    IReadOnlyList<int> Unavailable);

/// <summary>
/// 盯市价来源。
///
/// 与 <see cref="ILighterPoolsClient.FetchOrderBookAsync"/> 的分工必须分清，否则两边会互相拖累：
/// <list type="bullet">
///   <item><c>FetchOrderBookAsync</c> 是「**要按这个价成交**」的取法——每次都得是当下最新的盘口，
///     慢也得认，因为用它定价等于按一个过去的市场成交。</item>
///   <item>这里的是「**看一眼现在值多少钱**」的取法——允许几十秒的陈旧，于是可以缓存、
///     也可以把多个市场并发取回。</item>
/// </list>
///
/// 分开的理由是实测：一个跟单员可能同时持有十几个市场，逐个现取盘口要十几秒
/// （每个市场都要新建一次连接、等一次快照），页面在那段时间里是空的。
/// 而盯市价本来就只是估值输入，不是成交依据。
/// </summary>
public interface IMarkPrices
{
    /// <summary>
    /// 取这些市场的盯市中间价。取不到的市场会出现在结果的 <see cref="MarkPrices.Unavailable"/> 里，
    /// 而不是被静默当成 0——0 看起来像"刚好打平"，而事实是"不知道"。
    /// </summary>
    Task<MarkPrices> ResolveAsync(
        IReadOnlyList<int> marketIds,
        CancellationToken cancellationToken = default);
}
