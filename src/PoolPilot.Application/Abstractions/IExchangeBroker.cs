using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;

namespace PoolPilot.Application.Abstractions;

/// <summary>交易所账户的只读快照。</summary>
public sealed record BrokerAccount(
    decimal WalletBalance,
    decimal AvailableBalance,
    decimal UnrealizedPnl,
    /// <summary>是否处于双向持仓模式。**它决定下单参数形状**：双向模式下必须显式带 positionSide。</summary>
    bool IsHedgeMode,
    IReadOnlyList<BrokerPosition> Positions)
{
    /// <summary>当前持仓的名义总额（绝对值求和）。硬上限里的"总仓位名义"看的就是它。</summary>
    public decimal GrossNotional => Positions.Sum(p => Math.Abs(p.Notional));
}

/// <summary>一笔持仓。字段口径以交易所返回为准，不做二次加工。</summary>
public sealed record BrokerPosition(
    string Symbol,
    /// <summary><c>LONG</c> / <c>SHORT</c>（单向持仓模式下可能是 <c>BOTH</c>）。</summary>
    string PositionSide,
    /// <summary>数量。**双向模式下恒为正**，方向看 <see cref="PositionSide"/>；单向模式下带符号。</summary>
    decimal Size,
    decimal EntryPrice,
    /// <summary>交易所给的标记价。未实现盈亏就是它的函数（见 <c>LivePnl</c>）。</summary>
    decimal MarkPrice,
    int Leverage,
    decimal LiquidationPrice,
    decimal UnrealizedPnl,
    decimal Notional);

/// <summary>
/// 持仓的方向与带符号数量。**判定只在这一处**。
///
/// 两种模式给的形状不同，混着用会得到一个方向相反的仓位：
/// 双向模式下 <c>positionAmt</c> 恒为正、方向在 <c>positionSide</c> 里；
/// 单向模式下 <c>positionSide</c> 恒为 <c>BOTH</c>、真正的方向在**金额的符号**里。
/// 曾经只看 positionSide，于是单向账户的每个空头都会被当成多头（平空会变成开多）。
/// </summary>
public static class BrokerPositions
{
    /// <summary>带符号的数量：正为多、负为空。</summary>
    public static decimal SignedSize(this BrokerPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);

        return position.PositionSide switch
        {
            "SHORT" => -Math.Abs(position.Size),
            "LONG" => Math.Abs(position.Size),
            _ => position.Size,
        };
    }

    /// <summary><c>long</c> / <c>short</c>。</summary>
    public static string Direction(this BrokerPosition position) =>
        position.SignedSize() > 0m ? "long" : "short";
}

/// <summary>下单规格。来自交易所的 symbol filters——把数量量化到步长、并检查最小量与最小名义额都靠它。</summary>
public sealed record BrokerInstrument(
    string Symbol,
    int QuantityPrecision,
    int PricePrecision,
    decimal StepSize,
    decimal MinQuantity,
    decimal MinNotional)
{
    /// <summary>
    /// 把数量向下量化到步长。
    ///
    /// **向下而不是四舍五入**：多下一点会导致"超出我在信号里承诺的名义"，少下一点只是略微没跟满。
    /// 在真实下单的路径上，宁可小一点。
    /// </summary>
    public decimal Quantize(decimal quantity)
    {
        if (StepSize <= 0m)
        {
            return decimal.Round(quantity, Math.Max(0, QuantityPrecision), MidpointRounding.ToZero);
        }

        decimal steps = decimal.Truncate(quantity / StepSize);

        return decimal.Round(steps * StepSize, Math.Max(0, QuantityPrecision), MidpointRounding.ToZero);
    }
}

/// <summary>
/// 下单意图。
///
/// **为什么要有这一层**：双向持仓模式下，<c>positionSide</c> 指的是"要影响哪一侧的持仓"，
/// **不是买卖方向**——平多仓是「卖出 + positionSide=LONG」，开空是「卖出 + positionSide=SHORT」。
/// 两者都是"卖出"，只差 positionSide；而从买卖方向推导 positionSide 会把平多写成开空，
/// 那是一个会真金白银开错方向的错误。所以意图先在这里定死，再由它推出手数与 positionSide。
/// </summary>
public enum BrokerOrderIntent
{
    OpenLong = 0,
    CloseLong = 1,
    OpenShort = 2,
    CloseShort = 3,
}

/// <summary>
/// 一笔要下的单。
///
/// 标的用**系统标准交易对**（资产币 + 交易币）表示，不带平台自己的写法：
/// 合约代码由对接实现在签名前自己拼（币安 <c>BTCUSDT</c>、OKX <c>BTC-USDT-SWAP</c>、Gate <c>BTC_USDT</c>）。
/// 上层写死一个平台的写法，跨到别家就会错得悄无声息。
/// </summary>
public sealed record BrokerOrderRequest(
    /// <summary>资产币（如 <c>BTC</c>）。</summary>
    string BaseAsset,
    /// <summary>交易币（如 <c>USDT</c>）。</summary>
    string QuoteAsset,
    /// <summary>交易所侧的方向。</summary>
    OrderSide Side,
    /// <summary>
    /// 要影响哪一侧的持仓（<c>LONG</c>/<c>SHORT</c>）。
    /// 双向模式必填；单向模式传 null 并用 <see cref="ReduceOnly"/> 表达"只减不增"。
    /// </summary>
    string? PositionSide,
    decimal Quantity,
    /// <summary>只减仓。**仅单向模式使用**：双向模式带它会被币安拒单。</summary>
    bool ReduceOnly,
    /// <summary>客户端订单号，便于与本地记录对上。</summary>
    string ClientOrderId)
{
    /// <summary>
    /// 由意图得出手数与 positionSide（双向模式）。
    ///
    /// 四种组合逐一写出来而不是算出来的：这四行是"平多不能写成开空"的唯一保证，
    /// 用公式表达反而容易在下一次改动里被绕过去。
    /// </summary>
    public static (OrderSide Side, string PositionSide) FromIntent(BrokerOrderIntent intent) => intent switch
    {
        BrokerOrderIntent.OpenLong => (OrderSide.Buy, "LONG"),
        BrokerOrderIntent.CloseLong => (OrderSide.Sell, "LONG"),
        BrokerOrderIntent.OpenShort => (OrderSide.Sell, "SHORT"),
        _ => (OrderSide.Buy, "SHORT"),
    };
}

/// <summary>下单结果。</summary>
public sealed record BrokerOrderResult(
    string Symbol,
    long OrderId,
    string ClientOrderId,
    string Status,
    decimal ExecutedQuantity,
    decimal AveragePrice);

/// <summary>
/// 交易所券商的抽象（AGENTS.md 里预留的 <c>IBroker</c>）。
///
/// 它是**唯一能改变账户状态**的东西：只有 <see cref="PlaceOrderAsync"/> 会花钱，
/// 其余方法都是只读或"只校验"。因此实现方必须做到：
/// <list type="bullet">
///   <item>凭据只在调用时取自配置，绝不落到日志或异常消息里。</item>
///   <item>下单前先量化数量并检查交易所规格，让"交易所拒单"尽量发生在本地。</item>
///   <item>错误原样带上交易所的错误码与文案——那些码是有意义的（-2019 保证金不足、-4164 名义太小），
///     把它们翻译成自造的描述会让排查变难。</item>
/// </list>
/// </summary>
public interface IExchangeBroker
{
    /// <summary>平台标识（<c>binance</c> 等）。</summary>
    string Code { get; }

    Task<BrokerAccount> GetAccountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 取合约规格。标的用系统标准交易对给出，**平台写法在实现里转换**——
    /// 调用方不该知道、也不该拼"BTCUSDT"这类字符串。
    /// </summary>
    Task<BrokerInstrument> GetInstrumentAsync(
        string baseAsset,
        string quoteAsset,
        CancellationToken cancellationToken = default);

    /// <summary>该合约的最新价（公开端点，不需要签名）。用于把"目标名义额"换算成数量。</summary>
    Task<decimal> GetPriceAsync(
        string baseAsset,
        string quoteAsset,
        CancellationToken cancellationToken = default);

    /// <summary>确保该合约的杠杆为指定值（幂等）。返回实际生效的杠杆。</summary>
    Task<int> EnsureLeverageAsync(
        string baseAsset,
        string quoteAsset,
        int leverage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// **真实下单**。整个系统里唯一会动钱的调用。
    /// 调用方负责硬上限校验；这里只保证参数形状正确、数量已量化。
    /// </summary>
    Task<BrokerOrderResult> PlaceOrderAsync(
        BrokerOrderRequest order,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 只校验不成交（币安的 <c>/order/test</c>）：签名、规格、权限、保证金全部按真实路径检查一遍，
    /// 但不会产生订单。**任何下单参数形状的改动都要先用它验一遍**——它是唯一能在不花钱的前提下
    /// 证明"这单发得出去"的办法。
    /// </summary>
    Task ValidateOrderAsync(BrokerOrderRequest order, CancellationToken cancellationToken = default);
}

/// <summary>
/// 交易所的一个可交易合约（含两个命名空间）。
///
/// <see cref="ContractType"/> 必须带出来：币安的 <c>PERPETUAL</c>（加密永续）与
/// <c>TRADIFI_PERPETUAL</c>（传统金融永续，实测 216 个，覆盖股票/商品/指数）是两个不同的东西，
/// 落流水时要能看出这一笔开在哪个命名空间上。
/// </summary>
public sealed record ExchangeSymbolInfo(
    /// <summary>
    /// **平台自己的写法**（币安 <c>CLUSDT</c>、OKX <c>CL-USDT-SWAP</c>、Gate <c>CL_USDT</c>）。
    ///
    /// 它是这个平台目录里一条数据的名字，用于签名下单与在平台上找到它；
    /// **它不是本系统的标的表示**——系统内部一律用 <see cref="BaseAsset"/> +
    /// <see cref="QuoteAsset"/> 两段，也不该拿它入库或当作标的展示给使用者。
    /// 拼它、认它的只有对接实现自己。
    /// </summary>
    string Symbol,
    string BaseAsset,
    /// <summary>交易币（币安 U 本位合约为 <c>USDT</c>）。与资产币一起才是完整的交易对。</summary>
    string QuoteAsset,
    /// <summary>平台自己的合约类型口径（币安 <c>PERPETUAL</c>/<c>TRADIFI_PERPETUAL</c>、OKX <c>SWAP</c>、
    /// Gate 的 <c>stocks</c>/<c>indices</c> 等）。</summary>
    string ContractType,
    /// <summary>
    /// 是不是传统金融合约（股票/商品/指数）。**由各平台的对接实现自己判定**——
    /// 曾经写成"ContractType == TRADIFI_PERPETUAL"，那只对币安成立，
    /// 换到 OKX/Gate 会把股票合约一律标成"加密"（一个不会报错、只会说假话的标签）。
    /// </summary>
    bool IsTradFi,
    decimal MinNotional,
    decimal StepSize,
    int QuantityPrecision);

/// <summary>交易所的合约目录（公开接口，不需要签名）。</summary>
public interface IExchangeSymbolCatalog
{
    /// <summary>平台标识（<c>binance</c> 等）。</summary>
    string Code { get; }

    /// <summary>全部 USDT 合约（含传统金融那一类）。</summary>
    Task<IReadOnlyList<ExchangeSymbolInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 全部合约的最新价（一次调用取完，避免逐个标的往返）。
    ///
    /// **键是合约对象本身，不是任何字符串**：上层拿到的候选对象就是查价的钥匙，
    /// 这样"平台的写法"永远渗不出对接实现（曾经拿平台合约代码当键，上层于是要自己拼
    /// <c>BTCUSDT</c>，拼错了只表现为"取不到价"，看不出是拼错了）。
    /// </summary>
    Task<IReadOnlyDictionary<ExchangeSymbolInfo, decimal>> PricesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 这个目录里装着哪些合约，一句给人看的话。用于"这家没有这个标的"的拒绝原因里。
    ///
    /// 为什么由目录自己说、而不是由映射器统一措辞：**只有真正去枚举的那个东西才知道自己枚举了什么**。
    /// 这条不是洁癖，是一次实测教训——早期按 <c>contractType == PERPETUAL</c> 过滤，
    /// 于是得出"币安没有股票合约"的错误结论，而股票就在 <c>TRADIFI_PERPETUAL</c> 里。
    /// 所以拒绝时必须说清"我在哪些范围内找过"，让人能分辨"确实没有"与"我找漏了"。
    /// </summary>
    string Coverage { get; }
}

/// <summary>
/// 按**平台符号**造合约目录。
///
/// 参数是平台而不是配置：映射的知识是平台级的（"WTI 对应币安的 CL/USDT"），
/// 与使用者在那个平台上有几个账号无关——所以查合约清单也不该依赖某条配置存在。
///
/// 该平台还没有目录实现时返回 <c>null</c>，由调用方给出明确错误（"这家还不能扫"是正常回答）。
/// </summary>
public interface IExchangeSymbolCatalogFactory
{
    IExchangeSymbolCatalog? Create(ExchangeCode platform);

    /// <summary>系统支持的全部平台符号（界面的选择器列的就是它）。</summary>
    IReadOnlyList<ExchangeCode> SupportedPlatforms { get; }
}

/// <summary>按交易所配置造券商实例。不支持的平台返回 <c>null</c>，由调用方给出明确错误。</summary>
public interface IExchangeBrokerFactory
{
    IExchangeBroker? Create(ExchangeConfig config);
}
