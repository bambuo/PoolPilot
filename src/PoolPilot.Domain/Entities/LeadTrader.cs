using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 带单员：某个公共资金池的观察者。
///
/// 一个池最多一个带单员，身份就是池索引。它只做一件事——盯着池子，
/// 把池子的仓位变化翻译成信号发出去，并把信号落库。
///
/// **它的存在与否和有没有人跟单无关**：实例在，观察就在，信号照发照存。
/// 这正是把"观察"与"跟单"拆成两个对象的目的——否则取消跟单会顺手把观察也关掉，
/// 池子后来的动作就再也补不回来（历史只能靠事后重取，而当时的盘口已经不在了）。
/// </summary>
public sealed record LeadTrader
{
    public required AccountIndex AccountIndex { get; init; }

    /// <summary>池名快照。池子改名或下线后，历史信号仍然说得出当时跟的是谁。</summary>
    public required string Name { get; init; }

    /// <summary>
    /// 是否在观察。暂停后服务不再处理该池的推送、不再产生新信号；
    /// 已有信号与跟单员的仓位都不动——平仓是交易决策，必须由人做，不能因为一次暂停就替他平掉。
    /// </summary>
    public required bool IsEnabled { get; init; }

    public required LighterTimestamp CreatedAt { get; init; }
}
