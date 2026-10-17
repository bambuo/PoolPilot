using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.Mapping;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 开始跟单：新建一个跟单员，订阅指定的带单员。
///
/// 三步，每步都有一个明确的理由：
/// <list type="number">
///   <item>校验本金——它是规模比例的分母（比例 = 本金 / 池子权益），为 0 或负数算不出任何仓位。</item>
///   <item>带单员必须已经存在，否则拒绝。跟单员订阅的是**带单员**，而带单员与池 1:1、
///     它的存在就是"系统在盯着这个池"——那是「带单员」页的一个独立决定（占着上游订阅与持续落库），
///     不在这里顺手替使用者做。界面上的选择器列的也正是带单员，所以这一步本来不该被遇到。</item>
///   <item>落库并返回。跟单员一旦建立即为"跟随中"——没有"建了但先不跟"的中间态。</item>
/// </list>
/// </summary>
public sealed class CreateFollowerUseCase(
    IFollowerRepository followers,
    ILeadTraderRepository leads,
    IExchangeConfigRepository exchanges,
    IExchangeBrokerFactory brokers,
    LiveTradingLimits limits,
    ILeadTraderSync sync)
{
    public async Task<FollowerDto> ExecuteAsync(
        CreateFollowerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.InitialCapital <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.InitialCapital,
                "本金必须为正数——它是规模比例的分母，为 0 或负数算不出任何仓位。");
        }

        CopyMode mode = ParseMode(request.Mode);
        AccountIndex pool = AccountIndex.From(request.LeadAccountIndex);

        LeadTrader lead = await leads.GetAsync(pool, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException(
                $"池 {pool.Value} 还没有带单员。跟单员订阅的是带单员——"
                + "先到「带单员」页为这个池创建观察者，再来跟单。",
                nameof(request));

        long? exchangeConfigId = mode == CopyMode.Live
            ? await RequireLiveExchangeAsync(request.ExchangeConfigId, request.InitialCapital, cancellationToken)
                .ConfigureAwait(false)
            : null;

        Follower created = await followers.CreateAsync(
            new Follower
            {
                LeadAccountIndex = pool,
                InitialCapital = UsdAmount.From(request.InitialCapital),
                Mode = mode,
                ExchangeConfigId = exchangeConfigId,
                IsActive = true,
                CreatedAt = LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            },
            cancellationToken).ConfigureAwait(false);

        // 立刻挂上订阅者：刚建的账户当场按当前市价建仓，而不是等下一次心跳
        await sync.RequestSyncAsync(cancellationToken).ConfigureAwait(false);

        return FollowerMapper.Snapshot(created, lead);
    }

    /// <summary>
    /// 实盘必须指定一个**能下单**的交易所账号，且在把配置写进去之前就查清楚。
    ///
    /// 三条都要在建立之前拦下，而不是等到第一笔信号：
    /// 缺配置、配置不存在、平台没有执行侧实现——这三种情况下跟单员建出来是"看着在跑、
    /// 实际一单都发不出去"，而使用者要到很久以后才发现。宁可现在建不出来。
    /// </summary>
    private async Task<long> RequireLiveExchangeAsync(
        long? exchangeConfigId,
        decimal capital,
        CancellationToken cancellationToken)
    {
        if (exchangeConfigId is not { } configId)
        {
            throw new ArgumentException(
                "实盘跟单必须指定交易所账号：下单要落到某一个账号上（就是你配好钥匙的那个）。",
                nameof(exchangeConfigId));
        }

        ExchangeConfig config = await exchanges.GetAsync(configId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"交易所配置 {configId} 不存在。", nameof(exchangeConfigId));

        if (brokers.Create(config) is null)
        {
            throw new ArgumentException(
                $"{config.DisplayName}（{ExchangeEnumText.ToText(config.Code)}）还没有执行侧实现，"
                + "现在给它建实盘跟单会一单都发不出去。",
                nameof(exchangeConfigId));
        }

        if (!limits.Enabled)
        {
            throw new ArgumentException(
                "实盘总开关是关的（LiveTrading:Enabled=false）。"
                + "开着它才能真正下单——先确认这是你要的，再建实盘跟单。",
                nameof(exchangeConfigId));
        }

        // 规模基准不得超过账户里的钱：否则算出来的仓位账户开不出来，
        // 而使用者看到的是"跟单没跟上"，不是"本金填错了"
        await LiveCapitalLimit.EnsureWithinAvailableAsync(brokers, config, capital, cancellationToken)
            .ConfigureAwait(false);

        return config.Id;
    }

    private static CopyMode ParseMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        null or "" or "paper" => CopyMode.Paper,
        "live" => CopyMode.Live,
        _ => throw new ArgumentException($"未知的跟单模式：{mode}。可用值：paper / live。", nameof(mode)),
    };
}
