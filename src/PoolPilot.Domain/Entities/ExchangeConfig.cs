using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Entities;

/// <summary>
/// 一个交易所的连接配置。
///
/// 允许同一个交易所配多条（两个币安账号是常态），所以身份是自增 Id，
/// 而不是"交易所标识"——后者只能存一条。
///
/// 凭据**按原样保存（明文）**。这里没有加密：曾经做过主密钥 + AES-GCM 那一套，
/// 但密钥与密文躺在同一个库里，它谁也防不住，只换来"没设密钥就不让存凭据"这道坎——
/// 那种"看起来加密了"的假象比明文更糟。所以宁可如实说明，不做防护的表演。
/// 界面仍然只回显尾号（<see cref="ApiKeyHint"/>），那是减少屏幕泄露，不是加密。
///
/// ⚠️ 因此：这里只该放**没有提现权限、最好也没有交易权限**的密钥。
/// </summary>
public sealed record ExchangeConfig
{
    public long Id { get; init; }

    public required ExchangeCode Code { get; init; }

    public required ExchangeKind Kind { get; init; }

    /// <summary>显示名。同一个交易所配两条时靠它区分（"币安主账号"/"币安子账号"）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>测试网。凭据是分环境的，弄混了会去打真钱。</summary>
    public required bool IsTestnet { get; init; }

    public string? RestBaseUrl { get; init; }

    public string? WsUrl { get; init; }

    /// <summary>挂单费率（百分数，0.02 表示 0.02%）。与合约规格里的费率同一口径。</summary>
    public required decimal MakerFeePercent { get; init; }

    /// <summary>吃单费率（百分数）。</summary>
    public required decimal TakerFeePercent { get; init; }

    public required bool IsEnabled { get; init; }

    public string? Notes { get; init; }

    public required LighterTimestamp CreatedAt { get; init; }

    // ── 凭据（明文；null = 该项没配）────────────────────

    public string? ApiKey { get; init; }

    /// <summary>API Secret。Lighter 这类 DEX 放的是签名私钥，语义相同。</summary>
    public string? ApiSecret { get; init; }

    /// <summary>Passphrase。只有部分平台要（OKX 等）。</summary>
    public string? Passphrase { get; init; }

    /// <summary>
    /// API Key 的尾号（明文）。**只用于界面确认**：没有它，使用者无法分辨
    /// 卡片上这条配置配的是哪一把 key，也就无法察觉自己贴错了账号。
    /// 尾号本身不构成凭据——泄露它不足以调用任何接口。
    /// </summary>
    public string? ApiKeyHint { get; init; }

    /// <summary>凭据是否够用：API key 与 secret 都在才算配齐（passphrase 视平台而定）。</summary>
    public bool HasCredentials => ApiKey is not null && ApiSecret is not null;

    /// <summary>
    /// 取尾号：不足 8 位时**不返回任何字符**（短 key 的尾号可能占其大半，
    /// 那种"提示"就不是提示而是泄露了）。
    /// </summary>
    public static string? HintOf(string? plaintextKey) =>
        plaintextKey is { Length: >= 8 } key ? key[^4..] : null;
}
