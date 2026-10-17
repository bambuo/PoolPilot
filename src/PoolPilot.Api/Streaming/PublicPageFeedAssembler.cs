using Microsoft.Extensions.DependencyInjection;

using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;

namespace PoolPilot.Api.Streaming;

/// <summary>
/// 一帧快照要装什么。抽成接口不是为了"以后换实现"，而是为了让测试能换上一个
/// **只计数的替身**：并发连接与单飞的关键断言是"装配发生了几次"，
/// 数上游 fake 的调用次数会被缓存、多服务复用等无关因素干扰。
/// </summary>
public interface IPublicPageFeedAssembler
{
    /// <summary>装配列表连接的一帧：两份列表 + 装配时刻。</summary>
    Task<PublicPageFeedSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>装配详情连接的一帧；不存在 / 非公开 / 非纸面一律 <c>null</c>（与 G3 同判据）。</summary>
    Task<PublicFollowerDto?> BuildDetailAsync(long followerId, CancellationToken cancellationToken = default);

    /// <summary>装配带单员详情；池或观察关系不存在时 <c>null</c>。</summary>
    Task<PublicLeadTraderDetailDto?> BuildLeadDetailAsync(
        long accountIndex,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 真实的装配器：每次装配开一个**新的 DI 作用域**，把现有公开用例原样叫起来。
///
/// 为什么必须新开作用域：feed 是单例，而用例与 <c>DbContext</c> 是 Scoped。
/// 长连接（最长挂到客户端离开）若把某一个请求的作用域拽住，那条 DbContext 会一直不释放，
/// 还会和后续请求的写操作抢同一条连接。
///
/// 为什么用匿名的 <see cref="PublicCaller"/>：一帧快照/详情是**所有订阅者共享的**，
/// 里面不可能同时装下每个人的 Cookie。因此流上的 <c>isOwnedByCaller</c> 恒为 false；
/// "这一行是不是你建的"由前端在页面打开时用一次
/// <c>GET /api/public/followers/ownership</c> 单独取回（只回答归属，不装配数据）。
/// 这也是公开流只输出窄 DTO 的另一层理由：共享帧里不该有需要按人裁剪的字段。
/// </summary>
public sealed class PublicPageFeedAssembler(
    IServiceScopeFactory scopes,
    PublicPageSettings settings,
    TimeProvider clock) : IPublicPageFeedAssembler
{
    private static readonly PublicCaller SharedCaller = new(UserId: null, VisitorSecret: null);

    /// <summary>
    /// 快照的两半各自受自己的开关约束。流是一条共享连接，但**不能因为任意一片开着
    /// 就把另一片的数据也发出去**——两片开关的粒度不同，关掉跟单页时它的行必须从
    /// 快照里消失，否则这条流就成了绕过开关的第二条路。
    /// </summary>
    public async Task<PublicPageFeedSnapshot> BuildSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        IReadOnlyList<PublicLeadTraderDto> leads = settings.Leads
            ? await scope.ServiceProvider
                .GetRequiredService<ListPublicLeadTradersUseCase>()
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false)
            : [];

        IReadOnlyList<PublicFollowerSummaryDto> followers = settings.Followers
            ? await scope.ServiceProvider
                .GetRequiredService<ListPublicFollowersUseCase>()
                .ExecuteAsync(SharedCaller, cancellationToken)
                .ConfigureAwait(false)
            : [];

        return new PublicPageFeedSnapshot(leads, followers, clock.GetUtcNow());
    }

    public async Task<PublicFollowerDto?> BuildDetailAsync(
        long followerId,
        CancellationToken cancellationToken = default)
    {
        // 跟单页关着时，详情流的 404 判据与 G3 一样：那一行在这个部署里不存在。
        if (!settings.Followers)
        {
            return null;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<GetPublicFollowerUseCase>()
            .ExecuteAsync(followerId, SharedCaller, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PublicLeadTraderDetailDto?> BuildLeadDetailAsync(
        long accountIndex,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Leads)
        {
            return null;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<GetPublicLeadTraderDetailUseCase>()
            .ExecuteAsync(accountIndex, cancellationToken)
            .ConfigureAwait(false);
    }
}
