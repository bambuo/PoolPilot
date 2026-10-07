using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Enums;
using PoolPilot.Domain.Services;

namespace PoolPilot.Application.Mapping;

/// <summary>
/// 领域实体 → DTO。
///
/// 只做搬运与枚举转字符串，不含业务计算——所有派生量都在领域层算好，
/// 这里不再二次加工，避免同一口径存在两份实现。
/// </summary>
public static class PoolMapper
{
    public static PoolSummaryDto ToSummary(Pool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);

        return new PoolSummaryDto
        {
            AccountIndex = pool.AccountIndex.Value,
            Name = pool.Name,
            TotalAssetValue = pool.TotalAssetValue.Value,
            ReportedApyPercent = pool.ReportedAnnualPercentageYield.Percent,
            ReportedSharpe = pool.ReportedSharpe,
            OperatorFeePercent = pool.OperatorFee.Percent,
            TotalShares = pool.TotalShares,
            CreatedAt = pool.CreatedAt.Utc,
            StateCode = pool.StateCode,
            IsPublicPoolRange = pool.AccountIndex.IsPublicPoolRange,
            Origin = pool.Origin == PoolOrigin.Lighter ? "lighter" : "community",
            OperatorAddress = pool.L1Address,
            IsFrozen = pool.IsFrozen,
        };
    }

    public static NavMetricsDto ToMetrics(Pool pool, NavMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(metrics);

        return new NavMetricsDto
        {
            ReportedApyPercent = pool.ReportedAnnualPercentageYield.Percent,
            ReportedSharpe = pool.ReportedSharpe,
            RealTotalReturnPercent = metrics.TotalReturnPercent,
            RealAnnualizedReturnPercent = metrics.AnnualizedReturnPercent,
            RealMaxDrawdownPercent = metrics.MaxDrawdownPercent,
            RealSharpe = metrics.Sharpe,
            PeriodVolatilityPercent = metrics.PeriodVolatilityPercent,
            LossPeriods = metrics.LossPeriods,
            TotalPeriods = metrics.TotalPeriods,
            SpanDays = metrics.SpanDays,
            IsStatisticallyMeaningful = metrics.IsStatisticallyMeaningful,
        };
    }

    public static IReadOnlyList<NavPointDto> ToNavCurve(NavSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        return [.. series
            .Normalized()
            .Points
            .Select(p => new NavPointDto
            {
                At = p.Timestamp.Utc,
                SharePrice = p.SharePrice,
            })];
    }

    public static PoolPositionDto ToPosition(PoolPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);

        return new PoolPositionDto
        {
            MarketId = position.MarketId,
            Symbol = position.Symbol,
            Side = Describe(position.Side),
            Size = position.Size,
            AvgEntryPrice = position.AvgEntryPrice.Value,
            Notional = position.Notional.Value,
            UnrealizedPnl = position.UnrealizedPnl.Value,
            UnrealizedPnlPercent = position.UnrealizedPnlPercent?.Percent,
            MarkPrice = position.MarkPrice.Value,
            LiquidationDistancePercent = position.LiquidationDistancePercent?.Percent,
            Leverage = position.Margin?.Leverage,
            OpenOrderCount = position.OpenOrderCount,
            CumulativeFunding = position.CumulativeFunding.Value,

            // null 表示"不适用"（上游用 0），不是"缺数据"
            LiquidationPrice = position.LiquidationPrice?.Value,
        };
    }

    public static PoolOrderDto ToOrder(PoolOrder order, string symbol)
    {
        ArgumentNullException.ThrowIfNull(order);

        return new PoolOrderDto
        {
            OrderIndex = order.OrderIndex,
            MarketIndex = order.MarketIndex,
            Symbol = symbol,
            Side = order.Side == OrderSide.Buy ? "buy" : "sell",
            Price = order.Price.Value,
            RemainingSize = order.RemainingBaseAmount,
            InitialSize = order.InitialBaseAmount,
            RemainingNotional = order.RemainingNotional.Value,
            IsPartiallyFilled = order.IsPartiallyFilled,
            CreatedAt = order.CreatedAt.Utc,
            ExpiresAt = order.ExpiresAt.Utc,
        };
    }

    public static RoundTripDto ToRoundTrip(RoundTrip trip)
    {
        ArgumentNullException.ThrowIfNull(trip);

        return new RoundTripDto
        {
            MarketId = trip.MarketId,
            Symbol = trip.Symbol,
            Side = Describe(trip.Side),
            EntrySize = trip.EntrySize,
            EntryVwap = trip.EntryVwap.Value,
            ExitVwap = trip.ExitVwap.Value,
            EntryNotional = trip.EntryNotional.Value,
            GrossPnl = trip.GrossPnl.Value,
            ReturnPercent = trip.ReturnPercent.Percent,
            OpenedAt = trip.OpenedAt.Utc,
            ClosedAt = trip.ClosedAt.Utc,
            EntryRole = trip.EntryRole == TradeRole.Maker ? "maker" : "taker",
            ExitRole = trip.ExitRole == TradeRole.Maker ? "maker" : "taker",
            IsClosed = trip.IsClosed,
            HoldingMinutes = trip.HoldingPeriod.TotalMinutes,
        };
    }

    public static TradeCompositionDto ToComposition(TradeComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);

        return new TradeCompositionDto
        {
            TradeCount = composition.TradeCount,
            MakerCount = composition.MakerCount,
            TakerCount = composition.TakerCount,
            MakerSharePercent = composition.MakerSharePercent.Percent,
            BoughtNotional = composition.BoughtNotional.Value,
            SoldNotional = composition.SoldNotional.Value,
            MedianTradeNotional = composition.MedianTradeNotional,
        };
    }

    public static PoolSnapshotDto ToSnapshot(PoolSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new PoolSnapshotDto
        {
            TakenAt = snapshot.TakenAt.Utc,
            TotalAssetValue = snapshot.TotalAssetValue.Value,
            Collateral = snapshot.Collateral.Value,
            AvailableBalance = snapshot.AvailableBalance.Value,
            GrossExposure = snapshot.GrossExposure.Value,
            NetExposure = snapshot.NetExposure.Value,
            ExposureRatioPercent = snapshot.ExposureRatio.Percent,
            ImbalanceRatio = snapshot.ImbalanceRatio,
            InitialMarginRequirement = snapshot.InitialMarginRequirement.Value,
            MaintenanceMarginRequirement = snapshot.MaintenanceMarginRequirement.Value,
            LiquidationBuffer = snapshot.LiquidationBuffer.Value,
            ActivePositionCount = snapshot.ActivePositions.Count,
        };
    }

    public static PoolEventDto ToEvent(PoolEvent source, string poolName)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new PoolEventDto
        {
            AccountIndex = source.AccountIndex.Value,
            PoolName = poolName,
            Kind = Describe(source.Kind),
            MarketId = source.MarketId,
            Symbol = source.Symbol,
            Side = Describe(source.Side),
            SizeBefore = source.SizeBefore,
            SizeAfter = source.SizeAfter,
            NotionalDelta = source.NotionalDelta.Value,
            DetectedAt = source.DetectedAt.Utc,
            IsExposureIncreasing = source.IsExposureIncreasing,
        };
    }

    public static CopyFillDto ToCopyFill(CopyFill fill, string poolName)
    {
        ArgumentNullException.ThrowIfNull(fill);

        return new CopyFillDto
        {
            Id = fill.Id,
            AccountIndex = fill.AccountIndex.Value,
            PoolName = poolName,
            MarketId = fill.MarketId,
            Symbol = fill.Symbol,
            Side = fill.Side == OrderSide.Buy ? "buy" : "sell",
            Size = fill.Size,
            PoolPrice = fill.PoolPrice.Value,
            OurVwap = fill.OurVwap.Value,
            SlippageBps = fill.SlippageBps,
            DepthSufficient = fill.DepthSufficient,
            DetectionLatencyMs = fill.DetectionLatencyMs,
            IsWithinMeasurementWindow = fill.DetectionLatencyMs <= LighterConstants.SlippageValidityWindowMs,
            ObservedAt = fill.ObservedAt.Utc,
        };
    }

    private static string Describe(PositionSide side) => side == PositionSide.Long ? "long" : "short";

    private static string Describe(PoolEventKind kind) => kind switch
    {
        PoolEventKind.Open => "open",
        PoolEventKind.Increase => "increase",
        PoolEventKind.Reduce => "reduce",
        PoolEventKind.Close => "close",
        PoolEventKind.Flip => "flip",
        _ => kind.ToString(),
    };
}
