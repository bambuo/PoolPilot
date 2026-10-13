using Binance.Net;
using Binance.Net.Interfaces.Clients;
using CryptoClients.Net.Clients;

using Microsoft.Extensions.DependencyInjection;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;
using PoolPilot.Infrastructure.Brokers;

namespace PoolPilot.Lighter.Tests;

/// <summary>
/// 币安执行侧的**错误透传**契约测试：用假凭据实调一次账户接口，
/// 断言拿到的是**币安自己的错误码与文案**，而不是被翻译过的一句描述。
///
/// 为什么值得一条真调用：这段映射是"排查的唯一线索"（-2019 保证金不足、
/// -4164 名义太小、-1021 时间戳不同步），它一旦被库的字段名变化打散，
/// 表现是"能跑但说不清为什么失败"——纯逻辑测试看不见这种病。
///
/// 用的是**假凭据**（字符串形状合法但没登记过），所以它只产生一次被拒的只读请求，
/// 不碰任何真账户、不下单。
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class BinanceErrorPassthroughTests
{
    [TestMethod]
    public async Task Probe_WithFakeCredentials_SurfacesUpstreamCodeAndMessage()
    {
        // Arrange：走与线上同一条装配路径（DI + 按账号取客户端 + 工厂造券商）
        using ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .AddCryptoClients(options => options.EnabledExchanges = [CryptoClients.Net.Enums.Exchange.Binance])
            .BuildServiceProvider();

        ExchangeConfig config = new()
        {
            Id = 9_001,
            Code = ExchangeCode.Binance,
            Kind = ExchangeKind.Cex,
            DisplayName = "假凭据契约测试",
            IsTestnet = false,
            MakerFeePercent = 0.02m,
            TakerFeePercent = 0.05m,
            IsEnabled = true,
            CreatedAt = LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            ApiKey = "fake-key-for-error-mapping-0001",
            ApiSecret = "fake-secret-000000000000000000002",
        };

        IExchangeBroker broker = new ExchangeBrokerFactory(
            services.GetRequiredService<IBinanceUserClientProvider>(),
            new StubHttpClientFactory(),
            services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>())
            .Create(config)
            ?? throw new InvalidOperationException("币安配置应当造得出券商。");

        // Act
        BinanceApiException failure = await Assert.ThrowsExactlyAsync<BinanceApiException>(
            () => broker.GetAccountAsync());

        // Assert：码与文案都是币安原文（实测这条假 key 会被判 "API key format invalid"，
        // 码为负数）。**不断言具体是哪个码**——那取决于币安的校验顺序，不是我们要守的东西
        Assert.StartsWith("-", failure.Code, $"错误码应当是币安原文，实际 {failure.Code}");
        Assert.IsTrue(failure.Message.Contains(failure.Code, StringComparison.Ordinal), "文案里应当带上错误码");
        Assert.IsGreaterThan(0, failure.Status, "HTTP 状态码应当带出来");
        Assert.Contains("API key format invalid", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>契约测试用不到 HTTP 工厂（只有 /order/test 那处自研调用用它）。</summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
