using Binance.Net.Interfaces.Clients;
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
///
/// 客户端由库按账号造（见 <see cref="BinanceClients"/>）：同一个账号下的下单、用户流、
/// 标记价共用一份连接与限流；两个账号互不影响。
/// </summary>
public sealed class ExchangeBrokerFactory(
    IBinanceUserClientProvider binance,
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

        return new BinanceFuturesBroker(
            BinanceClients.Rest(binance, config),
            new BinanceOrderTestExtension(httpFactory.CreateClient(nameof(BinanceOrderTestExtension)), config),
            loggerFactory.CreateLogger<BinanceFuturesBroker>());
    }
}

/// <summary>
/// 按**平台符号**造合约目录。
///
/// 参数是平台而不是配置：映射是平台级的知识（"WTI 对应币安的 CL/USDT"），
/// 与使用者在那个平台上有几个账号无关——所以查合约清单也不该要求"你在那个平台已经配了一条"。
/// 目录是公开数据，走库的公开客户端（不需要凭据）。
/// </summary>
public sealed class ExchangeSymbolCatalogFactory(
    IBinanceUserClientProvider binance,
    OKX.Net.Interfaces.Clients.IOKXUserClientProvider okx,
    GateIo.Net.Interfaces.Clients.IGateIoUserClientProvider gate,
    ILoggerFactory loggerFactory) : IExchangeSymbolCatalogFactory
{
    /// <summary>系统支持（即"有目录实现"）的平台。**这就是映射页选择器上的那一列**。</summary>
    public IReadOnlyList<ExchangeCode> SupportedPlatforms { get; } =
        [ExchangeCode.Binance, ExchangeCode.Okx, ExchangeCode.Gate];

    public IExchangeSymbolCatalog? Create(ExchangeCode platform)
    {
        if (!SupportedPlatforms.Contains(platform))
        {
            // 还没有这个平台的目录实现。返回 null 而不是抛异常：
            // "这家还不能扫"是一句正常的回答，调用方据此给出可读的提示
            return null;
        }

        // 目录是公开数据：客户端不带凭据，userId 固定成 public-data——
        // 与"哪条配置"无关（没有配置的人也查得了合约清单）
        return platform switch
        {
            ExchangeCode.Binance => new BinanceSymbolCatalog(
                BinanceClients.PublicRest(binance),
                Binance.Net.BinanceEnvironment.Live,
                loggerFactory.CreateLogger<BinanceSymbolCatalog>()),

            ExchangeCode.Okx => new OkxSymbolCatalog(
                okx.GetRestClient("public-data", null, OKX.Net.OKXEnvironment.Live),
                loggerFactory.CreateLogger<OkxSymbolCatalog>()),

            ExchangeCode.Gate => new GateSymbolCatalog(
                gate.GetRestClient("public-data", null, GateIo.Net.GateIoEnvironment.Live),
                loggerFactory.CreateLogger<GateSymbolCatalog>()),

            _ => null,
        };
    }
}
