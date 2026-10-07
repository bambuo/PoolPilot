using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.UseCases;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Following;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Infrastructure.Monitoring;

/// <summary>
/// 实盘跟单员：把带单员的信号变成**目标交易所上的真实订单**。
///
/// 与纸面跟单员共用一套信号（<see cref="IFollower"/>），但"我方现在持有什么"的来源不同：
/// 纸面从流水重放出来，实盘**从交易所的账户状态读**。以交易所为准是刻意的——
/// 本地算一份、交易所存一份，两份一旦不一致，任何以本地账为准的动作都会越错越远
/// （比如重复开仓）。所以这里不维护本地持仓账，只维护**我发过什么**的日志。
///
/// 三个必须说明的设计点：
/// <list type="number">
///   <item>**复制名义额，不复制数量**。跨场所的合约单位不同（实测 Lighter 的 ANTHROPIC 与
///     OKX 的合约名义价差 10 倍），复制数量会开出 10 倍的仓位。见 <see cref="LiveCopyPlanner"/>。</item>
///   <item>**平掉池子已离场的仓位**。池子平掉某市场后，它的持仓列表里就没有这个标的了，
///     反查不出我方该平哪个合约——所以对应关系从**实盘流水**里查（那是我方唯一还记得的地方）。</item>
///   <item>**没跟上也要留痕**。低于最小下单量、没映射上、被交易所拒，都写进流水并带上原因；
///     只报"成功"的话，"跟单没跟上"这件事在界面上是不可见的。</item>
/// </list>
/// </summary>
public sealed class LiveFollower(
    IServiceScopeFactory scopeFactory,
    IExchangeBrokerFactory brokers,
    IExchangeSymbolCatalogFactory catalogs,
    LiveTradingLimits limits,
    ILogger<LiveFollower> logger,
    Follower config) : IFollower
{
    /// <summary>回看多少条流水来还原"某个合约是从哪个源标的来的"。仓位寿命远短于这个数。</summary>
    private const int SourceLookback = 500;

    private Follower _config = config;

    /// <summary>
    /// 跳过去重。键是**类别 + 池子的仓位数量**。
    ///
    /// 两处都踩过：用原因文本去重等于不去重（文本里带着当时的价，价格一动就是一条"新"原因）；
    /// 用池子的**名义额**去重也不行——名义额 = 数量 × 价，价一动它就动，实测两次心跳写了 2 条。
    /// 要从"池子真的动了"出发：数量是**阶梯函数**，池子成交才变；价格抖动不改变它。
    /// 这与纸面台账的去重口径一致（那边用的也是池仓）。
    /// </summary>
    private readonly HashSet<(string Symbol, LiveSkipKind Kind, decimal PoolSize)> _recordedSkips = [];

    public long Id => _config.Id;

    public AccountIndex LeadAccountIndex => _config.LeadAccountIndex;

    public bool IsActive => _config.IsActive;

    public Follower Config => _config;

    public void Reconfigure(Follower updated) => _config = updated;

    public async Task OnSignalAsync(
        LeadSignal signal,
        PoolSnapshot state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        logger.LogInformation(
            "实盘跟单员 {Id} 收到信号：{Pool} 在 {Symbol} {Kind}（{Before} → {After}）",
            Id,
            signal.PoolName,
            signal.Event.Symbol,
            signal.Kind,
            signal.Event.SizeBefore,
            signal.Event.SizeAfter);

        // **跟动作**：只按池子这一次动作的幅度下单，不对账。
        // 池子没动的标的一律不碰——手动干预过、上次没跟上（低于最小下单额）都不会被"修回去"。
        await ReconcileAsync(state, ReconcileMode.FollowAction, signal.Event, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 挂载/恢复时的对齐：把池子**当前**持有的仓位按比例补上，但每一笔都要过价格门
    /// （见 <see cref="LiveCatchUpRule"/>）——来晚了就买在比池子贵的位置，那不叫跟单，叫接盘。
    /// 且只**增加**敞口：不会因为对齐去减仓或反手（那是"池子没动我也动"，正是要避免的）。
    /// </summary>
    public Task CatchUpAsync(PoolSnapshot state, CancellationToken cancellationToken = default) =>
        ReconcileAsync(state, ReconcileMode.Align, action: null, cancellationToken);

    /// <summary>
    /// 心跳体检：算出"按比例我该持多少、实际持多少"，只记录**不下单**。
    ///
    /// 这是"只跟动作"的代价所在：漏掉的一次（断线、被最小下单额挡住、重启）不会自动补，
    /// 所以必须让人看得见——否则偏离是静默的。
    /// </summary>
    public async Task CheckDriftAsync(PoolSnapshot state, CancellationToken cancellationToken = default) =>
        await ReconcileAsync(state, ReconcileMode.DriftCheck, action: null, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>对账的三种用途。**它们的区别决定了会不会下单**。</summary>
    private enum ReconcileMode
    {
        /// <summary>池子刚发生了动作：只跟这一笔的幅度。</summary>
        FollowAction,

        /// <summary>挂载/恢复：把池子的现有仓位补上（过价格门、只增不减）。</summary>
        Align,

        /// <summary>心跳体检：只算偏差、只记录，不下任何单。</summary>
        DriftCheck,
    }

    private async Task ReconcileAsync(
        PoolSnapshot current,
        ReconcileMode mode,
        PoolEvent? action,
        CancellationToken cancellationToken)
    {
        if (!_config.IsActive)
        {
            return;
        }

        if (_config.ExchangeConfigId is not { } configId)
        {
            logger.LogError("实盘跟单员 {Id} 没有绑定交易所配置，无法下单。请到界面上重新指定。", Id);

            return;
        }

        // 总闸关着时**什么都不做**，而且**不写流水**：那不是"这次没跟上"，
        // 是整条实盘路径被关掉了。写进去反而会让台账上多出一堆看不懂的跳过。
        if (!limits.Enabled)
        {
            logger.LogDebug("实盘总开关是关的，跟单员 {Id} 这一轮不动", Id);

            return;
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;

        IExchangeConfigRepository exchanges = provider.GetRequiredService<IExchangeConfigRepository>();
        ILiveOrderRepository liveOrders = provider.GetRequiredService<ILiveOrderRepository>();

        ExchangeConfig exchange = await exchanges.GetAsync(configId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"交易所配置 {configId} 不存在，实盘跟单员 {Id} 无法下单。");

        string platform = ExchangeEnumText.ToText(exchange.Code);

        IExchangeBroker? broker = brokers.Create(exchange);

        if (broker is null)
        {
            logger.LogError("{Platform} 没有执行侧实现，实盘跟单员 {Id} 这一轮不动", platform, Id);

            return;
        }

        LiveTargetResolver resolver = new(
            provider.GetRequiredService<ISymbolMappingRepository>(),
            catalogs);

        BrokerAccount account = await broker.GetAccountAsync(cancellationToken).ConfigureAwait(false);

        // 我方的持仓按**目标交易对**索引（一个合约一条，多空合并成带符号数量 + 加权开仓均价）。
        // 平台写法 → 资产币/交易币 的翻译交给对接实现的目录去做：那边才知道自己的命名规则
        Dictionary<string, OurPosition> ours = await OurPositionsAsync(
            exchange.Code, account, cancellationToken).ConfigureAwait(false);


        // 源标的 → 目标交易对的对应关系：一边从池子的当前持仓推，另一边从流水里补
        Dictionary<string, decimal> poolNotionalBySymbol = [];
        Dictionary<string, decimal> poolSizeBySymbol = [];
        Dictionary<string, PoolPosition> poolBySymbol = [];
        Dictionary<string, (string Base, string Quote)> targets = [];

        foreach (PoolPosition position in current.ActivePositions)
        {
            poolBySymbol[position.Symbol] = position;

            poolNotionalBySymbol[position.Symbol] = position.Side == PositionSide.Long
                ? position.Notional.Value
                : -position.Notional.Value;

            // 去重用**数量**（阶梯函数），不用名义额（随价连续变化）
            poolSizeBySymbol[position.Symbol] = SignedSize.FromSide(position.Size, position.Side).Value;

            LiveTarget target = await resolver
                .ResolveAsync(platform, position.Symbol, position.MarkPrice.Value, cancellationToken)
                .ConfigureAwait(false);

            if (!target.IsUsable)
            {
                // 解析不出目标时目标交易对是**未知**的：如实写"—"，不要拿源标的冒充
                if (_recordedSkips.Add((
                    position.Symbol,
                    LiveSkipKind.NoTarget,
                    poolSizeBySymbol[position.Symbol])))
                {
                    await RecordSkipAsync(
                    liveOrders,
                    platform,
                    position.Symbol,
                    "—",
                    "—",
                    0m,
                    ourEntryPrice: 0m,
                    position.Notional.Value,
                    current,
                        target.Refusal ?? "解析不出目标合约",
                        cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            targets[position.Symbol] = (target.BaseAsset!, target.QuoteAsset!);
        }

        // 池子已经没有、而我方还持有的合约：从流水里查出它原来是哪个源标的，然后归零
        foreach ((string pair, OurPosition held) in ours.Where(o => o.Value.SignedQuantity != 0m))
        {
            if (targets.Values.Any(t => PairOf(t.Base, t.Quote) == pair))
            {
                continue;
            }

            (string baseAsset, string quoteAsset) = SplitPair(pair);

            IReadOnlyList<string> sources = await liveOrders
                .SourcesForAsync(Id, baseAsset, quoteAsset, SourceLookback, cancellationToken)
                .ConfigureAwait(false);

            if (sources.Count == 0)
            {
                logger.LogWarning(
                    "实盘跟单员 {Id} 持有 {Pair} 但查不到它的来源标的（可能是手工开的仓），不动它",
                    Id,
                    pair);

                continue;
            }

            // 池子已平（名义为 0）→ 目标数量为 0 → 差额就是"把我方平掉"
            poolNotionalBySymbol[sources[0]] = 0m;
            poolSizeBySymbol[sources[0]] = 0m;
            targets[sources[0]] = (baseAsset, quoteAsset);
        }

        decimal scale = LiveCopyPlanner.ScaleFor(_config.InitialCapital, current.TotalAssetValue);

        if (mode == ReconcileMode.DriftCheck)
        {
            // 心跳只体检：算出"按比例该持多少、实际持多少"，记进日志，**不下任何单**。
            // 这是"只跟动作"的代价所在——漏掉的一次不会自动补，所以必须让人看得见
            await ReportDriftAsync(broker, targets, poolNotionalBySymbol, ours, scale, current, cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        foreach ((string sourceSymbol, (string baseAsset, string quoteAsset)) in targets)
        {
            decimal poolNotional = poolNotionalBySymbol.GetValueOrDefault(sourceSymbol);
            decimal poolSize = poolSizeBySymbol.GetValueOrDefault(sourceSymbol);
            OurPosition our = ours.GetValueOrDefault(PairOf(baseAsset, quoteAsset));
            decimal ourQuantity = our.SignedQuantity;

            // 这个标的本轮的目标名义额，**按模式决定**——这是三种用途唯一的分岔处
            decimal? targetNotional = mode switch
            {
                // 跟动作：只处理池子这次动的那个标的，目标是"我方当前 + 这次动作的幅度 × 比例"。
                // 别的标的**一律不碰**（连算都不算）——池子没动就没我什么事
                ReconcileMode.FollowAction => sourceSymbol == action?.Symbol
                    ? ourQuantity * await PriceOfAsync(broker, baseAsset, quoteAsset, cancellationToken)
                        .ConfigureAwait(false)
                        + (action!.NotionalDelta.Value * scale)
                    : null,

                // 对齐：目标是池仓 × 比例，但要过价格门、且只增不减（在下单前判定，见 ReconcileOneAsync）
                ReconcileMode.Align when poolSize != 0m => poolNotional * scale,

                _ => null,
            };

            if (targetNotional is not { } wanted)
            {
                continue;
            }

            await ReconcileOneAsync(
                broker,
                liveOrders,
                platform,
                sourceSymbol,
                baseAsset,
                quoteAsset,
                poolNotional,
                poolSize,
                ourQuantity,
                our.EntryPrice,
                wanted,
                mode,
                poolBySymbol.GetValueOrDefault(sourceSymbol),
                account,
                current,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>体检里已经报过的偏差：按"标的 + 池仓数量"去重，避免每 15 秒刷同一条。</summary>
    private readonly HashSet<(string Symbol, decimal PoolSize)> _reportedDrift = [];

    /// <summary>
    /// 体检：算出"按比例我该持多少、实际持多少"，只写日志。
    ///
    /// 偏差**不自动修**——那是"只跟动作"的代价，也是使用者的选择：
    /// 池子没动的时候不该有单子从系统里冒出来。所以这里只负责让它可见：
    /// 日志 + （每条标的第一次出现时）一条说明，而不是静默地偏离下去。
    /// </summary>
    private async Task ReportDriftAsync(
        IExchangeBroker broker,
        Dictionary<string, (string Base, string Quote)> targets,
        Dictionary<string, decimal> poolNotionalBySymbol,
        Dictionary<string, OurPosition> ours,
        decimal scale,
        PoolSnapshot pool,
        CancellationToken cancellationToken)
    {
        foreach ((string sourceSymbol, (string baseAsset, string quoteAsset)) in targets)
        {
            decimal poolNotional = poolNotionalBySymbol.GetValueOrDefault(sourceSymbol);
            decimal ourQuantity = ours.GetValueOrDefault(PairOf(baseAsset, quoteAsset)).SignedQuantity;

            try
            {
                BrokerInstrument instrument = await broker
                    .GetInstrumentAsync(baseAsset, quoteAsset, cancellationToken)
                    .ConfigureAwait(false);

                decimal price = await broker.GetPriceAsync(baseAsset, quoteAsset, cancellationToken).ConfigureAwait(false);

                if (price <= 0m)
                {
                    continue;
                }

                decimal expected = poolNotional * scale / price;
                decimal deviation = expected - ourQuantity;

                // 小于一个步长/最小下单量的偏差不值得报：那是量化的必然残差
                decimal tolerance = Math.Max(instrument.StepSize, instrument.MinQuantity);

                if (Math.Abs(deviation) <= tolerance)
                {
                    _reportedDrift.Remove((sourceSymbol, poolNotional));
                    continue;
                }

                if (_reportedDrift.Add((sourceSymbol, poolNotional)))
                {
                    logger.LogWarning(
                        "跟单员 {Id} 存在跟踪偏差：{Symbol} 按比例应有 {Expected:F6}、实际 {Actual:F6}"
                        + "（名义 {ExpectedNotional:F2} vs {ActualNotional:F2} USDT）。"
                        + "偏差不会自动修（只跟动作）：要跟上，等池子下次动作，或手动处理",
                        Id,
                        sourceSymbol,
                        expected,
                        ourQuantity,
                        poolNotional * scale,
                        ourQuantity * price);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 体检本身失败不该刷屏，也不该影响别的标的
                logger.LogDebug(ex, "跟单员 {Id} 体检 {Symbol} 时取不到规格或价格", Id, sourceSymbol);
            }
        }
    }

    private static async Task<decimal> PriceOfAsync(
        IExchangeBroker broker,
        string baseAsset,
        string quoteAsset,
        CancellationToken cancellationToken) =>
        await broker.GetPriceAsync(baseAsset, quoteAsset, cancellationToken).ConfigureAwait(false);

    private async Task ReconcileOneAsync(
        IExchangeBroker broker,
        ILiveOrderRepository liveOrders,
        string platform,
        string sourceSymbol,
        string baseAsset,
        string quoteAsset,
        decimal poolNotional,
        decimal poolSize,
        decimal ourQuantity,
        decimal ourEntryPrice,
        decimal targetNotional,
        ReconcileMode mode,
        PoolPosition? poolBySymbolForGate,
        BrokerAccount account,
        PoolSnapshot pool,
        CancellationToken cancellationToken)
    {
        // 先算"要不要动"里不需要网络的部分：目标数量只依赖目标名义额、本金、池子权益与现价
        BrokerInstrument instrument;
        decimal price;

        try
        {
            instrument = await broker.GetInstrumentAsync(baseAsset, quoteAsset, cancellationToken)
                .ConfigureAwait(false);
            price = await broker.GetPriceAsync(baseAsset, quoteAsset, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_recordedSkips.Add((sourceSymbol, LiveSkipKind.NoPrice, poolSize)))
            {
                await RecordSkipAsync(
                    liveOrders, platform, sourceSymbol, baseAsset, quoteAsset, ourQuantity, ourEntryPrice, poolNotional, pool,
                    $"取不到目标合约的规格或价格：{ex.Message}",
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // 对齐要过价格门：**现价不比池子的开仓价差**才补——来晚了就买在比它贵的位置，
        // 那不叫跟单，叫接盘（池子的开仓价是它的成本线，我们的入场价必须不差于它）
        if (mode == ReconcileMode.Align && poolBySymbolForGate is { } poolPosition)
        {
            (bool align, string? refusal) = LiveCatchUpRule.Decide(
                sourceSymbol,
                poolPosition.AvgEntryPrice.Value,
                price,
                poolPosition.Side);

            if (!align)
            {
                if (_recordedSkips.Add((sourceSymbol, LiveSkipKind.NotWorthIt, poolSize)))
                {
                    await RecordSkipAsync(
                        liveOrders, platform, sourceSymbol, baseAsset, quoteAsset, ourQuantity, ourEntryPrice, poolNotional, pool,
                        refusal ?? "现价不如池子的开仓价，先不补",
                        cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            // 只增不减：对齐不该把仓位减下来或反手（那是"池子没动我也动"）
            if (targetNotional < ourQuantity * price)
            {
                logger.LogInformation(
                    "跟单员 {Id}：{Symbol} 按比例应有 {Expected:F2} USDT，我方现有 {Ours:F2} USDT 更多——"
                    + "对齐只增不减，跳过（多出来的部分不会被动）",
                    Id,
                    sourceSymbol,
                    targetNotional,
                    ourQuantity * price);

                return;
            }
        }

        LiveCopyPlan plan = LiveCopyPlanner.Plan(new LiveCopyInput
        {
            TargetNotional = targetNotional,
            PoolEquity = pool.TotalAssetValue,
            Capital = _config.InitialCapital,
            OurQuantity = ourQuantity,
            TargetPrice = price,
            Instrument = instrument,
        });

        if (plan is { SkipReason: { } reason, SkipKind: { } kind })
        {
            if (_recordedSkips.Add((sourceSymbol, kind, poolSize)))
            {
                await RecordSkipAsync(
                    liveOrders, platform, sourceSymbol, baseAsset, quoteAsset, ourQuantity, ourEntryPrice, poolNotional, pool,
                    reason,
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (!plan.HasWork)
        {
            return;
        }

        foreach (LiveCopyLeg leg in plan.Legs)
        {
            (OrderSide side, string positionSide) = BrokerOrderRequest.FromIntent(leg.Intent);

            if (leg.Intent is BrokerOrderIntent.OpenLong or BrokerOrderIntent.OpenShort)
            {
                // 杠杆幂等：重复设置同一个值交易所不会报错，所以不必先查
                try
                {
                    await broker.EnsureLeverageAsync(baseAsset, quoteAsset, limits.Leverage, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("设置 {Base}/{Quote} 的杠杆失败：{Message}", baseAsset, quoteAsset, ex.Message);
                }
            }

            decimal notional = leg.Quantity * price;
            string clientOrderId = $"pp{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 100_000_000}";

            BrokerOrderRequest order = new(
                BaseAsset: baseAsset,
                QuoteAsset: quoteAsset,
                Side: side,
                PositionSide: account.IsHedgeMode ? positionSide : null,
                Quantity: leg.Quantity,
                ReduceOnly: !account.IsHedgeMode
                    && leg.Intent is BrokerOrderIntent.CloseLong or BrokerOrderIntent.CloseShort,
                ClientOrderId: clientOrderId);

            try
            {
                await broker.ValidateOrderAsync(order, cancellationToken).ConfigureAwait(false);

                BrokerOrderResult result = await broker
                    .PlaceOrderAsync(order, cancellationToken)
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "实盘跟单员 {Id} 已下单：{Base}/{Quote} {Intent} {Quantity}（名义 {Notional:F2}）→ 订单 {OrderId}",
                    Id, baseAsset, quoteAsset, leg.Intent, leg.Quantity, notional, result.OrderId);

                await liveOrders.AddAsync(
                    NewOrder(platform, sourceSymbol, baseAsset, quoteAsset, leg.Intent, side, order, price, result.AveragePrice, ourEntryPrice, notional, poolNotional, pool, plan, mode)
                        with
                        {
                            Status = LiveOrderStatus.Placed,
                            VenueOrderId = result.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        },
                    cancellationToken).ConfigureAwait(false);

                // 平掉/减掉之后本地这份快照就旧了：后面的腿按它算差额会算错
                account = await broker.GetAccountAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 交易所的拒单原因原样带出来：那些码是有意义的（-2019 保证金不足、-4164 名义太小）
                logger.LogWarning(
                    "实盘跟单员 {Id} 下单被拒：{Base}/{Quote} {Intent} {Quantity} —— {Message}",
                    Id, baseAsset, quoteAsset, leg.Intent, leg.Quantity, ex.Message);

                await liveOrders.AddAsync(
                    // 被拒的单没有成交均价（回执都不存在），传 0 → 用参考价兜底，
                    // 开仓价仍是"这趟回合"的语境（平仓单上被平仓位的成本线）
                    NewOrder(platform, sourceSymbol, baseAsset, quoteAsset, leg.Intent, side, order, price, fillPrice: 0m, ourEntryPrice, notional, poolNotional, pool, plan, mode)
                        with
                        {
                            Status = LiveOrderStatus.Failed,
                            Reason = $"{ex.GetType().Name}：{ex.Message}",
                        },
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private LiveOrder NewOrder(
        string platform,
        string sourceSymbol,
        string baseAsset,
        string quoteAsset,
        BrokerOrderIntent intent,
        OrderSide side,
        BrokerOrderRequest order,
        decimal price,
        decimal fillPrice,
        decimal ourEntryPrice,
        decimal notional,
        decimal poolNotional,
        PoolSnapshot pool,
        LiveCopyPlan plan,
        ReconcileMode mode) => new()
        {
            FollowerId = Id,
            ExchangeConfigId = _config.ExchangeConfigId!.Value,
            Platform = platform,
            TargetBase = baseAsset,
            TargetQuote = quoteAsset,
            SourceSymbol = sourceSymbol,
            Intent = IntentText(intent),
            Side = side == OrderSide.Buy ? "buy" : "sell",
            PositionSide = order.PositionSide,
            Quantity = order.Quantity,
            Price = price,

            // 两个价按行的身份算：开仓单的"开仓价"是自己的成交均价；
            // 平仓单则带上被平仓位的开仓均价——一行看完整趟回合
            EntryPrice = LiveOrderPrices.Resolve(intent, ourEntryPrice, price, fillPrice).EntryPrice,
            ClosePrice = LiveOrderPrices.Resolve(intent, ourEntryPrice, price, fillPrice).ClosePrice,
            NotionalUsd = notional,
            PoolNotional = poolNotional,
            TargetNotionalUsd = plan.NotionalUsd,
            PoolEquity = pool.TotalAssetValue.Value,
            Capital = _config.InitialCapital.Value,

            // 触发者要分清：池子动作 / 挂载对齐——复盘时第一个要问的就是"这一笔是谁的决定"
            Trigger = mode == ReconcileMode.Align ? LiveOrderTrigger.Align : LiveOrderTrigger.Signal,
            Status = LiveOrderStatus.Placed,
            Reason = null,
            VenueOrderId = null,
            ClientOrderId = order.ClientOrderId,
            CreatedAt = LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        };

    private async Task RecordSkipAsync(
        ILiveOrderRepository liveOrders,
        string platform,
        string sourceSymbol,
        string baseAsset,
        string quoteAsset,
        decimal ourQuantity,
        decimal ourEntryPrice,
        decimal poolNotional,
        PoolSnapshot pool,
        string reason,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("实盘跟单员 {Id} 这一笔没发：{Source} —— {Reason}", Id, sourceSymbol, reason);

        await liveOrders.AddAsync(
            new LiveOrder
            {
                FollowerId = Id,
                ExchangeConfigId = _config.ExchangeConfigId!.Value,
                Platform = platform,
                TargetBase = baseAsset,
                TargetQuote = quoteAsset,
                SourceSymbol = sourceSymbol,
                Intent = "none",
                Side = "none",
                PositionSide = null,
                Quantity = 0m,
                Price = 0m,
                EntryPrice = ourEntryPrice,
                ClosePrice = null,
                NotionalUsd = 0m,
                PoolNotional = poolNotional,

                // 没发出去就没有目标名义：填 0 而不是复制池子名义——
                // 两个字段的含义不同，跳过时"目标"这个量根本没被算出来（或被算成了 0）
                TargetNotionalUsd = 0m,
                PoolEquity = pool.TotalAssetValue.Value,
                Capital = _config.InitialCapital.Value,
                Trigger = LiveOrderTrigger.Signal,
                Status = LiveOrderStatus.Skipped,
                Reason = reason,
                VenueOrderId = null,
                ClientOrderId = null,
                CreatedAt = LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>一个交易对上我方的合并持仓：带符号数量与**加权开仓均价**。</summary>
    private readonly record struct OurPosition(decimal SignedQuantity, decimal EntryPrice)
    {
        public static OurPosition Empty => default;
    }

    /// <summary>
    /// 我方在各合约上的持仓，按目标交易对索引、多空合并成带符号数量与加权开仓均价。
    ///
    /// 双向持仓模式下同一个合约会有两条（LONG 与 SHORT），必须合并成一条——
    /// 分开看会得到"既有多头又有空头"这种在对账里没有意义的形状。
    /// 均价按 |数量| 加权（两侧都有仓时，那是成本的真实混合）。
    ///
    /// 合约符号 → 资产币/交易币 的翻译**问目录**，不在这里拆字符串：
    /// <c>BTCUSDT</c>/<c>BTC-USDT-SWAP</c>/<c>BTC_USDT</c> 是三家各自的写法，
    /// 拆错了不会报错，只会把一个合约认成另一个（然后"对账"出错误的差额）。
    /// 目录里找不到的合约不去猜，直接跳过并留一条日志。
    /// </summary>
    private async Task<Dictionary<string, OurPosition>> OurPositionsAsync(
        ExchangeCode platform,
        BrokerAccount account,
        CancellationToken cancellationToken)
    {
        Dictionary<string, OurPosition> positions = [];

        if (catalogs.Create(platform) is not { } catalog)
        {
            return positions;
        }

        IReadOnlyList<ExchangeSymbolInfo> symbols = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, ExchangeSymbolInfo> byVenueSymbol = symbols
            .GroupBy(s => s.Symbol)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (BrokerPosition position in account.Positions)
        {
            if (position.Size == 0m)
            {
                continue;
            }

            if (!byVenueSymbol.TryGetValue(position.Symbol, out ExchangeSymbolInfo? info))
            {
                logger.LogWarning(
                    "实盘跟单员 {Id}：{Symbol} 不在 {Platform} 的合约目录里，无法确定它是哪个交易对，跳过",
                    Id, position.Symbol, platform);
                continue;
            }

            string key = PairOf(info.BaseAsset, info.QuoteAsset);

            // 方向与符号只由 BrokerPositions 判定：单向模式下 positionSide 是 BOTH，
            // 那时方向藏在金额的符号里（自己在这里推会让单向账户的空头被当成多头）
            OurPosition existing = positions.GetValueOrDefault(key);
            decimal size = position.SignedSize();
            decimal totalAbs = Math.Abs(existing.SignedQuantity) + Math.Abs(size);

            positions[key] = new OurPosition(
                existing.SignedQuantity + size,
                totalAbs > 0m
                    ? (existing.EntryPrice * Math.Abs(existing.SignedQuantity)
                        + position.EntryPrice * Math.Abs(size)) / totalAbs
                    : 0m);
        }

        return positions;
    }

    private static string PairOf(string baseAsset, string quoteAsset) => $"{baseAsset}/{quoteAsset}";

    private static (string Base, string Quote) SplitPair(string pair)
    {
        string[] parts = pair.Split('/');

        return (parts[0], parts.Length > 1 ? parts[1] : string.Empty);
    }

    private static string IntentText(BrokerOrderIntent intent) => intent switch
    {
        BrokerOrderIntent.OpenLong => "open_long",
        BrokerOrderIntent.CloseLong => "close_long",
        BrokerOrderIntent.OpenShort => "open_short",
        _ => "close_short",
    };
}
