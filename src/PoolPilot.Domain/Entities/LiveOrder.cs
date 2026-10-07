using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 实盘流水里的一笔记录：我方在某个交易所上做过的一次动作，或**本该做而没做成**的一次。
///
/// 与纸面流水（<see cref="PaperFill"/>）的分工：那个记的是"跟着池子做，账会变成多少"，
/// 它自己算成交价与盈亏；这个记的是"我在交易所那边实际发了什么、交易所回了什么"。
/// 真实持仓与盈亏以**交易所的账户状态**为准，不在这里算一遍——
/// 算出来的第二份账一旦与交易所对不上，就不知道该信哪一份。
///
/// 所以 <see cref="Status"/> 里有 <c>skipped</c> 与 <c>failed</c> 两种"没成"：
/// 前者是我方主动不发（低于最小下单量、没映射上、目标太小），后者是发出去了被拒。
/// 两者的排查方向完全不同，混成一个"失败"会让该看映射表的人去看交易所日志。
/// </summary>
public sealed record LiveOrder
{
    /// <summary>持久化主键；未入库时为 0。</summary>
    public long Id { get; init; }

    public required long FollowerId { get; init; }

    /// <summary>下单用的交易所配置（凭据与接入地址都在它里面）。</summary>
    public required long ExchangeConfigId { get; init; }

    /// <summary>平台符号（<c>binance</c> 等）。</summary>
    public required string Platform { get; init; }

    /// <summary>
    /// 目标交易对，**按系统标准形式给**（资产币 + 交易币）。
    /// 平台自己的合约代码（<c>BTCUSDT</c>）属于对接实现的细节，不入库。
    /// </summary>
    public required string TargetBase { get; init; }

    public required string TargetQuote { get; init; }

    /// <summary>源侧的标的（Lighter 的市场名）。没有它就无法回答"这笔是从哪个信号来的"。</summary>
    public required string SourceSymbol { get; init; }

    /// <summary>意图（<c>open_long</c> / <c>close_long</c> / <c>open_short</c> / <c>close_short</c>）。</summary>
    public required string Intent { get; init; }

    /// <summary>交易所侧的方向（<c>buy</c> / <c>sell</c>），与意图一起才能还原这一笔的形状。</summary>
    public required string Side { get; init; }

    /// <summary>双向模式下的持仓侧（<c>LONG</c> / <c>SHORT</c>）；单向模式为 null。</summary>
    public required string? PositionSide { get; init; }

    public required decimal Quantity { get; init; }

    /// <summary>
    /// 下单时的**参考价**（把名义额换算成数量用的那个）。
    /// 成交价以 <see cref="EntryPrice"/> / <see cref="ClosePrice"/> 为准——
    /// 这两个是交易所回执的成交均价（或其兜底），这一列只是"当时市价是多少"。
    /// </summary>
    public required decimal Price { get; init; }

    /// <summary>
    /// 这一单所涉仓位的**开仓均价**。
    /// 开仓单 = 本单的成交均价（新仓位的入场价）；平仓单 = **被平的那个仓位**的开仓均价
    /// （下单前从账户里读的）。为 0 表示未知（早于此功能落库的旧行）。
    /// </summary>
    public required decimal EntryPrice { get; init; }

    /// <summary>
    /// **平仓成交价**。只有平仓单有；开仓单与未发出的行为 null。
    /// 与 <see cref="EntryPrice"/> 摆在一行，单位盈亏（平仓价 − 开仓价）直接可算。
    /// </summary>
    public required decimal? ClosePrice { get; init; }

    /// <summary>这笔**实际**下单的名义额（数量 × 价），带符号。跳过时为 0。</summary>
    public required decimal NotionalUsd { get; init; }

    /// <summary>
    /// 池子在该市场的持仓名义额（带符号）。回答"这个跟单员面对的信号有多大"。
    ///
    /// 与 <see cref="TargetNotionalUsd"/> 不是一回事，**别把两者混成一个字段**：
    /// 曾经把目标名义写进了这个字段，于是流水上"池子名义 19.07"看起来像池子只有 19 USDT，
    /// 实际那是 50 USDT 本金比例复制**之后**的目标——读的人（包括我自己复盘时）会被误导。
    /// </summary>
    public required decimal PoolNotional { get; init; }

    /// <summary>
    /// 本次的目标名义额（池子名义 × 规模比例），带符号。
    /// 它与实际下单名义的差别来自"数量向下量化"，两者都留着才能看懂这一笔怎么来的。
    /// </summary>
    public required decimal TargetNotionalUsd { get; init; }

    /// <summary>池子权益与我方本金：规模比例由这两者得出，缺了就无法复盘。</summary>
    public required decimal PoolEquity { get; init; }

    public required decimal Capital { get; init; }

    /// <summary>
    /// 这一单是谁的决定：<c>signal</c>（带单员信号驱动的自动跟单）或
    /// <c>manual</c>（人在界面上点的平仓）。
    ///
    /// 必须区分：流水是复盘的全部依据，而"这一笔是系统跟的还是在手动的"是复盘时第一个要问的。
    /// 混在一起的话，手动平仓会被读成"跟单逻辑自己平掉了仓位"。
    /// </summary>
    public required string Trigger { get; init; }

    /// <summary><c>placed</c>（已发出）/ <c>skipped</c>（我方主动没发）/ <c>failed</c>（发出被拒）。</summary>
    public required string Status { get; init; }

    /// <summary>没发或没成的原因；成功时为 null。</summary>
    public required string? Reason { get; init; }

    /// <summary>交易所返回的订单号（成功时）。</summary>
    public required string? VenueOrderId { get; init; }

    /// <summary>客户端订单号，便于在交易所侧查出这一笔。</summary>
    public required string? ClientOrderId { get; init; }

    public required LighterTimestamp CreatedAt { get; init; }
}

/// <summary>这一单是谁的决定。</summary>
public static class LiveOrderTrigger
{
    /// <summary>带单员信号驱动的自动跟单。</summary>
    public const string Signal = "signal";

    /// <summary>挂载/恢复时的**对齐**（把池子现有仓位补上，过价格门）。</summary>
    public const string Align = "align";

    /// <summary>人在界面上手动下的（目前只有平仓）。</summary>
    public const string Manual = "manual";
}

/// <summary>实盘流水的三种结局。</summary>
public static class LiveOrderStatus
{
    /// <summary>已发给交易所并被接受。</summary>
    public const string Placed = "placed";

    /// <summary>我方主动没发（低于最小下单量、没映射上、差额为 0 之外的"不值得发"）。</summary>
    public const string Skipped = "skipped";

    /// <summary>发出去了，被交易所拒（保证金不足、规格不符、权限不够）。</summary>
    public const string Failed = "failed";
}
