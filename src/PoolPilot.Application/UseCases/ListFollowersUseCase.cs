using PoolPilot.Application.Dtos;
using PoolPilot.Application.Mapping;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 跟单员列表（卡片页）。
///
/// 逐个账户串行取：每个账户都要拉一次池子快照与盯市价，并发打上游会撞限速；
/// 而这是给一个人看的列表，串行足够快。单个账户失败**不放弃整页**——
/// 一个池子取不到快照不该让其他跟单员一起消失。
///
/// 带单员名字来自本地库（带单员表里存了名字快照），不再需要为了显示名字去拉全量池列表。
/// </summary>
public sealed class ListFollowersUseCase(
    IFollowerRepository followers,
    ILeadTraderRepository leads,
    GetFollowerUseCase getFollower,
    GetLiveAccountUseCase getLiveAccount,
    ILiveOrderRepository liveOrders)
{
    public async Task<IReadOnlyList<FollowerSummaryDto>> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Follower> configured = await followers.ListAsync(cancellationToken).ConfigureAwait(false);

        return await ExecuteAsync(configured, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按**给定的一批跟单员**装配卡片。
    ///
    /// 收这一个入口的理由是"账的算法只能有一份"：公开跟单页要同一套估值与降级口径
    /// （见 <see cref="ListPublicFollowersUseCase"/>），但它先按"公开 + 纸面"把行筛过——
    /// 于是筛行发生在调用方，装配仍然走这里，而不是在公开侧重写一遍。
    /// 私有端点走的正是同一条代码路径，行为与响应完全不变。
    /// </summary>
    public async Task<IReadOnlyList<FollowerSummaryDto>> ExecuteAsync(
        IReadOnlyList<Follower> configured,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configured);

        if (configured.Count == 0)
        {
            return [];
        }

        Dictionary<long, LeadTrader> leadsById = (await leads.ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(l => l.AccountIndex.Value);

        List<FollowerSummaryDto> summaries = [];

        foreach (Follower follower in configured)
        {
            try
            {
                // 实盘走另一条路：它**没有纸面账**（那本账是"跟着它会变成多少"的模拟），
                // 照旧去重放流水只会得到"权益 = 本金、收益率 0"这种编出来的数字，
                // 挂在卡片上就是一句假话。它的真相在交易所账号里
                if (follower.Mode == Domain.Enums.CopyMode.Live)
                {
                    summaries.Add(await ToLiveSummaryAsync(follower, leadsById, cancellationToken)
                        .ConfigureAwait(false));
                    continue;
                }

                FollowerDto? detail = await getFollower
                    .ExecuteAsync(follower.Id, cancellationToken)
                    .ConfigureAwait(false);

                if (detail is not null)
                {
                    summaries.Add(ToSummary(detail));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单个跟单员的池子取不到（网络抖动、池子下线）不该让整页打不开：
                // 用配置本身给一个"估值不可得"的条目，页面照样能操作暂停/改本金。
                summaries.Add(Fallback(follower, leadsById.GetValueOrDefault(follower.LeadAccountIndex.Value)));
            }
        }

        return summaries;
    }

    /// <summary>
    /// 实盘跟单员的卡片摘要：**读交易所**，不读纸面账。
    ///
    /// 读不到（网络、key 失效、交易所维护）时如实标记"估值不可得"，
    /// 而不是退回纸面那套数字——那会让人以为账户一切正常。
    /// </summary>
    private async Task<FollowerSummaryDto> ToLiveSummaryAsync(
        Follower follower,
        IReadOnlyDictionary<long, LeadTrader> leadsById,
        CancellationToken cancellationToken)
    {
        LeadTrader? lead = leadsById.GetValueOrDefault(follower.LeadAccountIndex.Value);
        LiveAccountDto account = await getLiveAccount.ExecuteAsync(follower.Id, cancellationToken).ConfigureAwait(false);

        // 卡片最后一行的两个数：纸面来自纸面台账，实盘来自实盘流水——
        // 槽位相同（发出去了多少 / 没跟上多少），读的人不必每张卡重新找
        (int placed, int notFollowed) = await liveOrders
            .CountAsync(follower.Id, cancellationToken)
            .ConfigureAwait(false);

        return new FollowerSummaryDto
        {
            Id = follower.Id,
            LeadAccountIndex = follower.LeadAccountIndex.Value,
            LeadName = lead?.Name ?? $"池 {follower.LeadAccountIndex.Value}",
            LeadIsEnabled = lead?.IsEnabled ?? false,
            Mode = FollowerMapper.Mode(follower.Mode),
            ExchangeName = account.ExchangeName,
            ExchangeConfigId = follower.ExchangeConfigId,
            AvailableBalance = account.AvailableBalance,
            LivePositions = account.Positions,
            InitialCapital = follower.InitialCapital.Value,
            IsActive = follower.IsActive,
            IsValuationAvailable = true,

            // 实盘的"权益"就是钱包余额——交易所给的，不是我们算的
            Equity = account.WalletBalance,

            // **不给收益率**：那个账号里可能还放着与这次跟单无关的资金，
            // 拿"钱包余额 ÷ 本金"当收益率等于宣称整个账户都是这次跟单赚的
            ReturnPercent = 0m,

            // 纸面口径的字段在实盘上**没有对应物**，一律留零并靠 Mode 区分显示：
            // 填上"看起来像那么回事"的数（比如把未实现盈亏塞进 RealizedPnl）才是真的误导
            RealizedPnl = 0m,
            UnrealizedPnl = account.UnrealizedPnl,
            Fees = 0m,

            // 比例 = 本金 ÷ **池子权益**，而池子权益要取池子快照（列表刻意不取）。
            // 拿钱包余额当分母会算出另一个数（实测 0.551% vs 详情页 0.0184%），
            // 两张页面显示同一个跟单员的"比例"却不一样——这种不一致比空着更糟
            Scale = 0m,
            PoolEquity = 0m,
            GrossNotional = account.GrossNotional,
            FillCount = placed,
            SkipCount = notFollowed,
            Positions = [],
            UnpricedMarketCount = 0,
        };
    }

    private static FollowerSummaryDto ToSummary(FollowerDto d) => new()
    {
        Id = d.Id,
        LeadAccountIndex = d.LeadAccountIndex,
        LeadName = d.LeadName,
        LeadIsEnabled = d.LeadIsEnabled,
        Mode = d.Mode,
        ExchangeName = d.ExchangeName,
        ExchangeConfigId = d.ExchangeConfigId,
        AvailableBalance = null,
        LivePositions = [],
        InitialCapital = d.InitialCapital,
        IsActive = d.IsActive,
        IsValuationAvailable = true,
        Equity = d.Equity,
        ReturnPercent = d.ReturnPercent,
        RealizedPnl = d.RealizedPnl,
        UnrealizedPnl = d.UnrealizedPnl,
        Fees = d.Fees,
        Scale = d.Scale,
        PoolEquity = d.PoolEquity,
        GrossNotional = d.GrossNotional,
        FillCount = d.FillCount,
        SkipCount = d.SkipCount,
        Positions =
        [
            // 非零仓位、按名义额降序的**完整**清单。这里不裁剪成前两项：卡片是一个
            // 固定两行高的视口 + 跑马灯（见 FollowListView 的 .card-positions），
            // 在这里截断会让第三行之后的仓位永远看不见，而那段滚动也成了死代码。
            // 只过滤 Size == 0 的行——那是已平掉的仓位，不是"看不下的仓位"。
            .. d.Positions
                .Where(p => p.Size != 0m)
                .OrderByDescending(p => Math.Abs(p.Notional))
                .Select(p => new PaperPositionBriefDto
                {
                    MarketId = p.MarketId,
                    Symbol = p.Symbol,
                    Side = p.Side,
                    Size = p.Size,
                    EntryVwap = p.EntryVwap,
                    Notional = p.Notional,
                    UnrealizedPnl = p.UnrealizedPnl,
                    TrackingDeviationPercent = p.TrackingDeviationPercent,
                }),
        ],
        UnpricedMarketCount = d.MarksUnavailable.Count,
    };

    /// <summary>
    /// 池子侧数据取不到时的降级条目。
    ///
    /// 配置照常可读可改（暂停、改本金都还能做），但估值一律标记为**不可得**：
    /// 这里把权益填成本金只是为了让字段非空，界面必须看 <c>IsValuationAvailable</c>
    /// 决定显示什么——把"填位的 0"渲染成收益率会凭空造出一个数字。
    /// </summary>
    private static FollowerSummaryDto Fallback(Follower follower, LeadTrader? lead) => new()
    {
        Id = follower.Id,
        LeadAccountIndex = follower.LeadAccountIndex.Value,
        LeadName = lead?.Name ?? $"池 {follower.LeadAccountIndex.Value}",
        LeadIsEnabled = lead?.IsEnabled ?? false,
        Mode = FollowerMapper.Mode(follower.Mode),

        // 降级路径拿不到配置（也就不打库），名字留空。界面据此显示"读取中/不可得"，
        // 而不是把一个编出来的交易所名挂在卡片上。
        // **Id 照给**：它是手上就有的字段（不需要打库），而交易所页要靠它标出"这条配置有人在用"——
        // 降级的是估值，不是这层关系
        ExchangeName = null,
        ExchangeConfigId = follower.ExchangeConfigId,
        AvailableBalance = null,
        LivePositions = [],
        InitialCapital = follower.InitialCapital.Value,
        IsActive = follower.IsActive,
        IsValuationAvailable = false,
        Equity = follower.InitialCapital.Value,
        ReturnPercent = 0m,
        RealizedPnl = 0m,
        UnrealizedPnl = 0m,
        Fees = 0m,
        Scale = 0m,
        PoolEquity = 0m,
        GrossNotional = 0m,
        FillCount = 0,
        SkipCount = 0,
        Positions = [],
        UnpricedMarketCount = 0,
    };
}
