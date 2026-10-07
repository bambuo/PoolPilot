using PoolPilot.Application.Abstractions;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 实盘跟单的算账规则。
///
/// 这里钉住的是**跨场所跟单与传统跟单唯一真正不同的那一点**：复制的是名义额，不是仓位数量。
/// 拿实测数据说话：Lighter 的 <c>ANTHROPIC</c> 报价 2128.7，OKX 的 <c>ANTHROPIC-USDT-SWAP</c>
/// 报价 210.74——同一个资产、相差 10 倍，因为一家的合约单位是另一家的 10 倍。
/// 按数量复制会开出 10 倍的仓位，而按名义额复制，两家开出来的是**同一个美元敞口**。
/// </summary>
[TestClass]
public sealed class LiveCopyPlannerTests
{
    private static BrokerInstrument Instrument(
        decimal step = 0.01m,
        decimal minQuantity = 0.01m,
        decimal minNotional = 5m,
        int precision = 2) => new("X", precision, 2, step, minQuantity, minNotional);

    /// <summary>
    /// 造一份输入。参数按**池仓名义**给（这是测试里最直观的表达），
    /// 由这里乘出目标名义额——也就是"对齐"那条路的做法（目标 = 池仓 × 比例）。
    /// 计划器本身**不再乘比例**：目标由调用方给（跟动作时是"我方 + 动作幅度 × 比例"）。
    /// </summary>
    private static LiveCopyInput Input(
        decimal poolNotional,
        decimal ourQuantity,
        decimal targetPrice,
        decimal capital = 5000m,
        decimal poolEquity = 100_000m,
        BrokerInstrument? instrument = null) => new()
        {
            // 用生产代码里的比例函数（权益非正时它返回 0），别在测试里自己写除法——
            // 那样零权益用例会先在测试里炸掉，而不是验到"计划器拒绝"
            TargetNotional = poolNotional * LiveCopyPlanner.ScaleFor(
                UsdAmount.From(capital),
                UsdAmount.From(poolEquity)),
            PoolEquity = UsdAmount.From(poolEquity),
            Capital = UsdAmount.From(capital),
            OurQuantity = ourQuantity,
            TargetPrice = targetPrice,
            Instrument = instrument ?? Instrument(),
        };

    /// <summary>
    /// 目标名义额**原样使用**，不再乘一次比例。
    ///
    /// 这条必须钉住：计划器里曾经自己乘比例（那时输入是池仓名义），后来改成由调用方给目标，
    /// 如果两边都乘，仓位会差整整一个比例系数——而那种错误不会报错，只会下一个悄悄偏大或偏小的单。
    /// </summary>
    [TestMethod]
    public void Plan_UsesTheGivenTargetNotionalAsIs()
    {
        LiveCopyPlan plan = LiveCopyPlanner.Plan(new LiveCopyInput
        {
            TargetNotional = 1000m,                 // 已经是"该持多少"的答案
            PoolEquity = UsdAmount.From(100_000m),
            Capital = UsdAmount.From(5000m),        // 比例 0.05：若被再乘一次，会变成 50
            OurQuantity = 0m,
            TargetPrice = 250m,
            Instrument = Instrument(),
        });

        Assert.AreEqual(1000m, plan.NotionalUsd, "目标名义额由调用方决定，计划器不再乘比例");
        Assert.AreEqual(4m, plan.Legs[0].Quantity);
    }

    [TestMethod]
    public void Plan_CopiesNotionalNotSize_WhenTheTwoVenuesUseDifferentContractUnits()
    {
        // 实测的一对：Lighter ANTHROPIC 报 2128.7、OKX ANTHROPIC-USDT-SWAP 报 210.74，
        // 相差 10 倍——同一个资产，两家的合约单位不同。
        //
        // 池子持有 1000 USDT 的名义（≈0.47 个 Lighter 单位）。比例 0.05，所以本次目标 50 USDT。
        // 若按**数量**复制会得到 0.47×0.05=0.0235 个 OKX 合约 = 4.95 USDT —— 只有应跟的十分之一，
        // 而且不会有任何报错。按名义额复制才得到 50 USDT（≈0.2372 个合约）。
        LiveCopyPlan plan = LiveCopyPlanner.Plan(Input(poolNotional: 1000m, ourQuantity: 0m, targetPrice: 210.74m));

        Assert.AreEqual(0.05m, plan.Scale);
        Assert.AreEqual(50m, plan.NotionalUsd, 0.01m, "复制的必须是名义额：数量复制会少 10 倍且不报错");
        Assert.AreEqual(0.23m, plan.Legs[0].Quantity, "数量由**目标合约的价格**折算，再向下量化到步长");
    }

    [TestMethod]
    public void Plan_SizesTheOrderFromTheTargetPrice()
    {
        // 本金 5000、池子权益 100000 → 比例 0.05；池子 20000 的仓位 → 目标 1000 USDT
        // 目标价 250 → 目标数量 4
        LiveCopyPlan plan = LiveCopyPlanner.Plan(Input(
            poolNotional: 20_000m,
            ourQuantity: 0m,
            targetPrice: 250m,
            capital: 5000m,
            poolEquity: 100_000m));

        Assert.AreEqual(0.05m, plan.Scale);
        Assert.AreEqual(1000m, plan.NotionalUsd, 0.01m);
        Assert.IsTrue(plan.HasWork);
        Assert.HasCount(1, plan.Legs);
        Assert.AreEqual(4m, plan.Legs[0].Quantity);
        Assert.AreEqual(BrokerOrderIntent.OpenLong, plan.Legs[0].Intent);
    }

    [TestMethod]
    public void Plan_WhenTheShortSideIsCopied_OpensAShort()
    {
        LiveCopyPlan plan = LiveCopyPlanner.Plan(Input(poolNotional: -20_000m, ourQuantity: 0m, targetPrice: 250m));

        Assert.HasCount(1, plan.Legs);
        Assert.AreEqual(BrokerOrderIntent.OpenShort, plan.Legs[0].Intent);
        Assert.AreEqual(-4m, plan.TargetQuantity, "目标仓位带符号：负为空");
    }

    [TestMethod]
    public void Plan_WhenAlreadyAligned_DoesNothing()
    {
        // 差额为 0 是常态（池子没动）。此时不该产生任何订单或记录
        LiveCopyPlan plan = LiveCopyPlanner.Plan(Input(poolNotional: 20_000m, ourQuantity: 4m, targetPrice: 250m));

        Assert.IsFalse(plan.HasWork);
        Assert.IsNull(plan.SkipReason, "对齐了不是「跳过」，是「无事可做」");
    }

    [TestMethod]
    public void Plan_WhenReducing_ClosesInsteadOfOpening()
    {
        // 我方 6 个，目标 4 个 → 卖出 2 个，且这是**平多**而不是开空
        LiveCopyPlan plan = LiveCopyPlanner.Plan(Input(poolNotional: 20_000m, ourQuantity: 6m, targetPrice: 250m));

        Assert.HasCount(1, plan.Legs);
        Assert.AreEqual(BrokerOrderIntent.CloseLong, plan.Legs[0].Intent);
        Assert.AreEqual(2m, plan.Legs[0].Quantity);
    }

    [TestMethod]
    public void Plan_WhenFlipping_SplitsIntoCloseThenOpen()
    {
        // 池子反手（多翻空）。双向持仓模式下"卖出"要配 positionSide 才有意义，
        // 一笔既平多又开空的订单在参数上表达不出来，所以必须拆成两笔
        LiveCopyPlan plan = LiveCopyPlanner.Plan(Input(poolNotional: -20_000m, ourQuantity: 4m, targetPrice: 250m));

        Assert.HasCount(2, plan.Legs);
        Assert.AreEqual(BrokerOrderIntent.CloseLong, plan.Legs[0].Intent);
        Assert.AreEqual(4m, plan.Legs[0].Quantity, "先把原来的多头平干净");
        Assert.AreEqual(BrokerOrderIntent.OpenShort, plan.Legs[1].Intent);
        Assert.AreEqual(4m, plan.Legs[1].Quantity);
    }

    [TestMethod]
    public void Plan_WhenThePoolClosedThePosition_ClosesOurs()
    {
        // 池子平掉（名义 0）而我方还有 3 个：目标是 0，差额就是把我方平掉
        LiveCopyPlan plan = LiveCopyPlanner.Plan(Input(poolNotional: 0m, ourQuantity: 3m, targetPrice: 250m));

        Assert.HasCount(1, plan.Legs);
        Assert.AreEqual(BrokerOrderIntent.CloseLong, plan.Legs[0].Intent);
        Assert.AreEqual(3m, plan.Legs[0].Quantity);
    }

    [TestMethod]
    public void Plan_WhenTheCopyIsTooSmallForTheExchange_SaysSoInsteadOfSending()
    {
        // 本金太小 → 目标名义额 3 USDT < 交易所最小名义额 5 USDT。
        // 与其让交易所回一个 -4164 再回头猜，不如在这里说清差多少
        LiveCopyPlan plan = LiveCopyPlanner.Plan(Input(
            poolNotional: 60m,
            ourQuantity: 0m,
            targetPrice: 250m,
            capital: 50m,
            poolEquity: 1000m,
            instrument: Instrument(minNotional: 5m)));

        Assert.IsFalse(plan.HasWork);
        Assert.IsNotNull(plan.SkipReason);
        Assert.Contains("最小名义价值", plan.SkipReason);
    }

    /// <summary>
    /// 实测踩过的那一条：本金 1 USDT、池子权益 5447、池子仓位 2081 USDT 时，
    /// 目标名义额 0.38 USDT 折算成 BTC 数量后是 0.0000044，被 0.001 的步长量化成 0。
    /// 此时差额也是 0（我方本来就没有仓位），若先判差额就会变成"无事可做"——
    /// 界面上什么都不记，看起来跟"跟得严丝合缝"一样，而事实是一笔都跟不了。
    /// </summary>
    [TestMethod]
    public void Plan_WhenTheTargetQuantizesToZero_ReportsItInsteadOfStayingSilent()
    {
        LiveCopyPlan plan = LiveCopyPlanner.Plan(Input(
            poolNotional: 2081m,
            ourQuantity: 0m,
            targetPrice: 86_000m,
            capital: 1m,
            poolEquity: 5447m,
            instrument: Instrument(step: 0.001m, minQuantity: 0.001m, minNotional: 5m, precision: 3)));

        Assert.IsFalse(plan.HasWork);
        Assert.IsNotNull(plan.SkipReason, "跟不动必须留痕，不能和「已对齐」一样安静");
        Assert.Contains("跟不动", plan.SkipReason);
    }

    /// <summary>
    /// 池子减仓到很小时，按比例算出的目标会落到步长以下。这时**必须把已有仓位平掉**：
    /// 池子已经卸掉了绝大部分风险，我方若只是"这次不动作"，留着的就是池子不再承担的那部分敞口。
    /// 实测形态：池子从 2078 USDT 减到 148、我方按 50/5441 的比例目标只剩 1.36 USDT，
    /// 折算成 ZEC 数量 0.000995 被 0.001 的步长量化成 0——旧逻辑在这里返回"跟不动"，
    /// 于是 17.7 USDT 的仓位一直挂着，而界面上看不出任何异常。
    /// </summary>
    [TestMethod]
    public void Plan_WhenThePoolShrinksBelowOneStep_ClosesOurPositionInsteadOfHoldingIt()
    {
        LiveCopyPlan plan = LiveCopyPlanner.Plan(Input(
            poolNotional: 148m,
            ourQuantity: 0.013m,
            targetPrice: 1366m,
            capital: 50m,
            poolEquity: 5441m,
            instrument: Instrument(step: 0.001m, minQuantity: 0.001m, minNotional: 5m, precision: 3)));

        Assert.IsTrue(plan.HasWork, "池子卸了风险，我方不能原地不动");
        Assert.HasCount(1, plan.Legs);
        Assert.AreEqual(BrokerOrderIntent.CloseLong, plan.Legs[0].Intent);
        Assert.AreEqual(0.013m, plan.Legs[0].Quantity);
        Assert.AreEqual(0m, plan.TargetQuantity);
    }

    /// <summary>同一情形下的空头：平空而不是开多。</summary>
    [TestMethod]
    public void Plan_WhenTheShortTargetRoundsToZero_ClosesTheShort()
    {
        LiveCopyPlan plan = LiveCopyPlanner.Plan(Input(
            poolNotional: 148m,
            ourQuantity: -0.013m,
            targetPrice: 1366m,
            capital: 50m,
            poolEquity: 5441m,
            instrument: Instrument(step: 0.001m, minQuantity: 0.001m, minNotional: 5m, precision: 3)));

        Assert.HasCount(1, plan.Legs);
        Assert.AreEqual(BrokerOrderIntent.CloseShort, plan.Legs[0].Intent);
    }

    /// <summary>真的对齐了（目标 4、我方 4）才是"无事可做"。</summary>
    [TestMethod]
    public void Plan_WhenTheTargetIsFlatAndWeAreFlat_DoesNothingQuietly()
    {
        LiveCopyPlan plan = LiveCopyPlanner.Plan(Input(poolNotional: 0m, ourQuantity: 0m, targetPrice: 250m));

        Assert.IsFalse(plan.HasWork);
        Assert.IsNull(plan.SkipReason);
    }

    [TestMethod]
    public void Plan_WithZeroPoolEquity_RefusesInsteadOfDividing()
    {
        // 池子权益为 0 时比例算不出来。取 1 或取无穷都是猜，猜错的代价是真钱。
        // 直接给目标名义额（对齐那条路由调用方乘比例，比例算不出来时它给不出来，
        // 但真给了一个非零目标时，这个守卫必须拦下）
        LiveCopyPlan plan = LiveCopyPlanner.Plan(new LiveCopyInput
        {
            TargetNotional = 1000m,
            PoolEquity = UsdAmount.From(0m),
            Capital = UsdAmount.From(5000m),
            OurQuantity = 0m,
            TargetPrice = 250m,
            Instrument = Instrument(),
        });

        Assert.IsFalse(plan.HasWork);
        Assert.Contains("池子权益", plan.SkipReason ?? string.Empty);
    }

    /// <summary>
    /// 目标为"不持有"时**不受比例影响**：哪怕比例算不出来（本金/权益取不到），该平的仓也要平。
    /// 否则一个凭证或配置问题会把仓位锁在账户里——那是最不该发生的。
    /// </summary>
    [TestMethod]
    public void Plan_WithZeroTarget_ClosesEvenWhenTheScaleIsUnknown()
    {
        LiveCopyPlan plan = LiveCopyPlanner.Plan(new LiveCopyInput
        {
            TargetNotional = 0m,
            PoolEquity = UsdAmount.From(0m),
            Capital = UsdAmount.From(0m),
            OurQuantity = 0.013m,
            TargetPrice = 1366m,
            Instrument = Instrument(step: 0.001m, minQuantity: 0.001m, minNotional: 5m, precision: 3),
        });

        Assert.IsTrue(plan.HasWork, "目标是不持有，就该把这笔平掉");
        Assert.AreEqual(BrokerOrderIntent.CloseLong, plan.Legs[0].Intent);
        Assert.AreEqual(0.013m, plan.Legs[0].Quantity);
    }

    [TestMethod]
    public void Plan_WithoutATargetPrice_RefusesInsteadOfGuessingTheSize()
    {
        LiveCopyPlan plan = LiveCopyPlanner.Plan(Input(poolNotional: 1000m, ourQuantity: 0m, targetPrice: 0m));

        Assert.IsFalse(plan.HasWork);
        Assert.Contains("价格", plan.SkipReason ?? string.Empty);
    }
}
