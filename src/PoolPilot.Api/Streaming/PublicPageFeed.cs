using System.Text.Json;
using System.Threading.Channels;

using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Api.Streaming;

/// <summary>列表连接的整帧数据（全量，不做增量）。</summary>
public sealed record PublicPageFeedSnapshot(
    IReadOnlyList<PublicLeadTraderDto> Leads,
    IReadOnlyList<PublicFollowerSummaryDto> Followers,
    DateTimeOffset TakenAt);

/// <summary>已经序列化好的一帧：同一个事件里的多个订阅者共用同一份文本。</summary>
public sealed record PublicStreamFrame(string Event, string Data);

/// <summary>SSE 的事件名。它们出现在前端契约里，改名就是契约改动。</summary>
public static class PublicStreamEvents
{
    public const string Snapshot = "snapshot";

    public const string Detail = "detail";

    /// <summary>带单员详情连接（<c>?lead=&lt;accountIndex&gt;</c>），数据为公开池详情。</summary>
    public const string Lead = "lead";

    /// <summary>
    /// 详情连接订阅的那一行已经不存在（删除，或不再是公开纸面行）。
    /// 数据里只有 <c>{ id }</c>；客户端收到后应关闭这条流。
    /// </summary>
    public const string Gone = "gone";
}

/// <summary>
/// 公开页的实时推送源（单例）——**事件驱动**，没有数据 ticker。
///
/// 数据只在事件到达时重装配并广播：
/// <list type="bullet">
///   <item>池的持仓/权益变化：<see cref="ILeadActivity"/> → 刷新列表快照与该池的详情。</item>
///   <item>纸面落账（流水/权益点）：<see cref="IFollowerLedgerActivity"/> → 刷新该行与列表。</item>
///   <item>实时行情（Lighter <c>market_stats</c>）：<see cref="IMarketPriceActivity"/> →
///     直接按新中间价修补在线详情的标记价/未实现盈亏，不重读库、不重打上游。</item>
///   <item>公开写成功：<see cref="PublishNowAsync"/> → 立即推一帧。</item>
/// </list>
///
/// 首帧仍是"建连即发"：列表快照复用当前 live 状态（没有 TTL 的过期语义，
/// 事件会刷新它）；详情每次建连都重新装配，不能拿旧账当实时首帧。
///
/// 连接额度也在这里：<see cref="TryAcquireConnection"/> 通过后才允许写 SSE 头。
/// 超额时由控制器在推流前回 503——不挂 <c>public-read</c> 限流正是为了避免
/// 长连接被"每分钟 60 次"的窗口误杀，额度只能由这个计数兜住。
/// </summary>
public sealed class PublicPageFeed(
    IPublicPageFeedAssembler assembler,
    PublicStreamSettings settings,
    IMarketStatsSubscription marketStats,
    ILogger<PublicPageFeed> logger)
    : IDisposable, IFollowerLedgerActivity, ILeadActivity, IMarketPriceActivity
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>装配单飞：列表与所有详情共用一道闸，同一时刻不会有两份装配同时读库。</summary>
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    /// <summary>订阅者表、在线详情与连接计数的锁。与 <see cref="refreshGate"/> 分开：
    /// 广播是纯内存操作，不该排在一次上游装配后面。</summary>
    private readonly object sync = new();

    /// <summary>live 快照字段的锁。装配仍在 <see cref="refreshGate"/> 里串行，这里只保护读写。</summary>
    private readonly object snapshotLock = new();

    private readonly List<Channel<PublicStreamFrame>> listSubscribers = [];

    private readonly Dictionary<long, List<Channel<PublicStreamFrame>>> detailSubscribers = [];

    /// <summary>在线详情的最近一帧。行情事件只改它的标记价相关字段，不重装配。</summary>
    private readonly Dictionary<long, PublicFollowerDto> detailViews = [];

    private readonly Dictionary<long, List<Channel<PublicStreamFrame>>> leadSubscribers = [];

    private readonly Dictionary<long, PublicLeadTraderDetailDto> leadViews = [];

    private PublicPageFeedSnapshot? snapshot;

    private int connections;

    /// <summary>占用一个并发连接名额。返回 false 时调用方必须回 503，且不能写 SSE 头。</summary>
    public bool TryAcquireConnection()
    {
        lock (sync)
        {
            if (connections >= settings.MaxConnections)
            {
                return false;
            }

            connections++;
            return true;
        }
    }

    /// <summary>归还名额。控制器在 finally 里调，客户端怎么断开都要走到。</summary>
    public void ReleaseConnection()
    {
        lock (sync)
        {
            if (connections > 0)
            {
                connections--;
            }
        }
    }

    /// <summary>订阅列表帧。返回的对象 Dispose 即退订。</summary>
    public PublicPageFeedSubscription Subscribe() => AddSubscriber(followerId: null);

    /// <summary>订阅某一行的详情帧。返回的对象 Dispose 即退订。</summary>
    public PublicPageFeedSubscription SubscribeDetail(long followerId) => AddSubscriber(followerId);

    /// <summary>订阅某个带单员的池详情帧。返回的对象 Dispose 即退订。</summary>
    public PublicPageFeedSubscription SubscribeLead(long accountIndex)
    {
        Channel<PublicStreamFrame> channel = NewChannel();

        lock (sync)
        {
            if (!leadSubscribers.TryGetValue(accountIndex, out List<Channel<PublicStreamFrame>>? channels))
            {
                leadSubscribers[accountIndex] = channels = [];
            }

            channels.Add(channel);
        }

        return new PublicPageFeedSubscription(
            channel.Reader,
            () => RemoveLeadSubscriber(accountIndex, channel));
    }

    /// <summary>记下一条刚建好的带单员详情连接的首帧；之后池事件会重装配它。</summary>
    public void SeedLeadDetail(PublicLeadTraderDetailDto detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        lock (sync)
        {
            leadViews[detail.AccountIndex] = detail;
        }
    }

    /// <summary>
    /// 取一帧带单员详情（首帧 + 不存在判定）。每次建连重新装配，不用旧账当首帧。
    /// </summary>
    public async Task<PublicLeadTraderDetailDto?> GetLeadDetailAsync(
        long accountIndex,
        CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await assembler.BuildLeadDetailAsync(accountIndex, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>
    /// 记下一条刚建好的详情连接的首帧，并确保它持仓里的市场已订上实时行情。
    /// 之后 <see cref="NotifyMarketPriceChanged"/> 会直接在它上面打补丁。
    /// </summary>
    public void SeedDetail(PublicFollowerDto detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        lock (sync)
        {
            detailViews[detail.Id] = detail;
        }

        EnsureMarkets(detail);
    }

    /// <summary>
    /// 取一帧当前快照（建连首帧用）。有 live 快照就直接给；没有才装配。
    /// 这里的异常**不吞**：首帧装不出来就该让请求失败，而不是先写 200 再挂一条空流。
    /// </summary>
    public async Task<PublicPageFeedSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            lock (snapshotLock)
            {
                if (snapshot is not null)
                {
                    return snapshot;
                }
            }

            PublicPageFeedSnapshot built = await assembler.BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);

            lock (snapshotLock)
            {
                snapshot = built;
            }

            EnsureSnapshotMarkets(built);

            return built;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>
    /// 取一帧详情（详情连接的首帧 + 404 判定用）。
    ///
    /// **不缓存**：详情是用户刚打开的那一行，首帧必须是此刻的账。
    /// 不存在 / 非公开 / 非纸面一律 <c>null</c>——判据在 <c>GetPublicFollowerUseCase</c> 里，这里不重写。
    /// </summary>
    public async Task<PublicFollowerDto?> GetDetailAsync(
        long followerId,
        CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await assembler.BuildDetailAsync(followerId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>
    /// 公开写成功后的即时帧。没有订阅者时什么都不做。
    ///
    /// 异常在这里记日志并吞掉：调用方是控制器里的 fire-and-forget，
    /// 一次推送失败不该变成未观察的任务异常；下一次事件会补上。
    /// </summary>
    public async Task PublishNowAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            InvalidateSnapshot();

            if (HasListSubscribers())
            {
                await PublishSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (long followerId in ActiveDetailIds())
            {
                await PublishDetailAsync(followerId, cancellationToken).ConfigureAwait(false);
            }

            foreach (long accountIndex in ActiveLeadIds())
            {
                await PublishLeadDetailAsync(accountIndex, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "公开页写后即时帧推送失败；下一次事件会再试。");
        }
    }

    /// <summary>
    /// 纸面跟单员落账后的即时推送（<see cref="IFollowerLedgerActivity"/>）。
    ///
    /// 复制管线在 fill/权益点写成功之后调用；这里只把工作丢出去，绝不阻塞对账。
    /// </summary>
    public void NotifyFollowerChanged(long followerId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await PublishFollowerChangedAsync(followerId).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "公开页落账后的即时帧推送失败；下一次事件会再试。");
            }
        });
    }

    /// <summary>
    /// 某一行落账后的定向刷新：列表订阅者要看到新的成交/权益，详情订阅者要看到新的账。
    /// public 是为了让测试直接等它完成。
    /// </summary>
    public async Task PublishFollowerChangedAsync(
        long followerId,
        CancellationToken cancellationToken = default)
    {
        InvalidateSnapshot();

        if (HasListSubscribers())
        {
            await PublishSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        if (HasDetailSubscribers(followerId))
        {
            await PublishDetailAsync(followerId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 带单员的池快照变化（<see cref="ILeadActivity"/>）：刷新列表与该池的在线详情。
    /// </summary>
    public void NotifyLeadChanged(long accountIndex)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                InvalidateSnapshot();

                if (HasListSubscribers())
                {
                    await PublishSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                }

                foreach (long followerId in DetailIdsForLead(accountIndex))
                {
                    await PublishDetailAsync(followerId, CancellationToken.None).ConfigureAwait(false);
                }

                if (HasLeadSubscribers(accountIndex))
                {
                    await PublishLeadDetailAsync(accountIndex, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "公开页池事件后的即时帧推送失败；下一次事件会再试。");
            }
        });
    }

    /// <summary>
    /// 实时行情变化（<see cref="IMarketPriceActivity"/>）：只修补在线详情里该市场的
    /// 标记价、名义价值、未实现盈亏与账户权益，然后推帧。不读库、不重打上游。
    /// </summary>
    public void NotifyMarketPriceChanged(int marketId, PriceAmount mid)
    {
        List<(long FollowerId, PublicStreamFrame Frame)> detailFrames = [];

        lock (sync)
        {
            foreach ((long followerId, PublicFollowerDto view) in detailViews)
            {
                if (!detailSubscribers.TryGetValue(followerId, out List<Channel<PublicStreamFrame>>? channels)
                    || channels.Count == 0)
                {
                    continue;
                }

                if (!view.Positions.Any(p => p.MarketId == marketId))
                {
                    continue;
                }

                PublicFollowerDto updated = ApplyMark(view, marketId, mid.Value);
                detailViews[followerId] = updated;
                detailFrames.Add((followerId, Serialize(PublicStreamEvents.Detail, updated)));
            }
        }

        PublicStreamFrame? listFrame = null;

        lock (snapshotLock)
        {
            if (snapshot is not null)
            {
                bool changed = false;
                List<PublicFollowerSummaryDto> followers = new(snapshot.Followers.Count);

                foreach (PublicFollowerSummaryDto follower in snapshot.Followers)
                {
                    PublicFollowerSummaryDto updated = ApplySummaryMark(follower, marketId, mid.Value);

                    if (!ReferenceEquals(updated, follower))
                    {
                        changed = true;
                    }

                    followers.Add(updated);
                }

                if (changed)
                {
                    snapshot = snapshot with
                    {
                        Followers = followers,
                        TakenAt = DateTimeOffset.UtcNow,
                    };

                    listFrame = Serialize(PublicStreamEvents.Snapshot, snapshot);
                }
            }
        }

        foreach ((long followerId, PublicStreamFrame frame) in detailFrames)
        {
            BroadcastDetail(followerId, frame);
        }

        if (listFrame is not null && HasListSubscribers())
        {
            BroadcastList(listFrame);
        }
    }

    public void Dispose() => refreshGate.Dispose();

    private PublicPageFeedSubscription AddSubscriber(long? followerId)
    {
        Channel<PublicStreamFrame> channel = NewChannel();

        lock (sync)
        {
            if (followerId is long id)
            {
                if (!detailSubscribers.TryGetValue(id, out List<Channel<PublicStreamFrame>>? channels))
                {
                    detailSubscribers[id] = channels = [];
                }

                channels.Add(channel);
            }
            else
            {
                listSubscribers.Add(channel);
            }
        }

        return new PublicPageFeedSubscription(
            channel.Reader,
            () => RemoveSubscriber(followerId, channel));
    }

    private static Channel<PublicStreamFrame> NewChannel() =>
        Channel.CreateBounded<PublicStreamFrame>(
            new BoundedChannelOptions(1)
            {
                // 慢客户端只保留最新一帧：全量帧没有增量语义，旧帧被覆盖不丢信息
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

    private void RemoveSubscriber(long? followerId, Channel<PublicStreamFrame> channel)
    {
        lock (sync)
        {
            if (followerId is long id)
            {
                if (detailSubscribers.TryGetValue(id, out List<Channel<PublicStreamFrame>>? channels))
                {
                    channels.Remove(channel);

                    if (channels.Count == 0)
                    {
                        detailSubscribers.Remove(id);
                        detailViews.Remove(id);
                    }
                }
            }
            else
            {
                listSubscribers.Remove(channel);
            }
        }

        channel.Writer.TryComplete();
    }

    private void RemoveLeadSubscriber(long accountIndex, Channel<PublicStreamFrame> channel)
    {
        lock (sync)
        {
            if (leadSubscribers.TryGetValue(accountIndex, out List<Channel<PublicStreamFrame>>? channels))
            {
                channels.Remove(channel);

                if (channels.Count == 0)
                {
                    leadSubscribers.Remove(accountIndex);
                    leadViews.Remove(accountIndex);
                }
            }
        }

        channel.Writer.TryComplete();
    }

    private async Task PublishSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                PublicPageFeedSnapshot current = await assembler
                    .BuildSnapshotAsync(cancellationToken)
                    .ConfigureAwait(false);

                lock (snapshotLock)
                {
                    snapshot = current;
                }

                EnsureSnapshotMarkets(current);

                if (HasListSubscribers())
                {
                    BroadcastList(Serialize(PublicStreamEvents.Snapshot, current));
                }
            }
            finally
            {
                refreshGate.Release();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 保留上一帧；连接不因一次装配失败而断，下一次事件会再试
            logger.LogWarning(exception, "公开页快照装配失败；保留上一帧。");
        }
    }

    private async Task PublishDetailAsync(long followerId, CancellationToken cancellationToken)
    {
        try
        {
            await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // 订阅者可能在等待装配期间走光；没有连接就不必花一次装配
                if (!HasDetailSubscribers(followerId))
                {
                    return;
                }

                PublicFollowerDto? detail = await assembler
                    .BuildDetailAsync(followerId, cancellationToken)
                    .ConfigureAwait(false);

                lock (sync)
                {
                    if (detail is null)
                    {
                        detailViews.Remove(followerId);
                    }
                    else
                    {
                        detailViews[followerId] = detail;
                    }
                }

                // 行被删掉/转私有之后，不能只停发新帧——客户端会以为"还活着"并继续显示旧账。
                // 发一条终止帧，客户端据此关掉连接并显示"已不存在"。
                if (detail is null)
                {
                    BroadcastDetail(followerId, Serialize(PublicStreamEvents.Gone, new { id = followerId }));
                    return;
                }

                EnsureMarkets(detail);
                BroadcastDetail(followerId, Serialize(PublicStreamEvents.Detail, detail));
            }
            finally
            {
                refreshGate.Release();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "公开页详情（id={FollowerId}）装配失败；保留上一帧。", followerId);
        }
    }

    private async Task PublishLeadDetailAsync(long accountIndex, CancellationToken cancellationToken)
    {
        try
        {
            await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (!HasLeadSubscribers(accountIndex))
                {
                    return;
                }

                PublicLeadTraderDetailDto? detail = await assembler
                    .BuildLeadDetailAsync(accountIndex, cancellationToken)
                    .ConfigureAwait(false);

                lock (sync)
                {
                    if (detail is null)
                    {
                        leadViews.Remove(accountIndex);
                    }
                    else
                    {
                        leadViews[accountIndex] = detail;
                    }
                }

                if (detail is null)
                {
                    BroadcastLead(accountIndex, Serialize(PublicStreamEvents.Gone, new { id = accountIndex }));
                    return;
                }

                BroadcastLead(accountIndex, Serialize(PublicStreamEvents.Lead, detail));
            }
            finally
            {
                refreshGate.Release();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "公开页带单员详情（账户 {AccountIndex}）装配失败；保留上一帧。", accountIndex);
        }
    }

    /// <summary>
    /// 按最新中间价修补一帧详情。只动随价格变化的字段；流水、权益曲线、本金等保持上一帧。
    /// 公式与 <c>PaperLedger.Value</c> 同源：
    /// <c>权益 = 本金 + 已实现 + 未实现 - 累计手续费</c>。
    /// </summary>
    private static PublicFollowerDto ApplyMark(PublicFollowerDto detail, int marketId, decimal markPrice)
    {
        if (!detail.IsValuationAvailable)
        {
            return detail;
        }

        List<PublicPositionDto> positions = new(detail.Positions.Count);
        decimal unrealized = 0m;
        decimal grossNotional = 0m;

        foreach (PublicPositionDto source in detail.Positions)
        {
            PublicPositionDto position = source;

            if (position.MarketId == marketId)
            {
                decimal notional = Math.Abs(position.Size) * markPrice;
                decimal unrealizedPnl = position.Size * (markPrice - position.EntryVwap);
                decimal? margin = position.InitialMarginPercent is > 0m && notional > 0m
                    ? notional * position.InitialMarginPercent.Value / 100m
                    : null;

                position = position with
                {
                    MarkPrice = markPrice,
                    HasMark = true,
                    Notional = notional,
                    UnrealizedPnl = unrealizedPnl,
                    MarginAmount = margin,
                    UnrealizedPnlPercent = margin is > 0m
                        ? unrealizedPnl / margin.Value * 100m
                        : null,
                };
            }

            unrealized += position.UnrealizedPnl;
            grossNotional += position.Notional;
            positions.Add(position);
        }

        decimal equity = detail.InitialCapital + detail.RealizedPnl + unrealized - detail.Fees;
        decimal returnPercent = detail.InitialCapital <= 0m
            ? 0m
            : (equity - detail.InitialCapital) / detail.InitialCapital * 100m;

        ApplyLiquidationPrices(positions, equity);

        return detail with
        {
            Positions = positions,
            UnrealizedPnl = unrealized,
            GrossNotional = grossNotional,
            Equity = equity,
            ReturnPercent = returnPercent,
            MarksUnavailable = [.. detail.MarksUnavailable.Where(id => id != marketId)],
        };
    }

    /// <summary>
    /// 行情补丁也要重算清算价：它随现价与权益一起动。任一在仓市场缺价或缺维持保证金率时
    /// 整批不更新——少算一笔占用会把清算价推远。
    /// </summary>
    private static void ApplyLiquidationPrices(List<PublicPositionDto> positions, decimal equity)
    {
        if (positions.Any(p => p.Size != 0m && (!p.HasMark || p.MaintenanceMarginPercent <= 0m)))
        {
            return;
        }

        List<PaperMarginPosition> marginPositions =
        [
            .. positions
                .Where(p => p.Size != 0m)
                .Select(p => new PaperMarginPosition
                {
                    MarketId = p.MarketId,
                    SignedSize = p.Size,
                    EntryPrice = p.EntryVwap,
                    MarkPrice = p.MarkPrice,
                    Notional = p.Notional,
                    UnrealizedPnl = p.UnrealizedPnl,
                    MaintenanceMarginPercent = p.MaintenanceMarginPercent,
                }),
        ];

        if (marginPositions.Count == 0)
        {
            return;
        }

        for (int i = 0; i < positions.Count; i++)
        {
            PublicPositionDto position = positions[i];

            if (position.Size == 0m)
            {
                continue;
            }

            PaperMarginPosition target = marginPositions.First(p => p.MarketId == position.MarketId);
            decimal? liquidation = PaperMarginAnalyzer.LiquidationPrice(target, marginPositions, equity);

            positions[i] = position with
            {
                LiquidationPrice = liquidation,
                LiquidationDistancePercent = liquidation is null || position.MarkPrice <= 0m
                    ? null
                    : (liquidation.Value - position.MarkPrice) / position.MarkPrice * 100m,
            };
        }
    }

    /// <summary>
    /// 列表卡片的摘要同一条补丁公式：只改该市场的名义价值/未实现盈亏，
    /// 再按 <c>权益 = 本金 + 已实现 + 未实现 - 手续费</c> 重算账户级数字。
    /// </summary>
    private static PublicFollowerSummaryDto ApplySummaryMark(
        PublicFollowerSummaryDto summary,
        int marketId,
        decimal markPrice)
    {
        if (!summary.IsValuationAvailable
            || !summary.Positions.Any(p => p.MarketId == marketId))
        {
            return summary;
        }

        List<PublicPositionBriefDto> positions = new(summary.Positions.Count);
        decimal unrealized = 0m;
        decimal grossNotional = 0m;

        foreach (PublicPositionBriefDto source in summary.Positions)
        {
            PublicPositionBriefDto position = source;

            if (position.MarketId == marketId)
            {
                position = position with
                {
                    Notional = Math.Abs(position.Size) * markPrice,
                    UnrealizedPnl = position.Size * (markPrice - position.EntryVwap),
                };
            }

            unrealized += position.UnrealizedPnl;
            grossNotional += position.Notional;
            positions.Add(position);
        }

        decimal equity = summary.InitialCapital + summary.RealizedPnl + unrealized - summary.Fees;
        decimal returnPercent = summary.InitialCapital <= 0m
            ? 0m
            : (equity - summary.InitialCapital) / summary.InitialCapital * 100m;

        return summary with
        {
            Positions = positions,
            UnrealizedPnl = unrealized,
            GrossNotional = grossNotional,
            Equity = equity,
            ReturnPercent = returnPercent,
        };
    }

    /// <summary>在线详情持有的市场要订上实时行情；按行去重与建连有关，不由 tick 驱动。</summary>
    private void EnsureMarkets(PublicFollowerDto detail)
    {
        foreach (PublicPositionDto position in detail.Positions)
        {
            if (position.Size != 0m)
            {
                marketStats.EnsureSubscribed(position.MarketId);
            }
        }
    }

    /// <summary>列表卡片看到的持仓也要订行情；否则卡片只能等池/落账事件，不会逐帧。</summary>
    private void EnsureSnapshotMarkets(PublicPageFeedSnapshot current)
    {
        foreach (PublicFollowerSummaryDto follower in current.Followers)
        {
            foreach (PublicPositionBriefDto position in follower.Positions)
            {
                if (position.Size != 0m)
                {
                    marketStats.EnsureSubscribed(position.MarketId);
                }
            }
        }
    }

    private void InvalidateSnapshot()
    {
        lock (snapshotLock)
        {
            snapshot = null;
        }
    }

    private bool HasListSubscribers()
    {
        lock (sync)
        {
            return listSubscribers.Count > 0;
        }
    }

    private bool HasDetailSubscribers(long followerId)
    {
        lock (sync)
        {
            return detailSubscribers.TryGetValue(followerId, out List<Channel<PublicStreamFrame>>? channels)
                && channels.Count > 0;
        }
    }

    private long[] ActiveDetailIds()
    {
        lock (sync)
        {
            return [.. detailSubscribers.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key)];
        }
    }

    private bool HasLeadSubscribers(long accountIndex)
    {
        lock (sync)
        {
            return leadSubscribers.TryGetValue(accountIndex, out List<Channel<PublicStreamFrame>>? channels)
                && channels.Count > 0;
        }
    }

    private long[] ActiveLeadIds()
    {
        lock (sync)
        {
            return [.. leadSubscribers.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key)];
        }
    }

    private long[] DetailIdsForLead(long accountIndex)
    {
        lock (sync)
        {
            return
            [
                .. detailViews
                    .Where(pair => pair.Value.LeadAccountIndex == accountIndex)
                    .Select(pair => pair.Key),
            ];
        }
    }

    private void BroadcastList(PublicStreamFrame frame)
    {
        Channel<PublicStreamFrame>[] targets;

        lock (sync)
        {
            targets = [.. listSubscribers];
        }

        foreach (Channel<PublicStreamFrame> channel in targets)
        {
            channel.Writer.TryWrite(frame);
        }
    }

    private void BroadcastDetail(long followerId, PublicStreamFrame frame)
    {
        Channel<PublicStreamFrame>[] targets;

        lock (sync)
        {
            targets = detailSubscribers.TryGetValue(followerId, out List<Channel<PublicStreamFrame>>? channels)
                ? [.. channels]
                : [];
        }

        foreach (Channel<PublicStreamFrame> channel in targets)
        {
            channel.Writer.TryWrite(frame);
        }
    }

    private void BroadcastLead(long accountIndex, PublicStreamFrame frame)
    {
        Channel<PublicStreamFrame>[] targets;

        lock (sync)
        {
            targets = leadSubscribers.TryGetValue(accountIndex, out List<Channel<PublicStreamFrame>>? channels)
                ? [.. channels]
                : [];
        }

        foreach (Channel<PublicStreamFrame> channel in targets)
        {
            channel.Writer.TryWrite(frame);
        }
    }

    private static PublicStreamFrame Serialize<T>(string eventName, T payload) =>
        new(eventName, JsonSerializer.Serialize(payload, JsonOptions));
}

/// <summary>
/// 一条连接的订阅句柄。<see cref="Reader"/> 里是最新待发帧；Dispose 时退订并完成通道。
/// </summary>
public sealed class PublicPageFeedSubscription : IDisposable
{
    private Action? unsubscribe;

    internal PublicPageFeedSubscription(ChannelReader<PublicStreamFrame> reader, Action unsubscribe)
    {
        Reader = reader;
        this.unsubscribe = unsubscribe;
    }

    public ChannelReader<PublicStreamFrame> Reader { get; }

    public void Dispose() => Interlocked.Exchange(ref unsubscribe, null)?.Invoke();
}
