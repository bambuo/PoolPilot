using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.Mapping;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 公开跟单页的卡片列表（G2）。
///
/// 两道筛子在这里，而且只在这里：
/// <list type="number">
///   <item><c>IsPublic == true</c>：工作台建的行对公开面一律不可见。</item>
///   <item><c>Mode == Paper</c>：公开面只讲"跟着它会变成多少"这本纸面账。
///     实盘行带着别人的账号事实（在哪个交易所、余额多少），一行都不能出现在匿名响应里。</item>
/// </list>
/// 筛完之后**装配整个复用工作台的同一条路径**（
/// <see cref="ListFollowersUseCase.ExecuteAsync(IReadOnlyList{Follower}, CancellationToken)"/>）：
/// 估值、取不到快照时的降级口径、单行失败不放弃整页——两边必须一致，
/// 否则同一个跟单员在工作台和公开页上会报出两个不同的数。
/// </summary>
public sealed class ListPublicFollowersUseCase(
    IFollowerRepository followers,
    ListFollowersUseCase list)
{
    public async Task<IReadOnlyList<PublicFollowerSummaryDto>> ExecuteAsync(
        PublicCaller caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // 先读全部行再筛，而不是给仓储加一条"公开行"查询：这一张表的规模由
        // PublicPageLimits.TotalPublicFollowers 封在几百行以内，而筛选条件是两个
        // 从不进 WHERE 的软属性（公开与模式）。多一个查询入口换来的是两处口径要同步维护。
        IReadOnlyList<Follower> visible =
            [.. (await followers.ListAsync(cancellationToken).ConfigureAwait(false))
                .Where(f => f.IsPublic && f.Mode == CopyMode.Paper)];

        if (visible.Count == 0)
        {
            return [];
        }

        IReadOnlyList<FollowerSummaryDto> summaries = await list
            .ExecuteAsync(visible, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<long, Follower> rowsById = visible.ToDictionary(f => f.Id);

        return
        [
            .. summaries
                .Where(summary => rowsById.ContainsKey(summary.Id))
                .Select(summary => PublicPageMapper.ToSummary(summary, rowsById[summary.Id], caller)),
        ];
    }
}

/// <summary>
/// 当前调用者在公开跟单页上拥有的行 Id（G2 的归属读口，不参与卡片数据）。
///
/// 为什么需要它：SSE 的 snapshot/detail 帧是由所有订阅者共享的，帧里不可能同时装下
/// 每个人的 Cookie，所以共享帧上的 <c>isOwnedByCaller</c> 只能是 false。若前端只看共享帧，
/// "你创建的"与删除入口就永远不会出现。
///
/// 这个读口只回答归属，不装配估值、不打上游；前端在页面打开时取一次，建/删后用响应
/// 本地增删即可。它不是轮询——卡片与详情的数据仍然只来自 SSE。
/// </summary>
public sealed class ListOwnedPublicFollowerIdsUseCase(IFollowerRepository followers)
{
    public async Task<IReadOnlyList<long>> ExecuteAsync(
        PublicCaller caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        IReadOnlyList<Follower> all = await followers.ListAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. all
                .Where(f => f.IsPublic && f.Mode == CopyMode.Paper)
                .Where(f => PublicFollowerOwnership.IsCreator(f, caller.UserId, caller.VisitorSecret))
                .Select(f => f.Id),
        ];
    }
}

/// <summary>
/// 公开跟单页的详情（G3）。
///
/// 404 把三种情况合成一个回答（不存在 / 不是公开行 / 是实盘行）：
/// 公开页不能变成"这个 Id 存在但我不给你看"的探针。
/// 装配同样复用 <see cref="GetFollowerUseCase"/>，只在出口裁剪字段与流水条数——
/// 盯市、重放、跟踪偏差那些算法一份都不重写。
/// </summary>
public sealed class GetPublicFollowerUseCase(
    IFollowerRepository followers,
    GetFollowerUseCase get)
{
    public async Task<PublicFollowerDto?> ExecuteAsync(
        long followerId,
        PublicCaller caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        Follower? row = await followers.GetAsync(followerId, cancellationToken).ConfigureAwait(false);

        if (row is null || !row.IsPublic || row.Mode != CopyMode.Paper)
        {
            return null;
        }

        FollowerDto? detail = await get.ExecuteAsync(followerId, cancellationToken).ConfigureAwait(false);

        return detail is null
            ? null
            : PublicPageMapper.ToDetail(detail, row, caller, valuationAvailable: true);
    }
}

/// <summary>
/// 公开页新建跟单员（W2）。
///
/// 与工作台那条（<see cref="CreateFollowerUseCase"/>）的三处不同，逐条有据：
/// <list type="number">
///   <item>**只建纸面账**：模式上 <c>live</c> 与任何交易所配置都是明确的 400，
///     不静默降级成纸面（"以为在开实盘、拿到一份模拟账"比建不出来危险得多）。</item>
///   <item>**多四列归属信息**：公开与创建者。它们是"谁能删这一行"的全部依据。</item>
///   <item>**多四道数量闸**（全站公开条数、单创建者条数、本金区间、名称长度）。
///     这一页面向陌生人，上限就是"这一页最多长这么大"的对外承诺。</item>
/// </list>
///
/// **相同的那一条更要知道**：跟单员订阅的是**带单员**，不是池。带单员与池 1:1
/// （它的身份就是池索引），作用是替系统盯着那个池产出信号；所以这里与工作台一样，
/// **该池没有带单员就拒绝**，绝不"顺手补一个观察者"。理由不是省事与否：
/// 公开建观察者会让"我想跟一份"变成"我替全站决定去长期观察一个池"——那是一条长期占着
/// 上游订阅与落库的决定，属于带单页（W1）那道明确的动作，有它自己的名额与限流。
/// 界面上的选择器因此列的是带单员，而不是池。
///
/// **写成功不在请求里等运行期全量同步**：公开面有实时流（<c>PublicPageFeed</c>），
/// 控制器在成功后 fire-and-forget 插一帧，并在后台发起一次 <c>RequestSyncAsync</c>
/// ——全量重同步会连上游、重建订阅，让匿名请求等它会把响应时间绑在网络上；
/// 但完全不通知也不行（已删行会在心跳前继续落账）。私有工作台的写仍然照旧同步。
/// </summary>
public sealed class CreatePublicFollowerUseCase(
    IFollowerRepository followers,
    ILeadTraderRepository leads,
    TimeProvider clock)
{
    public async Task<PublicFollowerDto> ExecuteAsync(
        CreatePublicFollowerRequest request,
        PublicCaller caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        PublicFollowerInputRules.EnsurePaperMode(request.Mode);
        PublicFollowerInputRules.EnsureNoExchangeConfig(request.ExchangeConfigId);
        PublicFollowerInputRules.EnsureCapital(request.InitialCapital);

        string? label = PublicFollowerInputRules.NormalizeLabel(request.Label);
        PublicFollowerInputRules.EnsureCallerCanOwnFollower(caller.UserId, caller.VisitorSecret);

        IReadOnlyList<Follower> all = await followers.ListAsync(cancellationToken).ConfigureAwait(false);

        EnsureWithinPublicQuotas(all, caller);

        AccountIndex pool = AccountIndex.From(request.LeadAccountIndex);

        // 与工作台同一条规则：没有观察者就没有可跟的东西（见类说明第"相同的那一条"）
        LeadTrader lead = await leads.GetAsync(pool, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException(
                $"「{pool.Value}」上还没有带单员，跟不了——跟单员订阅的是带单员，不是池。"
                + "先到公开带单页为这个池建立观察，再来开跟单。",
                nameof(request));

        Follower created = await followers.CreateAsync(
            new Follower
            {
                LeadAccountIndex = pool,
                InitialCapital = UsdAmount.From(request.InitialCapital),
                Mode = CopyMode.Paper,

                // 公开面没有实盘：这两样一律留空，与校验一致（校验在前，这里不可能收到非空值）
                ExchangeConfigId = null,
                IsActive = true,
                CreatedAt = LighterTimestamp.FromMilliseconds(
                    clock.GetUtcNow().ToUnixTimeMilliseconds()),
                IsPublic = true,
                OwnerUserId = caller.UserId,
                Label = label,
            },
            cancellationToken).ConfigureAwait(false);

        // 匿名建的行：凭证要等 Id 分配出来才派生得动（派生的输入之一就是 Id），所以是第二次写
        Follower owned = caller.UserId is null
            ? await AttachVisitorOwnerAsync(created, caller.VisitorSecret!, cancellationToken).ConfigureAwait(false)
            : created;

        // 写入后的即时快照：不在这次写里塞一整轮上游往返，估值交给随后的 G3。
        // valuationAvailable=false 就是"这些数还没算过"，界面必须显示读取中而不是把 0 当结果
        return PublicPageMapper.ToDetail(
            FollowerMapper.Snapshot(owned, lead),
            owned,
            caller,
            valuationAvailable: false);
    }

    /// <summary>
    /// 两道数量闸。全站那一闸算的是"公开行"而不是"匿名行"：
    /// 登录用户从公开页建的行同样在长这个页面的体积，也占同一个额度。
    /// </summary>
    private static void EnsureWithinPublicQuotas(IReadOnlyList<Follower> all, PublicCaller caller)
    {
        int publicCount = all.Count(f => f.IsPublic);

        if (publicCount >= PublicPageLimits.TotalPublicFollowers)
        {
            throw new ArgumentException(
                $"公开跟单页已达上限 {PublicPageLimits.TotalPublicFollowers} 条，暂时不能再新建。",
                nameof(caller));
        }

        int mine = all.Count(f => PublicFollowerOwnership.IsCreator(f, caller.UserId, caller.VisitorSecret));

        if (mine >= PublicPageLimits.FollowersPerVisitor)
        {
            throw new ArgumentException(
                $"你已经建了 {mine} 条公开跟单，上限是 {PublicPageLimits.FollowersPerVisitor} 条。"
                + "删掉不再看的那几条再来。",
                nameof(caller));
        }
    }

    /// <summary>
    /// 派生并回填匿名行的创建者凭证，返回带着凭证的那一行。
    ///
    /// 返回带凭证的副本而不是继续用 <c>created</c>：响应里的 <c>isOwnedByCaller</c>
    /// 要用同一把判据算，而判据读的就是这一列——留 null 会让刚刚建出这一行的
    /// 人在自己的响应里看到"这不是我的"。
    /// </summary>
    private async Task<Follower> AttachVisitorOwnerAsync(
        Follower created,
        string visitorSecret,
        CancellationToken cancellationToken)
    {
        string credential = PublicFollowerOwnership.DeriveCredential(visitorSecret, created.Id);

        await followers.SetOwnerCredentialAsync(created.Id, credential, cancellationToken).ConfigureAwait(false);

        return created with { OwnerCredentialHash = credential };
    }
}

/// <summary>
/// 公开跟单页的删除（W3）。三档结论由调用方映射成状态码，每档一句理由：
/// <list type="bullet">
///   <item>**404**：不存在，或存在但不是公开行（实盘行也一样 404）——不给"存在但看不到"的探针。</item>
///   <item>**403**：存在且公开，但创建者不是调用者。这里不隐瞒"它在这儿"：
///     列表本来就把全部公开行摊给所有人看，隐瞒换不来任何信息差。</item>
///   <item>**204**：删。走 <see cref="DeleteFollowerUseCase"/>，连带清掉流水与权益点——
///     与工作台删跟单员同一条清理路径，不另写一套。运行期同步**不在请求里等**：
///     控制器插一帧，并在后台发起一次同步（见类注释）。</item>
/// </list>
/// </summary>
public sealed class DeletePublicFollowerUseCase(
    IFollowerRepository followers,
    DeleteFollowerUseCase delete)
{
    public async Task<PublicFollowerDeleteOutcome> ExecuteAsync(
        long followerId,
        PublicCaller caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        Follower? row = await followers.GetAsync(followerId, cancellationToken).ConfigureAwait(false);

        if (row is null || !row.IsPublic || row.Mode != CopyMode.Paper)
        {
            return PublicFollowerDeleteOutcome.NotFound;
        }

        // 判据是领域层那一个函数（列表上的 isOwnedByCaller 用的也是它）：
        // 写成两遍迟早会变成两种答案
        if (!PublicFollowerOwnership.IsCreator(row, caller.UserId, caller.VisitorSecret))
        {
            return PublicFollowerDeleteOutcome.Forbidden;
        }

        await delete
            .ExecuteAsync(followerId, requestSync: false, cancellationToken)
            .ConfigureAwait(false);

        return PublicFollowerDeleteOutcome.Deleted;
    }
}
