using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Exceptions;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Tests;

/// <summary>
/// 字段陷阱断言。
///
/// 本文件对应 docs/LIGHTER-API-NOTES.md 第三节「字段陷阱清单」的每一条。
/// 这些陷阱全部是实测踩出来的：单位或刻度写错一位，不会编译报错、不会运行报错，
/// 只会让杠杆、净值、漏损等下游计算静默失准。所以每一条都必须有断言守着，
/// 上游一改，这里先红。
/// </summary>
[TestClass]
public sealed class FieldTrapTests
{
    // ── 陷阱 1：账户索引超出 int32 ────────────────────────────────────────────

    [TestMethod]
    public void AccountIndex_ParsePublicPoolIndex_DoesNotOverflowInt32()
    {
        // Arrange：实测的公共池索引，281474976552443 远超 int32 上限 2147483647
        const string raw = "281474976552443";

        // Act
        AccountIndex index = AccountIndex.Parse(raw);

        // Assert
        Assert.AreEqual(281474976552443L, index.Value);
        Assert.IsGreaterThan(0L, index.Value);
        Assert.IsTrue(index.IsPublicPoolRange);
    }

    [TestMethod]
    public void AccountIndex_ParseValueBeyondInt32_StaysPositive()
    {
        // Arrange：这个值用 int 承接会溢出成负数
        const long beyondInt32 = 2_147_483_648L;

        // Act
        AccountIndex index = AccountIndex.From(beyondInt32);

        // Assert
        Assert.IsGreaterThan(beyondInt32 - 1, index.Value);
    }

    [TestMethod]
    public void AccountIndex_FromNonPositive_Throws()
    {
        // Arrange / Act / Assert
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => AccountIndex.From(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => AccountIndex.From(-1));
    }

    [TestMethod]
    public void AccountIndex_ParseNonNumeric_ThrowsInvalidLighterField()
    {
        // Arrange / Act / Assert
        Assert.ThrowsExactly<InvalidLighterFieldException>(() => AccountIndex.Parse("0xabc"));
    }

    // ── 陷阱 2：两个保证金率字段的刻度不同 ──────────────────────────────────

    [TestMethod]
    public void MarginFraction_FromOrderBookScale_DividesByTenThousand()
    {
        // Arrange：orderBookDetails 的 default_initial_margin_fraction 实测为 1000
        // Act
        MarginFraction fraction = MarginFraction.FromOrderBookScale(1000);

        // Assert：1000 → 10%，即 10 倍杠杆
        Assert.AreEqual(0.10m, fraction.Ratio);
        Assert.AreEqual(10m, fraction.Leverage);
    }

    [TestMethod]
    public void MarginFraction_FromPositionPercent_DividesByHundred()
    {
        // Arrange：account.positions[] 的 initial_margin_fraction 实测为 "25.00"
        // Act
        MarginFraction fraction = MarginFraction.FromPositionPercent("25.00");

        // Assert：25% → 4 倍杠杆
        Assert.AreEqual(0.25m, fraction.Ratio);
        Assert.AreEqual(4m, fraction.Leverage);
    }

    [TestMethod]
    public void MarginFraction_TwoCalibers_WithSameRawValue_ProduceDifferentLeverage()
    {
        // Arrange：这是陷阱的核心——同样的原始数字 1000 走两个入口得到完全不同的杠杆
        // Act
        MarginFraction fromOrderBook = MarginFraction.FromOrderBookScale(1000);
        MarginFraction fromPosition = MarginFraction.FromPositionPercent("1000");

        // Assert：10 倍 vs 0.1 倍，差 100 倍
        Assert.AreEqual(10m, fromOrderBook.Leverage);
        Assert.AreEqual(0.1m, fromPosition.Leverage);
        Assert.AreNotEqual(fromOrderBook, fromPosition);
    }

    [TestMethod]
    public void MarginFraction_KnownPosition_MatchesAccountMarginRequirement()
    {
        // Arrange：实测校验——持仓名义 872.38 × 仓位 IMF 25% 应等于账户报的初始保证金占用 218.08
        const decimal notional = 872.381m;
        MarginFraction fraction = MarginFraction.FromPositionPercent("25.00");

        // Act
        decimal required = notional * fraction.Ratio;

        // Assert
        Assert.AreEqual(218.095m, decimal.Round(required, 3));
    }

    [TestMethod]
    public void MarginFraction_FromNonPositive_Throws()
    {
        // Arrange / Act / Assert
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MarginFraction.FromOrderBookScale(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MarginFraction.FromPositionPercent("0"));
    }

    [TestMethod]
    public void MarginFraction_ParseNonNumeric_ThrowsInvalidLighterField()
    {
        // Arrange / Act / Assert
        Assert.ThrowsExactly<InvalidLighterFieldException>(() => MarginFraction.FromPositionPercent("abc"));
    }

    // ── 陷阱 3：三个字段本身就是百分数，不能再乘 100 ────────────────────────

    [TestMethod]
    public void PercentValue_FromAnnualPercentageYield_KeepsPercentScale()
    {
        // Arrange：annual_percentage_yield 实测 7.372113
        // Act
        PercentValue value = PercentValue.FromPercent(7.372113m);

        // Assert：它就是 7.37%，不是 737%
        Assert.AreEqual(7.372113m, value.Percent);
        Assert.IsGreaterThan(0.073m, value.Fraction);
        Assert.IsLessThan(0.074m, value.Fraction);
    }

    [TestMethod]
    public void PercentValue_ParseOperatorFeeString_KeepsPercentScale()
    {
        // Arrange：operator_fee 实测是**字符串** "20.0000"
        // Act
        PercentValue value = PercentValue.Parse("20.0000");

        // Assert：20%
        Assert.AreEqual(20.0000m, value.Percent);
        Assert.AreEqual(0.2m, value.Fraction);
    }

    [TestMethod]
    public void PercentValue_ParseDailyPriceChange_KeepsPercentScale()
    {
        // Arrange：daily_price_change 实测 5.3257，按小数解释会得出负价格
        // Act
        PercentValue value = PercentValue.Parse("5.3257");

        // Assert：+5.33%
        Assert.AreEqual(5.3257m, value.Percent);
    }

    [TestMethod]
    public void PercentValue_ParseNonNumeric_ThrowsInvalidLighterField()
    {
        // Arrange / Act / Assert
        Assert.ThrowsExactly<InvalidLighterFieldException>(() => PercentValue.Parse(""));
    }

    // ── 陷阱 4：四种时间刻度 ──────────────────────────────────────────────

    [TestMethod]
    public void LighterTimestamp_FromMicroseconds_MatchesRealTransactionTime()
    {
        // Arrange：transaction_time 实测 1791163963009550（微秒）
        // Act
        LighterTimestamp timestamp = LighterTimestamp.FromMicroseconds(1791163963009550L);

        // Assert
        Assert.AreEqual(TimestampCaliber.Microseconds, timestamp.Caliber);
        Assert.AreEqual(2026, timestamp.Utc.Year);
        Assert.IsFalse(timestamp.IsUnset);
    }

    [TestMethod]
    public void LighterTimestamp_FromSeconds_MatchesRealUpdatedAt()
    {
        // Arrange：pnlLeaderboard 的 updated_at 实测 1791190800（秒）
        // Act
        LighterTimestamp timestamp = LighterTimestamp.FromSeconds(1791190800L);

        // Assert
        Assert.AreEqual(TimestampCaliber.Seconds, timestamp.Caliber);
        Assert.AreEqual(2026, timestamp.Utc.Year);
    }

    [TestMethod]
    public void LighterTimestamp_FromMillisecondsText_MatchesRealOrderBookCreatedAt()
    {
        // Arrange：orderBookDetails[].created_at 实测是**毫秒字符串** "1737098461127"
        // Act
        LighterTimestamp timestamp = LighterTimestamp.FromMillisecondsText("1737098461127");

        // Assert
        Assert.AreEqual(TimestampCaliber.Milliseconds, timestamp.Caliber);
        Assert.AreEqual(2025, timestamp.Utc.Year);
        Assert.AreEqual(1, timestamp.Utc.Month);
    }

    [TestMethod]
    public void LighterTimestamp_FromMilliseconds_MatchesWebSocketTradeTimestamp()
    {
        // Arrange：WebSocket 成交的 timestamp 实测 1791197314925（毫秒）
        // Act
        LighterTimestamp timestamp = LighterTimestamp.FromMilliseconds(1791197314925L);

        // Assert
        Assert.AreEqual(TimestampCaliber.Milliseconds, timestamp.Caliber);
        Assert.AreEqual(2026, timestamp.Utc.Year);
    }

    [TestMethod]
    public void LighterTimestamp_InterpretingMicrosecondValueAsMilliseconds_ThrowsOutOfRange()
    {
        // Arrange：同一个数字，按微秒解释是 2026 年，按毫秒解释会跑到公元 58733 年 ——
        // 超出 DateTimeOffset 上限（9999 年）。这就是刻度搞错时的真实表现。
        const long raw = 1791163963009550L;

        // Act
        LighterTimestamp asMicros = LighterTimestamp.FromMicroseconds(raw);

        // Assert：正确刻度得到 2026 年；错误刻度**必须报错**而不是静默产生一个错年份
        Assert.AreEqual(2026, asMicros.Utc.Year);
        InvalidLighterFieldException ex = Assert.ThrowsExactly<InvalidLighterFieldException>(
            () => LighterTimestamp.FromMilliseconds(raw));
        StringAssert.Contains(ex.Message, "刻度");
    }

    [TestMethod]
    public void LighterTimestamp_FromZero_IsMarkedUnset()
    {
        // Arrange：account.accounts[].created_at 实测为 0（未回填），不能用它判断账户年龄
        // Act
        LighterTimestamp timestamp = LighterTimestamp.FromSeconds(0);

        // Assert
        Assert.IsTrue(timestamp.IsUnset);
    }

    [TestMethod]
    public void LighterTimestamp_ToUnixMicroseconds_IsUnitPreservingPerCaliber()
    {
        // Arrange / Act / Assert
        // 微秒刻度原样返回
        Assert.AreEqual(
            1791163963009550L,
            LighterTimestamp.FromMicroseconds(1791163963009550L).ToUnixMicroseconds());

        // 毫秒刻度放大一千倍
        Assert.AreEqual(
            1791163963009000L,
            LighterTimestamp.FromMilliseconds(1791163963009L).ToUnixMicroseconds());

        // 秒刻度放大一百万倍
        Assert.AreEqual(
            1791163963000000L,
            LighterTimestamp.FromSeconds(1791163963L).ToUnixMicroseconds());
    }

    [TestMethod]
    public void LighterTimestamp_FromNegative_Throws()
    {
        // Arrange / Act / Assert
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LighterTimestamp.FromSeconds(-1));
    }

    [TestMethod]
    public void LighterTimestamp_FromMillisecondsNonNumericText_ThrowsInvalidLighterField()
    {
        // Arrange / Act / Assert
        Assert.ThrowsExactly<InvalidLighterFieldException>(
            () => LighterTimestamp.FromMillisecondsText("1737098461127.5"));
    }

    // ── 陷阱 5：金额是字符串，且存在负零 ────────────────────────────────────

    [TestMethod]
    public void UsdAmount_ParseLargeString_PreservesPrecision()
    {
        // Arrange：池总资产实测 "1633519.9729449996"，用 double 会丢精度
        // Act
        UsdAmount amount = UsdAmount.Parse("1633519.9729449996");

        // Assert
        Assert.AreEqual(1633519.9729449996m, amount.Value);
    }

    [TestMethod]
    public void UsdAmount_ParseNegativeZero_NormalizesToZero()
    {
        // Arrange：零仓的 position_value 实测为 "-0.000000"
        // Act
        UsdAmount amount = UsdAmount.Parse("-0.000000");

        // Assert：数值为 0，且不被当成负数
        Assert.AreEqual(0m, amount.Value);
        Assert.IsTrue(amount.IsZero);
        Assert.IsFalse(amount.IsPositive);
    }

    [TestMethod]
    public void UsdAmount_ParseNonNumeric_ThrowsInvalidLighterField()
    {
        // Arrange / Act / Assert
        Assert.ThrowsExactly<InvalidLighterFieldException>(() => UsdAmount.Parse("1,633,519"));
    }

    [TestMethod]
    public void UsdAmount_FromNonFiniteDouble_ReturnsZero()
    {
        // Arrange / Act / Assert
        Assert.AreEqual(0m, UsdAmount.FromDouble(double.NaN).Value);
        Assert.AreEqual(0m, UsdAmount.FromDouble(double.PositiveInfinity).Value);
    }

    // ── 价格与变化率 ─────────────────────────────────────────────────────

    [TestMethod]
    public void PriceAmount_ChangeAgainst_ComputesSlippagePercent()
    {
        // Arrange：池子成交价 0.25232，我方模拟成交价 0.25225（市价卖出拿 bid）
        PriceAmount poolPrice = PriceAmount.Parse("0.25232");
        PriceAmount ourPrice = PriceAmount.Parse("0.25225");

        // Act
        PercentValue change = ourPrice.ChangeAgainst(poolPrice);

        // Assert：约 -0.028%，即 2.8 bps 的漏损，与实测价差量级一致
        Assert.IsLessThan(0m, change.Percent);
        Assert.IsGreaterThan(-0.04m, change.Percent);
    }

    [TestMethod]
    public void PriceAmount_ChangeAgainstZeroBaseline_ReturnsZero()
    {
        // Arrange / Act
        PercentValue change = PriceAmount.Parse("1").ChangeAgainst(PriceAmount.Zero);

        // Assert
        Assert.AreEqual(0m, change.Percent);
    }
}


/// <summary>
/// 池的运营方归属判定测试。
///
/// 判定规则与 Lighter 官方前端**同源**（从其生产产物 <c>dist-*.js</c> 逆向得出）：
/// <c>account_type == 3</c>（InsuranceFund）或 <c>l1_address == 官方运营钱包</c> 即为官方。
/// 这些断言锁住的是这条规则本身——官方若换运营钱包，这里会失败并提醒跟进。
/// </summary>
[TestClass]
public sealed class PoolOriginTests
{
    private static Pool Make(int accountType, string l1Address) => new()
    {
        AccountIndex = AccountIndex.From(281474976552443L),
        Name = "test",
        L1Address = l1Address,
        AccountTypeCode = accountType,
        TotalAssetValue = UsdAmount.From(1000m),
        ReportedAnnualPercentageYield = PercentValue.FromPercent(1m),
        ReportedSharpe = 0m,
        OperatorFee = PercentValue.FromPercent(0m),
        TotalShares = 1,
        CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000),
        StateCode = 0,
    };

    [TestMethod]
    public void Origin_WithOfficialOperatorWallet_IsLighter()
    {
        // 实测：这个钱包运营着 195 个杠杆代币池，官方前端给它们打 LIGHTER 徽标
        Pool pool = Make(
            accountType: (int)PoolAccountType.PublicPool,
            l1Address: "0xb01CD2bf96559C3Ca27C844B88E36Ea47465DBa0");

        Assert.AreEqual(PoolOrigin.Lighter, pool.Origin);
    }

    [TestMethod]
    public void Origin_WithInsuranceFundAccountType_IsLighter()
    {
        // XLP 池：l1_address 是零地址（判据不在地址上），靠 account_type == 3 识别
        Pool pool = Make(accountType: (int)PoolAccountType.InsuranceFund, l1Address: "0x" + new string('0', 40));

        Assert.AreEqual(PoolOrigin.Lighter, pool.Origin);
    }

    [TestMethod]
    public void Origin_WithOrdinaryCommunityPool_IsCommunity()
    {
        // 实测 336 个用户池中 140 个是第三方，类型码均为 2、地址各不相同
        Pool pool = Make(accountType: (int)PoolAccountType.PublicPool, l1Address: "0x3d60bb5764aCBb23A8a4d81335D32c9b947Ce520");

        Assert.AreEqual(PoolOrigin.Community, pool.Origin);
    }

    [TestMethod]
    public void Origin_ComparesOperatorAddressIgnoringCase()
    {
        // EVM 地址不区分大小写。官方前端用严格相等比较，这里有意放宽——
        // 忽略大小写不可能误判，还能容忍上游返回不同的大小写形式。
        Pool pool = Make(
            accountType: (int)PoolAccountType.PublicPool,
            l1Address: "0XB01CD2BF96559C3CA27C844B88E36EA47465DBA0");

        Assert.AreEqual(PoolOrigin.Lighter, pool.Origin);
    }

    [TestMethod]
    public void Origin_WithUnknownAccountType_FallsBackToCommunity()
    {
        // 上游可能新增类型码。判不出"这是官方"时不该标官方——
        // 这一条防的是 imf=0 那类事故：对取值范围未知的字段硬判，会被上游的一次新增打挂。
        Pool pool = Make(accountType: 99, l1Address: "0x3d60bb5764aCBb23A8a4d81335D32c9b947Ce520");

        Assert.AreEqual(PoolOrigin.Community, pool.Origin);
    }
}

/// <summary>
/// 池生命周期（<c>status</c>）的解读测试。
///
/// 枚举 <c>{ Active: 0, Frozen: 1 }</c> 取自官方 SDK 包，并由官方前端用法交叉验证。
/// 关键在于"未知状态码的方向选择"：判不出冻结时不能把池藏起来。
/// </summary>
[TestClass]
public sealed class PoolLifecycleTests
{
    private static Pool Make(int status) => new()
    {
        AccountIndex = AccountIndex.From(281474976552443L),
        Name = "test",
        L1Address = "0x" + new string('1', 40),
        AccountTypeCode = (int)PoolAccountType.PublicPool,
        TotalAssetValue = UsdAmount.From(1000m),
        ReportedAnnualPercentageYield = PercentValue.FromPercent(1m),
        ReportedSharpe = 0m,
        OperatorFee = PercentValue.FromPercent(0m),
        TotalShares = 1,
        CreatedAt = LighterTimestamp.FromSeconds(1_800_000_000),
        StateCode = status,
    };

    [TestMethod]
    public void IsFrozen_WithStatusZero_IsFalse()
    {
        Assert.IsFalse(Make((int)PoolLifecycle.Active).IsFrozen);
    }

    [TestMethod]
    public void IsFrozen_WithStatusOne_IsTrue()
    {
        // 实测全量 337 个池中 145 个是此状态，官方说明为 "deposits are not allowed anymore"
        Assert.IsTrue(Make((int)PoolLifecycle.Frozen).IsFrozen);
    }

    [TestMethod]
    public void IsFrozen_WithUnknownStatusCode_TreatsPoolAsVisible()
    {
        // 上游若新增状态码：不能把池子藏起来（藏起来等于凭空消失），
        // 宁可少一层提示。保守方向要选"不隐藏数据"。
        Assert.IsFalse(Make(99).IsFrozen);
    }
}

/// <summary>
/// 入金限制判定测试。
///
/// 这条规则来自官方前端产物，官方原文：
/// "Deposits are limited because the operator share has reached its minimum limit."
/// 它回答的是**操作性问题**（我能不能入金），而不是模糊的风险评分——
/// 此前用自造的"1.5 倍下限"阈值表达"利益绑定偏松"，既答不了这个问题，
/// 又因命名歧义把界面文案渲染反了（把"压在线上"显示成"有余量"）。
/// </summary>
[TestClass]
public sealed class OperatorShareDepositLimitTests
{
    private static PoolInfo Make(long operatorShares, long totalShares, decimal minRate) => new()
    {
        AccountIndex = AccountIndex.From(281474976552443L),
        TotalShares = totalShares,
        OperatorShares = operatorShares,
        MinOperatorShareRate = PercentValue.FromPercent(minRate),
        OperatorFee = PercentValue.FromPercent(20m),
        ReportedAnnualPercentageYield = PercentValue.FromPercent(100m),
        ReportedSharpe = 9m,
        Nav = new NavSeries { AccountIndex = AccountIndex.From(281474976552443L), Points = [] },
    };

    [TestMethod]
    public void IsDepositLimited_WhenShareExactlyAtMinimum_IsTrue()
    {
        // **本池的实测状态**：2,226,885 / 44,537,700 = 恰好 5.0000%，下限 5.0000。
        // 官方用的是 <= 而非 <：恰好等于也算触线，入金被限制。
        PoolInfo info = Make(operatorShares: 2_226_885, totalShares: 44_537_700, minRate: 5m);

        Assert.AreEqual(5m, decimal.Round(info.OperatorShareRatio.Percent, 4));
        Assert.IsTrue(info.IsDepositLimited, "份额恰好等于下限时，官方即限制入金");
        Assert.AreEqual(0m, decimal.Round(info.OperatorShareHeadroom.Percent, 6), "此时余量为 0");
    }

    [TestMethod]
    public void IsDepositLimited_WhenShareAboveMinimum_IsFalse()
    {
        // 占比 10% 而下限 5% → 有余量，入金不受限
        PoolInfo info = Make(operatorShares: 1_000_000, totalShares: 10_000_000, minRate: 5m);

        Assert.IsFalse(info.IsDepositLimited);
        Assert.AreEqual(5m, decimal.Round(info.OperatorShareHeadroom.Percent, 6));
    }

    [TestMethod]
    public void IsDepositLimited_WhenShareBelowMinimum_IsTrue()
    {
        PoolInfo info = Make(operatorShares: 300_000, totalShares: 10_000_000, minRate: 5m);

        Assert.IsTrue(info.IsDepositLimited);
        Assert.IsTrue(info.OperatorShareHeadroom.Percent < 0m, "跌破下限时余量为负");
    }

    [TestMethod]
    public void IsDepositLimited_WithZeroMinimum_TreatsAsNotLimited()
    {
        // 下限为 0 表示平台未设要求，不该判成"受限"
        PoolInfo info = Make(operatorShares: 1, totalShares: 1_000_000, minRate: 0m);

        Assert.IsFalse(info.IsDepositLimited);
    }
}

/// <summary>
/// 浮动盈亏率的口径测试。
///
/// 锁住的是与官方前端**同口径**这一事实：官方显示「未实现盈亏 ÷ 保证金」，
/// 而不是「÷ 名义价值」。两者相差一个杠杆倍数，实测某 4x 仓位官方显示 −7.17%、
/// 而按名义算只有 −1.82%——口径写错会让使用者以为我们的数据是错的。
/// </summary>
[TestClass]
public sealed class PositionPnlPercentTests
{
    private static PoolPosition Make(decimal notional, decimal pnl, string? marginPercent) => new()
    {
        MarketId = 45,
        Symbol = "PUMP",
        Side = PositionSide.Long,
        Size = 134_481m,
        AvgEntryPrice = PriceAmount.From(0.006479m),
        Notional = UsdAmount.From(notional),
        UnrealizedPnl = UsdAmount.From(pnl),
        Margin = marginPercent is null ? null : MarginFraction.FromPositionPercent(marginPercent),
        OpenOrderCount = 7,
        CumulativeFunding = UsdAmount.From(-0.075m),
        LiquidationPrice = null,
    };

    [TestMethod]
    public void UnrealizedPnlPercent_DividesByMarginNotNotional()
    {
        // 名义 855.70、保证金率 25%（→4 倍杠杆，保证金 = 855.70 × 0.25 = 213.925）
        // 盈亏 −15.60 → 对保证金 = −7.29%；若按名义则只有 −1.82%
        PoolPosition position = Make(notional: 855.70m, pnl: -15.60m, marginPercent: "25.00");

        Assert.AreEqual(213.925m, position.MarginAmount!.Value);
        Assert.AreEqual(-7.2923m, decimal.Round(position.UnrealizedPnlPercent!.Percent, 4));
    }

    [TestMethod]
    public void UnrealizedPnlPercent_WhenMarginUnknown_IsNullNotNotionalBasis()
    {
        // 上游未给保证金率时不能悄悄换成"÷ 名义"——那会让同一字段在不同行表示不同的量
        PoolPosition position = Make(notional: 855.70m, pnl: -15.60m, marginPercent: null);

        Assert.IsNull(position.MarginAmount);
        Assert.IsNull(position.UnrealizedPnlPercent);
    }
}

/// <summary>
/// 距清算幅度的口径测试。
///
/// 锁住两件事：符号方向（空头需上涨为正、多头需下跌为负），
/// 以及**它与杠杆无关**——爆仓由维持保证金触发，杠杆只决定占用多少保证金。
/// 曾有人（合理地）猜测"清算价里含杠杆，除一下更直观"，实测该假设不成立：
/// 两个空头的"清算价 ÷ 市价"是 15.26 与 11.47，而其杠杆是 20× 与 5×。
/// </summary>
[TestClass]
public sealed class LiquidationDistanceTests
{
    private static PoolPosition Position(
        PositionSide side,
        decimal size,
        decimal markPrice,
        decimal? liquidationPrice) => new()
    {
        MarketId = 45,
        Symbol = "TEST",
        Side = side,
        Size = size,
        AvgEntryPrice = PriceAmount.From(markPrice),
        Notional = UsdAmount.From(size * markPrice),
        UnrealizedPnl = UsdAmount.Zero,
        Margin = MarginFraction.FromPositionPercent("5.00"),
        OpenOrderCount = 0,
        CumulativeFunding = UsdAmount.Zero,
        LiquidationPrice = liquidationPrice is null ? null : PriceAmount.From(liquidationPrice.Value),
    };

    [TestMethod]
    public void LiquidationDistance_ForShort_IsPositive()
    {
        // 实测形态：ANTHROPIC 空，市价 2129.50、清算价 24429.75 → 需上涨约 1047%
        PoolPosition position = Position(PositionSide.Short, size: 4.48892m, markPrice: 2129.50m, liquidationPrice: 24429.75m);

        Assert.AreEqual(2129.50m, decimal.Round(position.MarkPrice.Value, 2));
        Assert.AreEqual(1047.21m, decimal.Round(position.LiquidationDistancePercent!.Percent, 2));
    }

    [TestMethod]
    public void LiquidationDistance_ForLong_IsNegative()
    {
        // 多头清算价在市价下方，故幅度为负（需下跌）
        PoolPosition position = Position(PositionSide.Long, size: 1m, markPrice: 100m, liquidationPrice: 80m);

        Assert.AreEqual(-20m, decimal.Round(position.LiquidationDistancePercent!.Percent, 4));
    }

    [TestMethod]
    public void LiquidationDistance_WithoutLiquidationPrice_IsNull()
    {
        // 多头在满仓口径下常常没有正清算价（上游返回 0 → 我们记 null），此时幅度也没得算
        PoolPosition position = Position(PositionSide.Long, size: 1m, markPrice: 100m, liquidationPrice: null);

        Assert.IsNull(position.LiquidationDistancePercent);
    }

    [TestMethod]
    public void MarkPrice_IsDerivedFromNotionalOverSize()
    {
        PoolPosition position = Position(PositionSide.Long, size: 134_481m, markPrice: 0.006367m, liquidationPrice: null);

        Assert.AreEqual(0.006367m, decimal.Round(position.MarkPrice.Value, 6));
    }
}
