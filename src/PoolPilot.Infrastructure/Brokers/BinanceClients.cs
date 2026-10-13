using Binance.Net;
using Binance.Net.Interfaces.Clients;
using CryptoExchange.Net.Objects;

using PoolPilot.Domain.Entities;

namespace PoolPilot.Infrastructure.Brokers;

/// <summary>
/// 按配置取库（CryptoClients.Net / Binance.Net）的客户端，以及把库的失败结果翻成异常。
///
/// **为什么要有这一层**：本项目允许同一个平台配多条（两个币安账号是常态），
/// 而库的自带客户端是"一个环境一份凭据"。库为此提供了按用户取客户端的入口
/// （<see cref="IBinanceUserClientProvider"/>）：给一个 userId，它按需造一个只属于该用户的
/// 客户端并缓存起来。userId 就用配置的自增 Id——同一个账号下的下单、用户流、标记价
/// 因此共用一条连接与一份限流，而两个账号互不影响。
///
/// 凭据与环境的来源只有一处：<see cref="ExchangeConfig"/>。
/// 主网/测试网由库的 <see cref="BinanceEnvironment"/> 表达——地址不再由配置里的
/// RestBaseUrl 决定（那份字段对执行侧已经不作数，见 docs/EXCHANGES.md）。
/// </summary>
internal static class BinanceClients
{
    /// <summary>这条配置对应的库环境。测试网是另一套地址与另一份凭据，弄混了会去打真钱。</summary>
    public static BinanceEnvironment EnvironmentOf(ExchangeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.IsTestnet ? BinanceEnvironment.Testnet : BinanceEnvironment.Live;
    }

    /// <summary>库用来缓存客户端的键。取配置 Id：同一条配置的下单与数据流共用一个客户端。</summary>
    public static string UserIdOf(ExchangeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return $"config-{config.Id}";
    }

    /// <summary>取一条配置的 REST 客户端。凭据缺失时抛出——签名路径上一个空 key 只会换来一个难懂的上游错误。</summary>
    public static IBinanceRestClient Rest(IBinanceUserClientProvider provider, ExchangeConfig config)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(config);

        return provider.GetRestClient(UserIdOf(config), CredentialsOf(config), EnvironmentOf(config));
    }

    /// <summary>取一条配置的 WebSocket 客户端（用户数据流、标记价都走它）。</summary>
    public static IBinanceSocketClient Socket(IBinanceUserClientProvider provider, ExchangeConfig config)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(config);

        return provider.GetSocketClient(UserIdOf(config), CredentialsOf(config), EnvironmentOf(config));
    }

    /// <summary>
    /// 公开数据（合约目录、最新价）用的客户端：**不需要凭据**。
    ///
    /// userId 固定成 <c>public-data</c>：公开数据与账号无关，按账号各建一份纯属浪费；
    /// 而"哪条配置"在这里也不该参与——没有配置的人也查得了合约清单（映射页就是这样用的）。
    /// </summary>
    public static IBinanceRestClient PublicRest(IBinanceUserClientProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return provider.GetRestClient("public-data", null, BinanceEnvironment.Live);
    }

    /// <summary>
    /// 失败就抛出，成功返回数据。
    ///
    /// 抛出的异常**原样带上交易所的错误码与文案**——那些码是有意义的
    /// （-2019 保证金不足、-4164 名义太小、-1021 时间戳不同步），
    /// 翻译成自造的描述只会让排查变难。
    /// </summary>
    public static T OrThrow<T>(HttpResult<T> result, string what)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Success)
        {
            return result.Data;
        }

        throw Failure(result, what);
    }

    /// <summary>REST 的一次失败 → 异常。</summary>
    public static BinanceApiException Failure(HttpResult result, string what)
    {
        ArgumentNullException.ThrowIfNull(result);

        return Build(
            result.Error?.ErrorCode,
            result.Error?.Code,
            result.Error?.ErrorDescription,
            result.Error?.Message,
            result.RequestUrl ?? what,
            (int?)result.ResponseStatusCode ?? 0);
    }

    /// <summary>WebSocket 订阅的一次失败 → 异常（订阅失败没有 HTTP 状态码）。</summary>
    public static BinanceApiException Failure(WebSocketResult result, string what)
    {
        ArgumentNullException.ThrowIfNull(result);

        return Build(
            result.Error?.ErrorCode,
            result.Error?.Code,
            result.Error?.ErrorDescription,
            result.Error?.Message,
            what,
            0);
    }

    /// <summary>
    /// 把一次失败拼成异常。库把"币安说了什么"拆在几个字段里（码有字符串与数字两版、
    /// 文案有描述与消息两处），这里按**实测**取用：
    /// 业务错误（如 -2014 无效 key）落在 <c>ErrorDescription</c>，HTTP 层的兜底说明落在
    /// <c>Message</c>——所以优先取描述、没有才退到消息，代码与文案因此都是上游原文。
    /// </summary>
    private static BinanceApiException Build(
        string? errorCode,
        int? numericCode,
        string? description,
        string? message,
        string endpoint,
        int status)
    {
        string code = errorCode is { Length: > 0 }
            ? errorCode
            : numericCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";

        string text = !string.IsNullOrEmpty(description) ? description : message ?? string.Empty;

        return new BinanceApiException(
            endpoint,
            code,
            text.Length == 0 ? "上游没有给出原因" : text,
            status);
    }

    private static BinanceCredentials CredentialsOf(ExchangeConfig config) =>
        new(
            config.ApiKey ?? throw new InvalidOperationException(
                $"「{config.DisplayName}」没有 API Key，无法调用需要签名的接口。"),
            config.ApiSecret ?? throw new InvalidOperationException(
                $"「{config.DisplayName}」没有 API Secret，无法签名。"));
}
