using PoolPilot.Domain.Enums;

namespace PoolPilot.Application.Dtos;

/// <summary>
/// 交易所配置的读取表示。
///
/// 刻意不含两类字段：
/// <list type="bullet">
///   <item>**凭据**：不回传明文。界面需要的只有"配没配、配的是哪一把（尾号）"。</item>
///   <item>**接口地址**：它由平台预设决定，界面上不显示也不许改（表单里没有这两个字段），
///     因此没有理由把它发到前端——前端拿不到的东西，也就不会有人想着去改它。</item>
/// </list>
/// </summary>
public sealed record ExchangeConfigDto
{
    public required long Id { get; init; }

    /// <summary><c>lighter</c> / <c>binance</c> / <c>okx</c> / <c>gate</c> / <c>custom</c>。</summary>
    public required string Code { get; init; }

    /// <summary><c>cex</c> / <c>dex</c>。</summary>
    public required string Kind { get; init; }

    public required string DisplayName { get; init; }

    public required bool IsTestnet { get; init; }

    public required decimal MakerFeePercent { get; init; }

    public required decimal TakerFeePercent { get; init; }

    public required bool IsEnabled { get; init; }

    public required string? Notes { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>凭据是否配齐（API key 与 secret 都在）。</summary>
    public required bool HasCredentials { get; init; }

    /// <summary>API Key 尾号，界面据此确认配的是哪一把。</summary>
    public required string? ApiKeyHint { get; init; }

    /// <summary>是否配置了 passphrase（OKX 这类平台需要）。</summary>
    public required bool HasPassphrase { get; init; }

    /// <summary>
    /// 这个平台**能不能扫合约清单 / 登记标的映射**。
    ///
    /// 界面据此提前禁用按钮并说明原因，而不是让人点一下再吃一个错误——
    /// "这家还不能扫"是使用者在点之前就该知道的事。
    /// </summary>
    public required bool SupportsSymbolCatalog { get; init; }
}

/// <summary>
/// 新建交易所配置。
///
/// 凭据字段都是明文进来、**密文落库**：加密发生在用例里（经 <c>ISecretProtector</c>），
/// 明文只在这一次请求的内存里存在。
/// </summary>
public sealed record CreateExchangeRequest
{
    public required string Code { get; init; }

    public required string Kind { get; init; }

    public required string DisplayName { get; init; }

    public bool IsTestnet { get; init; }

    /// <summary>接口地址。**不传（null）即保持原值**——它由平台预设决定，界面不显示也不许改；
    /// 读取接口也不返回它，所以更新路径只能靠"不传就别动"来保住已存的值。</summary>
    public string? RestBaseUrl { get; init; }

    /// <summary>同 <see cref="RestBaseUrl"/>：不传即保持原值。</summary>
    public string? WsUrl { get; init; }

    public decimal MakerFeePercent { get; init; }

    public decimal TakerFeePercent { get; init; }

    public string? Notes { get; init; }

    /// <summary>API Key（明文，仅本次请求）。留空表示先不配凭据。</summary>
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public string? Passphrase { get; init; }
}

/// <summary>
/// 修改交易所配置。
///
/// 凭据采用**三态**而不是"要么改要么不改"的二态：
/// <c>null</c> = 保持原样（界面没动它）、空串 = 清除、有值 = 换成新的。
/// 少了"清除"这一态，使用者就永远删不掉一把贴错的 key。
/// </summary>
public sealed record UpdateExchangeRequest
{
    public required string DisplayName { get; init; }

    public bool IsTestnet { get; init; }

    public string? RestBaseUrl { get; init; }

    public string? WsUrl { get; init; }

    public decimal MakerFeePercent { get; init; }

    public decimal TakerFeePercent { get; init; }

    public required bool IsEnabled { get; init; }

    public string? Notes { get; init; }

    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public string? Passphrase { get; init; }
}

/// <summary>枚举值 ↔ 外部字符串。界面与接口用短横线小写，落库也用同一套，避免两套映射漂移。</summary>
public static class ExchangeEnumText
{
    public static string ToText(ExchangeCode code) => code switch
    {
        ExchangeCode.Lighter => "lighter",
        ExchangeCode.Binance => "binance",
        ExchangeCode.Okx => "okx",
        ExchangeCode.Gate => "gate",
        _ => "custom",
    };

    public static ExchangeCode ParseCode(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "custom" => ExchangeCode.Custom,
        "lighter" => ExchangeCode.Lighter,
        "binance" => ExchangeCode.Binance,
        "okx" => ExchangeCode.Okx,
        "gate" => ExchangeCode.Gate,
        _ => throw new ArgumentException($"未知的交易所标识：{text}。可用值：lighter / binance / okx / gate / custom。", nameof(text)),
    };

    /// <summary>
    /// 给使用者看的平台名。
    ///
    /// 与 <see cref="ToText(ExchangeCode)"/> 分开：那个是接口里用的**标识**（小写、稳定，
    /// 前端拿它当键、映射表拿它入库），这个是写进给人看的话里的**名字**
    /// （比如"OKX 没有这个标的的合约"，写成 "okx 没有…" 就是机器在说话）。
    /// </summary>
    public static string ToDisplayName(ExchangeCode code) => code switch
    {
        ExchangeCode.Lighter => "Lighter",
        ExchangeCode.Binance => "币安",
        ExchangeCode.Okx => "OKX",
        ExchangeCode.Gate => "Gate",
        _ => "自定义交易所",
    };

    public static string ToText(ExchangeKind kind) => kind == ExchangeKind.Dex ? "dex" : "cex";

    public static ExchangeKind ParseKind(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "cex" => ExchangeKind.Cex,
        "dex" => ExchangeKind.Dex,
        _ => throw new ArgumentException($"未知的交易所形态：{text}。可用值：cex / dex。", nameof(text)),
    };

    /// <summary>
    /// 宽容版读取，**只用于从库里读回配置**：遇到不认识的值退化成自定义，
    /// 而不是抛异常。严格版（<see cref="ParseCode"/>）留给外部请求——
    /// 那是输入，应当被明确拒绝；而库里的一条脏数据不该让整页打不开。
    /// </summary>
    public static ExchangeCode ReadCode(string? text) =>
        text?.Trim().ToLowerInvariant() switch
        {
            "lighter" => ExchangeCode.Lighter,
            "binance" => ExchangeCode.Binance,
            "okx" => ExchangeCode.Okx,
            "gate" => ExchangeCode.Gate,
            _ => ExchangeCode.Custom,
        };

    /// <summary>宽容版读取形态，理由同 <see cref="ReadCode"/>。</summary>
    public static ExchangeKind ReadKind(string? text) =>
        text?.Trim().ToLowerInvariant() == "dex" ? ExchangeKind.Dex : ExchangeKind.Cex;
}

/// <summary>连通性自检结果。失败时 <see cref="Error"/> 有值且 <see cref="Ok"/> 为 false——不抛异常，界面直接显示原因。</summary>
public sealed record ExchangeProbeDto
{
    public required bool Ok { get; init; }

    public required string? Error { get; init; }

    /// <summary>平台标识；连不上时为 null。</summary>
    public required string? Platform { get; init; }

    /// <summary>该系统能不能在这个平台下单（目前只有币安为 true）。</summary>
    public required bool CanTrade { get; init; }

    /// <summary>是否双向持仓模式。为 null 表示没取到。</summary>
    public required bool? IsHedgeMode { get; init; }

    public required decimal WalletBalance { get; init; }

    public required decimal AvailableBalance { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    public required decimal GrossNotional { get; init; }

    public required IReadOnlyList<BrokerPositionDto> Positions { get; init; }

    public required DateTimeOffset CheckedAt { get; init; }
}

/// <summary>交易所侧的一笔持仓。</summary>
public sealed record BrokerPositionDto
{
    public required string Symbol { get; init; }

    public required string PositionSide { get; init; }

    public required decimal Size { get; init; }

    public required decimal EntryPrice { get; init; }

    public required int Leverage { get; init; }

    public required decimal LiquidationPrice { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    public required decimal Notional { get; init; }
}

/// <summary>规划一笔实盘订单（**只校验不成交**）。</summary>
public sealed record LiveOrderPlanRequest
{
    public required long ExchangeId { get; init; }

    /// <summary>资产币（如 <c>BTC</c>）。标的一律按系统标准交易对给，不给平台写法。</summary>
    public required string BaseAsset { get; init; }

    /// <summary>交易币（如 <c>USDT</c>）。</summary>
    public required string QuoteAsset { get; init; }

    /// <summary>
    /// 下单意图：<c>open_long</c> / <c>close_long</c> / <c>open_short</c> / <c>close_short</c>。
    ///
    /// 刻意不提供"裸方向"（buy/sell）：双向持仓模式下平多与开空都是"卖出"，
    /// 只差 positionSide；允许调用方直接给方向，就等于允许把平多写成开空。
    /// </summary>
    public required string Action { get; init; }

    /// <summary>目标名义额（USDT）。数量由它与当前价折算后量化到步长得到。</summary>
    public required decimal NotionalUsd { get; init; }
}

/// <summary>订单规划结果：把"要下什么"算清楚，并把交易所的校验结果带回来。</summary>
public sealed record LiveOrderPlanDto
{
    public required long ExchangeId { get; init; }

    public required string Platform { get; init; }

    /// <summary>目标交易对的系统标准形式（<c>BTC/USDT</c>）。</summary>
    public required string TargetPair { get; init; }

    public required string Action { get; init; }

    public required string Side { get; init; }

    public required string? PositionSide { get; init; }

    public required bool ReduceOnly { get; init; }

    public required decimal Price { get; init; }

    public required decimal Quantity { get; init; }

    public required decimal NotionalUsd { get; init; }

    public required int Leverage { get; init; }

    public required decimal EstimatedMarginUsd { get; init; }

    public required decimal StepSize { get; init; }

    public required decimal MinQuantity { get; init; }

    public required decimal MinNotional { get; init; }

    /// <summary>本地就发现的规格问题（数量为 0、低于最小量/最小名义额、超总仓位上限）。</summary>
    public required IReadOnlyList<string> Problems { get; init; }

    /// <summary>交易所校验（/order/test）的拒单原因；为 null 表示交易所接受这个形状。</summary>
    public required string? ValidationError { get; init; }

    /// <summary>这单能不能发得出去：本地无问题且交易所校验通过。</summary>
    public required bool IsSendable { get; init; }
}

/// <summary>池里一个市场在某个目标交易所上的可跟性。</summary>
public sealed record MarketMappingDto
{
    public required string LighterSymbol { get; init; }

    public required int MarketId { get; init; }

    /// <summary>池子在该市场的持仓名义额（按它排序，先看重的）。</summary>
    public required decimal PoolNotional { get; init; }

    public required decimal LighterPrice { get; init; }

    /// <summary>
    /// 目标交易对的系统标准形式（<c>CL/USDT</c>）；映射不上时为 null。
    ///
    /// **不给平台合约代码**：那是各家自己的写法，前端也没有理由知道它
    /// （曾经在界面上显示 <c>BTC-USDT-SWAP</c>，等于把一家的方言当成了本系统的标的表示）。
    /// </summary>
    public required string? TargetPair { get; init; }

    /// <summary>目标合约类型，按各平台自己的口径（<c>PERPETUAL</c> / <c>SWAP</c> / <c>stocks</c>…）。</summary>
    public required string? ContractType { get; init; }

    /// <summary>目标合约是不是传统金融（股票/商品/指数）。</summary>
    public required bool IsTradFi { get; init; }

    /// <summary>该合约的最小名义额（计价币）。给不出确切值时为 0。</summary>
    public required decimal MinNotional { get; init; }

    public required bool IsMapped { get; init; }

    /// <summary>备注：映射失败的原因，或"价格差 X 倍"这类需要留意的提示。</summary>
    public required string? Note { get; init; }
}

/// <summary>一条标的映射。跟单路径查的就是它。</summary>
public sealed record SymbolMappingDto
{
    public required long Id { get; init; }

    public required string SourcePlatform { get; init; }

    /// <summary>源资产币（如 <c>WTI</c>）。</summary>
    public required string SourceBase { get; init; }

    /// <summary>源交易币（Lighter 侧为 <c>USDC</c>）。</summary>
    public required string SourceQuote { get; init; }

    /// <summary>源交易对的显示形式（<c>WTI/USDC</c>）。</summary>
    public required string SourcePair { get; init; }

    /// <summary>
    /// 目标**平台符号**（<c>binance</c> / <c>okx</c> …）。
    ///
    /// 不是某条配置的 Id：映射是"哪个资产对应哪个合约"的知识，与你有几个账号无关。
    /// 表格的「交易所」列显示的就是它。
    /// </summary>
    public required string TargetPlatform { get; init; }

    /// <summary>目标资产币（如 <c>CL</c>）。</summary>
    public required string TargetBase { get; init; }

    /// <summary>目标交易币（币安为 <c>USDT</c>）。</summary>
    public required string TargetQuote { get; init; }

    /// <summary>目标交易对的系统标准形式（<c>CL/USDT</c>）。</summary>
    public required string TargetPair { get; init; }

    /// <summary>目标合约类型，按各平台自己的口径（<c>PERPETUAL</c> / <c>SWAP</c> / <c>stocks</c>…）。</summary>
    public required string TargetContractType { get; init; }

    /// <summary>目标合约是不是传统金融（股票/商品/指数）。</summary>
    public required bool TargetIsTradFi { get; init; }

    /// <summary><c>scanned</c>（扫描出来的）/ <c>manual</c>（人工登记或修正过，扫描不再覆盖）。</summary>
    public required string Origin { get; init; }

    public required bool IsEnabled { get; init; }

    public required decimal? SourcePrice { get; init; }

    public required decimal? TargetPrice { get; init; }

    /// <summary>两侧价格的偏离倍数（1.0 表示几乎一致）。它是"这条映射还可信吗"的量化依据。</summary>
    public required decimal? PriceRatio { get; init; }

    public required DateTimeOffset? VerifiedAt { get; init; }

    public required string? Note { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>扫描结果。</summary>
public sealed record SymbolMappingScanResultDto
{
    /// <summary>扫描的目标平台符号。</summary>
    public required string TargetPlatform { get; init; }

    /// <summary>被观察池里出现的市场数。</summary>
    public required int SourceMarkets { get; init; }

    public required int Created { get; init; }

    public required int Updated { get; init; }

    /// <summary>
    /// 与源同名、**直接用**的个数。它们不会被登记——表是例外表，同名的标的不占一行。
    /// 把这个数报出来，是为了让"扫描了 18 个、表里只多 1 行"这件事看着是干完了，而不是漏了。
    /// </summary>
    public required int Direct { get; init; }

    /// <summary>这次扫描**收掉**的同名扫描行数（早先为同名标的留下的冗余行）。</summary>
    public required int Deleted { get; init; }

    /// <summary>同名但停用、因而保留下来的行数：那里面有一个"不要跟它"的决定。</summary>
    public required int Kept { get; init; }

    /// <summary>因为是人工作出的判断而跳过的条数。</summary>
    public required int SkippedManual { get; init; }

    /// <summary>解析不出目标合约的那些（不写进表，只在这里说明原因）。</summary>
    public required IReadOnlyList<SymbolMappingFailureDto> Failures { get; init; }
}

/// <summary>一条没能映射上的源标的，以及为什么。</summary>
public sealed record SymbolMappingFailureDto
{
    /// <summary>源侧的资产币（Lighter 的标的，如 <c>ANTHROPIC</c>）。</summary>
    public required string SourceBase { get; init; }

    /// <summary>源侧的交易币（本系统统一为 <c>USDC</c>）。</summary>
    public required string SourceQuote { get; init; }

    /// <summary>一句话原因，直接来自映射器（如"价格差得远，疑似不是同一个资产：…"）。</summary>
    public required string Reason { get; init; }
}

/// <summary>人工登记或修正一条映射。</summary>
public sealed record SaveSymbolMappingRequest
{
    /// <summary>目标平台符号（<c>binance</c> 等）。</summary>
    public required string TargetPlatform { get; init; }

    /// <summary>源资产币。<b>源交易币固定为 USDC</b>（实测 Lighter 逐市场的 quote 字段恒为 0）。</summary>
    public required string SourceBase { get; init; }

    /// <summary>目标资产币（如 <c>CL</c> / <c>NVDA</c>）。必须来自该平台的合约清单。</summary>
    public required string TargetBase { get; init; }

    /// <summary>目标交易币（币安 U 本位合约为 <c>USDT</c>）。</summary>
    public required string TargetQuote { get; init; }

    public required bool IsEnabled { get; init; }

    /// <summary>源侧价格（用于事后判断这条映射还可不可信）。</summary>
    public decimal? SourcePrice { get; init; }

    public string? Note { get; init; }
}
