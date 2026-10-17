using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 跟单员订阅关系的用例测试。
///
/// 这里锁住两条**产品规则**（它们不是实现细节，改起来必须是有意识的）：
/// <list type="number">
///   <item>**跟单员订阅的是带单员，不是池子**。所以没有带单员时必须拒绝，
///     而不是顺手替使用者建一个观察者——那样界面上会冒出一个他没创建过的东西，
///     「我到底在跟谁」也就说不清了。</item>
///   <item>**配置写完要立刻让运行期跟上**。不喊那一声的话，页面会在十几秒里
///     显示"已启用但未连接"，看起来像连接出了问题。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class FollowerSubscriptionTests
{
    private static readonly AccountIndex Pool = AccountIndex.From(281474976552443L);

    [TestMethod]
    public async Task CreateFollower_WithoutLeadTrader_IsRejectedAndExplainsWhere()
    {
        FakeFollowerRepository followers = new();
        FakeLeadTraderRepository leads = new();
        FakeSync sync = new();

        CreateFollowerUseCase useCase = new(followers, leads, new FakeExchangeRepository(), new FakeBrokerFactory(), Live(), sync);

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            useCase.ExecuteAsync(
                new Dtos.CreateFollowerRequest { LeadAccountIndex = Pool.Value, InitialCapital = 500m },
                CancellationToken.None));

        Assert.Contains("带单员", error.Message, "错误信息要指向正确的下一步，而不是只说不行");
        Assert.AreEqual(0, followers.Created, "被拒绝时不该写入任何跟单员");
        Assert.AreEqual(0, sync.Requests);
    }

    [TestMethod]
    public async Task CreateFollower_WithExistingLeadTrader_CreatesAndSyncsImmediately()
    {
        FakeFollowerRepository followers = new();
        FakeLeadTraderRepository leads = new();
        await leads.UpsertAsync(Lead(), CancellationToken.None);
        FakeSync sync = new();

        CreateFollowerUseCase useCase = new(followers, leads, new FakeExchangeRepository(), new FakeBrokerFactory(), Live(), sync);

        FollowerDto dto = await useCase.ExecuteAsync(
            new Dtos.CreateFollowerRequest { LeadAccountIndex = Pool.Value, InitialCapital = 750m },
            CancellationToken.None);

        Assert.AreEqual(750m, dto.InitialCapital);
        Assert.AreEqual("paper", dto.Mode);
        Assert.IsTrue(dto.IsActive, "创建即跟随：没有「建了但先不跟」的中间态");
        Assert.AreEqual(1, followers.Created);
        Assert.AreEqual(1, sync.Requests, "写完配置要立刻同步运行期");
    }

    [TestMethod]
    public async Task CreateFollower_WithLiveMode_IsRejectedWithTheMissingCapability()
    {
        FakeFollowerRepository followers = new();
        FakeLeadTraderRepository leads = new();
        await leads.UpsertAsync(Lead(), CancellationToken.None);

        CreateFollowerUseCase useCase = new(followers, leads, new FakeExchangeRepository(), new FakeBrokerFactory(), Live(), new FakeSync());

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            useCase.ExecuteAsync(
                new Dtos.CreateFollowerRequest
                {
                    LeadAccountIndex = Pool.Value,
                    InitialCapital = 500m,
                    Mode = "live",
                },
                CancellationToken.None));

        Assert.Contains("交易所账号", error.Message, "说清缺什么，而不是只说「不支持」");
        Assert.AreEqual(0, followers.Created);
    }

    [TestMethod]
    public async Task CreateFollower_LiveWithAnExchangeAccount_IsCreatedAndBoundToIt()
    {
        FakeFollowerRepository followers = new();
        FakeLeadTraderRepository leads = new();
        await leads.UpsertAsync(Lead(), CancellationToken.None);
        FakeExchangeRepository exchanges = new();
        exchanges.Rows.Add(Exchange(id: 7, code: ExchangeCode.Binance));

        CreateFollowerUseCase useCase = new(
            followers, leads, exchanges, new FakeBrokerFactory(), Live(), new FakeSync());

        FollowerDto dto = await useCase.ExecuteAsync(
            new Dtos.CreateFollowerRequest
            {
                LeadAccountIndex = Pool.Value,

                // 替身账户的可用保证金是 180：本金要在它以内，否则会被上限检查挡下
                InitialCapital = 120m,
                Mode = "live",
                ExchangeConfigId = 7,
            },
            CancellationToken.None);

        Assert.AreEqual("live", dto.Mode);
        Assert.AreEqual(7L, dto.ExchangeConfigId, "实盘跟单必须记住它落在哪个账号上");
        Assert.AreEqual(7L, followers.Last?.ExchangeConfigId);
    }

    [TestMethod]
    public async Task CreateFollower_LiveOnAPlatformWithoutAnExecutor_IsRejectedBeforeItExists()
    {
        FakeFollowerRepository followers = new();
        FakeLeadTraderRepository leads = new();
        await leads.UpsertAsync(Lead(), CancellationToken.None);
        FakeExchangeRepository exchanges = new();
        exchanges.Rows.Add(Exchange(id: 9, code: ExchangeCode.Lighter));

        // 只认币安的券商工厂：Lighter 建出来会"看着在跑、一单都发不出去"
        CreateFollowerUseCase useCase = new(
            followers, leads, exchanges, new FakeBrokerFactory(), Live(), new FakeSync());

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            useCase.ExecuteAsync(
                new Dtos.CreateFollowerRequest
                {
                    LeadAccountIndex = Pool.Value,
                    InitialCapital = 500m,
                    Mode = "live",
                    ExchangeConfigId = 9,
                },
                CancellationToken.None));

        Assert.Contains("执行侧", error.Message);
        Assert.AreEqual(0, followers.Created);
    }

    [TestMethod]
    public async Task CreateFollower_LiveWithCapitalAboveAvailableMargin_IsRejected()
    {
        // 实盘的本金是规模比例的分母：填一个账户开不出来的数，跟单会一路开仓一路被拒
        // （-2019），而使用者看到的是"跟单没跟上"，不是"本金填错了"
        FakeFollowerRepository followers = new();
        FakeLeadTraderRepository leads = new();
        await leads.UpsertAsync(Lead(), CancellationToken.None);
        FakeExchangeRepository exchanges = new();
        exchanges.Rows.Add(Exchange(id: 7, code: ExchangeCode.Binance));

        CreateFollowerUseCase useCase = new(
            followers, leads, exchanges, new FakeBrokerFactory(), Live(), new FakeSync());

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            useCase.ExecuteAsync(
                new Dtos.CreateFollowerRequest
                {
                    LeadAccountIndex = Pool.Value,
                    InitialCapital = 500m,
                    Mode = "live",
                    ExchangeConfigId = 7,
                },
                CancellationToken.None));

        Assert.Contains("可用保证金", error.Message);
        Assert.Contains("500.00", error.Message, "要说清你填了多少");
        Assert.Contains("180.00", error.Message, "也要说清账号里有多少");
        Assert.AreEqual(0, followers.Created, "被拒时不该写入任何跟单员");
    }

    [TestMethod]
    public async Task CreateFollower_LiveWithCapitalWithinMargin_IsCreated()
    {
        FakeFollowerRepository followers = new();
        FakeLeadTraderRepository leads = new();
        await leads.UpsertAsync(Lead(), CancellationToken.None);
        FakeExchangeRepository exchanges = new();
        exchanges.Rows.Add(Exchange(id: 7, code: ExchangeCode.Binance));

        CreateFollowerUseCase useCase = new(
            followers, leads, exchanges, new FakeBrokerFactory(), Live(), new FakeSync());

        FollowerDto dto = await useCase.ExecuteAsync(
            new Dtos.CreateFollowerRequest
            {
                LeadAccountIndex = Pool.Value,
                InitialCapital = 120m,
                Mode = "live",
                ExchangeConfigId = 7,
            },
            CancellationToken.None);

        Assert.AreEqual("live", dto.Mode);
        Assert.AreEqual(120m, dto.InitialCapital);
    }

    [TestMethod]
    public async Task UpdateFollower_Live_RaisingCapitalAboveAvailableMargin_IsRejectedAndKeepsHistory()
    {
        // "重设本金并继续"被拒时**不能清历史**：本金是规模比例的分母，改了本金历史就必须清，
        // 但既然这次改动没被接受，就什么都不该变
        FakeFollowerRepository followers = new();
        await followers.CreateAsync(LiveFollowerWithCapital(50m), CancellationToken.None);

        FakeExchangeRepository exchanges = new();
        exchanges.Rows.Add(Exchange(id: 7, code: ExchangeCode.Binance));

        FakeLedger ledger = new();

        UpdateFollowerUseCase useCase = new(
            followers, ledger, new FakeLeadTraderRepository(), exchanges,
            new FakeBrokerFactory(), new FakeSync());

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            useCase.ExecuteAsync(
                1,
                new Dtos.UpdateFollowerRequest { InitialCapital = 500m, IsActive = true },
                CancellationToken.None));

        Assert.Contains("可用保证金", error.Message);
        Assert.AreEqual(0, ledger.Cleared, "被拒时不该清历史");
        Assert.AreEqual(50m, followers.Last!.InitialCapital.Value, "被拒时本金不该变");
    }

    [TestMethod]
    public async Task UpdateFollower_Live_PausingWithAnOverLimitCapital_StillWorks()
    {
        // 暂停不涉及本金，不该被一次对交易所的网络请求挡住：
        // 否则"我想先暂停一下"会变成"先把本金调到合规"
        FakeFollowerRepository followers = new();
        await followers.CreateAsync(LiveFollowerWithCapital(500m), CancellationToken.None);

        FakeLedger ledger = new();

        UpdateFollowerUseCase useCase = new(
            followers, ledger, new FakeLeadTraderRepository(), new FakeExchangeRepository(),
            new FakeBrokerFactory(), new FakeSync());

        await useCase.ExecuteAsync(
            1,
            new Dtos.UpdateFollowerRequest { InitialCapital = 500m, IsActive = false },
            CancellationToken.None);

        Assert.IsFalse(followers.Last!.IsActive, "暂停要能停下来");
        Assert.AreEqual(0, ledger.Cleared, "本金没变，历史不该被清");
    }

    [TestMethod]
    public async Task CreateFollower_WhenTheMasterSwitchIsOff_RefusesToCreateALiveFollower()
    {
        FakeFollowerRepository followers = new();
        FakeLeadTraderRepository leads = new();
        await leads.UpsertAsync(Lead(), CancellationToken.None);
        FakeExchangeRepository exchanges = new();
        exchanges.Rows.Add(Exchange(id: 7, code: ExchangeCode.Binance));

        CreateFollowerUseCase useCase = new(
            followers, leads, exchanges, new FakeBrokerFactory(), Live(enabled: false), new FakeSync());

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            useCase.ExecuteAsync(
                new Dtos.CreateFollowerRequest
                {
                    LeadAccountIndex = Pool.Value,
                    InitialCapital = 500m,
                    Mode = "live",
                    ExchangeConfigId = 7,
                },
                CancellationToken.None));

        Assert.Contains("总开关", error.Message);
        Assert.AreEqual(0, followers.Created);
    }

    [TestMethod]
    public async Task CreateFollower_WithZeroCapital_IsRejected()
    {
        FakeFollowerRepository followers = new();
        FakeLeadTraderRepository leads = new();
        await leads.UpsertAsync(Lead(), CancellationToken.None);

        CreateFollowerUseCase useCase = new(followers, leads, new FakeExchangeRepository(), new FakeBrokerFactory(), Live(), new FakeSync());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            useCase.ExecuteAsync(
                new Dtos.CreateFollowerRequest { LeadAccountIndex = Pool.Value, InitialCapital = 0m },
                CancellationToken.None));

        Assert.AreEqual(0, followers.Created);
    }

    [TestMethod]
    public async Task DeleteLeadTrader_WithSubscribers_IsRejected()
    {
        // 删掉观察者而把订阅者留在原地，会造出"看着在跟随、其实永远收不到信号"的状态，
        // 而界面上一模一样。所以这里必须硬拦。
        FakeFollowerRepository followers = new();
        FakeLeadTraderRepository leads = new();
        await leads.UpsertAsync(Lead(), CancellationToken.None);
        await followers.CreateAsync(Follower(), CancellationToken.None);

        ConfigureLeadTraderUseCase useCase = new(
            leads,
            followers,
            new EnsureLeadTraderUseCase(leads, new FakePoolsClient()),
            new ListLeadTradersUseCase(leads, followers, new FakeEventRepository(), new FakeWatchState()),
            new FakeSync());

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            useCase.DeleteAsync(Pool, CancellationToken.None));

        Assert.Contains("跟单员", error.Message);
        Assert.AreEqual(1, leads.Rows.Count, "被拒绝时带单员必须还在");
    }

    [TestMethod]
    public async Task DeleteLeadTrader_WithoutSubscribers_RemovesItAndSyncs()
    {
        FakeFollowerRepository followers = new();
        FakeLeadTraderRepository leads = new();
        await leads.UpsertAsync(Lead(), CancellationToken.None);
        FakeSync sync = new();

        ConfigureLeadTraderUseCase useCase = new(
            leads,
            followers,
            new EnsureLeadTraderUseCase(leads, new FakePoolsClient()),
            new ListLeadTradersUseCase(leads, followers, new FakeEventRepository(), new FakeWatchState()),
            sync);

        await useCase.DeleteAsync(Pool, CancellationToken.None);

        Assert.IsEmpty(leads.Rows);
        Assert.AreEqual(1, sync.Requests, "删完要立刻让运行期退订");
    }

    private static LeadTrader Lead() => new()
    {
        AccountIndex = Pool,
        Name = "ENA 池",
        IsEnabled = true,
        CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000),
    };

    private static Follower Follower() => new()
    {
        LeadAccountIndex = Pool,
        InitialCapital = UsdAmount.From(500m),
        Mode = CopyMode.Paper,
        IsActive = true,
        CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000),
    };

    // ── fakes（不用 mock 框架，按项目约定）────────────────

    private sealed class FakeFollowerRepository : IFollowerRepository
    {
        private readonly List<Follower> _rows = [];

        public int Created { get; private set; }

        /// <summary>最后写入的那一条——断言"配置里到底存了什么"，比断言返回的 DTO 更硬。</summary>
        public Follower? Last => _rows.Count > 0 ? _rows[^1] : null;

        public Task<Follower?> GetAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.FirstOrDefault(f => f.Id == id));

        public Task<IReadOnlyList<Follower>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Follower>>([.. _rows]);

        public Task<IReadOnlyList<Follower>> ListByLeadAsync(
            AccountIndex leadAccountIndex,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Follower>>([.. _rows.Where(f => f.LeadAccountIndex == leadAccountIndex)]);

        public Task<Follower> CreateAsync(Follower follower, CancellationToken cancellationToken = default)
        {
            Created++;
            Follower stored = follower with { Id = _rows.Count + 1 };
            _rows.Add(stored);

            return Task.FromResult(stored);
        }

        public Task UpdateAsync(Follower follower, CancellationToken cancellationToken = default)
        {
            // 要真的改：否则"暂停生效了吗""被拒时本金变了吗"都断言不了
            int index = _rows.FindIndex(f => f.Id == follower.Id);

            if (index >= 0)
            {
                _rows[index] = follower;
            }

            return Task.CompletedTask;
        }

        public Task SetOwnerCredentialAsync(
            long id,
            string ownerCredentialHash,
            CancellationToken cancellationToken = default)
        {
            // 匿名公开行的创建者凭证要在建行之后回填（派生的输入之一就是 Id）：
            // 这一组用例不建公开行，但接口形状要如实实现，否则回填这一步没地方落
            int index = _rows.FindIndex(f => f.Id == id);

            if (index >= 0)
            {
                _rows[index] = _rows[index] with { OwnerCredentialHash = ownerCredentialHash };
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
        {
            _rows.RemoveAll(f => f.Id == id);

            return Task.CompletedTask;
        }
    }

    private sealed class FakeLeadTraderRepository : ILeadTraderRepository
    {
        private readonly List<LeadTrader> _rows = [];

        public List<LeadTrader> Rows => _rows;

        public Task<LeadTrader?> GetAsync(AccountIndex account, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.FirstOrDefault(l => l.AccountIndex == account));

        public Task<IReadOnlyList<LeadTrader>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LeadTrader>>([.. _rows]);

        public Task UpsertAsync(LeadTrader lead, CancellationToken cancellationToken = default)
        {
            _rows.RemoveAll(l => l.AccountIndex == lead.AccountIndex);
            _rows.Add(lead);

            return Task.CompletedTask;
        }

        public Task DeleteAsync(AccountIndex account, CancellationToken cancellationToken = default)
        {
            _rows.RemoveAll(l => l.AccountIndex == account);

            return Task.CompletedTask;
        }
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

    private sealed class FakeWatchState : ILeadWatchState
    {
        public bool IsWatching(AccountIndex account) => true;
    }

    private sealed class FakeEventRepository : IPoolEventRepository
    {
        public Task AppendAsync(IReadOnlyList<PoolEvent> events, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PoolEvent>> GetRecentAsync(
            AccountIndex account,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PoolEvent>>([]);

        public Task<IReadOnlyList<PoolEvent>> GetRecentAcrossPoolsAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PoolEvent>>([]);

        public Task<IReadOnlyList<PoolSignalStats>> GetSignalStatsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PoolSignalStats>>([]);
    }

    /// <summary>只提供"创建带单员"需要的那一步（池名解析）；其余一律抛——测试里不该有静默走通的假实现。</summary>
    private sealed class FakePoolsClient : ILighterPoolsClient
    {
        public Task<IReadOnlyList<Pool>> FetchPoolsAsync(
            PoolFilter filter,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Pool>>([]);

        public Task<LighterSystemConfig> FetchSystemConfigAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PoolSnapshot> FetchSnapshotAsync(
            AccountIndex account,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PoolInfo> FetchPoolInfoAsync(
            AccountIndex account,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PoolTrade>> FetchRecentTradesAsync(
            AccountIndex account,
            int limit = 50,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PoolOrder>> FetchOrdersAsync(
            AccountIndex account,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<InstrumentSpec>> FetchInstrumentsAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<OrderBookSnapshot> FetchOrderBookAsync(
            int marketId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static LiveTradingLimits Live(bool enabled = true) => new() { Enabled = enabled, Leverage = 10 };

    private static Follower LiveFollowerWithCapital(decimal capital) => new()
    {
        Id = 1,
        LeadAccountIndex = Pool,
        InitialCapital = UsdAmount.From(capital),
        Mode = CopyMode.Live,
        ExchangeConfigId = 7,
        IsActive = true,
        CreatedAt = LighterTimestamp.FromMilliseconds(0),
    };

    /// <summary>只记"历史被清了没有"——这条规则关心的是被拒时不该有副作用。</summary>
    private sealed class FakeLedger : IFollowerLedgerRepository
    {
        public int Cleared { get; private set; }

        public Task ClearHistoryAsync(long followerId, CancellationToken cancellationToken = default)
        {
            Cleared++;

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PaperFill>> GetFillsAsync(
            long followerId, int limit = 2000, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PaperFill>>([]);

        public Task<IReadOnlyList<PaperEquityPoint>> GetEquityCurveAsync(
            long followerId, int limit = 2000, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PaperEquityPoint>>([]);

        public Task AppendFillsAsync(
            IReadOnlyList<PaperFill> fills, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task AppendEquityPointAsync(
            PaperEquityPoint point, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static ExchangeConfig Exchange(long id, ExchangeCode code) => new()
    {
        Id = id,
        Code = code,
        Kind = ExchangeKind.Cex,
        DisplayName = $"{ExchangeEnumText.ToText(code)} 主账号",
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

    private sealed class FakeExchangeRepository : IExchangeConfigRepository
    {
        public List<ExchangeConfig> Rows { get; } = [];

        public Task<IReadOnlyList<ExchangeConfig>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExchangeConfig>>([.. Rows]);

        public Task<ExchangeConfig?> GetAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));

        public Task<ExchangeConfig> CreateAsync(ExchangeConfig config, CancellationToken cancellationToken = default)
        {
            Rows.Add(config);

            return Task.FromResult(config);
        }

        public Task UpdateAsync(ExchangeConfig config, CancellationToken cancellationToken = default)
        {
            Rows.RemoveAll(r => r.Id == config.Id);
            Rows.Add(config);

            return Task.CompletedTask;
        }

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
        {
            Rows.RemoveAll(r => r.Id == id);

            return Task.CompletedTask;
        }
    }

    /// <summary>只认币安的券商工厂，行为与生产实现一致：别的平台返回 null。</summary>
    private sealed class FakeBrokerFactory : IExchangeBrokerFactory
    {
        public IExchangeBroker? Create(ExchangeConfig config) =>
            config.Code == ExchangeCode.Binance ? new NoopBroker() : null;

        private sealed class NoopBroker : IExchangeBroker
        {
            public string Code => "binance";

            /// <summary>钱包 200、可用 180：建实盘跟单要核对本金，替身得给得出账户。</summary>
            public Task<BrokerAccount> GetAccountAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new BrokerAccount(200m, 180m, 0m, true, []));

            public Task<BrokerInstrument> GetInstrumentAsync(
                string baseAsset,
                string quoteAsset,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<decimal> GetPriceAsync(
                string baseAsset,
                string quoteAsset,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<int> EnsureLeverageAsync(
                string baseAsset,
                string quoteAsset,
                int leverage,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<BrokerOrderResult> PlaceOrderAsync(
                BrokerOrderRequest order,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task ValidateOrderAsync(
                BrokerOrderRequest order,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
