/// 带单员详情的展示模型。
///
/// 工作台与公开面各自从不同契约取数（完整池详情 / 公开窄 DTO），
/// 但详情页的排版、术语与表格行为必须只有一套。两个页面都先把数据映射成
/// 这个模型，再交给 `LeadDetailContent` 渲染。
export interface LeadDetailMetrics {
  reportedApyPercent: number
  reportedSharpe: number
  realTotalReturnPercent: number
  realAnnualizedReturnPercent: number
  realMaxDrawdownPercent: number
  realSharpe: number
  periodVolatilityPercent: number
  lossPeriods: number
  totalPeriods: number
  spanDays: number
  isStatisticallyMeaningful: boolean
}

export interface LeadDetailExposure {
  grossExposure: number
  netExposure: number
  exposureRatioPercent: number
  liquidationBuffer: number
  activePositionCount: number
}

export interface LeadDetailPosition {
  marketId: number
  symbol: string
  side: 'long' | 'short'
  avgEntryPrice: number
  markPrice: number
  liquidationPrice: number | null
  notional: number
  weightPercent: number
  /** 近端成交窗口内已平仓轮次的毛利合计；上游持仓不提供逐仓已实现盈亏。 */
  realizedPnl: number
  unrealizedPnl: number
  /** 未实现盈亏 ÷ 保证金；取不到保证金率时为 null。 */
  unrealizedPnlPercent: number | null
}

export interface LeadDetailOrder {
  marketId: number
  orderIndex: number
  side: 'buy' | 'sell'
  price: number
  remainingSize: number
  remainingNotional: number
  isPartiallyFilled: boolean
}

export interface LeadDetailNavPoint {
  at: string
  sharePrice: number
}

export interface LeadDetailViewData {
  totalAssetValue: number
  operatorFeePercent: number
  operatorSharePercent: number
  minOperatorSharePercent: number
  isDepositLimited: boolean
  metrics: LeadDetailMetrics
  exposure: LeadDetailExposure
  positions: LeadDetailPosition[]
  orders: LeadDetailOrder[]
  navCurve: LeadDetailNavPoint[]
  followerCount: number
  signalCount: number
  /** 「带单天数」悬浮说明。 */
  trackingHint: string
}
