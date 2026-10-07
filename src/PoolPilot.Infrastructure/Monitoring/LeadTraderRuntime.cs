using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Following;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Infrastructure.Monitoring;

/// <summary>
/// 带单员的运行实例：**一个池一个**。
///
/// 它只做两件事——维护基线快照、把每次快照变化翻译成信号；以及把信号送给订阅者。
/// 刻意不碰数据库、不碰 HTTP、不碰 WebSocket：那些是主机（<see cref="LeadTraderHostedService"/>）
/// 与注册表的事。因此这个类的行为可以被直接测出来，而"订阅者归零后观察是否继续"
/// 这类语义就落在它身上——答案是：**继续**，因为它的产出（信号与其落库）
/// 完全不经过订阅者这条路径。
///
/// 订阅者是从**外部**挂上来的，取消跟单只是把订阅者摘掉，
/// 不会改变这个实例是否在观察一个像素。这正是"观察"与"跟单"分离的落点。
/// </summary>
public sealed class LeadTraderRuntime(LeadTrader lead)
{
    private readonly List<IFollower> _followers = [];

    /// <summary>带单员的身份与配置。禁用只表示"不再产生新信号"，实例与基线都保留。</summary>
    public LeadTrader Lead { get; private set; } = lead;

    /// <summary>最近一次看到的完整池状态。第一次写入即"建立基线"。</summary>
    public PoolSnapshot? LastSnapshot { get; private set; }

    /// <summary>最近一次产出信号的时刻（仅内存，供状态展示）。</summary>
    public DateTimeOffset? LastSignalAt { get; private set; }

    public IReadOnlyList<IFollower> Followers => _followers;

    public AccountIndex AccountIndex => Lead.AccountIndex;

    public bool IsEnabled => Lead.IsEnabled;

    /// <summary>更新配置（启停、改名）。基线不动——暂停期间的池子照样在动，重开时不该假装它没动过。</summary>
    public void Reconfigure(LeadTrader updated) => Lead = updated;

    /// <summary>
    /// 记下一个快照并返回它相对上一次的变化。
    ///
    /// 第一次调用只建立基线并返回空：没有"上一次"，也就无从谈变化——
    /// 把首帧当成"从 0 开始的开仓"会凭空造出一串假信号。
    /// </summary>
    public IReadOnlyList<PoolEvent> ApplySnapshot(PoolSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        PoolSnapshot? previous = LastSnapshot;
        LastSnapshot = current;

        if (previous is null)
        {
            return [];
        }

        IReadOnlyList<PoolEvent> events = PositionDiffer.Diff(previous, current);

        if (events.Count > 0)
        {
            LastSignalAt = DateTimeOffset.UtcNow;
        }

        return events;
    }

    /// <summary>
    /// 重新建立基线：用于重连之后。**丢弃差分**是刻意的——断线期间的变化无从差分，
    /// 假装知道那段时间发生了什么才是错的。断线期间的真实变化由首个新快照
    /// 与跟单员的对账补上（跟单员是按当前状态对齐的，不依赖逐笔信号）。
    /// </summary>
    public void ResetBaseline(PoolSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        LastSnapshot = current;
    }

    public void Subscribe(IFollower follower)
    {
        ArgumentNullException.ThrowIfNull(follower);

        _followers.RemoveAll(f => f.Id == follower.Id);
        _followers.Add(follower);
    }

    public void Unsubscribe(long followerId) => _followers.RemoveAll(f => f.Id == followerId);

    /// <summary>
    /// 把一次信号送给订阅者。
    ///
    /// 订阅者自己的启用状态由它自己判定——带单员不知道也不该知道谁在暂停，
    /// 更不该因为"没人在跟"就少发一条信号。这个方向上的无知是刻意的：
    /// 一旦带单员开始关心订阅者，两条生命周期就又缠在一起了。
    /// </summary>
    public async Task NotifyAsync(LeadSignal signal, PoolSnapshot state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);

        foreach (IFollower follower in _followers.ToArray())
        {
            await follower.OnSignalAsync(signal, state, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 让所有订阅者按**当前状态**对齐一次仓位。用于：订阅瞬间、心跳兜底、断线期间的 REST 兜底。
    ///
    /// 与 <see cref="NotifyAsync"/> 的区别是它不携带具体信号：语义是"不管刚才发生了什么，
    /// 现在应该和池子一样"。池子不动时它什么都不做（差额为 0 既不取盘口也不写记录）。
    /// </summary>
    public async Task CatchUpSubscribersAsync(PoolSnapshot state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        foreach (IFollower follower in _followers.ToArray())
        {
            await follower.CatchUpAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }
}
