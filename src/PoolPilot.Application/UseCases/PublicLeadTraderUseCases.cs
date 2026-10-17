using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.Mapping;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 公开带单页的列表（G1）。
///
/// 装配整个走 <see cref="ListLeadTradersUseCase"/>——那一页要合起来的三类信息
/// （配置、运行期、信号产出）与订阅数都在这里一样需要，公开的这一份只是**换了出口字段**：
/// 不输出 <c>isEnabled</c>（公开侧既不能开也不能关，给了只会引来"点了没反应"），
/// 也不输出 <c>activeFollowerCount</c>（卡片上只写"有几个人在跟"）。
/// </summary>
public sealed class ListPublicLeadTradersUseCase(ListLeadTradersUseCase leads)
{
    public async Task<IReadOnlyList<PublicLeadTraderDto>> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LeadTraderDto> all = await leads.ExecutePublicAsync(cancellationToken).ConfigureAwait(false);

        return [.. all.Select(PublicPageMapper.ToPublicLead)];
    }
}

/// <summary>
/// 公开带单页的「观察这个池」（W1）。
///
/// 三条与私有端点不同的地方，各有各的理由：
/// <list type="number">
///   <item>**用 <see cref="EnsureLeadTraderUseCase"/> 而不是
///     <see cref="ConfigureLeadTraderUseCase"/>**：已存在的带单员一律原样返回，
///     <c>IsEnabled</c> 一个字都不动。这是"公开侧无法暂停、也无法撤销暂停"的落点——
///     换成 Configure 就等于让陌生人把使用者特意关掉的观察者打开。</item>
///   <item>只有**真的会新增**时才核对数量上限。已存在时这一发不增加任何东西，
///     卡在上限上回 400 会把"这个池早就有人看着"谎报成"没看着"。</item>
///   <item>写成功后**不在请求里等运行期全量同步**：控制器会 fire-and-forget 插一帧，
///     并在后台发起一次同步（新观察者要尽快订上频道）。全量重同步会连上游、重建订阅，
///     把匿名请求的耗时绑在它上面不值得；工作台自己的写路径照旧。</item>
/// </list>
/// </summary>
public sealed class AddPublicLeadTraderUseCase(
    ILeadTraderRepository leads,
    EnsureLeadTraderUseCase ensure,
    ListLeadTradersUseCase list,
    PublicLeadTraderQuota quota)
{
    public async Task<PublicLeadTraderDto> ExecuteAsync(
        long accountIndex,
        CancellationToken cancellationToken = default)
    {
        // 索引非法（0 或负数）由值对象拒收 → 中间件映射成 400
        AccountIndex pool = AccountIndex.From(accountIndex);

        LeadTrader? existing = await leads.GetAsync(pool, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            await quota.EnsureCanAddAsync(cancellationToken).ConfigureAwait(false);
        }

        // 池不存在时这里抛 ArgumentException（"找不到账户索引为 X 的池…"），沿用现有文案 → 400
        LeadTrader lead = await ensure
            .ExecuteAsync(pool, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return PublicPageMapper.ToPublicLead(
            await list.ExecutePublicAsync(lead.AccountIndex, cancellationToken).ConfigureAwait(false));
    }
}
