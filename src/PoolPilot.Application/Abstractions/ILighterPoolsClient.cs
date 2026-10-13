using PoolPilot.Domain.Entities;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Abstractions;

/// <summary>池类型筛选，对应上游 <c>filter</c> 参数。</summary>
public enum PoolFilter
{
    /// <summary>用户创建的池。</summary>
    User,

    /// <summary>协议自营池。</summary>
    Protocol,

    /// <summary>质押池。</summary>
    Stake,

    /// <summary>用户池 + 协议池。</summary>
    All,
}

/// <summary>
/// 上游系统配置。
///
/// 曾有一个 <c>FeeCollectorAccountIndex</c>：迁移到 CryptoClients.Net 后库的模型没映射
/// <c>fee_collector_account_index</c>，而全项目没有任何消费方——于是删掉它，
/// 而不是留一个永远为 0 的字段（那正是"读不到"与"真的是 0"分不清的来源）。
/// </summary>
public sealed record LighterSystemConfig
{
    /// <summary>池枚举的起始游标（最位高的池索引）。</summary>
    public required long LiquidityPoolIndex { get; init; }

    public required long StakingPoolIndex { get; init; }
}

/// <summary>
/// Lighter 公开数据的读取端口。
///
/// 定义在 Application 层（用例需要它），实现在 Infrastructure 层。
/// 全部方法只读且免鉴权——下单需要签名，本项目不做（见 AGENTS.md）。
/// </summary>
public interface ILighterPoolsClient
{
    /// <summary>枚举指定类型的全部公共池。</summary>
    Task<IReadOnlyList<Pool>> FetchPoolsAsync(PoolFilter filter, CancellationToken cancellationToken = default);

    /// <summary>读取系统配置（池枚举的起始游标来自这里）。</summary>
    Task<LighterSystemConfig> FetchSystemConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>读取单个账户（池）的当前状态快照。</summary>
    Task<PoolSnapshot> FetchSnapshotAsync(AccountIndex account, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取池的详细信息：净值序列（份额净值）+ 运营者份额结构 + 上游点估计指标。
    ///
    /// 走 WS <c>pool_info</c> 频道——<c>operator_shares</c> 与 <c>min_operator_share_rate</c>
    /// 只有这个频道提供，<c>publicPoolsMetadata</c> 实测没有这两个字段。
    /// </summary>
    Task<PoolInfo> FetchPoolInfoAsync(AccountIndex account, CancellationToken cancellationToken = default);

    /// <summary>读取池的最近成交。上游最多返回 50 笔/次。</summary>
    Task<IReadOnlyList<PoolTrade>> FetchRecentTradesAsync(
        AccountIndex account,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取池的未成交挂单。
    ///
    /// **必须走 WS <c>account_all_orders</c>**——REST 的挂单接口实测需要鉴权（HTTP 400）。
    /// 这一步也是免鉴权能拿到挂单的唯一通道。
    /// </summary>
    Task<IReadOnlyList<PoolOrder>> FetchOrdersAsync(
        AccountIndex account,
        CancellationToken cancellationToken = default);

    /// <summary>读取全市场合约规格。</summary>
    Task<IReadOnlyList<InstrumentSpec>> FetchInstrumentsAsync(CancellationToken cancellationToken = default);

    /// <summary>读取指定市场的盘口（用于跟单漏损计算）。</summary>
    Task<OrderBookSnapshot> FetchOrderBookAsync(int marketId, CancellationToken cancellationToken = default);
}
