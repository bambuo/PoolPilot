using Microsoft.Extensions.Logging;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;

namespace PoolPilot.Infrastructure.Brokers;

/// <summary>
/// 按配置造券商实例。
///
/// 目前只有币安 U 本位合约一家——**不支持的平台返回 <c>null</c> 而不是抛异常**：
/// 界面上列着四家，其中三家（Lighter/OKX/Gate）还没有执行侧实现，
/// "这家还不能下单"是一句正常的回答，不该让调用方去捕获异常来发现。
/// </summary>
/// <summary>
/// 按**平台符号**造合约目录。
///
/// 参数是平台而不是配置：映射是平台级的知识，查合约清单不该要求"你在那个平台已经配了一条"。
/// 地址取该平台的公开主网端点（见 <see cref="PublicRestEndpoint"/>）——目录是公开数据，不需要凭据。
/// </summary>
public sealed class ExchangeSymbolCatalogFactory(
    IHttpClientFactory httpFactory,
    ILoggerFactory loggerFactory) : IExchangeSymbolCatalogFactory
{
    /// <summary>系统支持（即"有目录实现"）的平台。**这就是映射页选择器上的那一列**。</summary>
    public IReadOnlyList<ExchangeCode> SupportedPlatforms { get; } =
        [ExchangeCode.Binance, ExchangeCode.Okx, ExchangeCode.Gate];

    public IExchangeSymbolCatalog? Create(ExchangeCode platform)
    {
        if (PublicRestEndpoint(platform) is not { } endpoint)
        {
            // 还没有这个平台的目录实现。返回 null 而不是抛异常：
            // "这家还不能扫"是一句正常的回答，调用方据此给出可读的提示
            return null;
        }

        HttpClient http = httpFactory.CreateClient(nameof(ExchangeSymbolCatalogFactory));
        http.BaseAddress = endpoint;

        return platform switch
        {
            ExchangeCode.Binance => new BinanceSymbolCatalog(http, loggerFactory.CreateLogger<BinanceSymbolCatalog>()),
            ExchangeCode.Okx => new OkxSymbolCatalog(http, loggerFactory.CreateLogger<OkxSymbolCatalog>()),
            ExchangeCode.Gate => new GateSymbolCatalog(http, loggerFactory.CreateLogger<GateSymbolCatalog>()),
            _ => null,
        };
    }

    /// <summary>
    /// 各平台的公开主网接口地址；没有实现的平台返回 null。
    ///
    /// 这是**后端侧**的一份端点表（前端预设里那份是用来预填配置表单的，两者用途不同）：
    /// 平台级的操作（查合约清单）不该依赖某条配置存在，所以需要这里的默认端点。
    /// </summary>
    internal static Uri? PublicRestEndpoint(ExchangeCode platform) => platform switch
    {
        ExchangeCode.Binance => new Uri("https://fapi.binance.com"),
        ExchangeCode.Okx => new Uri("https://www.okx.com"),
        ExchangeCode.Gate => new Uri("https://api.gateio.ws"),
        _ => null,
    };
}

public sealed class ExchangeBrokerFactory(
    IHttpClientFactory httpFactory,
    ILoggerFactory loggerFactory) : IExchangeBrokerFactory
{
    public IExchangeBroker? Create(ExchangeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Code != ExchangeCode.Binance)
        {
            return null;
        }

        if (config.ApiKey is null || config.ApiSecret is null)
        {
            return null;
        }

        // 测试网与主网地址不同：这条不能猜，配置里存了就用配置的
        HttpClient http = httpFactory.CreateClient(nameof(ExchangeBrokerFactory));
        http.BaseAddress = new Uri(
            config.IsTestnet
                ? "https://testnet.binancefuture.com"
                : config.RestBaseUrl ?? "https://fapi.binance.com");

        return new BinanceFuturesBroker(http, config, loggerFactory.CreateLogger<BinanceFuturesBroker>());
    }
}
