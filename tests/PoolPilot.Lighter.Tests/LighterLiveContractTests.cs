using Microsoft.Extensions.DependencyInjection;

using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;
using PoolPilot.Infrastructure.Lighter;

namespace PoolPilot.Lighter.Tests;

/// <summary>
/// 对 Lighter 公开接口的真实契约测试。
///
/// 全部标记 <c>[TestCategory("Live")]</c>，日常门禁排除：
/// <code>dotnet test -m:1 --filter "TestCategory!=Live"</code>
///
/// 存在理由：本项目的全部领域结论都建立在实测之上。上游改一个字段的类型、单位或刻度，
/// 就会让杠杆、净值、漏损等下游计算静默失准。这一层是"上游变了立刻知道"的防线。
///
/// 断言设计原则：**断言机制，不断言总体统计**。上游的某些量（如某池的挂单成交占比、
/// 成交流水的快照条数）会随市场状态变化，把它们写死会制造脆弱测试；而这些量背后的
/// 解析与分类规则是稳定的，那才是该守的东西。
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class LighterLiveContractTests
{
    /// <summary>实测已知的公共池，用于校验解析链路。</summary>
    private const long KnownPoolIndex = 281_474_976_552_443L;

    /// <summary>契约测试用的容器：整个类共用一个，直到 ClassCleanup 才释放
    /// （库的客户端与容器绑在一起，中途释放会让还在用的 handler 变成已释放对象）。</summary>
    private static ServiceProvider _services = null!;

    [ClassInitialize]
    public static void Setup(TestContext context)
    {
        _services = new ServiceCollection()
            .AddLogging()
            .AddCryptoClients(options => options.EnabledExchanges = [CryptoClients.Net.Enums.Exchange.Lighter])
            .BuildServiceProvider();
    }

    [ClassCleanup]
    public static void Cleanup() => _services.Dispose();

    /// <summary>
    /// 走与线上同一条装配路径：库覆盖到的三条走 CryptoClients.Net，池的那五条走扩展。
    /// 两半都在这条链上，所以契约测试同时盯着"库的模型"与"自研的解析"。
    /// </summary>
    private static LighterPoolsClient CreateClient()
    {
        HttpClient http = new()
        {
            BaseAddress = new Uri($"{LighterConstants.MainnetRestBaseUrl}/"),
            Timeout = TimeSpan.FromSeconds(60),
        };

        return new LighterPoolsClient(
            _services.GetRequiredService<global::Lighter.Net.Interfaces.Clients.ILighterUserClientProvider>()
                .GetRestClient("live-contract-test", null, global::Lighter.Net.LighterEnvironment.Live),
            new LighterPoolDataExtension(http, new LighterWebSocketClient()));
    }

    [TestMethod]
    public async Task FetchSystemConfig_ReturnsIndicesBeyondInt32()
    {
        // Arrange
        LighterPoolsClient client = CreateClient();

        // Act
        LighterSystemConfig config = await client.FetchSystemConfigAsync();

        // Assert：池索引远超 int32 —— 这是全项目用 long 的理由
        Assert.IsGreaterThan((long)int.MaxValue, config.LiquidityPoolIndex);
        Assert.IsGreaterThan(0L, config.StakingPoolIndex);
    }

    [TestMethod]
    public async Task FetchPools_EnumeratesWithoutBoundaryGaps()
    {
        // Arrange
        LighterPoolsClient client = CreateClient();

        // Act
        IReadOnlyList<Pool> userPools = await client.FetchPoolsAsync(PoolFilter.User);
        IReadOnlyList<Pool> allPools = await client.FetchPoolsAsync(PoolFilter.All);

        // Assert
        Assert.IsGreaterThan(0, userPools.Count);
        Assert.IsGreaterThan(userPools.Count - 1, allPools.Count);

        // 分页正确性的不变量：user 必须是 all 的子集。
        // 分页游标若用「本页最小索引 − 1」会在页边界漏池，症状正是这个差集出现双向不对称。
        // 历史上就是这样发现并修复的（见 docs/LIGHTER-API-NOTES.md 5.3）。
        HashSet<long> allIndices = [.. allPools.Select(p => p.AccountIndex.Value)];
        List<long> missingInAll = [.. userPools
            .Select(p => p.AccountIndex.Value)
            .Where(i => !allIndices.Contains(i))];

        Assert.AreEqual(
            0,
            missingInAll.Count,
            $"user 中这 {missingInAll.Count} 个池未出现在 all 里：{string.Join(", ", missingInAll)}");
    }

    [TestMethod]
    public async Task FetchPools_ReadsOperatorFeeOnPercentScale()
    {
        // Arrange
        LighterPoolsClient client = CreateClient();

        // Act
        IReadOnlyList<Pool> pools = await client.FetchPoolsAsync(PoolFilter.User);

        // Assert：operator_fee 本身就是百分数，实测取值区间为 1%~30%。
        // 这是判定"百分数字段未被重复缩放"的决定性断言——若有人再乘 100，
        // 上界会立刻被突破（3000% 远超 30%）。
        Pool[] withFees = [.. pools.Where(p => p.OperatorFee.Percent > 0m)];

        Assert.IsGreaterThan(0, withFees.Length, "应至少有部分池配置了运营费");

        foreach (Pool pool in withFees)
        {
            Assert.IsLessThan(
                100m,
                pool.OperatorFee.Percent,
                $"{pool.Name} 的运营费 {pool.OperatorFee.Percent}% 超出合理范围，疑似重复缩放");
        }

        // 实测运营费上限恰为 30%（协议封顶）。断言取 20 作为下界，既确认读到了真实量级，
        // 又不把"恰好等于封顶值"写死成脆弱断言。
        Assert.IsGreaterThan(20m, withFees.Max(p => p.OperatorFee.Percent), "实测运营费最高约 30%");
    }

    [TestMethod]
    public async Task FetchPools_ReportedApyIsUnreliableByDesign()
    {
        // Arrange
        LighterPoolsClient client = CreateClient();

        // Act
        IReadOnlyList<Pool> pools = await client.FetchPoolsAsync(PoolFilter.User);
        decimal[] apys = [.. pools
            .Where(p => p.ReportedAnnualPercentageYield.Percent != 0m)
            .Select(p => p.ReportedAnnualPercentageYield.Percent)];

        // Assert：实测该字段可以荒谬到数千百分点（短命池把微小波动年化）。
        // 这条断言的用途是**把"点估计不可信"钉成事实**，从而证明界面上必须与
        // 净值序列实算值并排展示，而不是单独采信上游。
        Assert.IsGreaterThan(0, apys.Length);

        decimal maxAbsolute = apys.Max(Math.Abs);
        Assert.IsGreaterThan(
            1_000m,
            maxAbsolute,
            "实测应存在远超 1000% 的 APY 点估计；若此刻没有，说明上游改了算法，需复核展示策略");
    }

    [TestMethod]
    public async Task FetchPoolInfo_ProvidesOperatorShareStructureAbsentFromPoolList()
    {
        // Arrange
        LighterPoolsClient client = CreateClient();
        AccountIndex account = AccountIndex.From(KnownPoolIndex);

        // Act
        PoolInfo info = await client.FetchPoolInfoAsync(account);

        // Assert：这两个字段 publicPoolsMetadata 实测不提供，只能走 WS pool_info。
        // 它们决定"运营者利益绑定有多紧"，是评估池子时可用的少数非收益信号之一。
        Assert.IsGreaterThan(0L, info.TotalShares);
        Assert.IsGreaterThan(0L, info.OperatorShares);
        Assert.IsGreaterThan(0m, info.MinOperatorShareRate.Percent);

        Assert.IsTrue(
            info.OperatorShareRatio.Percent >= info.MinOperatorShareRate.Percent - 0.01m,
            $"运营者份额 {info.OperatorShareRatio.Percent}% 低于协议最低要求 {info.MinOperatorShareRate.Percent}%");
    }

    [TestMethod]
    public async Task FetchSnapshot_ParsesMarginFractionWithCorrectScale()
    {
        // Arrange
        LighterPoolsClient client = CreateClient();
        AccountIndex account = AccountIndex.From(KnownPoolIndex);

        // Act
        PoolSnapshot snapshot = await client.FetchSnapshotAsync(account);

        // Assert：保证金占用的交叉校验——持仓名义 × 仓位 IMF 应等于账户报的初始保证金占用。
        // 这是判定"持仓 IMF 是百分数（÷100）"这一读法是否正确的决定性校验；
        // 若刻度读错 100 倍，偏差会立刻暴露。
        Assert.IsGreaterThan(0m, snapshot.TotalAssetValue.Value);
        Assert.IsGreaterThan(0m, snapshot.Collateral.Value);

        // 持仓解析才是这条断言真正要守的东西：它曾在"持仓为空"时**静默跳过**整段校验，
        // 于是一个"持仓全被丢掉"的回归也能报绿（实测踩到过：换了数据来源之后持仓映射失效，
        // 整套测试照样全过）。所以这里显式要求至少有一笔持仓；池子确实空仓时用 Inconclusive
        // 表达"没验到"，而不是让它悄悄变成"验过了"。
        if (snapshot.ActivePositions.Count == 0)
        {
            Assert.Inconclusive($"池 {account} 此刻没有持仓，持仓解析这一层没被校验到——换个时间或换一个池再跑");
        }

        // Margin 为 null = 上游此刻没给有效保证金率（仓位变动瞬间），该行无法参与校验，跳过
        decimal sumOfPositions = snapshot.ActivePositions.Sum(p => p.Notional.Value * (p.Margin?.Ratio ?? 0m));
        decimal reported = snapshot.InitialMarginRequirement.Value;

        if (sumOfPositions > 0m && reported > 0m)
        {
            decimal relativeGap = Math.Abs(sumOfPositions - reported) / reported;
            Assert.IsLessThan(
                0.05m,
                relativeGap,
                $"持仓名义 × IMF 之和 {sumOfPositions} 与账户报的初始保证金 {reported} 偏差 {relativeGap:P1}，疑似 IMF 刻度读错");
        }
    }

    [TestMethod]
    public async Task FetchSnapshot_LiquidationPriceIsNullWhenUpstreamReportsZero()
    {
        // Arrange
        LighterPoolsClient client = CreateClient();

        // Act
        PoolSnapshot snapshot = await client.FetchSnapshotAsync(AccountIndex.From(KnownPoolIndex));

        // Assert：上游用 0 表示"不适用"，领域模型必须转成 null。
        // 若把 0 当成真实强平价，会得出"价格跌到 0 才强平"的荒谬结论。
        foreach (PoolPosition position in snapshot.ActivePositions)
        {
            Assert.IsTrue(
                position.LiquidationPrice is null || position.LiquidationPrice.Value > 0m,
                $"{position.Symbol} 的强平价不应为 0");
        }
    }

    [TestMethod]
    public async Task FetchInstruments_ParsesMinimumsAndFees()
    {
        // Arrange
        LighterPoolsClient client = CreateClient();

        // Act
        IReadOnlyList<InstrumentSpec> instruments = await client.FetchInstrumentsAsync();

        // Assert
        Assert.IsGreaterThan(0, instruments.Count);

        InstrumentSpec? ena = instruments.FirstOrDefault(i => i.MarketId == LighterConstants.EnaMarketId);
        Assert.IsNotNull(ena, "应能读到 ENA 市场的规格");

        // 实测：ENA 最小下单额 10 USDT、最小数量 35 个、Standard 档费率为 0
        Assert.AreEqual(10m, ena.MinOrderNotional);
        Assert.AreEqual(35m, ena.MinOrderSize);
        Assert.AreEqual(0m, ena.TakerFeePercent);
        Assert.AreEqual(0m, ena.MakerFeePercent);
    }

    [TestMethod]
    public async Task FetchPoolInfo_ReturnsNavSeriesUsableForDrawdownAnalysis()
    {
        // Arrange
        LighterPoolsClient client = CreateClient();

        // Act
        PoolInfo info = await client.FetchPoolInfoAsync(AccountIndex.From(KnownPoolIndex));
        NavMetrics metrics = NavAnalyzer.Compute(info.Nav);

        // Assert：净值序列是上游唯一的历史数据来源，也是算真实回撤的唯一依据
        Assert.IsGreaterThan(30, info.Nav.Points.Count, "净值点应足够多");
        Assert.IsTrue(metrics.IsStatisticallyMeaningful, "样本应具备统计意义");
        Assert.IsTrue(info.Nav.Points.All(p => p.SharePrice > 0m), "份额净值应全为正");

        // 上游根本不提供最大回撤——这是实算出来的
        Assert.IsLessThan(0m, metrics.MaxDrawdownPercent, "任何有波动的序列都应有回撤");
    }

    [TestMethod]
    public async Task FetchRecentTrades_ClassifiesRolesConsistentlyWithUpstreamFlags()
    {
        // Arrange
        LighterPoolsClient client = CreateClient();
        AccountIndex account = AccountIndex.From(KnownPoolIndex);

        // Act
        IReadOnlyList<PoolTrade> trades = await client.FetchRecentTradesAsync(account);

        // Assert：不断言"挂单占比多少"——那个比例会随池子的动作节奏变化
        // （实测同一池在不同时刻的快照里，成交条数从 50 变到 2，maker 占比也随之变）。
        // 该守的是**分类规则与上游标志位的一致性**，这才是稳定的契约。
        Assert.IsGreaterThan(0, trades.Count, "应能读到该池的成交流水");

        foreach (PoolTrade trade in trades)
        {
            // 池必须恰好出现在成交的一侧
            Assert.IsTrue(trade.Involves(account), $"成交 {trade.TradeId} 里没有该池");

            // 角色判定必须与上游的 is_maker_ask 语义一致：
            // is_maker_ask 表示挂单方在卖侧，故"账户在卖侧"与"挂单方在卖侧"同真同假时账户才是挂单方
            bool accountIsAsk = trade.AskAccountIndex == account;
            TradeRole expected = accountIsAsk == trade.IsMakerAsk ? TradeRole.Maker : TradeRole.Taker;
            Assert.AreEqual(expected, trade.RoleOf(account), $"成交 {trade.TradeId} 的角色判定与上游标志位不符");

            // 方向判定必须与账户索引一致
            Assert.AreEqual(
                accountIsAsk ? OrderSide.Sell : OrderSide.Buy,
                trade.SideOf(account),
                $"成交 {trade.TradeId} 的方向判定与账户索引不符");

            // 两个时间字段的刻度不同，必须都能解析出正数
            Assert.IsGreaterThan(0L, trade.Timestamp.RawValue);
            Assert.IsGreaterThan(0L, trade.TransactionTime.RawValue);
        }
    }

    [TestMethod]
    public async Task FetchRecentTrades_ReconstructsRoundTripsWithPositiveSize()
    {
        // Arrange
        LighterPoolsClient client = CreateClient();
        AccountIndex account = AccountIndex.From(KnownPoolIndex);

        // Act
        IReadOnlyList<PoolTrade> trades = await client.FetchRecentTradesAsync(account);
        IReadOnlyList<RoundTrip> trips = TradeClassifier.ReconstructRoundTrips(trades, account);

        // Assert：每笔重建的单轮都应有正的建仓量与可读的均价。
        // 这条断言守的是"已完结的单轮不能报出 size=0"——那正是把当前持仓量
        // 误当成累计建仓量时会犯的错（平仓后当前持仓归零）。
        foreach (RoundTrip trip in trips)
        {
            Assert.IsGreaterThan(0m, trip.EntrySize, $"market {trip.MarketId} 的单轮建仓量应为正");
            Assert.IsGreaterThan(0m, trip.EntryVwap.Value, $"market {trip.MarketId} 的入场均价应为正");
            Assert.IsGreaterThan(0m, trip.ExitVwap.Value, $"market {trip.MarketId} 的出场均价应为正");
        }
    }

    [TestMethod]
    public async Task FetchOrderBook_ParsesNestedPayloadAndHasTwoSidedDepth()
    {
        // Arrange
        LighterPoolsClient client = CreateClient();

        // Act
        OrderBookSnapshot book = await client.FetchOrderBookAsync(LighterConstants.EnaMarketId);

        // Assert：盘口嵌在 order_book 对象里，不是顶层。
        // 这条断言守的是那个曾经把顶层 asks 当数据源、导致永远取不到盘口的缺陷。
        Assert.IsGreaterThan(0, book.Bids.Count);
        Assert.IsGreaterThan(0, book.Asks.Count);
        Assert.IsNotNull(book.MidPrice);

        // 买价必须严格低于卖价（嵌套读错会把两边搞混）
        Assert.IsLessThan(book.BestAsk!.Value, book.BestBid!.Value);
        Assert.IsGreaterThan(0m, book.SpreadBps);

        // 实测 ENA 价差在数个基点内
        Assert.IsLessThan(100m, book.SpreadBps, $"ENA 价差 {book.SpreadBps:F2} bps 异常偏大");
    }

    [TestMethod]
    public async Task EndToEnd_SlippageCalculationStaysWithinMeasuredSpreadScale()
    {
        // Arrange
        LighterPoolsClient client = CreateClient();
        AccountIndex account = AccountIndex.From(KnownPoolIndex);

        // Act
        IReadOnlyList<PoolTrade> trades = await client.FetchRecentTradesAsync(account);

        // **不写死市场**：实测该池的交易标的会整个换掉（ENA → ZRO → PUMP），
        // 写死 ENA 会让这个测试在该池换标的后永久跳过——名义上还在、实际什么都不测。
        // 改为取"近期成交最活跃的市场"，它衡量的是"池子现在做的这一笔的摩擦成本"，
        // 与池子当前在做什么保持一致。
        PoolTrade? sample = trades
            .Where(t => t.IsRegularTrade)
            .GroupBy(t => t.MarketId)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.FirstOrDefault();

        if (sample is null)
        {
            Assert.Inconclusive("该池近期成交流水中没有可用样本，无法完成端到端漏损校验");
            return;
        }

        OrderBookSnapshot book = await client.FetchOrderBookAsync(sample.MarketId);

        OrderSide followerSide = sample.SideOf(account);
        CopySlippage slippage = CopySlippageCalculator.Compute(
            book,
            followerSide,
            sample.Size,
            sample.Price);

        // Assert：量级应与实测价差结构一致（深盘数个基点），而不是几百个基点。
        // 这是整个方案的决策门——若算出的摩擦成本与实测量级不符，后面的跟单设计都无意义。
        Assert.IsLessThan(
            200m,
            Math.Abs(slippage.SlippageBps),
            $"{sample.MarketId} 号市场漏损 {slippage.SlippageBps:F2} bps 量级异常");
        Assert.AreEqual(followerSide, slippage.Side);
    }
}
