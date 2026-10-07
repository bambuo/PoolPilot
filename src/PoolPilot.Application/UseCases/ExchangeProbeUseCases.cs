using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 交易所凭据与连通性自检：**只读**，不改变账户任何状态。
///
/// 它回答三个问题：凭据能不能用、账户里有什么、这个平台能不能下单。
/// 失败**不抛异常而是带回一条可读的原因**——"钥匙不对"是使用者要看到的结果，
/// 不该变成 500；而币安的错误码（-2015 权限/IP、-1021 时间戳、-2019 保证金）要原样带出来，
/// 自造描述只会让排查变难。
/// </summary>
public sealed class ProbeExchangeUseCase(
    IExchangeConfigRepository exchanges,
    IExchangeBrokerFactory brokers)
{
    public async Task<ExchangeProbeDto> ExecuteAsync(long id, CancellationToken cancellationToken = default)
    {
        Domain.Entities.ExchangeConfig config = await exchanges.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"交易所配置 {id} 不存在。", nameof(id));

        if (!config.IsEnabled)
        {
            return Failed("这条配置已停用。先在卡片上启用它，再测试连接。");
        }

        if (!config.HasCredentials)
        {
            return Failed("这条配置还没有填凭据（API Key 与 Secret 都要）。");
        }

        IExchangeBroker? broker = brokers.Create(config);

        if (broker is null)
        {
            return Failed(
                $"{config.Code} 还没有执行侧实现——目前只有币安 U 本位合约能连。"
                + "（这条配置本身没问题，只是系统还不能替它下单。）");
        }

        try
        {
            BrokerAccount account = await broker.GetAccountAsync(cancellationToken).ConfigureAwait(false);

            return new ExchangeProbeDto
            {
                Ok = true,
                Error = null,
                Platform = broker.Code,
                CanTrade = true,
                IsHedgeMode = account.IsHedgeMode,
                WalletBalance = account.WalletBalance,
                AvailableBalance = account.AvailableBalance,
                UnrealizedPnl = account.UnrealizedPnl,
                GrossNotional = account.GrossNotional,
                Positions =
                [
                    .. account.Positions.Select(p => new BrokerPositionDto
                    {
                        Symbol = p.Symbol,
                        PositionSide = p.PositionSide,
                        Size = p.Size,
                        EntryPrice = p.EntryPrice,
                        Leverage = p.Leverage,
                        LiquidationPrice = p.LiquidationPrice,
                        UnrealizedPnl = p.UnrealizedPnl,
                        Notional = p.Notional,
                    }),
                ],
                CheckedAt = DateTimeOffset.UtcNow,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed($"{ex.GetType().Name}：{ex.Message}");
        }
    }

    private static ExchangeProbeDto Failed(string reason) => new()
    {
        Ok = false,
        Error = reason,
        Platform = null,
        CanTrade = false,
        IsHedgeMode = null,
        WalletBalance = 0m,
        AvailableBalance = 0m,
        UnrealizedPnl = 0m,
        GrossNotional = 0m,
        Positions = [],
        CheckedAt = DateTimeOffset.UtcNow,
    };
}

/// <summary>
/// 规划一笔实盘订单并**只校验不成交**（币安的 <c>/order/test</c>）。
///
/// 存在的意义：把"名义额 → 数量 → 量化到步长 → 过交易所规格 → 签名的形状对不对"这一整条链
/// 在**不花钱**的前提下跑通。任何与下单参数有关的改动都应该先过这里——
/// 实盘路径上唯一能既验证又不出血的检查点。
///
/// **规模不在这里设限**：目标名义额由标准跟单口径（比例复制）算出，这里只负责
/// "这个名义额在这个合约上能不能下"，不能下的原因（数量为 0、低于最小量/最小名义额）如实列出。
/// </summary>
/// 上限直接注入实例而不是 <c>IOptions&lt;T&gt;</c>：应用层刻意不引任何包（见 csproj 注释），
/// 而配置的绑定与注册属于组合根的事。
public sealed class PlanLiveOrderUseCase(
    IExchangeConfigRepository exchanges,
    IExchangeBrokerFactory brokers,
    LiveTradingLimits limits)
{
    public async Task<LiveOrderPlanDto> ExecuteAsync(
        LiveOrderPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Domain.Entities.ExchangeConfig config = await exchanges
            .GetAsync(request.ExchangeId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ArgumentException($"交易所配置 {request.ExchangeId} 不存在。", nameof(request));

        IExchangeBroker broker = brokers.Create(config)
            ?? throw new ArgumentException($"{config.Code} 还没有执行侧实现，无法下单。", nameof(request));

        BrokerOrderIntent intent = request.Action.Trim().ToLowerInvariant() switch
        {
            "open_long" => BrokerOrderIntent.OpenLong,
            "close_long" => BrokerOrderIntent.CloseLong,
            "open_short" => BrokerOrderIntent.OpenShort,
            "close_short" => BrokerOrderIntent.CloseShort,
            var other => throw new ArgumentException(
                $"未知的下单意图：{other}。可用值：open_long / close_long / open_short / close_short。",
                nameof(request)),
        };

        (OrderSide side, string positionSideOfIntent) = BrokerOrderRequest.FromIntent(intent);

        if (!limits.Enabled)
        {
            throw new ArgumentException("实盘总开关是关的（LiveTrading:Enabled=false），现在不会下任何单。");
        }

        if (request.NotionalUsd <= 0m)
        {
            throw new ArgumentException("目标名义价值必须为正数。", nameof(request));
        }

        BrokerInstrument instrument = await broker
            .GetInstrumentAsync(request.BaseAsset, request.QuoteAsset, cancellationToken)
            .ConfigureAwait(false);

        decimal price = await broker
            .GetPriceAsync(request.BaseAsset, request.QuoteAsset, cancellationToken)
            .ConfigureAwait(false);

        if (price <= 0m)
        {
            throw new ArgumentException(
                $"取不到 {request.BaseAsset}/{request.QuoteAsset} 的价格，无法把名义价值换算成数量。",
                nameof(request));
        }

        decimal quantity = instrument.Quantize(request.NotionalUsd / price);
        decimal notional = quantity * price;

        // 这里列的是**交易所的硬规则**，不是自设限额：过不了就是下不出去，
        // 与其让币安回一个 -4164 再回头猜，不如先算出来并说清差多少
        List<string> problems = [];

        if (quantity <= 0m)
        {
            problems.Add($"按 {request.NotionalUsd} USDT 换算出数量为 0（最小下单量 {instrument.MinQuantity}）——本金太小或价格太高。");
        }

        if (quantity < instrument.MinQuantity)
        {
            problems.Add($"数量 {quantity} 低于交易所最小下单量 {instrument.MinQuantity}。");
        }

        if (notional < instrument.MinNotional)
        {
            problems.Add($"名义价值 {notional:F2} USDT 低于交易所最小名义价值 {instrument.MinNotional} USDT。");
        }

        BrokerAccount account = await broker.GetAccountAsync(cancellationToken).ConfigureAwait(false);

        // 双向模式：positionSide 由**意图**给出（平多 = SELL+LONG，开空 = SELL+SHORT，两者不能混）；
        // 单向模式：不带 positionSide，用 reduceOnly 表达"只减不增"
        bool closing = intent is BrokerOrderIntent.CloseLong or BrokerOrderIntent.CloseShort;

        BrokerOrderRequest order = new(
            BaseAsset: request.BaseAsset,
            QuoteAsset: request.QuoteAsset,
            Side: side,
            PositionSide: account.IsHedgeMode ? positionSideOfIntent : null,
            Quantity: quantity,
            ReduceOnly: !account.IsHedgeMode && closing,

            // 客户端订单号前缀标记来源，便于在币安侧筛选出本系统下的单
            ClientOrderId: $"pp{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 100_000_000}");

        string? validationError = null;

        if (problems.Count == 0)
        {
            try
            {
                await broker.ValidateOrderAsync(order, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 交易所的拒单原因原样带出来：那些码是有意义的
                validationError = $"{ex.GetType().Name}：{ex.Message}";
            }
        }

        return new LiveOrderPlanDto
        {
            ExchangeId = config.Id,
            Platform = broker.Code,
            Action = request.Action.Trim().ToLowerInvariant(),
            TargetPair = $"{request.BaseAsset}/{request.QuoteAsset}",
            Side = side == OrderSide.Buy ? "buy" : "sell",
            PositionSide = order.PositionSide,
            ReduceOnly = order.ReduceOnly,
            Price = price,
            Quantity = quantity,
            NotionalUsd = notional,
            Leverage = limits.Leverage,
            EstimatedMarginUsd = limits.Leverage > 0 ? notional / limits.Leverage : 0m,
            StepSize = instrument.StepSize,
            MinQuantity = instrument.MinQuantity,
            MinNotional = instrument.MinNotional,
            Problems = problems,
            ValidationError = validationError,

            // 只有"没有问题、且 /order/test 也过了"才算这单发得出去
            IsSendable = problems.Count == 0 && validationError is null,
        };
    }
}
