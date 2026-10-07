using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Services;

/// <summary>一笔纸面动作的处置方式。</summary>
public enum PaperActionKind
{
    /// <summary>无需动作：目标仓位与现有仓位一致（差额为 0）。</summary>
    None,

    /// <summary>成交。</summary>
    Fill,

    /// <summary>信号有效，但因约束无法执行。**必须记录**，否则账户会显得完美跟踪了池子。</summary>
    Skip,
}

/// <summary>对一笔信号的处置结果。</summary>
public sealed record PaperDecision
{
    public required PaperActionKind Kind { get; init; }

    /// <summary>成交方向；<see cref="PaperActionKind.Skip"/> 与 <see cref="PaperActionKind.None"/> 时为 <c>null</c>。</summary>
    public required OrderSide? Side { get; init; }

    /// <summary>成交数量（绝对值）。</summary>
    public required decimal Size { get; init; }

    public required PriceAmount Price { get; init; }

    public required UsdAmount Notional { get; init; }

    public required UsdAmount Fee { get; init; }

    public required bool DepthSufficient { get; init; }

    public required PaperSkipReason? SkipReason { get; init; }

    /// <summary>比例复制算出的目标仓位，保留下来供界面显示跟踪偏差。</summary>
    public required SignedSize TargetPosition { get; init; }

    /// <summary>动作完成后的我方仓位。</summary>
    public required SignedSize PositionAfter { get; init; }

    /// <summary>本次平仓部分结转的已实现盈亏。</summary>
    public required UsdAmount RealizedPnl { get; init; }
}

/// <summary><see cref="PaperLedger.Decide"/> 的输入。全部显式传入，不读任何外部状态。</summary>
public sealed record PaperDecisionRequest
{
    /// <summary>池子在该市场的最新仓位（带符号），即我们要复制的目标。</summary>
    public required SignedSize PoolPositionAfter { get; init; }

    public required SignedSize OurPositionBefore { get; init; }

    /// <summary>我方在该市场的开仓均价，用于结转已实现盈亏。</summary>
    public required PriceAmount OurEntryVwapBefore { get; init; }

    /// <summary>池子权益，与 <see cref="Capital"/> 一起决定规模比例。</summary>
    public required UsdAmount PoolEquity { get; init; }

    public required UsdAmount Capital { get; init; }

    public required InstrumentSpec Instrument { get; init; }

    public required OrderBookSnapshot Book { get; init; }

    /// <summary>手续费率（百分数）。实测 Lighter Standard 档挂单与吃单均为 0。</summary>
    public required decimal FeePercent { get; init; }
}

/// <summary>由流水重放出的仓位账。</summary>
public sealed record PaperBook
{
    public required IReadOnlyDictionary<int, PaperPosition> Positions { get; init; }

    public UsdAmount RealizedPnl => UsdAmount.From(Positions.Values.Sum(p => p.RealizedPnl.Value));

    public UsdAmount Fees => UsdAmount.From(Positions.Values.Sum(p => p.Fees.Value));

    /// <summary>未平仓的市场。</summary>
    public IReadOnlyList<PaperPosition> OpenPositions => [.. Positions.Values.Where(p => !p.IsFlat)];
}

/// <summary>盯市估值结果。</summary>
public sealed record PaperValuation
{
    public required UsdAmount Equity { get; init; }

    public required UsdAmount RealizedPnl { get; init; }

    public required UsdAmount UnrealizedPnl { get; init; }

    public required UsdAmount Fees { get; init; }

    public required UsdAmount GrossNotional { get; init; }

    /// <summary>收益率（百分数），相对初始本金。</summary>
    public required PercentValue ReturnPercent { get; init; }
}

/// <summary>
/// 纸面跟单账簿。**账户会计的全部规则**。
///
/// 与"执行成本测量"（<see cref="CopySlippageCalculator"/>）的区别：
/// 后者只回答"某一腿比池子差几个基点"，不产生任何账户状态；
/// 这里维护完整的仓位账与盈亏，回答的才是"跟着它做，我的钱会变成多少"。
///
/// 规模口径取**比例复制**：我方目标仓位 = 池子仓位 × (我方本金 / 池子权益)。
/// 只有这个口径下"跟单能不能赚"才有确定答案——因为**止盈止损藏在池子手里**，
/// 跟单者只能继承它的进出场。若改成"每个信号固定下 N 单位"，就必须自己发明
/// 一套出场规则，那测的就不是跟单，而是自己的策略了。
/// </summary>
public static class PaperLedger
{
    /// <summary>比例复制的目标仓位。</summary>
    public static SignedSize TargetFor(SignedSize poolPosition, decimal scale)
    {
        ArgumentNullException.ThrowIfNull(poolPosition);

        return scale <= 0m || poolPosition.IsFlat
            ? SignedSize.Zero
            : SignedSize.From(poolPosition.Value * scale);
    }

    /// <summary>
    /// 比例复制的目标仓位，已量化到交易所步长。
    ///
    /// 对外暴露量化这一步，是为了让界面显示的"应该持多少"与引擎实际下单用的值**必然一致**。
    /// 若让调用方各自量化，界面与账本会出现"看起来差一点"的分歧，那种分歧极难排查。
    /// </summary>
    public static SignedSize TargetFor(SignedSize poolPosition, decimal scale, int sizeDecimals) =>
        Quantize(TargetFor(poolPosition, scale), sizeDecimals);

    /// <summary>规模比例 = 本金 / 池子权益。池子权益非正时返回 0（不跟，而不是无限大）。</summary>
    public static decimal ScaleFor(UsdAmount capital, UsdAmount poolEquity)
    {
        ArgumentNullException.ThrowIfNull(capital);
        ArgumentNullException.ThrowIfNull(poolEquity);

        return poolEquity.Value <= 0m || capital.Value <= 0m
            ? 0m
            : capital.Value / poolEquity.Value;
    }

    /// <summary>
    /// 决定这一笔该做什么。纯函数：同样的输入必得同样的输出，因此可以被完整测试。
    /// </summary>
    public static PaperDecision Decide(PaperDecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        decimal scale = ScaleFor(request.Capital, request.PoolEquity);

        // 目标必须量化到交易所允许的步长。
        // 不量化会被算术残差毒化：7931.8 × 5000/72100 这次算出来与上次的结果相差 1e-18，
        // 于是每收到一条持仓推送都会产生一条"低于最小下单量"的假跳过记录，把台账淹掉。
        // 量化不是权宜之计——交易所本来就不接受带小数尾巴的数量，量化后的账户才是真实可持有的。
        SignedSize target = Quantize(
            TargetFor(request.PoolPositionAfter, scale),
            request.Instrument.SizeDecimals);

        SignedSize delta = SignedSize.From(target.Value - request.OurPositionBefore.Value);

        if (scale <= 0m || delta.IsFlat)
        {
            return NoAction(target, request.OurPositionBefore);
        }

        OrderSide side = delta.Value > 0m ? OrderSide.Buy : OrderSide.Sell;
        decimal requestedSize = delta.Absolute;
        decimal referencePrice = request.Book.BestPriceFor(side);

        if (referencePrice <= 0m)
        {
            return Skipped(target, side, PaperSkipReason.NoLiquidity, request.OurPositionBefore);
        }

        PaperSkipReason? violated = BindingConstraint(request, requestedSize, referencePrice);

        if (violated is { } reason)
        {
            return Skipped(target, side, reason, request.OurPositionBefore);
        }

        // 走盘口取加权成交价。深度不足时按实际能吃到的量成交，不假装全成。
        BookWalkResult walk = CopySlippageCalculator.Walk(request.Book, side, requestedSize);

        if (walk.FilledSize <= 0m)
        {
            return Skipped(target, side, PaperSkipReason.NoLiquidity, request.OurPositionBefore);
        }

        decimal signedFill = side == OrderSide.Buy ? walk.FilledSize : -walk.FilledSize;
        SizingResult sizing = ApplyFill(
            request.OurPositionBefore,
            request.OurEntryVwapBefore,
            SignedSize.From(signedFill),
            walk.Vwap);

        decimal fee = walk.Notional.Value * request.FeePercent / 100m;

        return new PaperDecision
        {
            Kind = PaperActionKind.Fill,
            Side = side,
            Size = walk.FilledSize,
            Price = walk.Vwap,
            Notional = walk.Notional,
            Fee = UsdAmount.From(fee),
            DepthSufficient = walk.FullyFilled,
            SkipReason = null,
            TargetPosition = target,
            PositionAfter = sizing.PositionAfter,
            RealizedPnl = sizing.RealizedPnl,
        };
    }

    /// <summary>
    /// 判断违反了哪个下单约束；都没违反时返回 <c>null</c>。
    ///
    /// 两个约束可能同时被违反，此时报告**真正卡住的那个**：把两个约束各自换算成
    /// "要多少本金才能跨过"，取要求更高的那个。只说"低于最小下单数量"而实际门槛
    /// 来自最小下单金额，会让人以为"把仓位调大一点就行"，而真正的解法是加本金。
    /// </summary>
    private static PaperSkipReason? BindingConstraint(
        PaperDecisionRequest request,
        decimal requestedSize,
        decimal referencePrice)
    {
        bool sizeViolated = requestedSize < request.Instrument.MinOrderSize;
        bool notionalViolated = requestedSize * referencePrice < request.Instrument.MinOrderNotional;

        if (!sizeViolated && !notionalViolated)
        {
            return null;
        }

        // 每 1 USDT 本金对应的等比池仓数量，用它把两个约束都换算成所需本金
        decimal poolPerCapital = request.PoolEquity.Value <= 0m
            ? 0m
            : request.PoolPositionAfter.Absolute / request.PoolEquity.Value;

        if (poolPerCapital <= 0m)
        {
            return PaperSkipReason.BelowMinimumSize;
        }

        decimal capitalForSize = request.Instrument.MinOrderSize / poolPerCapital;
        decimal capitalForNotional = request.Instrument.MinOrderNotional / (poolPerCapital * referencePrice);

        return capitalForNotional >= capitalForSize
            ? PaperSkipReason.BelowMinimumNotional
            : PaperSkipReason.BelowMinimumSize;
    }

    /// <summary>把数量对齐到 <paramref name="sizeDecimals"/> 位小数（即交易所的步长），四舍五入到最近一档。</summary>
    private static SignedSize Quantize(SignedSize size, int sizeDecimals)
    {
        if (size.IsFlat)
        {
            return SignedSize.Zero;
        }

        int decimals = Math.Clamp(sizeDecimals, 0, 8);
        decimal step = 1m;

        for (int i = 0; i < decimals; i++)
        {
            step /= 10m;
        }

        return SignedSize.From(decimal.Round(size.Value / step, 0, MidpointRounding.AwayFromZero) * step);
    }

    /// <summary>
    /// 跳过记录的幂等键。
    ///
    /// 写入前的去重判定、以及服务重启后从流水重建去重表，**必须用同一套键**。
    /// 曾两次在这里栽跟头：
    /// 第一次是两边各写一套键（一边"我方仓位"、一边"池仓"），键永远匹配不上，
    /// 每次重启补写一条重复记录——所以键只在这里构造一次；
    /// 第二次是键做成了**字符串**：<c>decimal.ToString()</c> 保留小数位数（scale），
    /// 上游给的 134481 经 SQLite TEXT 往返变成 134481.0，两个字符串不相等，
    /// 于是又每次重启补写重复。教训：**数值等值比较必须留在数值层面**——
    /// <c>decimal</c> 的相等与哈希与 scale 无关，把数值转成字符串再比较，
    /// 等于把数值语义换成文本语义，scale 这种存储细节就会漏进来。
    /// </summary>
    /// <param name="poolPosition">键含池仓的理由：池仓一变，说明池子真的动了，
    /// 那条跳过才有记录价值——它的含义是"池子这一动，我没跟上"。</param>
    public static (int MarketId, PaperSkipReason Reason, decimal PoolPosition) SkipKey(
        int marketId, PaperSkipReason reason, decimal poolPosition) =>
        (marketId, reason, poolPosition);

    /// <summary>取一条流水的幂等键；成交记录返回 <c>null</c>。</summary>
    public static (int MarketId, PaperSkipReason Reason, decimal PoolPosition)? SkipKeyOf(PaperFill fill)
    {
        ArgumentNullException.ThrowIfNull(fill);

        return fill.SkipReason is { } reason
            ? SkipKey(fill.MarketId, reason, fill.PoolPositionAfter.Value)
            : null;
    }

    /// <summary>
    /// 由成交流水重放出仓位账。
    ///
    /// 仓位账**只从流水推出**，不单独存一份。这样"持仓与流水对不上"这种脏状态
    /// 在结构上就不可能存在。
    /// </summary>
    public static PaperBook Replay(IReadOnlyList<PaperFill> fills)
    {
        ArgumentNullException.ThrowIfNull(fills);

        Dictionary<int, PaperPosition> book = [];

        foreach (PaperFill fill in fills.OrderBy(f => f.ObservedAt.Utc).ThenBy(f => f.Id))
        {
            if (!fill.IsFilled)
            {
                // 被跳过的信号不改变仓位，但保留在流水里可追溯
                continue;
            }

            book.TryGetValue(fill.MarketId, out PaperPosition? existing);

            decimal before = existing?.Size.Value ?? 0m;
            decimal after = fill.PositionAfter.Value;

            book[fill.MarketId] = new PaperPosition
            {
                MarketId = fill.MarketId,
                Symbol = fill.Symbol,
                Size = SignedSize.From(after),
                EntryVwap = ResolveEntryVwap(before, existing?.EntryVwap, after, fill),
                RealizedPnl = UsdAmount.From((existing?.RealizedPnl.Value ?? 0m) + fill.RealizedPnl.Value),
                Fees = UsdAmount.From((existing?.Fees.Value ?? 0m) + fill.Fee.Value),
                FillCount = (existing?.FillCount ?? 0) + 1,
            };
        }

        return new PaperBook { Positions = book };
    }

    /// <summary>盯市估值。<paramref name="marks"/> 缺失的市场按开仓均价估（即未实现盈亏为 0）。</summary>
    public static PaperValuation Value(
        PaperBook book,
        UsdAmount initialCapital,
        IReadOnlyDictionary<int, PriceAmount> marks)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(initialCapital);
        ArgumentNullException.ThrowIfNull(marks);

        decimal unrealized = 0m;
        decimal grossNotional = 0m;

        foreach (PaperPosition position in book.OpenPositions)
        {
            PriceAmount mark = marks.TryGetValue(position.MarketId, out PriceAmount? found) && found.Value > 0m
                ? found
                : position.EntryVwap;

            unrealized += position.UnrealizedPnlAt(mark).Value;
            grossNotional += position.NotionalAt(mark).Value;
        }

        decimal realized = book.RealizedPnl.Value;
        decimal fees = book.Fees.Value;
        decimal equity = initialCapital.Value + realized + unrealized - fees;

        return new PaperValuation
        {
            Equity = UsdAmount.From(equity),
            RealizedPnl = UsdAmount.From(realized),
            UnrealizedPnl = UsdAmount.From(unrealized),
            Fees = UsdAmount.From(fees),
            GrossNotional = UsdAmount.From(grossNotional),
            ReturnPercent = initialCapital.Value <= 0m
                ? PercentValue.FromPercent(0m)
                : PercentValue.FromPercent((equity - initialCapital.Value) / initialCapital.Value * 100m),
        };
    }

    /// <summary>
    /// 把一笔成交应用到仓位账上，产出新仓位、新开仓均价与结转的已实现盈亏。
    ///
    /// 四种情形必须分开处理，合成一条公式一定会漏：
    /// 加仓（均价加权）、减仓（结转部分盈亏、均价不变）、全平（均价归零）、反手（先平后开）。
    /// </summary>
    private static SizingResult ApplyFill(
        SignedSize before,
        PriceAmount entryVwapBefore,
        SignedSize signedFill,
        PriceAmount fillPrice)
    {
        SignedSize after = SignedSize.From(before.Value + signedFill.Value);

        if (before.IsFlat)
        {
            return new SizingResult(after, UsdAmount.Zero);
        }

        bool isReducing = !before.HasSameDirectionAs(signedFill);

        if (!isReducing)
        {
            // 加仓：均价按数量加权
            return new SizingResult(after, UsdAmount.Zero);
        }

        decimal closingSize = Math.Min(before.Absolute, signedFill.Absolute);

        // 平掉的那部分结转盈亏。符号靠 before 的方向承担：空头为负，价格下跌时结果为正。
        decimal realized = (fillPrice.Value - entryVwapBefore.Value) * closingSize * Math.Sign(before.Value);

        return new SizingResult(after, UsdAmount.From(realized));
    }

    /// <summary>
    /// 重放时还原开仓均价。
    ///
    /// 成交记录里只存了成交价与仓位前后值，没存当时的均价——均价是可以推出来的，
    /// 多存一份反而多一个可能不一致的来源。
    /// </summary>
    private static PriceAmount ResolveEntryVwap(
        decimal before,
        PriceAmount? entryVwapBefore,
        decimal after,
        PaperFill fill)
    {
        decimal fillPrice = fill.Price.Value;
        PriceAmount previous = entryVwapBefore ?? PriceAmount.Zero;

        if (before == 0m)
        {
            // 从空仓开出
            return fillPrice > 0m ? fill.Price : PriceAmount.Zero;
        }

        if (after == 0m)
        {
            // 全平，均价归零
            return PriceAmount.Zero;
        }

        bool flipped = Math.Sign(before) != Math.Sign(after);
        bool added = Math.Sign(before) == Math.Sign(after) && Math.Abs(after) > Math.Abs(before);

        if (flipped || previous.Value <= 0m)
        {
            // 反手：旧仓已被全部平掉，剩余部分按本次成交价开仓
            return fill.Price;
        }

        if (!added)
        {
            // 纯减仓：均价不变
            return previous;
        }

        decimal addedSize = Math.Abs(after) - Math.Abs(before);

        return PriceAmount.From(
            ((previous.Value * Math.Abs(before)) + (fillPrice * addedSize)) / Math.Abs(after));
    }

    private static PaperDecision NoAction(SignedSize target, SignedSize positionBefore) => new()
    {
        Kind = PaperActionKind.None,
        Side = null,
        Size = 0m,
        Price = PriceAmount.Zero,
        Notional = UsdAmount.Zero,
        Fee = UsdAmount.Zero,
        DepthSufficient = true,
        SkipReason = null,
        TargetPosition = target,
        PositionAfter = positionBefore,
        RealizedPnl = UsdAmount.Zero,
    };

    private static PaperDecision Skipped(
        SignedSize target,
        OrderSide side,
        PaperSkipReason reason,
        SignedSize positionBefore) => new()
    {
        Kind = PaperActionKind.Skip,
        Side = side,
        Size = 0m,
        Price = PriceAmount.Zero,
        Notional = UsdAmount.Zero,
        Fee = UsdAmount.Zero,
        DepthSufficient = true,
        SkipReason = reason,
        TargetPosition = target,
        PositionAfter = positionBefore,
        RealizedPnl = UsdAmount.Zero,
    };

    private readonly record struct SizingResult(SignedSize PositionAfter, UsdAmount RealizedPnl);
}
