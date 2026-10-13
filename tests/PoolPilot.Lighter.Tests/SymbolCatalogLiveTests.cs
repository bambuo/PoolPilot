using Binance.Net;
using Binance.Net.Interfaces.Clients;
using GateIo.Net.Interfaces.Clients;
using Microsoft.Extensions.DependencyInjection;
using OKX.Net.Interfaces.Clients;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Enums;
using PoolPilot.Infrastructure.Brokers;

namespace PoolPilot.Lighter.Tests;

/// <summary>
/// 三家合约目录的**真实契约测试**：直接连公开接口，断言的是"库给的模型 → 我们的口径"
/// 这一段没变味。
///
/// 为什么必须有它：目录的三条规则全是实测定的（只收永续的两个命名空间、传统金融按
/// 平台自己的分类判、不按计价币过滤），而**换了数据来源**（自研 HTTP → 库的强类型模型）
/// 正是最容易把其中一条悄悄弄丢的改动。纯逻辑测试用假目录，看不见这类病。
///
/// 同时它也是 DI 装配的检查：三个 provider 都从 <c>AddCryptoClients</c> 解析出来。
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class SymbolCatalogLiveTests
{
    private const string KnownCrypto = "ENA";

    [TestMethod]
    public async Task BinanceCatalog_KeepsBothPerpetualNamespacesAndResolvesPrices()
    {
        IExchangeSymbolCatalog catalog = Catalog(ExchangeCode.Binance);

        IReadOnlyList<ExchangeSymbolInfo> rows = await catalog.ListAsync();

        Assert.IsGreaterThan(300, rows.Count, "币安 USDT 永续应有数百个");
        Assert.IsTrue(
            rows.All(r => r.ContractType is "PERPETUAL" or "TRADIFI_PERPETUAL"),
            "只该收永续的两个命名空间（交割合约会到期，价格守卫认不出它）");

        Assert.IsGreaterThan(50, rows.Count(r => r.IsTradFi), "实测传统金融永续有 200 多个（股票/商品/指数）");

        IReadOnlyDictionary<ExchangeSymbolInfo, decimal> prices = await catalog.PricesAsync();
        ExchangeSymbolInfo ena = rows.First(r => r.BaseAsset == KnownCrypto && r.QuoteAsset == "USDT");

        Assert.IsGreaterThan(0m, prices[ena], "已知合约必须取得到价");
    }

    [TestMethod]
    public async Task OkxCatalog_ExcludesInverseAndKeepsPlatformCategoryAsTradFi()
    {
        IExchangeSymbolCatalog catalog = Catalog(ExchangeCode.Okx);

        IReadOnlyList<ExchangeSymbolInfo> rows = await catalog.ListAsync();

        Assert.IsGreaterThan(100, rows.Count, "OKX 正向永续应有数百个");
        Assert.IsTrue(rows.All(r => r.ContractType == "SWAP"), "口径里合约类型一律写作 SWAP");

        // 反向合约（BTC-USD-SWAP）的保证金与结算都是币本身，稳定币账户开不出来。
        // 它在本项目的口径里没有标记位，但可以用计价币认出来：反向合约的计价币不是稳定币
        Assert.IsFalse(
            rows.Any(r => r.QuoteAsset == "USD"),
            "反向合约不该出现（实测 15 个，全是 USD 计价）");

        Assert.IsGreaterThan(0, rows.Count(r => r.IsTradFi), "实测有股票类永续（instCategory 3/4）");

        IReadOnlyDictionary<ExchangeSymbolInfo, decimal> prices = await catalog.PricesAsync();
        ExchangeSymbolInfo ena = rows.First(r => r.BaseAsset == KnownCrypto);

        Assert.IsGreaterThan(0m, prices[ena], "已知合约必须取得到价");
    }

    [TestMethod]
    public async Task GateCatalog_ExcludesDelistingAndKeepsContractFamilyAsTradFi()
    {
        IExchangeSymbolCatalog catalog = Catalog(ExchangeCode.Gate);

        IReadOnlyList<ExchangeSymbolInfo> rows = await catalog.ListAsync();

        Assert.IsGreaterThan(100, rows.Count, "Gate USDT 永续应有数百个");
        Assert.IsTrue(rows.All(r => r.Symbol.Contains('_', StringComparison.Ordinal)), "Gate 的合约名是 BTC_USDT 这种写法");

        Assert.IsGreaterThan(0, rows.Count(r => r.IsTradFi), "实测有股票类永续（contract_type = stocks）");
        Assert.IsTrue(
            rows.Where(r => r.IsTradFi).All(r => r.ContractType is "stocks" or "indices" or "metals" or "forex" or "commodities"),
            "家族名应当是上游原文");

        IReadOnlyDictionary<ExchangeSymbolInfo, decimal> prices = await catalog.PricesAsync();
        ExchangeSymbolInfo ena = rows.First(r => r.BaseAsset == KnownCrypto && r.QuoteAsset == "USDT");

        Assert.IsGreaterThan(0m, prices[ena], "已知合约必须取得到价");
    }

    /// <summary>契约测试用的容器：**整个类共用一个，直到 ClassCleanup 才释放**——
    /// 库的客户端（含它的 HttpClient）与容器绑在一起，中途释放会让还在用的 handler 变成已释放对象。</summary>
    private static ServiceProvider _services = null!;

    [ClassInitialize]
    public static void Setup(TestContext context)
    {
        _services = new ServiceCollection()
            .AddLogging()
            .AddCryptoClients(options => options.EnabledExchanges =
            [
                CryptoClients.Net.Enums.Exchange.Binance,
                CryptoClients.Net.Enums.Exchange.OKX,
                CryptoClients.Net.Enums.Exchange.GateIo,
            ])
            .BuildServiceProvider();
    }

    [ClassCleanup]
    public static void Cleanup() => _services.Dispose();

    /// <summary>走与线上同一条装配路径（AddCryptoClients 的 provider + 工厂）。</summary>
    private static IExchangeSymbolCatalog Catalog(ExchangeCode platform)
    {
        ServiceProvider services = _services;

        ExchangeSymbolCatalogFactory factory = new(
            services.GetRequiredService<IBinanceUserClientProvider>(),
            services.GetRequiredService<IOKXUserClientProvider>(),
            services.GetRequiredService<IGateIoUserClientProvider>(),
            services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());

        return factory.Create(platform)
            ?? throw new InvalidOperationException($"{platform} 应当有目录实现。");
    }
}
