using PoolPilot.Application.Abstractions;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 跨场所标的映射：把 Lighter 的市场映射到目标交易所的合约（币安 / OKX / Gate）。
///
/// 文档里凡是写"币安"的地方，读作"目标交易所"——早期只有币安一个目标，
/// 现在的候选来自各平台自己的合约目录，措辞保留只是为了不把已核对过的实测记录改花。
///
/// 这里**只看名字是不够的**，实测有两类坑：
/// <list type="number">
///   <item>**同名不同物**：币安有两个命名空间（<c>PERPETUAL</c> 加密永续、
///     <c>TRADIFI_PERPETUAL</c> 传统金融永续），实测两者**互不相交**（0 个撞名）。
///     但"在不同交易所同名"仍可能是两回事——Lighter 的 <c>SPX</c> 对应的币安合约是
///     <c>SPXUSDT</c>，而那是 meme 币 SPX6900，不是标普指数。按名字直接映射会去买错东西。</item>
///   <item>**看起来像股票的加密币**：<c>DASH</c>（Dash 币 vs DoorDash）、<c>MET</c>、<c>AI</c> 这类名字
///     在两边都存在，靠名字无法判别。</item>
/// </list>
///
/// 所以映射规则是"名字必须先对上，**再用价格量级确认是同一个资产**"：
/// 两边的价格比值落在 [1/3, 3] 之外就判定为同名不同物，拒绝映射并记下原因。
/// 这是能自动做的最强检查——它挡得住 SPX 那种差几千倍的情况，也会挡住拆分/改名后的价差，
/// 而后者本来就该人工确认。
///
/// ⚠️ 价格检查不是证明。它挡的是"明显不是一个东西"，不是"一定是同一个东西"。
/// </summary>
public static class SymbolMapper
{
    /// <summary>价格比值允许的偏离倍数。超过它就是两个资产。</summary>
    private const decimal MaxPriceRatio = 3m;

    /// <summary>
    /// 人工登记的别名：两边是同一个物，但用了不同的名字。
    ///
    /// **值必须是币安的 baseAsset，不是合约代码**——候选表是按 baseAsset 建的键
    /// （实测踩过：写成 <c>CLUSDT</c> 查不到，因为它的 baseAsset 是 <c>CL</c>，
    /// 于是别名静默失效、WTI 一直显示"币安没有这个标的"）。
    ///
    /// 全部由**两边价格对得上**验证过（2026-10-06 实测）：
    /// <list type="bullet">
    ///   <item><c>WTI → CL</c>（轻质原油；NYMEX 代码是 CL，合约 CLUSDT）—— 87.947 vs 87.98</item>
    ///   <item><c>BRENTOIL → BZ</c>（布伦特原油，合约 BZUSDT）—— 98.97 vs 99.06</item>
    ///   <item><c>XCU → COPPER</c>（铜，合约 COPPERUSDT）—— 6.6791 vs 6.668</item>
    /// </list>
    ///
    /// 别名只是"多给一个候选"，**价格检查照旧执行**：哪天这对不再同物（改名、换标的、拆股），
    /// 检查会把它挡下来，而不是照旧下错单。
    ///
    /// 反例（**刻意不登记**）：Lighter 的 <c>US500</c>（7790 点，指数点位）与币安的
    /// <c>SPYUSDT</c>（776.9，ETF 价格）差 10 倍——它们是不同工具，映射过去名义额虽然近似，
    /// 但"指数点位 vs ETF 份额"不是同一个东西，该由人来决定要不要这么跟。
    /// </summary>
    private static readonly Dictionary<string, string[]> AliasBaseAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WTI"] = ["CL"],
        ["BRENTOIL"] = ["BZ"],
        ["XCU"] = ["COPPER"],
    };

    /// <summary>
    /// 交易币的偏好次序。**同名标的在目标平台可能有多个合约**（实测币安的 BTC 就有
    /// USDT / USDC / USD1 三个计价），这时"用哪一个"必须有确定答案，否则同一个信号
    /// 会因为目录顺序变化而开在不同合约上。
    ///
    /// 次序的道理：跟单账户是 <b>USDT 本位</b>，USDT 计价的合约不需要额外的保证金口径转换；
    /// 其余按流动性常识排在后面。这不是"最优选择"的断言，是一条**可预测**的规则。
    /// </summary>
    private static int QuoteRank(string quoteAsset) => quoteAsset.ToUpperInvariant() switch
    {
        "USDT" => 0,
        "USDC" => 1,
        "USD1" => 2,
        _ => 3,
    };

    /// <summary>
    /// 取一个 Lighter 标的所有候选：先按同名 base 匹配，再补上人工登记的别名。
    ///
    /// 为什么两个都查而不是"别名优先"：同名是多数情况、别名是例外；
    /// 合并之后交给价格检查裁决，规则只有一条，不会出现"别名把同名挤掉"这种隐蔽行为。
    /// </summary>
    public static IReadOnlyList<ExchangeSymbolInfo> CandidatesFor(
        string lighterSymbol,
        IReadOnlyDictionary<string, List<ExchangeSymbolInfo>> byBase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lighterSymbol);
        ArgumentNullException.ThrowIfNull(byBase);

        List<ExchangeSymbolInfo> candidates = [.. (byBase.GetValueOrDefault(lighterSymbol) ?? [])
            .OrderBy(c => QuoteRank(c.QuoteAsset))];

        foreach (string alias in AliasBaseAssets.GetValueOrDefault(lighterSymbol) ?? [])
        {
            foreach (ExchangeSymbolInfo candidate in byBase.GetValueOrDefault(alias) ?? [])
            {
                if (!candidates.Contains(candidate))
                {
                    candidates.Add(candidate);
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// 映射结果。**只按"资产币 + 交易币"两段给出**——这是系统内标的的统一表示。
    ///
    /// 解析结果里**不带平台合约代码**：`BTCUSDT` / `BTC-USDT-SWAP` / `BTC_USDT` 是各家的写法，
    /// 由对接实现在下单、取价时自己转换。放在这里就等于让全系统都学会了一种平台的方言，
    /// 换一家就要改一遍所有读它的地方。
    /// </summary>
    public sealed record Resolution
    {
        /// <summary>目标资产币（如 <c>CL</c>）；映射失败时为 null。</summary>
        public required string? Base { get; init; }

        /// <summary>目标交易币（币安为 <c>USDT</c>）；映射失败时为 null。</summary>
        public required string? Quote { get; init; }

        /// <summary>目标合约类型，按各平台自己的口径（<c>PERPETUAL</c> / <c>SWAP</c> / <c>stocks</c>…）。</summary>
        public required string? ContractType { get; init; }

        /// <summary>目标合约是不是传统金融（股票/商品/指数）。</summary>
        public required bool IsTradFi { get; init; }

        /// <summary>该合约的最小名义额（计价币）。给不出确切值时为 0（"不知道"，不是"没有下限"）。</summary>
        public required decimal MinNotional { get; init; }

        /// <summary>
        /// 解析时用到的目标侧价格。一并带出来，免得调用方拿着结果再去取一次价
        /// （那一次取价就得让调用方知道"目标合约叫什么"，正是要避免的）。
        /// </summary>
        public decimal? Price { get; init; }

        public required bool IsMapped { get; init; }

        /// <summary>没映射上时的一句话原因（会记进流水）。</summary>
        public required string? Reason { get; init; }
    }

    /// <summary>
    /// 解析一个 Lighter 标的。
    /// </summary>
    /// <param name="symbol">Lighter 的标的（如 <c>ENA</c>、<c>NVDA</c>）。</param>
    /// <param name="lighterPrice">Lighter 侧的标记价。为 0 表示取不到，此时**拒绝映射**（见下）。</param>
    /// <param name="candidates">目标交易所侧的候选（同一个 baseAsset 可能有多个合约）。</param>
    /// <param name="priceOf">取某个候选合约现在的价格。**按候选对象取**，不让上层接触平台写法。</param>
    /// <param name="venue">目标交易所的显示名，用于写拒绝原因（<c>币安</c> / <c>OKX</c> / <c>Gate</c>）。
    /// 原因是要给人看的，写错交易所名会让人去错误的地方找原因。</param>
    /// <param name="coverage">那边的合约目录覆盖了什么（来自目录自己，见
    /// <see cref="IExchangeSymbolCatalog.Coverage"/>）。</param>
    public static Resolution Resolve(
        string symbol,
        decimal lighterPrice,
        IReadOnlyList<ExchangeSymbolInfo> candidates,
        Func<ExchangeSymbolInfo, decimal> priceOf,
        string venue,
        string coverage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);
        ArgumentException.ThrowIfNullOrWhiteSpace(coverage);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(priceOf);

        if (candidates.Count == 0)
        {
            return Refuse($"{venue}没有这个标的的合约（在{coverage}里找过）");
        }

        // 取不到 Lighter 侧的价格就无法做那个确认，**宁可拒绝**：
        // 猜错的代价是仓位开在另一个资产上，而拒绝的代价只是这一笔没跟上
        if (lighterPrice <= 0m)
        {
            return Refuse("取不到 Lighter 侧的标记价，无法确认两边是同一个资产");
        }

        ExchangeSymbolInfo? best = null;
        decimal bestPrice = 0m;
        decimal bestRatio = 0m;
        List<string> tried = [];

        foreach (ExchangeSymbolInfo candidate in candidates)
        {
            decimal targetPrice = priceOf(candidate);

            if (targetPrice <= 0m)
            {
                tried.Add($"{candidate.Symbol} 取不到价");
                continue;
            }

            decimal ratio = lighterPrice > targetPrice
                ? lighterPrice / targetPrice
                : targetPrice / lighterPrice;

            tried.Add($"{candidate.Symbol}={targetPrice}（差 {ratio:F1} 倍）");

            if (ratio <= MaxPriceRatio)
            {
                best = candidate;
                bestPrice = targetPrice;
                bestRatio = ratio;
                break;
            }
        }

        if (best is null)
        {
            return Refuse($"{venue}侧同名但价格差得远，疑似不是同一个资产：{string.Join("、", tried)}");
        }

        return new Resolution
        {
            Base = best.BaseAsset,
            Quote = best.QuoteAsset,
            ContractType = best.ContractType,
            IsTradFi = best.IsTradFi,
            MinNotional = best.MinNotional,
            Price = bestPrice,
            IsMapped = true,
            Reason = bestRatio > 1.2m ? $"价格差 {bestRatio:F1} 倍（可能溢价或口径不同，请留意）" : null,
        };
    }

    private static Resolution Refuse(string reason) => new()
    {
        Base = null,
        Quote = null,
        ContractType = null,
        IsTradFi = false,
        MinNotional = 0m,
        IsMapped = false,
        Reason = reason,
    };
}
