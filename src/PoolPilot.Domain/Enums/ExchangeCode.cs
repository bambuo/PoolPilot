namespace PoolPilot.Domain.Enums;

/// <summary>
/// 交易所标识。
///
/// <see cref="Custom"/> 是刻意的出口：预设只覆盖常用的四家，
/// 而"能不能接一个新平台"不该由我们预先列出的清单决定。
/// </summary>
public enum ExchangeCode
{
    /// <summary>Lighter（zk 永续 DEX，本项目当前的唯一数据源）。</summary>
    Lighter = 0,

    Binance = 1,

    Okx = 2,

    Gate = 3,

    /// <summary>自填。名称、接口地址、凭据字段全部由使用者提供。</summary>
    Custom = 99,
}

/// <summary>
/// 交易所形态。**存下来而不是从标识推导**：自填的条目推导不出形态，
/// 而"这是链上还是中心化"直接决定凭据要怎么管、将来下单要怎么签。
/// </summary>
public enum ExchangeKind
{
    /// <summary>中心化交易所（API key + secret，部分平台还要 passphrase）。</summary>
    Cex = 0,

    /// <summary>去中心化交易所（凭据通常是链上账户或签名私钥）。</summary>
    Dex = 1,
}
