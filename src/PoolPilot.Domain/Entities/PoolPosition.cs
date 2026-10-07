using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 一笔持仓（对应上游 <c>account.positions[]</c> 的一项）。
///
/// 刻意不建模的三个上游字段（实测恒为 0，见 docs/LIGHTER-API-NOTES.md 3.5）：
/// <c>realized_pnl</c>、<c>margin_mode</c>、<c>allocated_margin</c>。
/// 把它们排除在领域模型外，是为了让"不要用这三个字段"成为编译期事实。
/// </summary>
public sealed record PoolPosition
{
    public required int MarketId { get; init; }

    public required string Symbol { get; init; }

    public required PositionSide Side { get; init; }

    /// <summary>持仓规模（绝对值）。方向见 <see cref="Side"/>。</summary>
    public required decimal Size { get; init; }

    public required PriceAmount AvgEntryPrice { get; init; }

    /// <summary>持仓名义价值（USDT）。</summary>
    public required UsdAmount Notional { get; init; }

    public required UsdAmount UnrealizedPnl { get; init; }

    /// <summary>
    /// 该账户在**该市场**设定的保证金率。<c>Leverage</c> 即该仓位的最大杠杆倍数。
    /// 可高于市场默认值（即主动降杠杆），不要当作市场属性读。
    ///
    /// <c>null</c> 表示**上游此刻未给出有效保证金率**。实测仓位变动瞬间
    /// <c>initial_margin_fraction</c> 会短暂变成 <c>"0.00"</c>——0% 保证金率意味着无穷杠杆，
    /// 显然不是任何账户的真实设定，它表达的是"这一行正在更新中"。
    /// 曾因把 0 当非法值抛异常，导致一个 44 个市场、正在活跃调仓的池子
    /// 让整个账户列表接口 400。可空是有意的：读不到有效值时如实说不知道，
    /// 而不是编造一个杠杆倍数。
    /// </summary>
    public MarginFraction? Margin { get; init; }

    /// <summary>该市场的未成交挂单数。免鉴权可见，是唯一能嗅到挂单存在的通道。</summary>
    public required int OpenOrderCount { get; init; }

    /// <summary>累计资金费。负数表示净付出。</summary>
    public required UsdAmount CumulativeFunding { get; init; }

    /// <summary>
    /// 强平价。<c>null</c> 表示**不存在正的清算价**（上游返回 <c>0</c>）。
    ///
    /// **语义与逐仓不同**（池子是全仓）：它是"该仓位**单独**亏掉整个账户清算缓冲所需的单价"
    /// ——多头靠下跌、空头靠上涨——而不是"这一个仓位自己的爆仓价"。
    /// 所以缓冲远大于单仓规模时，这个数字可以远离当前价（实测某池缓冲 $83.6K 而单仓 $7.6K，
    /// 算出的空头清算价是当前价的 15 倍）。
    ///
    /// **多空不对称是结构性的，不是数据缺失**：上涨没有上限、下跌有下限。
    /// 多头要亏掉缓冲所需的跌幅会超过价格本身，那样的正清算价不存在，上游即返回 0
    /// （实测某 17 仓位的池：15 个多头全为 0、2 个空头全有值，用"缓冲 ÷ 持仓量"逐个验算全部吻合）。
    /// 用可空类型而非 0 表达，避免把 0 当成真实强平价参与计算——
    /// 那会得出"价格跌到 0 才强平"这种荒谬结论。
    /// </summary>
    public PriceAmount? LiquidationPrice { get; init; }

    /// <summary>
    /// 保证金占用额（名义价值 × 保证金率）。无法算出时为 <c>null</c>。
    /// </summary>
    public UsdAmount? MarginAmount =>
        Margin is null ? null : UsdAmount.From(Notional.Value * Margin.Ratio);

    /// <summary>
    /// 浮动盈亏率（**对保证金**，即 ROE）。
    ///
    /// 口径刻意与官方前端一致：官方持仓行显示的是「未实现盈亏 ÷ 保证金」。
    /// 实测某仓位官方显示 "−$15.60 (−7.17%)"，其保证金 $213.92、名义 $855.70——
    /// 用名义算只有 −1.82%，差了整整一个杠杆倍数（该仓位 4x）。
    /// 此前本属性按「÷ 名义价值」算，于是同一个仓位在两个界面上相差 4 倍，
    /// 使用者会直接判定我们的数据错了。
    ///
    /// 保证金算不出时返回 <c>null</c>，**不退化成「÷ 名义」**——
    /// 悄悄换分母会让同一个字段在不同行表示不同的量，比缺失更糟。
    /// </summary>
    public PercentValue? UnrealizedPnlPercent =>
        MarginAmount is not { Value: > 0m } margin
            ? null
            : PercentValue.FromPercent(UnrealizedPnl.Value / margin.Value * 100m);

    /// <summary>
    /// 标记价（由上游给的 <c>position_value ÷ position</c> 反推）。
    ///
    /// 上游不单独给标记价字段（实测载荷里没有），但名义价值本身就是"按标记价折算"的，
    /// 所以这个反推是精确的，不是估算。
    /// </summary>
    public PriceAmount MarkPrice =>
        Size == 0m ? PriceAmount.Zero : PriceAmount.From(Notional.Value / Size);

    /// <summary>
    /// 距清算的幅度（百分数）：标的还要走多少才触及 <see cref="LiquidationPrice"/>。
    ///
    /// 符号自带方向：**空头为正**（需上涨）、**多头为负**（需下跌）。无正清算价时为 <c>null</c>。
    ///
    /// 为什么要同时给幅度：全仓口径下清算价是"该仓位单独亏掉整个缓冲"的反事实价格，
    /// 可以离市价非常远（实测某池空头是市价的 15 倍），单看绝对价格读不出危险程度；
    /// 幅度把"多远"变成可比的数字。
    ///
    /// ⚠️ **不含杠杆，也不该含**：杠杆决定占用多少保证金（初始保证金率），
    /// 爆仓由维持保证金率触发。实测两个空头的"清算价 ÷ 市价"为 15.26 与 11.47，
    /// 而其杠杆是 20× 与 5×——两者不相干。用清算价除杠杆还会算出空头不可能出现的
    /// 低于市价的清算价（1368 ÷ 20 = 68 < 市价 89.66）。
    /// </summary>
    public PercentValue? LiquidationDistancePercent =>
        LiquidationPrice is null || MarkPrice.Value <= 0m
            ? null
            : PercentValue.FromPercent(
                (LiquidationPrice.Value - MarkPrice.Value) / MarkPrice.Value * 100m);
}