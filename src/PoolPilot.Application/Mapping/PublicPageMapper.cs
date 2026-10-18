using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Services;

namespace PoolPilot.Application.Mapping;

/// <summary>
/// 公开面窄 DTO 的裁剪。
///
/// 这里**没有**任何估值算法：账与估值由既有的装配路径算好
/// （<see cref="ListFollowersUseCase"/> / <see cref="GetFollowerUseCase"/> /
/// <see cref="ListLeadTradersUseCase"/>），本类只做两件事——
/// <list type="number">
///   <item>把不该对匿名访客输出的字段舍掉（交易所、实盘余额与持仓、池子侧的对照量）；</item>
///   <item>补上公开面自己的两项：<c>label</c> 与 <c>isOwnedByCaller</c>。</item>
/// </list>
/// <c>isOwnedByCaller</c> 与删除端点走同一个判据（<see cref="PublicFollowerOwnership.IsCreator"/>），
/// 所以界面上的"删除"按钮不会出现"看得见、点了 403"。
/// </summary>
public static class PublicPageMapper
{
    /// <summary>带单员：私有列表项 → 公开列表项（去掉 isEnabled 与 activeFollowerCount）。</summary>
    public static PublicLeadTraderDto ToPublicLead(LeadTraderDto lead) => new()
    {
        AccountIndex = lead.AccountIndex,
        Name = lead.Name,
        IsWatching = lead.IsWatching,
        FollowerCount = lead.FollowerCount,
        SignalCount = lead.SignalCount,
        LastSignalAt = lead.LastSignalAt,
        CreatedAt = lead.CreatedAt,
    };

    /// <summary>
    /// 跟单员卡片。
    ///
    /// <paramref name="row"/> 是这一行的领域对象：卡片上的 <c>createdAt</c> 与 <c>label</c>
    /// 只在那上面有（私有摘要刻意不带它们），归属判据也要读它那两列。
    /// </summary>
    public static PublicFollowerSummaryDto ToSummary(
        FollowerSummaryDto source,
        Follower row,
        PublicCaller caller) => new()
        {
            Id = source.Id,
            Label = row.Label,
            LeadAccountIndex = source.LeadAccountIndex,
            LeadName = source.LeadName,
            LeadIsEnabled = source.LeadIsEnabled,
            IsActive = source.IsActive,
            CreatedAt = row.CreatedAt.Utc,
            IsOwnedByCaller = PublicFollowerOwnership.IsCreator(row, caller.UserId, caller.VisitorSecret),
            IsValuationAvailable = source.IsValuationAvailable,
            InitialCapital = source.InitialCapital,
            Equity = source.Equity,
            ReturnPercent = source.ReturnPercent,
            RealizedPnl = source.RealizedPnl,
            UnrealizedPnl = source.UnrealizedPnl,
            Fees = source.Fees,
            GrossNotional = source.GrossNotional,
            Scale = source.Scale,
            PoolEquity = source.PoolEquity,
            FillCount = source.FillCount,
            SkipCount = source.SkipCount,

            // 仓位清单与工作台同一份（非零、按名义额降序、不裁剪）：公开侧不另立一套裁剪，
            // 否则两个页面对同一个跟单员会给出不同的持仓；两行的显示上限由卡片的视口负责
            Positions = [.. source.Positions.Select(ToBriefPosition)],
        };

    /// <summary>
    /// 跟单员详情。
    /// </summary>
    /// <param name="valuationAvailable">
    /// 估值是否已经算过。刚建完的那一发（写入后的即时快照）传 <c>false</c>：
    /// 那时 Equity 就是 InitialCapital、其余一律为 0，那**不是结果而是"还没算"**
    /// ——一次写操作里不该塞进一整轮上游网络往返（与 <see cref="FollowerMapper.Snapshot"/> 同一口径）。
    /// </param>
    public static PublicFollowerDto ToDetail(
        FollowerDto source,
        Follower row,
        PublicCaller caller,
        bool valuationAvailable) => new()
        {
            Id = source.Id,
            Label = row.Label,
            LeadAccountIndex = source.LeadAccountIndex,
            LeadName = source.LeadName,
            LeadIsEnabled = source.LeadIsEnabled,
            IsActive = source.IsActive,
            CreatedAt = source.CreatedAt,
            IsOwnedByCaller = PublicFollowerOwnership.IsCreator(row, caller.UserId, caller.VisitorSecret),
            Mode = source.Mode,
            IsValuationAvailable = valuationAvailable,
            InitialCapital = source.InitialCapital,
            Equity = source.Equity,
            ReturnPercent = source.ReturnPercent,
            RealizedPnl = source.RealizedPnl,
            UnrealizedPnl = source.UnrealizedPnl,
            Fees = source.Fees,
            GrossNotional = source.GrossNotional,
            Scale = source.Scale,
            PoolEquity = source.PoolEquity,
            FillCount = source.FillCount,
            SkipCount = source.SkipCount,
            MarksUnavailable = source.MarksUnavailable,
            Positions = [.. source.Positions.Select(ToPosition)],

            // 私有装配给的流水是按时刻降序的，所以"最近 N 条"就是 Take(N)
            Fills = [.. source.Fills.Take(PublicPageLimits.FillsShownOnDetail).Select(ToFill)],
            Curve = [.. source.Curve.Select(ToCurvePoint)],
        };

    private static PublicPositionBriefDto ToBriefPosition(PaperPositionBriefDto position) => new()
    {
        MarketId = position.MarketId,
        Symbol = position.Symbol,
        Side = position.Side,
        Size = position.Size,
        EntryVwap = position.EntryVwap,
        Notional = position.Notional,
        UnrealizedPnl = position.UnrealizedPnl,
        InitialMarginPercent = position.InitialMarginPercent,
    };

    private static PublicPositionDto ToPosition(PaperPositionDto position) => new()
    {
        MarketId = position.MarketId,
        Symbol = position.Symbol,
        Side = position.Side,
        Size = position.Size,
        EntryVwap = position.EntryVwap,
        MarkPrice = position.MarkPrice,
        HasMark = position.HasMark,
        Notional = position.Notional,
        UnrealizedPnl = position.UnrealizedPnl,
        RealizedPnl = position.RealizedPnl,
        InitialMarginPercent = position.InitialMarginPercent,
        MarginAmount = position.MarginAmount,
        MaintenanceMarginPercent = position.MaintenanceMarginPercent,
        UnrealizedPnlPercent = position.UnrealizedPnlPercent,
        LiquidationPrice = position.LiquidationPrice,
        LiquidationDistancePercent = position.LiquidationDistancePercent,
        TrackingDeviationPercent = position.TrackingDeviationPercent,
    };

    private static PublicFillDto ToFill(PaperFillDto fill) => new()
    {
        Id = fill.Id,
        MarketId = fill.MarketId,
        Symbol = fill.Symbol,
        Side = fill.Side,
        Size = fill.Size,
        Price = fill.Price,
        Notional = fill.Notional,
        Fee = fill.Fee,
        RealizedPnl = fill.RealizedPnl,
        InitialMarginPercent = fill.InitialMarginPercent,
        IsReducing = fill.IsReducing,
        IsFilled = fill.IsFilled,
        SkipReason = fill.SkipReason,
        ObservedAt = fill.ObservedAt,
    };

    private static PublicEquityPointDto ToCurvePoint(PaperEquityPointDto point) => new()
    {
        At = point.At,
        Equity = point.Equity,
        RealizedPnl = point.RealizedPnl,
        UnrealizedPnl = point.UnrealizedPnl,
        GrossNotional = point.GrossNotional,
    };
}
