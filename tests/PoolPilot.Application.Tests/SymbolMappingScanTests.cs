using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 扫描的"例外表"规则。
///
/// 这条规则是使用者提出的工作方式：**与源同名的标的直接用，不必登记**——
/// 表里只放不一样的那些。它有两个容易写反的地方，所以逐条钉住：
/// <list type="number">
///   <item>同名时**不能**写行（写了就是让人去看、去核对的常识抄本）；</item>
///   <item>同名时**不能**把已有的决定一起收掉（停用的行里有一个"不要跟它"）。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class SymbolMappingScanTests
{
    /// <summary>源侧价格与目标侧一致（同名同物），这是"直接用"的典型情形。</summary>
    [TestMethod]
    public async Task Scan_WhenSourceNameMatchesTarget_RegistersNothingAndSaysSo()
    {
        Harness harness = new()
        {
            Sources = [("BTC", 86000m)],
            Target = [Row("BTCUSDT", "BTC", "USDT", 86050m)],
        };

        SymbolMappingScanResultDto result = await harness.ScanAsync();

        Assert.AreEqual(0, result.Created, "同名的标的不该在表里占一行");
        Assert.AreEqual(1, result.Direct, "它属于「同名直接用」，要报出来，否则看着像漏扫了");
        Assert.AreEqual(0, result.Deleted);
        Assert.IsEmpty(harness.After, "表里不该出现任何行");
    }

    /// <summary>早先的扫描为同名标的留下过行：这次要把它收掉（表在收缩，不是越积越多）。</summary>
    [TestMethod]
    public async Task Scan_WhenEarlierScanLeftARedundantSameNameRow_RemovesIt()
    {
        Harness harness = new()
        {
            Sources = [("BTC", 86000m)],
            Target = [Row("BTCUSDT", "BTC", "USDT", 86050m)],
            Seed = [Mapping("BTC", "BTC", "USDT", origin: SymbolMappingOrigin.Scanned, enabled: true)],
        };

        SymbolMappingScanResultDto result = await harness.ScanAsync();

        Assert.AreEqual(1, result.Deleted);
        Assert.AreEqual(1, result.Direct);
        Assert.IsEmpty(harness.After, "同名行是冗余的，扫描要收掉它");
    }

    /// <summary>
    /// 同名但**停用**的行必须留下：它表达的是"不要跟这个标的"。
    /// 删掉它，这个标的就会退回"同名直接用"——把使用者的决定反过来了。
    /// </summary>
    [TestMethod]
    public async Task Scan_WhenSameNameRowIsDisabled_KeepsItBecauseItIsADecision()
    {
        Harness harness = new()
        {
            Sources = [("BTC", 86000m)],
            Target = [Row("BTCUSDT", "BTC", "USDT", 86050m)],
            Seed = [Mapping("BTC", "BTC", "USDT", origin: SymbolMappingOrigin.Scanned, enabled: false)],
        };

        SymbolMappingScanResultDto result = await harness.ScanAsync();

        Assert.AreEqual(0, result.Deleted);
        Assert.AreEqual(0, result.Direct, "它没有被直接用——它是被停用的");
        Assert.AreEqual(1, result.Kept);
        Assert.HasCount(1, harness.After);
    }

    /// <summary>改了名（WTI 在币安叫 CL）才是真正需要登记的情形。</summary>
    [TestMethod]
    public async Task Scan_WhenTargetHasADifferentName_RegistersThatDifference()
    {
        Harness harness = new()
        {
            Sources = [("WTI", 87.9m)],
            Target = [Row("CLUSDT", "CL", "USDT", 87.98m)],
        };

        SymbolMappingScanResultDto result = await harness.ScanAsync();

        Assert.AreEqual(1, result.Created);
        Assert.AreEqual(0, result.Direct);
        Assert.HasCount(1, harness.After);
        Assert.AreEqual("WTI", harness.After[0].SourceBase);
        Assert.AreEqual("CL", harness.After[0].TargetBase);
    }

    /// <summary>同名但价格差得远：既不登记，也不允许"直接用"，只报出来等人定夺。</summary>
    [TestMethod]
    public async Task Scan_WhenSameNameIsADifferentAsset_ReportsItInsteadOfUsingIt()
    {
        Harness harness = new()
        {
            Sources = [("SPX", 6000m)],
            Target = [Row("SPXUSDT", "SPX", "USDT", 0.43m)],
        };

        SymbolMappingScanResultDto result = await harness.ScanAsync();

        Assert.AreEqual(0, result.Created);
        Assert.AreEqual(0, result.Direct, "价格对不上就不能算「同名直接用」");
        Assert.HasCount(1, result.Failures);
        Assert.AreEqual("SPX", result.Failures[0].SourceBase);
        Assert.IsEmpty(harness.After);
    }

    /// <summary>人工行是人的判断，扫描既不改也不收。</summary>
    [TestMethod]
    public async Task Scan_NeverTouchesAManualRow()
    {
        Harness harness = new()
        {
            Sources = [("BTC", 86000m)],
            Target = [Row("BTCUSDT", "BTC", "USDT", 86050m)],
            Seed = [Mapping("BTC", "BTC", "USDC", origin: SymbolMappingOrigin.Manual, enabled: true)],
        };

        SymbolMappingScanResultDto result = await harness.ScanAsync();

        Assert.AreEqual(1, result.SkippedManual);
        Assert.AreEqual(0, result.Deleted);
        Assert.HasCount(1, harness.After);
        Assert.AreEqual("USDC", harness.After[0].TargetQuote, "人工指定的计价币不能被扫描改掉");
    }

    // ── 测试台 ──────────────────────────────────────────────

    /// <summary>目标平台目录里的一条：合约本身 + 它的现价。</summary>
    private static (ExchangeSymbolInfo Info, decimal Price) Row(
        string symbol,
        string baseAsset,
        string quote,
        decimal price) => (new ExchangeSymbolInfo(
            symbol,
            baseAsset,
            quote,
            "PERPETUAL",
            IsTradFi: false,
            MinNotional: 5m,
            StepSize: 0.01m,
            QuantityPrecision: 2), price);

    private static SymbolMapping Mapping(
        string sourceBase,
        string targetBase,
        string targetQuote,
        SymbolMappingOrigin origin,
        bool enabled) => new()
        {
            Id = 1,
            SourcePlatform = SymbolMapping.SourcePlatformLighter,
            SourceBase = sourceBase,
            SourceQuote = "USDC",
            TargetPlatform = "binance",
            TargetBase = targetBase,
            TargetQuote = targetQuote,
            TargetContractType = "PERPETUAL",
            TargetIsTradFi = false,
            Origin = origin,
            IsEnabled = enabled,
            SourcePrice = 1m,
            TargetPrice = 1m,
            VerifiedAt = null,
            Note = null,
            CreatedAt = LighterTimestamp.FromMilliseconds(0),
        };

    /// <summary>把扫描需要的四样东西拼起来：源、目标目录、表、被观察的带单员。</summary>
    private sealed class Harness
    {
        public required IReadOnlyList<(string Symbol, decimal Price)> Sources { get; init; }

        public required IReadOnlyList<(ExchangeSymbolInfo Info, decimal Price)> Target { get; init; }

        /// <summary>喂给扫描的初始行。</summary>
        public IReadOnlyList<SymbolMapping> Seed { get; init; } = [];

        /// <summary>扫描之后的表。断言看的是它——看种子等于什么都没验。</summary>
        public List<SymbolMapping> After { get; } = [];

        public async Task<SymbolMappingScanResultDto> ScanAsync()
        {
            MemoryMappingRepository repository = new(Seed);

            ScanSymbolMappingsUseCase useCase = new(
                repository,
                new OneLeadRepository(),
                new FixedPoolsClient(Sources),
                new FixedCatalogFactory(Target));

            SymbolMappingScanResultDto result = await useCase.ExecuteAsync("binance", CancellationToken.None);

            After.Clear();
            After.AddRange(repository.Rows);

            return result;
        }
    }

    private sealed class MemoryMappingRepository(IReadOnlyList<SymbolMapping> seed) : ISymbolMappingRepository
    {
        private readonly List<SymbolMapping> _rows = [.. seed];

        public IReadOnlyList<SymbolMapping> Rows => _rows;

        public Task<IReadOnlyList<SymbolMapping>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SymbolMapping>>([.. _rows]);

        public Task<SymbolMapping?> GetAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.FirstOrDefault(r => r.Id == id));

        public Task<SymbolMapping?> FindAsync(
            string targetPlatform,
            string sourceBase,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.FirstOrDefault(r =>
                r.TargetPlatform == targetPlatform && r.SourceBase == sourceBase));

        public Task<SymbolMapping> CreateAsync(SymbolMapping mapping, CancellationToken cancellationToken = default)
        {
            _rows.Add(mapping with { Id = _rows.Count + 1 });

            return Task.FromResult(mapping);
        }

        public Task UpdateAsync(SymbolMapping mapping, CancellationToken cancellationToken = default)
        {
            int index = _rows.FindIndex(r => r.Id == mapping.Id);

            if (index >= 0)
            {
                _rows[index] = mapping;
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
        {
            _rows.RemoveAll(r => r.Id == id);

            return Task.CompletedTask;
        }
    }

    private sealed class OneLeadRepository : ILeadTraderRepository
    {
        public Task<LeadTrader?> GetAsync(AccountIndex account, CancellationToken cancellationToken = default) =>
            Task.FromResult<LeadTrader?>(null);

        public Task<IReadOnlyList<LeadTrader>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LeadTrader>>(
            [
                new LeadTrader
                {
                    AccountIndex = AccountIndex.From(1),
                    Name = "测试池",
                    IsEnabled = true,
                    CreatedAt = LighterTimestamp.FromMilliseconds(0),
                },
            ]);

        public Task UpsertAsync(LeadTrader lead, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(AccountIndex account, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedPoolsClient(IReadOnlyList<(string Symbol, decimal Price)> positions) : ILighterPoolsClient
    {
        public Task<IReadOnlyList<Pool>> FetchPoolsAsync(PoolFilter filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LighterSystemConfig> FetchSystemConfigAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PoolSnapshot> FetchSnapshotAsync(AccountIndex account, CancellationToken cancellationToken = default)
        {
            int marketId = 1;

            return Task.FromResult(new PoolSnapshot
            {
                AccountIndex = account,
                TakenAt = LighterTimestamp.FromMilliseconds(0),
                TotalAssetValue = UsdAmount.From(0m),
                Collateral = UsdAmount.From(0m),
                AvailableBalance = UsdAmount.From(0m),
                InitialMarginRequirement = UsdAmount.From(0m),
                MaintenanceMarginRequirement = UsdAmount.From(0m),
                Positions =
                [
                    .. positions.Select(p => new PoolPosition
                    {
                        MarketId = marketId++,
                        Symbol = p.Symbol,
                        Side = PositionSide.Long,
                        Size = 1m,
                        AvgEntryPrice = PriceAmount.From(p.Price),
                        Notional = UsdAmount.From(p.Price),
                        UnrealizedPnl = UsdAmount.From(0m),
                        CumulativeFunding = UsdAmount.From(0m),
                        OpenOrderCount = 0,
                    }),
                ],
                Orders = [],
            });
        }

        public Task<PoolInfo> FetchPoolInfoAsync(AccountIndex account, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PoolTrade>> FetchRecentTradesAsync(
            AccountIndex account,
            int limit = 50,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PoolOrder>> FetchOrdersAsync(
            AccountIndex account,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<InstrumentSpec>> FetchInstrumentsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OrderBookSnapshot> FetchOrderBookAsync(int marketId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedCatalogFactory(
        IReadOnlyList<(ExchangeSymbolInfo Info, decimal Price)> rows) : IExchangeSymbolCatalogFactory
    {
        public IReadOnlyList<ExchangeCode> SupportedPlatforms { get; } = [ExchangeCode.Binance];

        public IExchangeSymbolCatalog? Create(ExchangeCode platform) =>
            platform == ExchangeCode.Binance ? new FixedCatalog(rows) : null;

        private sealed class FixedCatalog(
            IReadOnlyList<(ExchangeSymbolInfo Info, decimal Price)> rows) : IExchangeSymbolCatalog
        {
            public string Code => "binance";

            public string Coverage => "测试目录";

            public Task<IReadOnlyList<ExchangeSymbolInfo>> ListAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ExchangeSymbolInfo>>([.. rows.Select(r => r.Info)]);

            public Task<IReadOnlyDictionary<ExchangeSymbolInfo, decimal>> PricesAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyDictionary<ExchangeSymbolInfo, decimal>>(
                    rows.ToDictionary(row => row.Info, row => row.Price));
        }
    }
}
