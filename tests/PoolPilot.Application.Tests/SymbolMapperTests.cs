using PoolPilot.Application.Abstractions;
using PoolPilot.Application.UseCases;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 跨场所标的映射的测试。
///
/// 这里挡的是**会买错东西**的错误，而不是"少跟一笔"：
/// 映射错了会把仓位开在另一个资产上，而界面上显示的合约代码看起来完全正常。
/// 所以断言集中在两处：同名但不同物必须被拒、别名必须经价格确认才生效。
/// </summary>
[TestClass]
public sealed class SymbolMapperTests
{
    [TestMethod]
    public void Resolve_WhenPriceMatches_MapsToTheSameSymbol()
    {
        SymbolMapper.Resolution result = SymbolMapper.Resolve(
            "ENA",
            lighterPrice: 0.2447m,
            candidates: [Symbol("ENAUSDT", "PERPETUAL", minNotional: 5m)],
            priceOf: _ => 0.2451m,
            venue: "币安",
            coverage: "加密永续与传统金融永续（股票、商品、指数）");

        Assert.IsTrue(result.IsMapped);
        Assert.AreEqual("ENA", result.Base);
        Assert.AreEqual("USDT", result.Quote);
        Assert.AreEqual("PERPETUAL", result.ContractType);
        Assert.AreEqual(5m, result.MinNotional);

        // 结果里**不带平台合约代码**：解析只回答"是哪个资产、哪个计价币"
        // （价格一并带出来，省得调用方拿着结果再回对接实现问一次价）
        Assert.AreEqual(0.2451m, result.Price);
    }

    [TestMethod]
    public void Resolve_WhenSameNameButDifferentAsset_IsRefused()
    {
        // 实测过的真实形态：交易所常有同名不同物的两个标的。
        // 价格量级检查是唯一能自动发现的信号——差得远就该拒，而不是照名字下过去
        SymbolMapper.Resolution result = SymbolMapper.Resolve(
            "SPX",
            lighterPrice: 6000m,
            candidates: [Symbol("SPXUSDT", "PERPETUAL", 5m)],
            priceOf: _ => 0.43m,
            venue: "币安",
            coverage: Coverage);

        Assert.IsFalse(result.IsMapped);
        Assert.IsNull(result.Base);
        Assert.IsNull(result.Price);
        Assert.Contains("同名但价格差得远", result.Reason ?? string.Empty);
    }

    [TestMethod]
    public void Resolve_WhenNoCandidate_NamesTheVenueAndWhatWasSearched()
    {
        // 拒绝原因要能回答两个问题：**在哪家**没找到、**找过它家的什么范围**。
        // 后者不是修辞：早期按 contractType == PERPETUAL 过滤时，"没有"其实是我漏看了
        // TRADIFI_PERPETUAL 那个命名空间——不写清范围，这两件事在界面上长得一模一样
        SymbolMapper.Resolution result = SymbolMapper.Resolve(
            "某个冷门币",
            lighterPrice: 1m,
            candidates: [],
            priceOf: _ => 0m,
            venue: "币安",
            coverage: "加密永续与传统金融永续（股票、商品、指数）");

        Assert.IsFalse(result.IsMapped);
        Assert.Contains("币安没有这个标的的合约", result.Reason ?? string.Empty);
        Assert.Contains("传统金融永续", result.Reason ?? string.Empty);
    }

    [TestMethod]
    public void Resolve_WhenScanningAnotherVenue_DoesNotBlameBinance()
    {
        // 同一个拒绝原因会被三家平台复用。写死"币安"的话，扫 OKX 时看到的人会跑去币安那边找原因
        SymbolMapper.Resolution result = SymbolMapper.Resolve(
            "某个冷门币",
            lighterPrice: 1m,
            candidates: [],
            priceOf: _ => 0m,
            venue: "OKX",
            coverage: "该平台全部正向（linear）永续，含股票代币类");

        Assert.IsFalse(result.IsMapped);
        Assert.Contains("OKX", result.Reason ?? string.Empty);
        Assert.DoesNotContain("币安", result.Reason ?? string.Empty);
    }

    [TestMethod]
    public void Resolve_WithoutLighterPrice_IsRefusedInsteadOfGuessing()
    {
        // 取不到价格就做不了"同物"确认。猜错的代价是开错资产，拒绝的代价只是这一笔没跟上——
        // 两者不对称，所以必须拒
        SymbolMapper.Resolution result = SymbolMapper.Resolve(
            "ENA",
            lighterPrice: 0m,
            candidates: [Symbol("ENAUSDT", "PERPETUAL", 5m)],
            priceOf: _ => 0.2451m,
            venue: "币安",
            coverage: Coverage);

        Assert.IsFalse(result.IsMapped);
        Assert.Contains("取不到 Lighter 侧的标记价", result.Reason ?? string.Empty);
    }

    [TestMethod]
    public void Resolve_WhenCandidateHasNoPrice_TriesTheNextOne()
    {
        // 同一个 base 在币安可能有多个合约（实测 SPCX 有 SPCXUSDT 与 SPCXUSD1），
        // 其中一个取不到价不该让整个标的映射失败
        SymbolMapper.Resolution result = SymbolMapper.Resolve(
            "SPCX",
            lighterPrice: 392.2m,
            candidates: [Symbol("SPCXUSD1", "TRADIFI_PERPETUAL", 5m), Symbol("SPCXUSDT", "TRADIFI_PERPETUAL", 5m)],
            priceOf: symbol => symbol.Symbol == "SPCXUSDT" ? 393m : 0m,
            venue: "币安",
            coverage: Coverage);

        Assert.IsTrue(result.IsMapped);
        Assert.AreEqual("SPCX", result.Base);

        // 这一条真正要验的是"挑中了能取到价的那一个"——价格是那个选择的证据
        // （两个候选的资产币都是 SPCX，只看 Base 分不出挑的是哪个）
        Assert.AreEqual(393m, result.Price);
        Assert.IsTrue(result.IsTradFi);
    }

    [TestMethod]
    public void Resolve_WhenRatioIsSlightlyOff_StillMapsButSaysSo()
    {
        // 价格差 1.2 倍以上仍算同物，但要在备注里说出来（溢价、口径差异都值得看一眼）
        SymbolMapper.Resolution result = SymbolMapper.Resolve(
            "SNDK",
            lighterPrice: 1693m,
            candidates: [Symbol("SNDKUSDT", "TRADIFI_PERPETUAL", 5m)],
            priceOf: _ => 2100m,
            venue: "币安",
            coverage: Coverage);

        Assert.IsTrue(result.IsMapped);
        Assert.Contains("价格差", result.Reason ?? string.Empty);
    }

    [TestMethod]
    public void CandidatesFor_Alias_AddsTheSameAssetUnderADifferentCode()
    {
        // 实测：WTI 原油在币安叫 CLUSDT（NYMEX 的 CL），靠同名永远匹配不上
        // 键是 baseAsset（"CL"），不是合约代码（"CLUSDT"）——这一条正是实测踩过的坑
        Dictionary<string, List<ExchangeSymbolInfo>> byBase = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CL"] = [Symbol("CLUSDT", "TRADIFI_PERPETUAL", 5m)],
        };

        IReadOnlyList<ExchangeSymbolInfo> candidates = SymbolMapper.CandidatesFor("WTI", byBase);

        Assert.HasCount(1, candidates);
        Assert.AreEqual("CLUSDT", candidates[0].Symbol);
    }

    [TestMethod]
    public void CandidatesFor_WithoutAlias_ReturnsOnlyTheSameName()
    {
        // 与用例里一致：字典按 **baseAsset** 建键（"ENA" → [ENAUSDT]），不是按合约代码
        Dictionary<string, List<ExchangeSymbolInfo>> byBase = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ENA"] = [Symbol("ENAUSDT", "PERPETUAL", 5m)],
        };

        Assert.AreEqual("ENAUSDT", SymbolMapper.CandidatesFor("ENA", byBase).Single().Symbol);
        Assert.IsEmpty(SymbolMapper.CandidatesFor("没有这个标的", byBase));
    }

    [TestMethod]
    public void CandidatesFor_AliasIsNotUsedToOverrideTheSameName()
    {
        // 别名只**追加**候选，不挤掉同名候选：规则只有"价格确认"一条，
        // 否则"别名优先"会变成一条隐蔽的覆盖逻辑，出问题时没人想得到
        Dictionary<string, List<ExchangeSymbolInfo>> byBase = new(StringComparer.OrdinalIgnoreCase)
        {
            ["US500"] = [Symbol("US500USDT", "TRADIFI_PERPETUAL", 5m)],
        };

        IReadOnlyList<ExchangeSymbolInfo> candidates = SymbolMapper.CandidatesFor("US500", byBase);

        Assert.HasCount(1, candidates);
        Assert.AreEqual("US500USDT", candidates[0].Symbol);
    }

    /// <summary>测试里"找过的范围"这一项与断言无关，统一给一份币安的说法。</summary>
    private const string Coverage = "加密永续与传统金融永续（股票、商品、指数）";

    private static ExchangeSymbolInfo Symbol(string symbol, string contractType, decimal minNotional) => new(
        Symbol: symbol,
        BaseAsset: symbol.Replace("USDT", string.Empty, StringComparison.Ordinal),
        QuoteAsset: "USDT",
        ContractType: contractType,

        // 币安口径的判据：传统金融走 TRADIFI_PERPETUAL 命名空间
        IsTradFi: contractType == "TRADIFI_PERPETUAL",
        MinNotional: minNotional,
        StepSize: 0.01m,
        QuantityPrecision: 2);
}

/// <summary>
/// "源不可作为目标"这条规则的一致性测试。
///
/// 它有三个落点（领域常量、扫描用例、登记用例），任何一个漏掉都会让使用者
/// 得到一条"映射到 Lighter 自己"的、看起来正常却毫无意义的记录。
/// </summary>
[TestClass]
public sealed class SymbolMappingSourcePlatformTests
{
    [TestMethod]
    public async Task Scan_WithLighterAsTarget_IsRejectedWithAnExplanation()
    {
        ScanSymbolMappingsUseCase useCase = new(
            new NoopMappingRepository(),
            new NoopLeadRepository(),
            new NoopPoolsClient(),
            new FakeCatalogs());

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            useCase.ExecuteAsync("lighter", CancellationToken.None));

        Assert.Contains("源", error.Message);
    }

    /// <summary>扫描要在"取目录"之前就拒绝，所以这些替身根本不会被真正用到。</summary>
    private sealed class NoopMappingRepository : Domain.Repositories.ISymbolMappingRepository
    {
        public Task<IReadOnlyList<Domain.Entities.SymbolMapping>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Domain.Entities.SymbolMapping>>([]);

        public Task<Domain.Entities.SymbolMapping?> GetAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Domain.Entities.SymbolMapping?>(null);

        public Task<Domain.Entities.SymbolMapping?> FindAsync(
            string targetPlatform,
            string sourceBase,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Domain.Entities.SymbolMapping?>(null);

        public Task<Domain.Entities.SymbolMapping> CreateAsync(
            Domain.Entities.SymbolMapping mapping,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateAsync(Domain.Entities.SymbolMapping mapping, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopLeadRepository : Domain.Repositories.ILeadTraderRepository
    {
        public Task<Domain.Entities.LeadTrader?> GetAsync(
            Domain.ValueObjects.AccountIndex account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Domain.Entities.LeadTrader?>(null);

        public Task<IReadOnlyList<Domain.Entities.LeadTrader>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Domain.Entities.LeadTrader>>([]);

        public Task UpsertAsync(Domain.Entities.LeadTrader lead, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(Domain.ValueObjects.AccountIndex account, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopPoolsClient : Abstractions.ILighterPoolsClient
    {
        public Task<IReadOnlyList<Domain.Entities.Pool>> FetchPoolsAsync(
            Abstractions.PoolFilter filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Abstractions.LighterSystemConfig> FetchSystemConfigAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Domain.Entities.PoolSnapshot> FetchSnapshotAsync(
            Domain.ValueObjects.AccountIndex account, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Domain.Entities.PoolInfo> FetchPoolInfoAsync(
            Domain.ValueObjects.AccountIndex account, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Domain.Entities.PoolTrade>> FetchRecentTradesAsync(
            Domain.ValueObjects.AccountIndex account, int limit = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Domain.Entities.PoolOrder>> FetchOrdersAsync(
            Domain.ValueObjects.AccountIndex account, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Domain.Entities.InstrumentSpec>> FetchInstrumentsAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Domain.Entities.OrderBookSnapshot> FetchOrderBookAsync(
            int marketId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeCatalogs : Abstractions.IExchangeSymbolCatalogFactory
    {
        public IReadOnlyList<Domain.Enums.ExchangeCode> SupportedPlatforms { get; } =
            [Domain.Enums.ExchangeCode.Binance];

        public Abstractions.IExchangeSymbolCatalog? Create(Domain.Enums.ExchangeCode platform) =>
            throw new InvalidOperationException("不该走到这里：Lighter 作为目标必须在取目录之前就被拒");
    }
}
