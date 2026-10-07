using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Repositories;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 读实盘跟单员的**真实账户状态**（余额、持仓、未实现盈亏）。
///
/// 这份数据只有交易所知道，本系统不重算——本地再存一份"我以为持有什么"，
/// 一旦与交易所对不上，就会有人拿着错的那份去下单（重复开仓、平错方向）。
/// 所以这里是**现场读交易所**，读不到就如实说读不到。
///
/// 与纸面跟单的读取（<see cref="GetFollowerUseCase"/>）是两条路：
/// 那条由流水重放出账，这条只是把交易所的回答翻译成界面的字段。
/// </summary>
public sealed class GetLiveAccountUseCase(
    IFollowerRepository followers,
    IExchangeConfigRepository exchanges,
    IExchangeBrokerFactory brokers,
    IExchangeSymbolCatalogFactory catalogs)
{
    public async Task<LiveAccountDto> ExecuteAsync(
        long followerId,
        CancellationToken cancellationToken = default)
    {
        Follower follower = await followers.GetAsync(followerId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"跟单员 {followerId} 不存在。", nameof(followerId));

        if (follower.ExchangeConfigId is not { } configId)
        {
            throw new ArgumentException(
                $"跟单员 {followerId} 没有绑定交易所账号（纸面模式不需要账号）。",
                nameof(followerId));
        }

        ExchangeConfig config = await exchanges.GetAsync(configId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"交易所配置 {configId} 不存在。", nameof(followerId));

        IExchangeBroker broker = brokers.Create(config)
            ?? throw new ArgumentException(
                $"{config.DisplayName} 没有执行侧实现，读不到账户。",
                nameof(followerId));

        BrokerAccount account = await broker.GetAccountAsync(cancellationToken).ConfigureAwait(false);

        // 交易所报的是它自己的合约代码；翻回系统标准的资产币/交易币要问目录，
        // 不在这里拆字符串——拆错了不会报错，只会把 A 合约显示成 B
        Dictionary<string, ExchangeSymbolInfo> byVenueSymbol = [];

        if (catalogs.Create(config.Code) is { } catalog)
        {
            IReadOnlyList<ExchangeSymbolInfo> symbols = await catalog
                .ListAsync(cancellationToken)
                .ConfigureAwait(false);

            byVenueSymbol = symbols
                .GroupBy(s => s.Symbol)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        List<LivePositionDto> positions = [];

        foreach (BrokerPosition position in account.Positions)
        {
            if (position.Size == 0m)
            {
                continue;
            }

            byVenueSymbol.TryGetValue(position.Symbol, out ExchangeSymbolInfo? info);

            positions.Add(new LivePositionDto
            {
                VenueSymbol = position.Symbol,
                BaseAsset = info?.BaseAsset,
                QuoteAsset = info?.QuoteAsset,
                Pair = info is null ? null : $"{info.BaseAsset}/{info.QuoteAsset}",
                PositionSide = position.PositionSide,
                Direction = position.Direction(),
                Size = position.Size,
                EntryPrice = position.EntryPrice,
                MarkPrice = position.MarkPrice,
                Leverage = position.Leverage,
                LiquidationPrice = position.LiquidationPrice,
                UnrealizedPnl = position.UnrealizedPnl,
                Notional = position.Notional,
            });
        }

        return new LiveAccountDto
        {
            FollowerId = follower.Id,
            ExchangeConfigId = config.Id,
            ExchangeName = config.DisplayName,
            Platform = ExchangeEnumText.ToText(config.Code),
            WalletBalance = account.WalletBalance,
            AvailableBalance = account.AvailableBalance,
            UnrealizedPnl = account.UnrealizedPnl,
            GrossNotional = account.GrossNotional,
            IsHedgeMode = account.IsHedgeMode,
            InitialCapital = follower.InitialCapital.Value,
            Positions = positions,
        };
    }
}
