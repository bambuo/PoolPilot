using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 手动平仓的**订单形状**。
///
/// 这里验的是这件事：一笔手动平仓发出去的参数，必须恰好是"减仓"而不是"反向开仓"。
/// 双向持仓模式下这两者的差别只在 <c>positionSide</c> 上（平多 = 卖出 + LONG，
/// 开空 = 卖出 + SHORT），写错一个字段就会把平仓变成反手开新仓，而且是真钱。
///
/// 真正下单不做自动化测试（那是花钱的），所以这里把下单**请求**截下来断言：
/// 意图、方向、持仓侧、数量量化、以及流水里标明 manual。
/// </summary>
[TestClass]
public sealed class CloseLivePositionTests
{
    private static readonly long FollowerId = 8;

    [TestMethod]
    public async Task ClosePosition_LongInHedgeMode_SellsTheLongSide()
    {
        Harness harness = new();
        harness.Broker.Account = Account(hedge: true, ("ZECUSDT", "LONG", 0.013m));

        await harness.CloseAsync("ZEC", "USDT", pauseFollower: false);

        BrokerOrderRequest sent = harness.Broker.Sent.Single();
        Assert.AreEqual(OrderSide.Sell, sent.Side, "平多 = 卖出");
        Assert.AreEqual("LONG", sent.PositionSide, "双向模式下 positionSide 指的是**要影响哪一侧的持仓**");
        Assert.IsFalse(sent.ReduceOnly, "双向模式带 reduceOnly 会被币安拒单");
        Assert.AreEqual("ZEC", sent.BaseAsset);
        Assert.AreEqual("USDT", sent.QuoteAsset);
    }

    [TestMethod]
    public async Task ClosePosition_ShortInHedgeMode_BuysTheShortSide()
    {
        Harness harness = new();
        harness.Broker.Account = Account(hedge: true, ("ZECUSDT", "SHORT", 0.013m));

        await harness.CloseAsync("ZEC", "USDT", pauseFollower: false);

        BrokerOrderRequest sent = harness.Broker.Sent.Single();
        Assert.AreEqual(OrderSide.Buy, sent.Side, "平空 = 买入");
        Assert.AreEqual("SHORT", sent.PositionSide);
    }

    [TestMethod]
    public async Task ClosePosition_OneWayMode_UsesReduceOnlyAndSignedAmount()
    {
        // 单向模式：positionSide 恒为 BOTH，方向藏在 amount 的符号里。
        // 曾经只看 positionSide，于是单向账户的每个空头都会被当成多头
        Harness harness = new();
        harness.Broker.Account = Account(hedge: false, ("ZECUSDT", "BOTH", -0.013m));

        await harness.CloseAsync("ZEC", "USDT", pauseFollower: false);

        BrokerOrderRequest sent = harness.Broker.Sent.Single();
        Assert.AreEqual(OrderSide.Buy, sent.Side, "负数金额 = 空头，平空要买入");
        Assert.IsNull(sent.PositionSide, "单向模式不带 positionSide");
        Assert.IsTrue(sent.ReduceOnly, "单向模式用 reduceOnly 表达只减不增");
    }

    [TestMethod]
    public async Task ClosePosition_QuantizesTheQuantityToTheStepSize()
    {
        Harness harness = new();
        harness.Broker.Account = Account(hedge: true, ("ZECUSDT", "LONG", 0.0137m));

        await harness.CloseAsync("ZEC", "USDT", pauseFollower: false);

        Assert.AreEqual(0.013m, harness.Broker.Sent.Single().Quantity, "数量按步长向下量化");
    }

    [TestMethod]
    public async Task ClosePosition_RecordsTheOrderAsManual()
    {
        Harness harness = new();
        harness.Broker.Account = Account(hedge: true, ("ZECUSDT", "LONG", 0.013m));

        await harness.CloseAsync("ZEC", "USDT", pauseFollower: false);

        LiveOrder recorded = harness.Orders.Rows.Single();
        Assert.AreEqual(LiveOrderTrigger.Manual, recorded.Trigger, "流水必须能回答「这一笔是谁的决定」");
        Assert.AreEqual("close_long", recorded.Intent);
        Assert.AreEqual(LiveOrderStatus.Placed, recorded.Status);
        Assert.IsNotNull(recorded.VenueOrderId);
    }

    [TestMethod]
    public async Task ClosePosition_WithoutThatPosition_RefusesWithoutSending()
    {
        Harness harness = new();
        harness.Broker.Account = Account(hedge: true, ("BTCUSDT", "LONG", 0.01m));

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.CloseAsync("ZEC", "USDT", pauseFollower: false));

        Assert.Contains("没有", error.Message);
        Assert.IsEmpty(harness.Broker.Sent, "没有持仓就不该发出任何单");
        Assert.IsEmpty(harness.Orders.Rows);
    }

    [TestMethod]
    public async Task ClosePosition_WithTheMasterSwitchOff_IsRefused()
    {
        Harness harness = new();
        harness.Limits.Enabled = false;
        harness.Broker.Account = Account(hedge: true, ("ZECUSDT", "LONG", 0.013m));

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.CloseAsync("ZEC", "USDT", pauseFollower: false));

        Assert.Contains("总开关", error.Message, "要说清是哪个开关挡住了，而不是只说不行");
        Assert.IsEmpty(harness.Broker.Sent);
    }

    [TestMethod]
    public async Task ClosePosition_WhenAsked_PausesTheFollowerToo()
    {
        // 不暂停的话，下一次对账会按池子仓位把这个仓位重新建起来，
        // 使用者点了"平仓"却看到仓位自己回来了
        Harness harness = new();
        harness.Broker.Account = Account(hedge: true, ("ZECUSDT", "LONG", 0.013m));

        await harness.CloseAsync("ZEC", "USDT", pauseFollower: true);

        Assert.IsFalse(harness.Followers.Stored!.IsActive, "跟单员应被暂停");
        Assert.AreEqual(1, harness.Sync.Requests);
    }

    [TestMethod]
    public async Task ClosePosition_WhenRejectedByTheExchange_ReturnsTheReasonInsteadOfThrowing()
    {
        Harness harness = new();
        harness.Broker.Account = Account(hedge: true, ("ZECUSDT", "LONG", 0.013m));
        harness.Broker.FailWith = "币安拒单：-2019 保证金不足";

        // 不抛：抛出去会被中间件吞成"服务内部错误"，而拒单原因正是使用者要看的东西
        LiveOrderDto result = await harness.CloseAsync("ZEC", "USDT", pauseFollower: false);

        Assert.AreEqual(LiveOrderStatus.Failed, result.Status);
        Assert.Contains("-2019", result.Reason ?? string.Empty);

        LiveOrder recorded = harness.Orders.Rows.Single();
        Assert.AreEqual(LiveOrderStatus.Failed, recorded.Status);
        Assert.Contains("-2019", recorded.Reason ?? string.Empty, "交易所的错误码要原样留痕");
    }

    // ── 测试台 ──────────────────────────────────────────────

    private static BrokerAccount Account(
        bool hedge,
        params (string Symbol, string Side, decimal Size)[] positions)
    {
        List<BrokerPosition> rows = [];

        foreach ((string symbol, string side, decimal size) in positions)
        {
            rows.Add(new BrokerPosition(
                Symbol: symbol,
                PositionSide: side,
                Size: size,
                EntryPrice: 1366m,
                MarkPrice: 1366m,
                Leverage: 10,
                LiquidationPrice: 0m,
                UnrealizedPnl: 0m,
                Notional: size * 1366m));
        }

        return new BrokerAccount(
            WalletBalance: 181m,
            AvailableBalance: 179m,
            UnrealizedPnl: 0m,
            IsHedgeMode: hedge,
            Positions: rows);
    }

    private sealed class Harness
    {
        public RecordingBroker Broker { get; } = new();

        public MemoryOrders Orders { get; } = new();

        public FakeFollowers Followers { get; } = new();

        public FakeSync Sync { get; } = new();

        public LiveTradingLimits Limits { get; } = new() { Enabled = true, Leverage = 10 };

        private static readonly ExchangeConfig Config = new()
        {
            Id = 12,
            Code = ExchangeCode.Binance,
            Kind = ExchangeKind.Cex,
            DisplayName = "主账号",
            IsTestnet = false,
            RestBaseUrl = "https://example.invalid",
            WsUrl = "wss://example.invalid",
            TakerFeePercent = 0.05m,
            MakerFeePercent = 0.02m,
            IsEnabled = true,
            Notes = null,
            ApiKey = "k",
            ApiSecret = "s",
            Passphrase = null,
            CreatedAt = LighterTimestamp.FromMilliseconds(0),
        };

        public Task<LiveOrderDto> CloseAsync(string baseAsset, string quoteAsset, bool pauseFollower)
        {
            CloseLivePositionUseCase useCase = new(
                Followers,
                new FakeExchanges(Config),
                new FakeBrokers(Broker),
                new FakeCatalogs(),
                Orders,
                Limits,
                Sync);

            return useCase.ExecuteAsync(
                FollowerId,
                new CloseLivePositionRequest
                {
                    BaseAsset = baseAsset,
                    QuoteAsset = quoteAsset,
                    PauseFollower = pauseFollower,
                },
                CancellationToken.None);
        }
    }

    /// <summary>把发出去的下单请求截下来（真下单不做自动化测试），并按需模拟拒单。</summary>
    private sealed class RecordingBroker : IExchangeBroker
    {
        public List<BrokerOrderRequest> Sent { get; } = [];

        public BrokerAccount Account { get; set; } = new(0m, 0m, 0m, true, []);

        public string? FailWith { get; set; }

        public string Code => "binance";

        public Task<BrokerAccount> GetAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Account);

        public Task<BrokerInstrument> GetInstrumentAsync(
            string baseAsset, string quoteAsset, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BrokerInstrument("ZECUSDT", 3, 2, 0.001m, 0.001m, 5m));

        public Task<decimal> GetPriceAsync(
            string baseAsset, string quoteAsset, CancellationToken cancellationToken = default) =>
            Task.FromResult(1366m);

        public Task<int> EnsureLeverageAsync(
            string baseAsset, string quoteAsset, int leverage, CancellationToken cancellationToken = default) =>
            Task.FromResult(leverage);

        public Task<BrokerOrderResult> PlaceOrderAsync(
            BrokerOrderRequest order, CancellationToken cancellationToken = default)
        {
            Sent.Add(order);

            if (FailWith is { } message)
            {
                throw new InvalidOperationException(message);
            }

            return Task.FromResult(new BrokerOrderResult("ZECUSDT", 808794382166, order.ClientOrderId, "FILLED", order.Quantity, 1366m));
        }

        public Task ValidateOrderAsync(BrokerOrderRequest order, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class MemoryOrders : ILiveOrderRepository
    {
        public List<LiveOrder> Rows { get; } = [];

        public Task AddAsync(LiveOrder order, CancellationToken cancellationToken = default)
        {
            Rows.Add(order with { Id = Rows.Count + 1 });

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LiveOrder>> ListAsync(
            long followerId, int limit = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LiveOrder>>([.. Rows]);

        public Task<(int Placed, int NotFollowed)> CountAsync(
            long followerId, CancellationToken cancellationToken = default) =>
            Task.FromResult((Rows.Count(r => r.Status == LiveOrderStatus.Placed), 0));

        public Task<IReadOnlyList<string>> SourcesForAsync(
            long followerId, string targetBase, string targetQuote, int limit = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeFollowers : IFollowerRepository
    {
        public Follower? Stored { get; private set; } = new()
        {
            Id = FollowerId,
            LeadAccountIndex = AccountIndex.From(281474976488856L),
            InitialCapital = UsdAmount.From(50m),
            Mode = CopyMode.Live,
            ExchangeConfigId = 12,
            IsActive = true,
            CreatedAt = LighterTimestamp.FromMilliseconds(0),
        };

        public Task<Follower?> GetAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Stored);

        public Task<IReadOnlyList<Follower>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Follower>>([Stored!]);

        public Task<IReadOnlyList<Follower>> ListByLeadAsync(
            AccountIndex leadAccountIndex, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Follower>>([Stored!]);

        public Task<Follower> CreateAsync(Follower follower, CancellationToken cancellationToken = default) =>
            Task.FromResult(follower);

        public Task UpdateAsync(Follower follower, CancellationToken cancellationToken = default)
        {
            Stored = follower;

            return Task.CompletedTask;
        }

        public Task SetOwnerCredentialAsync(
            long id,
            string ownerCredentialHash,
            CancellationToken cancellationToken = default)
        {
            // 这一组用例不建公开行；照接口的形状实现，回填落在它自己的行上
            if (Stored is { } stored && stored.Id == id)
            {
                Stored = stored with { OwnerCredentialHash = ownerCredentialHash };
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeSync : ILeadTraderSync
    {
        public int Requests { get; private set; }

        public Task RequestSyncAsync(CancellationToken cancellationToken = default)
        {
            Requests++;

            return Task.CompletedTask;
        }
    }

    private sealed class FakeExchanges(ExchangeConfig config) : IExchangeConfigRepository
    {
        public Task<IReadOnlyList<ExchangeConfig>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExchangeConfig>>([config]);

        public Task<ExchangeConfig?> GetAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExchangeConfig?>(config);

        public Task<ExchangeConfig> CreateAsync(ExchangeConfig entity, CancellationToken cancellationToken = default) =>
            Task.FromResult(entity);

        public Task UpdateAsync(ExchangeConfig entity, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeBrokers(IExchangeBroker broker) : IExchangeBrokerFactory
    {
        public IExchangeBroker? Create(ExchangeConfig config) => broker;
    }

    /// <summary>只认币安的目录：ZECUSDT ↔ ZEC/USDT。</summary>
    private sealed class FakeCatalogs : IExchangeSymbolCatalogFactory
    {
        public IReadOnlyList<ExchangeCode> SupportedPlatforms { get; } = [ExchangeCode.Binance];

        public IExchangeSymbolCatalog? Create(ExchangeCode platform) =>
            platform == ExchangeCode.Binance ? new FakeCatalog() : null;

        private sealed class FakeCatalog : IExchangeSymbolCatalog
        {
            public string Code => "binance";

            public string Coverage => "测试目录";

            public Task<IReadOnlyList<ExchangeSymbolInfo>> ListAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ExchangeSymbolInfo>>(
                [
                    new ExchangeSymbolInfo("ZECUSDT", "ZEC", "USDT", "PERPETUAL", IsTradFi: false, 5m, 0.001m, 3),
                    new ExchangeSymbolInfo("BTCUSDT", "BTC", "USDT", "PERPETUAL", IsTradFi: false, 5m, 0.001m, 3),
                ]);

            public Task<IReadOnlyDictionary<ExchangeSymbolInfo, decimal>> PricesAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyDictionary<ExchangeSymbolInfo, decimal>>(
                    new Dictionary<ExchangeSymbolInfo, decimal>());
        }
    }
}
