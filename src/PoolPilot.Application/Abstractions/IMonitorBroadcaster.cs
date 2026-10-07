using PoolPilot.Application.Dtos;

namespace PoolPilot.Application.Abstractions;

/// <summary>
/// 把监控到的变化推给前端工作台。
///
/// 定义在 Application 层，实现在 Api 层（SignalR）。用抽象隔开是因为：
/// 监控服务属于基础设施，而"怎么把消息送到浏览器"属于表现层，两者不该相互依赖。
/// </summary>
public interface IMonitorBroadcaster
{
    /// <summary>
    /// 最近一次状态。
    ///
    /// 状态是**推送**的，而推送只发生在状态**变化**的时刻。浏览器如果在所有变化之后
    /// 才打开页面，就永远收不到当前状态——而"收不到状态"恰好和"监控断了"长得一模一样，
    /// 正是状态通道要消除的那种歧义。所以除了广播变化，还要能回答"此刻的状态是什么"。
    /// </summary>
    MonitorStatusDto? CurrentStatus { get; }

    /// <summary>推送一条持仓变化事件。</summary>
    Task BroadcastEventAsync(PoolEventDto poolEvent, CancellationToken cancellationToken = default);

    /// <summary>推送一条跟单对照记录（含实时漏损）。</summary>
    Task BroadcastCopyFillAsync(CopyFillDto fill, CancellationToken cancellationToken = default);

    /// <summary>推送监控状态变化（连接、重连、数据陈旧等），让界面能区分"没信号"与"没动作"。</summary>
    Task BroadcastStatusAsync(MonitorStatusDto status, CancellationToken cancellationToken = default);

    /// <summary>
    /// 推送实盘账户的**实时快照**（由交易所的用户数据流触发：仓位、余额一变就推）。
    ///
    /// 与轮询的区别是"谁知道变化"：轮询里变化只在我们去问的时候才被发现，
    /// 这条通道里交易所主动说"变了"，我们据此重读一次再推给界面。
    /// </summary>
    /// <param name="followerId">这几个数字属于哪个跟单员——同一个账号可能挂着多个。</param>
    Task BroadcastLiveAccountAsync(
        long followerId,
        Dtos.LiveAccountDto account,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 推送**实时的未实现盈亏**（由每秒的标记价触发）。
    ///
    /// 与 <see cref="BroadcastLiveAccountAsync"/> 的分工：那个是交易所给的账户快照
    /// （仓位、余额——只在账户真的变了时才有），这个是**每秒都在动**的那部分：
    /// 未实现盈亏是标记价的函数，而交易所不推价格，所以我们拿它的标记价、用它的公式当场算。
    /// </summary>
    /// <param name="byPair">按**系统标准交易对**（<c>ETHFI/USDT</c>）给，界面上的持仓行就是按它渲染的。</param>
    /// <param name="marksByPair">同一时刻的标记价（同样按系统标准交易对给）。
    /// 盈亏是标记价的函数，把自变量一并推出去，界面才能既显示盈亏又显示价格——
    /// 否则"盈亏在动但看不到价"会让人怀疑数从哪来。</param>
    Task BroadcastLivePnlAsync(
        long followerId,
        IReadOnlyDictionary<string, decimal> byPair,
        decimal total,
        IReadOnlyDictionary<string, decimal> marksByPair,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 推送"这条实时通道健不健康"。
    ///
    /// 为什么必须推：用户数据流在**账户没动作时本来就不发帧**，所以"一直没更新"与
    /// "链路根本不通"在界面上一模一样。实测存在后者（握手通过、数据一条不来），
    /// 界面上的「实时」如果在这种链路上还亮着，就是在说假话。
    /// </summary>
    Task BroadcastAccountStreamHealthAsync(
        long followerId,
        bool healthy,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 监控状态。
///
/// 存在的理由：本项目最容易被误读的一点是"收不到推送"到底意味着什么。
/// 实测池的活动率只有市场成交的约 0.24%，所以**长时间无事件是正常现象**，
/// 而连接断开才是故障。把这两者显式区分开，界面上才不会误导使用者。
/// </summary>
public sealed record MonitorStatusDto
{
    public required string State { get; init; }

    public required IReadOnlyList<long> MonitoredAccounts { get; init; }

    public required IReadOnlyList<string> SubscribedChannels { get; init; }

    /// <summary>
    /// 最近一次**收到上游消息**的时间，不是"状态生成时间"。
    ///
    /// 这个区分很关键：界面用它算"静默时长"，如果填的是状态生成时间，
    /// 那么每广播一次状态就会把静默时长清零，计时器永远显示 0——恰好掩盖了它要暴露的问题。
    /// 未连接或还没收到过任何上游消息时为 null。
    /// </summary>
    public required DateTimeOffset? LastMessageAt { get; init; }

    public required string Detail { get; init; }

    /// <summary>当前是否处于已连接状态。</summary>
    public required bool IsConnected { get; init; }
}
