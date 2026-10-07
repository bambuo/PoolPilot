namespace PoolPilot.Domain.Enums;

/// <summary>
/// 一笔跟单信号没有被执行的原因。
///
/// 这些不是错误，而是**跟单的物理约束**在起作用。实测 ENA 的最小下单额是 10 USDT，
/// 而按比例复制时某池单笔敞口只占其权益的 1.2%——小资金会大量撞上
/// <see cref="BelowMinimumNotional"/>。把这三种情况分开记，界面才能告诉使用者
/// "是本金不够，还是那一下盘口真的没深度"，这两种结论的应对完全不同。
/// </summary>
public enum PaperSkipReason
{
    /// <summary>差额低于市场的最小下单数量。池子的变动太小，等比复制后不足一手。</summary>
    BelowMinimumSize,

    /// <summary>差额的名义金额低于市场的最小下单额。本金不足时的典型结果。</summary>
    BelowMinimumNotional,

    /// <summary>盘口没有任何可成交的档位。</summary>
    NoLiquidity,
}
