using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Application.Mapping;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// **手动平仓**：把实盘账户上的某一笔仓位平掉。这是人下的指令，不是信号驱动的动作。
///
/// 三条纪律：
/// <list type="number">
///   <item>**数量以交易所为准，不信传进来的数字**。请求只说"平哪个交易对"，
///     实际数量在成交前现场读一次账户——界面上的数字可能已经过期几秒，
///     照着一个过期的数量下单会要么平不干净、要么把仓位反向打开。</item>
///   <item>**平仓是"减"，不是"开反方向"**。意图由持仓方向定死（多头 → CloseLong，
///     空头 → CloseShort），这样双向模式下卖出才会配 LONG 侧，不会把平多写成开空。</item>
///   <item>**留痕，并标明是手动的**。流水里既有信号驱动的单、也有人点的单，
///     不区分的话复盘时无法回答"这一笔是谁做的决定"。</item>
/// </list>
///
/// 总闸（<c>LiveTrading:Enabled</c>）关着时**拒绝**：那个开关的含义就是"现在不下任何单"，
/// 悄悄绕过去会让它失去意义。要平仓就先开总闸，或者到交易所界面上平。
/// </summary>
public sealed class CloseLivePositionUseCase(
    IFollowerRepository followers,
    IExchangeConfigRepository exchanges,
    IExchangeBrokerFactory brokers,
    IExchangeSymbolCatalogFactory catalogs,
    ILiveOrderRepository liveOrders,
    LiveTradingLimits limits,
    ILeadTraderSync sync)
{
    public async Task<LiveOrderDto> ExecuteAsync(
        long followerId,
        CloseLivePositionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Follower follower = await followers.GetAsync(followerId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"跟单员 {followerId} 不存在。", nameof(followerId));

        if (follower.ExchangeConfigId is not { } configId)
        {
            throw new ArgumentException($"跟单员 {followerId} 不是实盘（没有绑定交易所账号）。", nameof(followerId));
        }

        if (!limits.Enabled)
        {
            throw new ArgumentException(
                "实盘总开关是关的（LiveTrading:Enabled=false），现在不会下任何单——"
                + "包括手动平仓。要平仓请先打开总开关，或到交易所界面上操作。",
                nameof(request));
        }

        ExchangeConfig config = await exchanges.GetAsync(configId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"交易所配置 {configId} 不存在。", nameof(followerId));

        IExchangeBroker broker = brokers.Create(config)
            ?? throw new ArgumentException($"{config.DisplayName} 没有执行侧实现，下不了单。", nameof(followerId));

        string baseAsset = request.BaseAsset.Trim().ToUpperInvariant();
        string quoteAsset = request.QuoteAsset.Trim().ToUpperInvariant();

        // 现场读账户，从里面找出要平的那一笔。找不到就说找不到——
        // 不拿界面上的旧数字去下单调
        BrokerAccount account = await broker.GetAccountAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, ExchangeSymbolInfo> byVenueSymbol = await VenueSymbolsAsync(
            config.Code, cancellationToken).ConfigureAwait(false);

        BrokerPosition? position = account.Positions.FirstOrDefault(p =>
            p.SignedSize() != 0m
            && byVenueSymbol.TryGetValue(p.Symbol, out ExchangeSymbolInfo? info)
            && string.Equals(info.BaseAsset, baseAsset, StringComparison.OrdinalIgnoreCase)
            && string.Equals(info.QuoteAsset, quoteAsset, StringComparison.OrdinalIgnoreCase));

        if (position is null)
        {
            throw new ArgumentException(
                $"交易所账号上没有 {baseAsset}/{quoteAsset} 的持仓（可能刚刚已经平掉了）。刷新一下再看。",
                nameof(request));
        }

        BrokerInstrument instrument = await broker
            .GetInstrumentAsync(baseAsset, quoteAsset, cancellationToken)
            .ConfigureAwait(false);

        decimal price = await broker.GetPriceAsync(baseAsset, quoteAsset, cancellationToken).ConfigureAwait(false);
        decimal quantity = instrument.Quantize(Math.Abs(position.SignedSize()));

        if (quantity <= 0m)
        {
            throw new ArgumentException(
                $"{baseAsset}/{quoteAsset} 的持仓数量 {Math.Abs(position.SignedSize())} 量化到步长后为 0，平不了。",
                nameof(request));
        }

        // 方向决定意图：多头只能"平多"，空头只能"平空"。用别的写法就会把平仓变成反向开仓
        BrokerOrderIntent intent = position.Direction() == "short"
            ? BrokerOrderIntent.CloseShort
            : BrokerOrderIntent.CloseLong;

        (OrderSide side, string positionSide) = BrokerOrderRequest.FromIntent(intent);

        BrokerOrderRequest order = new(
            BaseAsset: baseAsset,
            QuoteAsset: quoteAsset,
            Side: side,
            PositionSide: account.IsHedgeMode ? positionSide : null,

            // 单向模式下没有 positionSide，用 reduceOnly 表达"只减不增"。
            // 手动平仓**一定要**带上它：万一数量算多了，没有它就会反手开出新仓
            ReduceOnly: !account.IsHedgeMode,
            Quantity: quantity,
            ClientOrderId: $"pm{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 100_000_000}");

        LighterTimestamp now = LighterTimestamp.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        try
        {
            BrokerOrderResult result = await broker.PlaceOrderAsync(order, cancellationToken).ConfigureAwait(false);

            LiveOrder recorded = await RecordAsync(
                follower,
                config,
                baseAsset,
                quoteAsset,
                position,
                intent,
                order,
                price,
                result.AveragePrice,
                LiveOrderStatus.Placed,
                reason: null,
                venueOrderId: result.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                now,
                cancellationToken).ConfigureAwait(false);

            if (request.PauseFollower)
            {
                // 一起暂停：不平掉它的话，下一次对账会按池子仓位把这个仓位重新建起来，
                // 使用者点了"平仓"却看到仓位自己回来了
                await followers
                    .UpdateAsync(follower with { IsActive = false }, cancellationToken)
                    .ConfigureAwait(false);

                await sync.RequestSyncAsync(cancellationToken).ConfigureAwait(false);
            }

            return LiveOrderMapper.ToDto(recorded, config.DisplayName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 拒单原因原样带出来（-2019 保证金不足之类）并落进流水，然后**把它作为结果返回**。
            // 不抛异常：中间件会把非 ArgumentException 一律吞成"服务内部错误"，
            // 而"为什么没平掉"正是使用者此刻唯一要看的东西（信号驱动的路径也是这么处理的）
            string reason = $"{ex.GetType().Name}：{ex.Message}";

            LiveOrder failed = await RecordAsync(
                follower,
                config,
                baseAsset,
                quoteAsset,
                position,
                intent,
                order,
                price,
                fillPrice: 0m,
                LiveOrderStatus.Failed,
                reason,
                venueOrderId: null,
                now,
                cancellationToken).ConfigureAwait(false);

            return LiveOrderMapper.ToDto(failed, config.DisplayName);
        }
    }

    private async Task<IReadOnlyDictionary<string, ExchangeSymbolInfo>> VenueSymbolsAsync(
        ExchangeCode platform,
        CancellationToken cancellationToken)
    {
        if (catalogs.Create(platform) is not { } catalog)
        {
            return new Dictionary<string, ExchangeSymbolInfo>(StringComparer.OrdinalIgnoreCase);
        }

        IReadOnlyList<ExchangeSymbolInfo> symbols = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);

        return symbols
            .GroupBy(s => s.Symbol)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    private async Task<LiveOrder> RecordAsync(
        Follower follower,
        ExchangeConfig config,
        string baseAsset,
        string quoteAsset,
        BrokerPosition position,
        BrokerOrderIntent intent,
        BrokerOrderRequest order,
        decimal price,
        decimal fillPrice,
        string status,
        string? reason,
        string? venueOrderId,
        LighterTimestamp now,
        CancellationToken cancellationToken)
    {
        decimal quantity = order.Quantity;

        // 手动平仓的两个价都有实据：开仓价来自**被平仓位**（下单前读的账户），平仓价来自回执均价
        (decimal entryPrice, decimal? closePrice) = LiveOrderPrices.Resolve(
            intent,
            position.EntryPrice,
            price,
            fillPrice);

        LiveOrder row = new()
        {
                FollowerId = follower.Id,
                ExchangeConfigId = config.Id,
                Platform = ExchangeEnumText.ToText(config.Code),
                TargetBase = baseAsset,
                TargetQuote = quoteAsset,
                SourceSymbol = "—",
                Intent = IntentText(intent),
                Side = order.Side == OrderSide.Buy ? "buy" : "sell",
                PositionSide = order.PositionSide,
                Quantity = quantity,
                Price = price,
                EntryPrice = entryPrice,
                ClosePrice = closePrice,
                NotionalUsd = quantity * price,

                // 手动单没有"池仓名义"与"目标名义"这两个概念（它不是按比例算出来的），
                // 留 0 而不是拿别的数字顶上
                PoolNotional = 0m,
                TargetNotionalUsd = 0m,
                PoolEquity = 0m,
                Capital = follower.InitialCapital.Value,
                Trigger = LiveOrderTrigger.Manual,
                Status = status,
                Reason = reason,
                VenueOrderId = venueOrderId,
                ClientOrderId = order.ClientOrderId,
                CreatedAt = now,
        };

        await liveOrders.AddAsync(row, cancellationToken).ConfigureAwait(false);

        return row;
    }

    private static string IntentText(BrokerOrderIntent intent) => intent switch
    {
        BrokerOrderIntent.OpenLong => "open_long",
        BrokerOrderIntent.CloseLong => "close_long",
        BrokerOrderIntent.OpenShort => "open_short",
        _ => "close_short",
    };
}
