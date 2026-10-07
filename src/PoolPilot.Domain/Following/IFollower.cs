using PoolPilot.Domain.Entities;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Following;

/// <summary>
/// 跟单员：带单员信号的订阅者。
///
/// 契约只有两件事——**跟信号**与**对齐仓位**。分成两个入口是因为语义不同：
/// 信号是"池子刚动了一下"，对齐是"我现在应该和池子一样"；后者必须按**当前市价**建仓，
/// 而不是按池子当初的成交价假装成交（真实跟单服务跟的是领单人的现有仓位，不是他的开仓价）。
///
/// 实现方由 Infrastructure 提供：它需要仓储、盘口与合约规格，而 Domain 不该知道这些。
/// </summary>
public interface IFollower
{
    long Id { get; }

    /// <summary>订阅的带单员（池账户索引）。信号只送给订阅了它的跟单员。</summary>
    AccountIndex LeadAccountIndex { get; }

    bool IsActive { get; }

    /// <summary>当前配置。注册表拿它比较"这条配置变了没有"，据此决定要不要立刻重新对齐。</summary>
    Follower Config { get; }

    /// <summary>
    /// 配置变了（改本金、启停、换模式）。**实例按 Id 复用**，所以配置是就地更新的；
    /// 实现方要自己处理"本金变了意味着什么"（纸面会清空重放，实盘无所谓）。
    /// </summary>
    void Reconfigure(Follower updated);

    /// <summary>
    /// 收到信号。这是事件驱动的入口——池子一变就调用，不依赖轮询。
    ///
    /// <paramref name="state"/> 是带单员此刻的完整状态（池子全部仓位与账户权益），
    /// 因为一次仓位变化往往还伴随着权益变化，而规模比例依赖权益。
    /// </summary>
    Task OnSignalAsync(LeadSignal signal, PoolSnapshot state, CancellationToken cancellationToken = default);

    /// <summary>
    /// 体检：算出"按比例我该持多少、实际持多少"，**只记录、不下单**。
    ///
    /// 心跳走这个入口。为什么与 <see cref="CatchUpAsync"/> 分开：
    /// 对齐会下单（挂载时要当场建仓），而心跳每 15 秒一次、若也下单，就变成了
    /// "池子没动系统也在动"——手动平掉的仓位会被它一次次买回来。
    /// 各实现按自己的语义处理：实盘只报偏差（跟动作），纸面照旧对账（它是模拟）。
    /// </summary>
    Task CheckDriftAsync(PoolSnapshot state, CancellationToken cancellationToken = default);

    /// <summary>
    /// 对齐仓位。在订阅瞬间、断线恢复、服务重启后调用。
    ///
    /// 按池子的**当前**状态建仓，不重放历史成交——"我来晚了"时这是唯一正确的做法，
    /// 也是与真实跟单一致的做法。差额为 0 时不产生任何记录。
    /// </summary>
    Task CatchUpAsync(PoolSnapshot state, CancellationToken cancellationToken = default);
}
