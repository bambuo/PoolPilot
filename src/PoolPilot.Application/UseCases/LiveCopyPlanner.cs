using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 实盘跟单的算账：池子的持仓 → 我方该在目标交易所持多少、发什么单。
///
/// **与纸面跟单的根本差别在"复制什么"**：纸面跟单跟的是同一个场所（Lighter），
/// 所以按**仓位数量**等比复制就对了；实盘要跨到另一个场所，而各家的合约单位不一样
/// （实测 Lighter 的 ANTHROPIC 与 OKX 的 ANTHROPIC-USDT-SWAP 名义价差 10 倍），
/// 按数量复制会开出 10 倍的仓位。所以这里复制的是**名义额**：
///
/// <code>
/// 目标名义额 = 池子该仓位的名义额 × 规模比例
/// 目标数量   = 目标名义额 ÷ 目标合约现价（再量化到步长）
/// </code>
///
/// 名义额是"这个仓位值多少钱"，与合约单位无关，跨场所才是可比的量。
/// 反过来说，只有当池子的名义额本身就是错的（取不到价、价格异常）时它才会错，
/// 而那属于上游数据问题，不该由这里猜。
///
/// 纯函数：同样的输入必得同样的输出，因此可以被完整测试——实盘路径上这是最该被钉死的部分。
/// </summary>
public static class LiveCopyPlanner
{
    /// <summary>规模比例 = 本金 ÷ 池子权益。池子权益非正时返回 0（不跟，而不是无限大）。</summary>
    public static decimal ScaleFor(UsdAmount capital, UsdAmount poolEquity)
    {
        ArgumentNullException.ThrowIfNull(capital);
        ArgumentNullException.ThrowIfNull(poolEquity);

        return poolEquity.Value <= 0m || capital.Value <= 0m
            ? 0m
            : capital.Value / poolEquity.Value;
    }

    /// <summary>
    /// 规划这一轮要发的单。
    ///
    /// 反手（多头直接翻空头）会拆成**两笔**：先平掉原方向，再开新方向。
    /// 不合成一笔的理由是双向持仓模式下"卖出"要配 positionSide 才有意义，
    /// 一笔既平多又开空的订单在参数上根本表达不出来；而且平仓与开仓分开记，
    /// 流水的每一步都能与交易所的成交流对上。
    /// </summary>
    public static LiveCopyPlan Plan(LiveCopyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        decimal scale = ScaleFor(input.Capital, input.PoolEquity);

        // 目标是"不持有"这一档**排在比例检查之前**：平仓不需要算仓位大小，
        // 所以哪怕本金或池子权益取不到（比例算不出来），该平的仓也照样要平——
        // 否则一个凭证/配置问题会把仓位锁在账户里，那是最不该发生的
        if (input.TargetNotional == 0m)
        {
            return input.OurQuantity == 0m
                ? LiveCopyPlan.Nothing()
                : LiveCopyPlan.Close(
                    input.OurQuantity > 0m ? BrokerOrderIntent.CloseLong : BrokerOrderIntent.CloseShort,
                    Math.Abs(input.OurQuantity),
                    0m,
                    scale,
                    input);
        }

        if (scale <= 0m)
        {
            return LiveCopyPlan.Skip(
                LiveSkipKind.NoScale,
                "本金或池子权益非正，算不出规模比例——跟多大仓这个数没有答案时，不该猜一个");
        }

        decimal targetNotional = input.TargetNotional;

        if (input.TargetPrice <= 0m)
        {
            return LiveCopyPlan.Skip(LiveSkipKind.NoPrice, "取不到目标合约的价格，无法把名义价值换算成数量");
        }

        decimal targetQuantity = Quantize(targetNotional / input.TargetPrice, input.Instrument);

        // **目标量化成 0 的两种来路，结论都是"这个标的上我不该有仓位"**，所以：
        //   · 我方已有仓位 → **平掉**。池子减仓到很小时按比例算出的目标会落到步长以下，
        //     此时若只是"这次不动作"，就会留着池子已经卸掉的风险敞口
        //     （实测的形态：池子卸掉 90% 多，我方一分未动，而这在界面上看起来一切正常）。
        //   · 我方本来就是空仓 → 记一笔"跟不动"，让人知道这个标的一直没跟上。
        if (targetQuantity == 0m)
        {
            // 目标名义额非零（为零的情况上面处理过），却被步长量化成 0——两种走向：
            //   · 我方还持着仓 → **平掉**。目标"接近于零"在交易所里能表达的最小仓位就是 0；
            //     留着不动等于让我方继续承担池子已经卸掉的那部分敞口（实测：池子减到 1/15，
            //     按比例目标只剩 1.36 USDT、量化成 0，而旧逻辑留了 17.7 USDT 挂着）。
            //   · 我方本来就空仓 → 记一笔"跟不动"，让人知道这个标的一直没跟上。
            if (input.OurQuantity != 0m)
            {
                return LiveCopyPlan.Close(
                    input.OurQuantity > 0m ? BrokerOrderIntent.CloseLong : BrokerOrderIntent.CloseShort,
                    Math.Abs(input.OurQuantity),
                    targetNotional,
                    scale,
                    input);
            }

            return LiveCopyPlan.Skip(
                LiveSkipKind.TooSmall,
                $"按比例算出的目标名义价值 {Math.Abs(targetNotional):F2} USDT，"
                + $"折算成数量后被交易所步长量化成 0（步长 {input.Instrument.StepSize}，"
                + $"价格 {input.TargetPrice}）——本金太小，这个标的跟不动");
        }

        decimal delta = targetQuantity - input.OurQuantity;

        if (delta == 0m)
        {
            return LiveCopyPlan.Nothing();
        }

        List<LiveCopyLeg> legs = [];

        // 该发什么单，看的是**目标方向与现有持仓的关系**，不是差额的正负：
        // 差额为负既可能是"减仓"（目标仍为多，只是比现在少），也可能是"反手"（目标已转空）——
        // 两者在双向持仓模式下的参数完全不同（平多 vs 平多再开空），
        // 用差额的正负去判断会把减仓写成"先平干净再开空"，那是把仓位反过来了。
        if (targetQuantity > 0m)
        {
            if (input.OurQuantity < 0m)
            {
                legs.Add(new LiveCopyLeg(BrokerOrderIntent.CloseShort, Math.Abs(input.OurQuantity)));
                legs.Add(new LiveCopyLeg(BrokerOrderIntent.OpenLong, targetQuantity));
            }
            else if (delta > 0m)
            {
                legs.Add(new LiveCopyLeg(BrokerOrderIntent.OpenLong, delta));
            }
            else if (delta < 0m)
            {
                legs.Add(new LiveCopyLeg(BrokerOrderIntent.CloseLong, Math.Abs(delta)));
            }
        }
        else if (targetQuantity < 0m)
        {
            if (input.OurQuantity > 0m)
            {
                legs.Add(new LiveCopyLeg(BrokerOrderIntent.CloseLong, Math.Abs(input.OurQuantity)));
                legs.Add(new LiveCopyLeg(BrokerOrderIntent.OpenShort, Math.Abs(targetQuantity)));
            }
            else if (delta < 0m)
            {
                legs.Add(new LiveCopyLeg(BrokerOrderIntent.OpenShort, Math.Abs(delta)));
            }
            else if (delta > 0m)
            {
                legs.Add(new LiveCopyLeg(BrokerOrderIntent.CloseShort, Math.Abs(delta)));
            }
        }
        else if (input.OurQuantity != 0m)
        {
            // 目标归零（池子平了）：把我方平掉。带符号数量的方向决定平哪一侧
            legs.Add(input.OurQuantity > 0m
                ? new LiveCopyLeg(BrokerOrderIntent.CloseLong, Math.Abs(input.OurQuantity))
                : new LiveCopyLeg(BrokerOrderIntent.CloseShort, Math.Abs(input.OurQuantity)));
        }

        // 最后一道：交易所自己的硬规则。过不了就如实说差多少，而不是发出去等它拒单
        List<LiveCopyLeg> sendable = [];
        List<string> problems = [];

        foreach (LiveCopyLeg leg in legs)
        {
            decimal notional = leg.Quantity * input.TargetPrice;

            if (leg.Quantity < input.Instrument.MinQuantity)
            {
                problems.Add($"{leg.Intent} 的数量 {leg.Quantity} 低于最小下单量 {input.Instrument.MinQuantity}");
                continue;
            }

            if (input.Instrument.MinNotional > 0m && notional < input.Instrument.MinNotional)
            {
                problems.Add($"{leg.Intent} 的名义额 {notional:F2} USDT 低于最小名义价值 {input.Instrument.MinNotional} USDT");
                continue;
            }

            sendable.Add(leg);
        }

        return problems.Count > 0 && sendable.Count == 0
            ? LiveCopyPlan.Skip(LiveSkipKind.TooSmall, string.Join("；", problems))
            : new LiveCopyPlan
            {
                TargetQuantity = targetQuantity,
                Delta = delta,
                NotionalUsd = targetNotional,
                Scale = scale,
                Legs = sendable,
                Problems = problems,
            };
    }

    /// <summary>
    /// 把数量向下量化到交易所的步长。
    ///
    /// **向下而不是四舍五入**：多下一点会超出"按比例算出的名义额"，少下一点只是没跟满。
    /// 与纸面跟单同一口径（<see cref="Domain.Services.PaperLedger"/> 的量化亦然），
    /// 两边的"我应该持多少"才对得上。
    /// </summary>
    private static decimal Quantize(decimal quantity, BrokerInstrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);

        if (instrument.StepSize <= 0m)
        {
            return decimal.Round(quantity, Math.Max(0, instrument.QuantityPrecision), MidpointRounding.ToZero);
        }

        decimal steps = decimal.Truncate(quantity / instrument.StepSize);

        return decimal.Round(steps * instrument.StepSize, Math.Max(0, instrument.QuantityPrecision), MidpointRounding.ToZero);
    }
}

/// <summary>规划实盘跟单一笔动作的输入。</summary>
public sealed record LiveCopyInput
{
    /// <summary>
    /// **本次的目标名义额**（带符号，正多负空）。0 表示目标是不持有（平掉）。
    ///
    /// 由调用方决定它怎么来，这是跟单语义的分界处：
    /// <list type="bullet">
    ///   <item><b>池子动作</b>：目标 = 我方当前名义 + 池子本次动作的名义 × 比例
    ///     ——**只跟动作**，池子没动就不动，人手动干预过也不会被"修回去"。</item>
    ///   <item><b>挂载/恢复时的对齐</b>：目标 = 池仓名义 × 比例，
    ///     且必须先过价格门（<see cref="LiveCatchUpRule"/>）——来晚了不追高。</item>
    /// </list>
    /// </summary>
    public required decimal TargetNotional { get; init; }

    public required UsdAmount PoolEquity { get; init; }

    public required UsdAmount Capital { get; init; }

    /// <summary>我方在目标合约上现在的持仓，**带符号**（按目标合约的单位）。</summary>
    public required decimal OurQuantity { get; init; }

    /// <summary>目标合约的现价。</summary>
    public required decimal TargetPrice { get; init; }

    /// <summary>目标合约的规格（步长、最小量、最小名义额）。</summary>
    public required BrokerInstrument Instrument { get; init; }
}

/// <summary>一笔要发的单。</summary>
public sealed record LiveCopyLeg(BrokerOrderIntent Intent, decimal Quantity);

/// <summary>规划结果。</summary>
public sealed record LiveCopyPlan
{
    /// <summary>目标仓位（按目标合约单位，带符号）。</summary>
    public decimal TargetQuantity { get; init; }

    /// <summary>本次变动量（带符号）。</summary>
    public decimal Delta { get; init; }

    /// <summary>目标名义额（带符号）。</summary>
    public decimal NotionalUsd { get; init; }

    public decimal Scale { get; init; }

    /// <summary>要发的单；空且 <see cref="SkipReason"/> 为空表示"无事可做"。</summary>
    public IReadOnlyList<LiveCopyLeg> Legs { get; init; } = [];

    /// <summary>部分腿过不了交易所规格时的说明（其余腿照发）。</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>不发的理由。非空即表示这一轮不发单。</summary>
    public string? SkipReason { get; init; }

    /// <summary>
    /// 不发的**类别**。文本里带着当时的价与量（那是排查要看的细节），
    /// 所以不能拿文本去重——价格每动一次就是一条新文本，去重会失效、台账会被同一件事刷屏。
    /// 去重按这个类别（加上池仓）来。
    /// </summary>
    public LiveSkipKind? SkipKind { get; init; }

    public bool HasWork => Legs.Count > 0;

    public static LiveCopyPlan Nothing() => new();

    public static LiveCopyPlan Skip(LiveSkipKind kind, string reason) => new()
    {
        SkipReason = reason,
        SkipKind = kind,
    };

    /// <summary>
    /// 平掉现有仓位的一笔。与"加仓"走同一套交易所硬规则检查——
    /// 平仓单一样要满足最小下单量与最小名义额（不满足时上面的检查会把它记成没跟上）。
    /// </summary>
    internal static LiveCopyPlan Close(
        BrokerOrderIntent intent,
        decimal quantity,
        decimal targetNotional,
        decimal scale,
        LiveCopyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        decimal notional = quantity * input.TargetPrice;

        if (quantity < input.Instrument.MinQuantity)
        {
            return Skip(
                LiveSkipKind.TooSmall,
                $"要平的仓位 {quantity} 低于交易所最小下单量 {input.Instrument.MinQuantity}，平不掉");
        }

        if (input.Instrument.MinNotional > 0m && notional < input.Instrument.MinNotional)
        {
            return Skip(
                LiveSkipKind.TooSmall,
                $"要平的仓位名义价值 {notional:F2} USDT 低于最小名义价值 {input.Instrument.MinNotional} USDT，平不掉");
        }

        return new LiveCopyPlan
        {
            TargetQuantity = 0m,
            Delta = -quantity,
            NotionalUsd = targetNotional,
            Scale = scale,
            Legs = [new LiveCopyLeg(intent, quantity)],
        };
    }
}

/// <summary>没发出去的几类原因。与纸面台账的跳过原因同一用途：去重、分组、界面筛选。</summary>
public enum LiveSkipKind
{
    /// <summary>本金或池子权益非正，规模比例算不出来。</summary>
    NoScale = 0,

    /// <summary>取不到目标合约的规格或价格。</summary>
    NoPrice = 1,

    /// <summary>按比例算出的名义额过不了交易所的最小下单量/最小名义额。</summary>
    TooSmall = 2,

    /// <summary>这个标的在目标交易所上没有对应合约（或同名但价格对不上）。</summary>
    NoTarget = 3,

    /// <summary>现价不如池子的开仓价：补上去等于买在比它贵的位置（见 <c>LiveCatchUpRule</c>）。</summary>
    NotWorthIt = 4,
}
