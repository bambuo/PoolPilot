/// 匿名公开面（G1–G4 读、W1–W3 写）的类型与请求。
///
/// 单独成文件而不是并入 client.ts：这一组端点**匿名可达**，DTO 是从工作台类型
/// 刻意裁剪出来的投影——公开跟单卡片没有 exchangeName / exchangeConfigId /
/// availableBalance / livePositions 这些实盘字段，界面上不显示、也不该试图显示。
/// 混在一起写太容易"顺手复用 FollowerSummary"，把不该露出的字段带到公开页上。
///
/// 请求复用 client.ts 的 getJson / deleteJson：错误体（{"error"}）的解析与 401
/// 统一出口全站只有一套。公开端点设计上不回 401——回工作台时不会被弹走。

import { ApiError, deleteJson, getJson } from '@/api/client'

/** G1：一个正在观察池子的带单员。 */
export interface PublicLeadTrader {
  accountIndex: number
  name: string
  /** 此刻是否在收推送。false 时界面如实说"未在观察"——公开侧拿不到 isEnabled，
   *  区分不了"管理员暂停"与"连接断了"，就不替用户猜。 */
  isWatching: boolean
  followerCount: number
  signalCount: number
  lastSignalAt: string | null
  createdAt: string
}

/** G2：卡片上的持仓摘要。 */
export interface PublicPositionBrief {
  /** 市场 Id：行情事件补丁按它定位持仓；卡片不显示。 */
  marketId: number
  symbol: string
  side: 'long' | 'short' | null
  size: number
  /** 开仓均价：行情事件补丁按它重算未实现盈亏；卡片不显示。 */
  entryVwap: number
  notional: number
  unrealizedPnl: number
  /** 源池初始保证金率（百分数）；卡片据它换算标的杠杆。取不到时为 null。 */
  initialMarginPercent: number | null
}

/** G2：公开的纸面跟单卡片（留言板式列表用）。 */
export interface PublicFollowerSummary {
  id: number
  /** 创建者留的名字，可以没有。 */
  label: string | null
  leadAccountIndex: number
  leadName: string
  /** 上游带单员是否还在观察。false 时卡片标"上游已暂停"。 */
  leadIsEnabled: boolean
  isActive: boolean
  createdAt: string
  /** 是否由**当前这个浏览器**创建（按 Cookie 身份判定）。删除入口只在 true 时渲染。 */
  isOwnedByCaller: boolean
  initialCapital: number
  equity: number
  returnPercent: number
  realizedPnl: number
  unrealizedPnl: number
  /** 累计手续费：行情事件补丁用它重算权益；卡片不显示。 */
  fees: number
  grossNotional: number
  scale: number
  poolEquity: number
  fillCount: number
  skipCount: number
  /** false 时下面的估值字段都没有意义——界面显示"估值不可得"，不能当 0 渲染。 */
  isValuationAvailable: boolean
  positions: PublicPositionBrief[]
}

/** G3：详情里的纸面持仓。 */
export interface PublicFollowerPosition {
  marketId: number
  symbol: string
  side: 'long' | 'short' | null
  size: number
  entryVwap: number
  markPrice: number
  /** false = 取不到盯市价，该标的的未实现盈亏不可得（不是 0）。 */
  hasMark: boolean
  notional: number
  unrealizedPnl: number
  realizedPnl: number
  initialMarginPercent: number | null
  marginAmount: number | null
  /** 维持保证金率（百分数）；未知为 0。 */
  maintenanceMarginPercent: number
  unrealizedPnlPercent: number | null
  liquidationPrice: number | null
  liquidationDistancePercent: number | null
  trackingDeviationPercent: number
}

/** G3：流水行（服务端只给最近 50 条）。 */
export interface PublicFollowerFill {
  id: number
  marketId: number
  symbol: string
  /** 未跟上的行没有方向，为 null。 */
  side: 'buy' | 'sell' | null
  size: number
  price: number
  notional: number
  fee: number
  realizedPnl: number
  initialMarginPercent: number | null
  isReducing: boolean
  isFilled: boolean
  skipReason: string | null
  observedAt: string
}

/** G3：权益曲线点。 */
export interface PublicEquityPoint {
  at: string
  equity: number
  realizedPnl: number
  unrealizedPnl: number
  grossNotional: number
}

/** G3：公开跟单详情 = 卡片全部字段 + 账本明细。 */
export interface PublicFollower extends Omit<PublicFollowerSummary, 'positions'> {
  /** 公开侧只有纸面。 */
  mode: 'paper'
  /** 累计手续费（纸面账重放出来的），与工作台详情的「累计手续费」同口径。 */
  fees: number
  /** 取不到盯市价的市场 id 清单。 */
  marksUnavailable: number[]
  positions: PublicFollowerPosition[]
  fills: PublicFollowerFill[]
  curve: PublicEquityPoint[]
}

/** 带单员详情的收益/风险指标：上报值与按净值实算值并列，口径不混用。 */
export interface PublicLeadMetrics {
  /** 上游上报的年化（点估计）。 */
  reportedApyPercent: number
  reportedSharpe: number
  realTotalReturnPercent: number
  realAnnualizedReturnPercent: number
  /** 上游完全不提供最大回撤，这项只能实算。 */
  realMaxDrawdownPercent: number
  realSharpe: number
  periodVolatilityPercent: number
  lossPeriods: number
  totalPeriods: number
  spanDays: number
  /** 样本不足 30 点时实算指标只能当参考。 */
  isStatisticallyMeaningful: boolean
}

/** 带单员详情的敞口摘要。全仓保证金下真正的风险判据是清算缓冲。 */
export interface PublicLeadExposure {
  grossExposure: number
  netExposure: number
  exposureRatioPercent: number
  imbalanceRatio: number
  liquidationBuffer: number
  activePositionCount: number
}

/** 带单员详情里的持仓（只给前几大，按名义额降序）。 */
export interface PublicLeadPosition {
  marketId: number
  symbol: string
  side: 'long' | 'short'
  avgEntryPrice: number
  markPrice: number
  liquidationPrice: number | null
  notional: number
  /** 该仓位占总敞口的比例（百分数）。 */
  weightPercent: number
  /** 近端成交窗口内已平仓轮次的毛利合计；上游持仓不提供逐仓已实现盈亏。 */
  realizedPnl: number
  unrealizedPnl: number
  /** 该仓位当前的最大杠杆倍数（1 ÷ 保证金率）；上游未给有效保证金率时为 null。 */
  leverage: number | null
  /** 未实现盈亏 ÷ 保证金；取不到保证金率时为 null。 */
  unrealizedPnlPercent: number | null
}

/** 带单员详情里持仓市场的挂单（只用于持仓行展开区）。 */
export interface PublicLeadOrder {
  marketId: number
  orderIndex: number
  side: 'buy' | 'sell'
  price: number
  remainingSize: number
  remainingNotional: number
  isPartiallyFilled: boolean
}

/** 带单员详情里的净值点。 */
export interface PublicLeadNavPoint {
  at: string
  sharePrice: number
}

/**
 * 带单员（池）详情：与工作台共用同一套展示模型，由公开窄 DTO 提供数据。
 *
 * 不携带逐笔成交与运营者钱包地址；只给前几大持仓及其所在市场的挂单。
 */
export interface PublicLeadDetail {
  accountIndex: number
  name: string
  origin: string
  isFrozen: boolean
  isWatching: boolean
  followerCount: number
  signalCount: number
  lastSignalAt: string | null
  /** 本机开始观察该池的时刻。 */
  observedSince: string
  poolCreatedAt: string
  totalAssetValue: number
  totalShares: number
  /** 最新份额净值；没有净值点时是 null（不是 0）。 */
  sharePrice: number | null
  navAt: string | null
  operatorFeePercent: number
  operatorSharePercent: number
  minOperatorSharePercent: number
  /** 运营者份额触到下限，平台不再接受新入金（跟单不受影响）。 */
  isDepositLimited: boolean
  metrics: PublicLeadMetrics
  exposure: PublicLeadExposure
  positions: PublicLeadPosition[]
  orders: PublicLeadOrder[]
  navCurve: PublicLeadNavPoint[]
}

/** G4：池选择器的一个候选池（q 为池名子串或精确账户索引；limit 服务端默认 20、上限 50）。 */
export interface PublicPoolOption {
  accountIndex: number
  name: string
  /** 池子总资产（USDC）；与工作台候选框同源，用于"这个池多大"的量级参照。 */
  totalAssetValue: number
}

/** 公开面两页各自的开关。两片都关时这个接口仍然 200——页面据此显示"未启用"。 */
export interface PublicPageStatus {
  leads: boolean
  followers: boolean
  /** false = 登录/注册流程在服务端被关闭；公开页据此隐藏入口并拦下认证页。 */
  accountsEnabled: boolean
}

/**
 * W2 请求体。
 *
 * `mode` 恒为 `'paper'`、`exchangeConfigId` 恒为 `null`——服务端硬性拒绝 live，
 * 前端**不做静默降级**：400 的 {"error"} 原样显示给用户。
 * 本金区间 10–10,000、label 最长 24 字符由表单先把关，超长由服务端 400 兜底。
 */
export interface PublicCreateFollowerPayload {
  leadAccountIndex: number
  initialCapital: number
  label: string | null
  mode: string | null
  exchangeConfigId: number | null
}

export const publicApi = {
  /** 两页的开关状态。匿名可读，两片都关时也回 200。 */
  status: () => getJson<PublicPageStatus>('/api/public/status', { cache: 'no-store' }),

  /** G1。 */
  leadTraders: () => getJson<PublicLeadTrader[]>('/api/public/lead-traders'),

  /** G2。 */
  followers: () => getJson<PublicFollowerSummary[]>('/api/public/followers'),

  /** G3。不存在或非公开行时后端回 404，ensureOk 把它翻成带 {"error"} 文案的 ApiError。 */
  follower: (id: number) => getJson<PublicFollower>(`/api/public/followers/${id}`),

  /**
   * G2b：当前浏览器拥有（可删除）的公开跟单 Id。
   *
   * SSE 帧是所有订阅者共享的，装不下按 Cookie 裁剪的 isOwnedByCaller；页面用这条
   * **一次性**读口补归属。它不是轮询：卡片与详情的数据仍然只来自 SSE。
   */
  ownedFollowerIds: () => getJson<number[]>('/api/public/followers/ownership', { cache: 'no-store' }),

  /** G4。 */
  poolOptions: (q = '', limit = 20) =>
    getJson<PublicPoolOption[]>(
      `/api/public/pool-options?q=${encodeURIComponent(q)}&limit=${limit}`,
    ),

  /** W1：无请求体。已存在时返回现状，不会改动它。 */
  observeLeadTrader: (accountIndex: number) =>
    getJson<PublicLeadTrader>(`/api/public/lead-traders/${accountIndex}`, { method: 'POST' }),

  /** W2：成功回 201 的详情。 */
  createFollower: (payload: PublicCreateFollowerPayload) =>
    getJson<PublicFollower>('/api/public/followers', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(payload),
    }),

  /** W3：204；403（不是创建者）、404（不存在或非公开行）都带 {"error"}。 */
  deleteFollower: (id: number): Promise<void> => deleteJson(`/api/public/followers/${id}`),
}

/// 把一次失败翻成一句能直接展示给用户的话。
///
/// 429 单独翻：限流不是"操作错了"，先把这层意思给出来，再把服务端 error 原文带上
/// （里面通常有"等多久"的信息）。其余状态码（400/403/404）用 ensureOk 已解析好的
/// {"error"} 原文——服务端那句话就是最准确的解释，前端不再改写它。
export function describePublicError(error: unknown): string {
  if (error instanceof ApiError && error.status === 429) {
    return `操作太频繁，等一会儿再试（${error.message}）`
  }

  return error instanceof Error ? error.message : String(error)
}
