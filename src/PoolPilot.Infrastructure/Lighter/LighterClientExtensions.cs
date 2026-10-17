using System.Globalization;

using Lighter.Net.Enums;
using Lighter.Net.Interfaces.Clients;
using Lighter.Net.Objects.Models;

using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Exceptions;
using PoolPilot.Domain.ValueObjects;

using LibrarySystemConfig = global::Lighter.Net.Objects.Models.LighterSystemConfig;
using PoolPilotSystemConfig = PoolPilot.Application.Abstractions.LighterSystemConfig;
using LibraryPositionSide = global::Lighter.Net.Enums.PositionSide;

namespace PoolPilot.Infrastructure.Lighter;

/// <summary>
/// **在本项目的领域口径上，给库的客户端加扩展成员**（C# 扩展成员，不是派生类）。
///
/// 加什么、不加什么，有一条清楚的界线：
/// <list type="bullet">
///   <item><b>加</b>：把库的模型翻成本项目的领域实体的那些读法——系统配置游标、账户快照、
///     合约规格。它们**全部由库的公开方法组合而成**（<c>GetSystemConfigAsync</c>、
///     <c>GetAccountsAsync</c>、<c>GetSymbolDetailsAsync</c>），扩展里只做字段挑选与刻度翻译。
///     放在扩展里而不是包一层自己的类：调用面就是库的客户端本身，
///     `client.GetPoolSnapshotAsync(...)` 读起来只有一处真相。</item>
///   <item><b>不加</b>：库没封装的端点（池枚举、净值序列、池的成交与挂单、盘口）。
///     扩展方法只能碰**公开**成员，而库的请求管线 <c>RestApiClient.SendAsync</c> 是 protected
///     （公开的只有 <c>GetAuthenticationProvider</c>/<c>SetOptions</c>）——所以这几个端点
///     没法用扩展方法接进库的管线，只能自己发（见 <see cref="LighterPoolDataExtension"/>）。</item>
/// </list>
///
/// **internal 的映射函数是为了被测试直接锁住**（InternalsVisibleTo）：字段挑错不会报错，
/// 只会让"最小下单量"与"最小名义额"悄悄换位——那是会把单下错的一类错，必须有离线的手续盯着。
/// </summary>
internal static class LighterClientExtensions
{
    extension(ILighterRestClient client)
    {
        /// <summary>
        /// 池枚举的起始游标。上游给的是 <c>liquidity_pool_index</c>（最高位的池索引）。
        ///
        /// 顺带说明一个**已删字段**：上游还有 <c>fee_collector_account_index</c>，
        /// 库的模型没有映射它，而全项目没有任何消费方——于是把领域的
        /// <c>LighterSystemConfig.FeeCollectorAccountIndex</c> 删掉了，
        /// 而不是留一个永远为 0 的字段（那正是"读不到"与"真的是 0"分不清的来源）。
        /// </summary>
        public async Task<PoolPilotSystemConfig> GetPoolCursorsAsync(CancellationToken cancellationToken = default)
        {
            LibrarySystemConfig config = Require(
                await client.ExchangeApi.ExchangeData
                    .GetSystemConfigAsync(cancellationToken)
                    .ConfigureAwait(false),
                "systemConfig");

            return new PoolPilotSystemConfig
            {
                LiquidityPoolIndex = config.LiquidityPoolIndex,
                StakingPoolIndex = config.StakingPoolIndex,
            };
        }

        /// <summary>
        /// 账户（池）的当前状态快照。<c>by=index</c> 取一条，取不到就抛——
        /// 静默给一份全零的快照会让"这个池读不到"与"这个池真的是空的"分不清。
        /// </summary>
        public async Task<PoolSnapshot> GetPoolSnapshotAsync(
            AccountIndex account,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(account);

            LighterAccounts result = Require(
                await client.ExchangeApi.Account
                    .GetAccountsAsync(
                        AccountBy.AccountIndex,
                        account.Value.ToString(CultureInfo.InvariantCulture),
                        null,
                        null,
                        cancellationToken)
                    .ConfigureAwait(false),
                "account");

            if (result.Accounts.Length == 0)
            {
                throw new InvalidLighterFieldException("account.accounts", "(空数组)", $"账户 {account} 无数据");
            }

            LighterAccountDetails details = result.Accounts[0];

            return new PoolSnapshot
            {
                AccountIndex = account,
                TakenAt = LighterTimestamp.FromMilliseconds(
                    new DateTimeOffset(DateTime.SpecifyKind(details.TransactionTime, DateTimeKind.Utc))
                        .ToUnixTimeMilliseconds()),
                TotalAssetValue = UsdAmount.From(details.TotalAssetValue),
                Collateral = UsdAmount.From(details.Collateral),
                AvailableBalance = UsdAmount.From(details.AvailableBalance),
                InitialMarginRequirement = UsdAmount.From(details.CrossInitialMarginRequirement),
                MaintenanceMarginRequirement = UsdAmount.From(details.CrossMaintenanceMarginRequirement),
                Positions = [.. details.Positions.Select(MapPosition)],

                // 挂单在 REST 需要鉴权（实测 403），只能从 WS 的 account_all_orders 取。
                // 快照里留空，由上层按需补全——静默给空数组会让"无挂单"与"没查挂单"混为一谈。
                Orders = [],
            };
        }

        /// <summary>全市场合约规格。库给的是永续与现货两段，本项目的跟单只关心永续那一段。</summary>
        public async Task<IReadOnlyList<InstrumentSpec>> GetInstrumentSpecsAsync(
            CancellationToken cancellationToken = default)
        {
            LighterSymbolDetails details = Require(
                await client.ExchangeApi.ExchangeData
                    .GetSymbolDetailsAsync(null, null, cancellationToken)
                    .ConfigureAwait(false),
                "orderBookDetails");

            return [.. details.PerpSymbols.Select(MapInstrument)];
        }
    }

    /// <summary>
    /// 上游的仓位 → 领域实体。**每一条口径都与迁移前的自研解析一致**：
    /// 数量取绝对值（方向在 <c>PositionSide</c> 里）、保证金率 0 翻译成 null
    /// （"没给有效值"与"真的是 0%"是两回事，后者语义上是无穷杠杆）、
    /// 强平价 0 表示不适用（全仓多头理论强平价 ≤ 0）。
    /// </summary>
    internal static PoolPosition MapPosition(LighterPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);

        return new PoolPosition
        {
            MarketId = position.MarketId,
            Symbol = position.Symbol,
            Side = position.PositionSide == LibraryPositionSide.Short
                ? PoolPilot.Domain.Enums.PositionSide.Short
                : PoolPilot.Domain.Enums.PositionSide.Long,
            Size = Math.Abs(position.Position),
            AvgEntryPrice = PriceAmount.Parse(position.AverageEntryPrice.ToString(CultureInfo.InvariantCulture)),
            Notional = UsdAmount.From(position.PositionValue),
            UnrealizedPnl = UsdAmount.From(position.UnrealizedPnl),
            Margin = position.InitialMarginFraction > 0m
                ? MarginFraction.FromPositionPercent(position.InitialMarginFraction)
                : null,
            OpenOrderCount = position.OpenOrderCount,
            CumulativeFunding = UsdAmount.From(position.TotalFundingPaidOut),
            LiquidationPrice = position.LiquidationPrice > 0m
                ? PriceAmount.From(position.LiquidationPrice)
                : null,
        };
    }

    /// <summary>
    /// 上游的合约规格 → 领域实体。费率字段是百分数字符串（"0.0000" 表示 0%），
    /// 库的解码不做缩放，这里照原值给。
    /// </summary>
    internal static InstrumentSpec MapInstrument(LighterSymbolPerpDetails symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        return new InstrumentSpec
        {
            MarketId = symbol.MarketId,
            Symbol = symbol.Symbol,
            MinOrderNotional = symbol.MinQuoteQuantity,
            MinOrderSize = symbol.MinBaseQuantity,
            SizeDecimals = symbol.SupportedQuantityDecimals,
            PriceDecimals = symbol.SupportedPriceDecimals,
            TakerFeePercent = symbol.TakerFee,
            MakerFeePercent = symbol.MakerFee,

            // 上游这个字段是整数刻度：1200 → 12%。先 ÷10000 成比例，再 ×100 成百分数，
            // 合起来就是 ÷100；与 positions[].initial_margin_fraction 的百分数口径分开记。
            MaintenanceMarginPercent = symbol.MaintenanceMarginFraction / 100m,
        };
    }

    /// <summary>库的失败 → 抛异常，成功取数据。上游的 code 与 message 原样带出。</summary>
    private static T Require<T>(CryptoExchange.Net.Objects.HttpResult<T> result, string what)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Success)
        {
            return result.Data;
        }

        string code = result.Error?.ErrorCode is { Length: > 0 } upstream ? upstream : "?";
        string message = result.Error?.ErrorDescription ?? result.Error?.Message ?? "上游没有给出原因";

        throw new LighterHttpException($"{what}（code {code}: {message}）", (int?)result.ResponseStatusCode ?? 0);
    }
}
