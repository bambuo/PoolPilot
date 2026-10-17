using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Api.Tests.Fakes;

/// <summary>
/// 公开页那组用例的手写替身：上游池数据、盯市价、运行期同步。
///
/// 为什么必须换掉这三样（而登录/菜单那几组测试不用）：
/// <list type="bullet">
///   <item>**上游**：公开页的读路径要按带单员拉池子快照，写路径要校验池子存在。
///     集成测试里真打 Lighter 等于把测试结果绑到网络上，而这套工装要测的是接线。</item>
///   <item>**盯市价</c>：真实现会为取不到的市场去连盘口，慢且不确定。</item>
///   <item>**运行期同步</c>：真实现（<c>LeadTraderRegistry</c>）会为新建的带单员订频道、
///     取 REST 基线。W2/W3 之后都会喊这一声，不换就会在每个写用例里连一次交易所。</item>
/// </list>
/// 其余成员一律抛 <see cref="NotSupportedException"/>：它们在测试里就是
/// "这条路不该被走到"的断言——被走到了会直接红，而不是静默地多打一次上游。
/// </summary>
internal sealed class PublicPageUpstream : ILighterPoolsClient
{
    /// <summary>假上游里存在的池。索引与名字都由测试自己种，断言才不用猜。</summary>
    internal static readonly AccountIndex KnownPool = AccountIndex.From(281474976552443L);

    internal static readonly AccountIndex MissingPool = AccountIndex.From(999_999L);

    /// <summary>一个已冻结的池：候选框必须把它排除，即使它的名字/索引都能搜到。</summary>
    internal static readonly AccountIndex FrozenPool = AccountIndex.From(281474976552444L);

    internal static readonly decimal PoolEquity = 1_000m;

    public int PoolListReads { get; private set; }

    public int SnapshotReads { get; private set; }

    public Task<IReadOnlyList<Pool>> FetchPoolsAsync(
        PoolFilter filter,
        CancellationToken cancellationToken = default)
    {
        PoolListReads++;

        return Task.FromResult<IReadOnlyList<Pool>>(
        [
            KnownPoolOf(KnownPool.Value, "ENA 池"),
            KnownPoolOf(FrozenPool.Value, "已冻结的池", (int)PoolLifecycle.Frozen),
        ]);
    }

    public Task<PoolSnapshot> FetchSnapshotAsync(
        AccountIndex account,
        CancellationToken cancellationToken = default)
    {
        SnapshotReads++;

        return Task.FromResult(new PoolSnapshot
        {
            AccountIndex = account,
            TakenAt = LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            TotalAssetValue = UsdAmount.From(PoolEquity),
            Collateral = UsdAmount.From(PoolEquity),
            AvailableBalance = UsdAmount.From(PoolEquity / 2m),
            InitialMarginRequirement = UsdAmount.From(PoolEquity / 4m),
            MaintenanceMarginRequirement = UsdAmount.From(PoolEquity / 8m),
            Positions = [],
            Orders = [],
        });
    }

    public Task<IReadOnlyList<InstrumentSpec>> FetchInstrumentsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InstrumentSpec>>([]);

    public Task<LighterSystemConfig> FetchSystemConfigAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("公开页不读系统配置");

    public Task<PoolInfo> FetchPoolInfoAsync(AccountIndex account, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PoolInfo
        {
            AccountIndex = account,
            TotalShares = 1_000L,
            OperatorShares = 100L,
            MinOperatorShareRate = PercentValue.FromPercent(5m),
            OperatorFee = PercentValue.FromPercent(10m),
            ReportedAnnualPercentageYield = PercentValue.FromPercent(12m),
            ReportedSharpe = 1.5m,
            Nav = new NavSeries
            {
                AccountIndex = account,
                Points =
                [
                    new NavPoint { Timestamp = LighterTimestamp.FromSeconds(1_800_000_000), SharePrice = 1m },
                    new NavPoint { Timestamp = LighterTimestamp.FromSeconds(1_800_086_400), SharePrice = 1.05m },
                    new NavPoint { Timestamp = LighterTimestamp.FromSeconds(1_800_172_800), SharePrice = 1.1m },
                ],
            },
        });

    public Task<IReadOnlyList<PoolTrade>> FetchRecentTradesAsync(
        AccountIndex account,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("公开页不读池成交");

    public Task<IReadOnlyList<PoolOrder>> FetchOrdersAsync(
        AccountIndex account,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("公开页不读挂单");

    public Task<OrderBookSnapshot> FetchOrderBookAsync(
        int marketId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("公开页不按盘口定价成交");

    internal static Pool KnownPoolOf(long accountIndex, string name, int stateCode = 0) => new()
    {
        AccountIndex = AccountIndex.From(accountIndex),
        Name = name,
        L1Address = "0x0000000000000000000000000000000000000000",
        AccountTypeCode = 1,
        TotalAssetValue = UsdAmount.From(PoolEquity),
        ReportedAnnualPercentageYield = PercentValue.FromPercent(12m),
        ReportedSharpe = 1.5m,
        OperatorFee = PercentValue.FromPercent(10m),
        TotalShares = 1_000L,
        // 秒刻度要落在 DateTimeOffset 可表示范围内：账户索引是 15 位的数，直接加到秒上
        // 会撞上 LighterTimestamp 的刻度守卫（那正是守卫该拦的形状）。取模只用来让不同池的
        // 时刻彼此可区分——候选框的排序断言不依赖这里
        CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000 + accountIndex % 1_000L),
        StateCode = stateCode,
    };
}

/// <summary>一个盯市价都不给：没有持仓的跟单员本来就不该问市场。</summary>
internal sealed class PublicPageMarks : IMarkPrices
{
    public Task<MarkPrices> ResolveAsync(
        IReadOnlyList<int> marketIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marketIds);

        return Task.FromResult(new MarkPrices(new Dictionary<int, PriceAmount>(), [.. marketIds]));
    }
}

/// <summary>记下"喊过几次运行期同步"，但不去连任何交易所。</summary>
internal sealed class PublicPageSync : ILeadTraderSync
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Requests { get; private set; }

    /// <summary>为真时把同步挂起，用来证明公开写请求没有 await 它。测试结束前必须 <see cref="Release"/>。</summary>
    public bool Block { get; set; }

    public Task RequestSyncAsync(CancellationToken cancellationToken = default)
    {
        Requests++;

        return Block ? _release.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
    }

    public void Release() => _release.TrySetResult();
}
