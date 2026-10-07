using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>映射是怎么来的。</summary>
public enum SymbolMappingOrigin
{
    /// <summary>扫描出来的（同名 + 两边价格量级确认过）。</summary>
    Scanned = 0,

    /// <summary>人工登记或修正过。**扫描永不覆盖人工行**——人的判断优先。</summary>
    Manual = 1,
}

/// <summary>
/// 跨交易所标的映射：源市场 → **目标交易平台**的合约。
///
/// 目标侧的身份是平台符号（<c>binance</c>），不是某条交易所配置——
/// 映射是"哪个资产对应哪个合约"的知识，与你有几个账号无关。
///
/// **交易对按"资产币 + 交易币"两个字段存**，与系统里交易对的统一口径一致：
/// 一个 <c>ENA</c> 在 Lighter 上是 <c>ENA/USDC</c>、在币安是 <c>ENA/USDT</c> ——
/// 同一个资产币、不同的交易币，这是两件不同的事，压成一个拼好的字符串就看不出来了。
/// （实测 Lighter 的 <c>base_asset_id</c>/<c>quote_asset_id</c> 恒为 0，所以源侧交易币
/// 按系统口径取 <c>USDC</c>——Lighter 的合约以 USDC 计价与结算。）
///
/// **为什么要落成一张表而不是每次现算**：
/// <list type="number">
///   <item>跟单员收到信号时要在一个毫秒级窗口里决定"这单能不能下"，那次查询不该打网络。</item>
///   <item>"同名不同物"的判断需要人工介入（Lighter 的 US500 与币安 SPY 是不同工具），
///     而人的判断必须有地方存——存在代码注释里等于没有。</item>
///   <item>映射错了会把仓位开在另一个资产上，而界面上看起来完全正常。
///     所以每条映射都记下最近一次验证时两侧的价格，漂移了就看得出来。</item>
/// </list>
/// </summary>
public sealed record SymbolMapping
{
    /// <summary>
    /// 源平台：本系统观察的池**全部**在 Lighter 上，所以映射的源侧固定是它。
    ///
    /// 因此 <see cref="TargetPlatform"/> 的候选里不含 Lighter——它已经在源这一侧了，
    /// "把 Lighter 映射到 Lighter"没有意义。这条规则写在这里一次，不被各处重复判断。
    /// </summary>
    public const string SourcePlatformLighter = "lighter";

    public long Id { get; init; }

    /// <summary>源头平台（恒为 <see cref="SourcePlatformLighter"/>）。</summary>
    public required string SourcePlatform { get; init; }

    /// <summary>源资产币（Lighter 的市场符号，如 <c>WTI</c>）。</summary>
    public required string SourceBase { get; init; }

    /// <summary>源交易币（Lighter 侧为 <c>USDC</c>）。</summary>
    public required string SourceQuote { get; init; }

    /// <summary>
    /// 目标**平台符号**（<c>binance</c> / <c>okx</c> …），不是某条配置的 Id。
    ///
    /// 理由是映射这件事与"你配了几个账号"无关：同一个平台下你有两个币安账号，
    /// "Lighter 的 WTI 对应币安的 CL/USDT" 这句话仍然只有一句。
    /// 按配置存会让同一份映射被迫存两遍，还得手工保持两边一致。
    ///
    /// **不会是 <see cref="SourcePlatformLighter"/> 本身**：Lighter 是源，不是目标。
    /// </summary>
    public required string TargetPlatform { get; init; }

    /// <summary>目标资产币（如 <c>CL</c>）。</summary>
    public required string TargetBase { get; init; }

    /// <summary>目标交易币（币安侧为 <c>USDT</c>）。</summary>
    public required string TargetQuote { get; init; }

    /// <summary>
    /// 目标合约类型，**按各平台自己的口径**（币安 <c>PERPETUAL</c>/<c>TRADIFI_PERPETUAL</c>、
    /// OKX <c>SWAP</c>、Gate <c>stocks</c> 等）。原样记录，不做跨平台的"统一翻译"——
    /// 翻译过的类型名在排查时会与交易所界面对不上。
    /// </summary>
    public required string TargetContractType { get; init; }

    /// <summary>目标合约是不是传统金融（股票/商品/指数）。由登记时的平台目录给出。</summary>
    public required bool TargetIsTradFi { get; init; }

    public required SymbolMappingOrigin Origin { get; init; }

    /// <summary>停用后跟单路径视为"未映射"，但记录仍在——排查时能看到"本来是有的，被停了"。</summary>
    public required bool IsEnabled { get; init; }

    /// <summary>最近一次验证时源侧的价格。</summary>
    public decimal? SourcePrice { get; init; }

    /// <summary>最近一次验证时目标侧的价格。</summary>
    public decimal? TargetPrice { get; init; }

    /// <summary>最近一次验证的时间。为 null 表示还没验证过（例如手工登记的）。</summary>
    public required LighterTimestamp? VerifiedAt { get; init; }

    public string? Note { get; init; }

    public required LighterTimestamp CreatedAt { get; init; }

    /// <summary>源交易对的显示形式，如 <c>ENA/USDC</c>。</summary>
    public string SourcePair => $"{SourceBase}/{SourceQuote}";

    /// <summary>目标交易对的显示形式，如 <c>ENA/USDT</c>。</summary>
    public string TargetPair => $"{TargetBase}/{TargetQuote}";

    /// <summary>
    /// 两边价格的偏离倍数（1.0 表示几乎完全一致）。没有两侧价格时为 null。
    /// 它是"这条映射还可信吗"的唯一量化依据。
    /// </summary>
    public decimal? PriceRatio =>
        SourcePrice is not { } source || TargetPrice is not { } target || source <= 0m || target <= 0m
            ? null
            : source > target
                ? source / target
                : target / source;
}
