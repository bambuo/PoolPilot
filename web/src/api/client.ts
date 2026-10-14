/// 后端 API 客户端与类型定义。
///
/// 约定：所有金额、比例、时间戳的后端 DTO 都已经是**经过口径归一化**的值
/// （后端领域层已把上游的字符串金额、四种时间刻度、两种保证金率刻度处理过），
/// 前端不再做任何单位换算——换算逻辑只有一份，在后端。

/** 池列表项。字段与后端 PoolSummaryDto 一一对应。 */
export interface PoolSummary {
  accountIndex: number
  name: string
  totalAssetValue: number
  /** 上游报的年化收益率（百分数，点估计）。实测可达数千万百分点。 */
  reportedApyPercent: number
  /** 上游报的夏普比率（点估计）。 */
  reportedSharpe: number
  /** 运营费（百分数）。实测区间 1%~30%。 */
  operatorFeePercent: number
  totalShares: number
  createdAt: string
  stateCode: number
  isPublicPoolRange: boolean
  /** lighter = Lighter 官方运营；community = 第三方社区运营。 */
  origin: 'lighter' | 'community'
  /** 运营者钱包地址。 */
  operatorAddress: string
  /** 已冻结 = 停止接受新入金（既有仓位与净值继续）。实测约 43% 的池处于此状态。 */
  isFrozen: boolean
}

export interface NavPoint {
  at: string
  sharePrice: number
}

/** 净值指标：上游点估计与实算值并列。 */
export interface NavMetrics {
  reportedApyPercent: number
  reportedSharpe: number
  realTotalReturnPercent: number
  realAnnualizedReturnPercent: number
  /** 最大回撤（百分数，负值）。上游完全不提供这一项。 */
  realMaxDrawdownPercent: number
  realSharpe: number
  periodVolatilityPercent: number
  lossPeriods: number
  totalPeriods: number
  spanDays: number
  isStatisticallyMeaningful: boolean
}

export interface PoolPosition {
  marketId: number
  symbol: string
  side: 'long' | 'short'
  size: number
  avgEntryPrice: number
  notional: number
  unrealizedPnl: number
  /** 浮动盈亏率：**对保证金**（ROE），与官方前端同口径。null = 上游未给保证金率，算不出。 */
  unrealizedPnlPercent: number | null
  /** null = 上游此刻未给出有效保证金率（仓位变动瞬间），不是 0 倍。 */
  leverage: number | null
  openOrderCount: number
  cumulativeFunding: number
  /** null 表示不适用（上游返回 0）。 */
  liquidationPrice: number | null
  /** 标记价（名义价值 ÷ 数量反推，上游不单独给）。 */
  markPrice: number
  /** 距清算幅度（%，空头为正需上涨、多头为负需下跌）。null = 无正清算价。不含杠杆。 */
  liquidationDistancePercent: number | null
}

export interface PoolOrder {
  orderIndex: number
  marketIndex: number
  /** 市场符号。由合约规格表解析——订单载荷里只有 market_index。 */
  symbol: string
  side: 'buy' | 'sell'
  price: number
  remainingSize: number
  initialSize: number
  remainingNotional: number
  isPartiallyFilled: boolean
  createdAt: string
  expiresAt: string
}

export interface RoundTrip {
  marketId: number
  /** 市场符号。由合约规格表解析，含池子已平掉的历史市场。 */
  symbol: string
  side: 'long' | 'short'
  entrySize: number
  entryVwap: number
  exitVwap: number
  entryNotional: number
  grossPnl: number
  returnPercent: number
  openedAt: string
  closedAt: string
  entryRole: 'maker' | 'taker'
  exitRole: 'maker' | 'taker'
  isClosed: boolean
  holdingMinutes: number
}

export interface TradeComposition {
  tradeCount: number
  makerCount: number
  takerCount: number
  makerSharePercent: number
  boughtNotional: number
  soldNotional: number
  medianTradeNotional: number
}

export interface PoolSnapshot {
  takenAt: string
  totalAssetValue: number
  collateral: number
  availableBalance: number
  grossExposure: number
  netExposure: number
  exposureRatioPercent: number
  imbalanceRatio: number
  initialMarginRequirement: number
  maintenanceMarginRequirement: number
  liquidationBuffer: number
  activePositionCount: number
}

export interface PoolDetail {
  summary: PoolSummary
  operatorSharePercent: number
  minOperatorSharePercent: number
  /** 入金是否已被限制（运营者份额触到下限，官方会拒绝新入金）。 */
  isDepositLimited: boolean
  /** 份额占比高出下限多少个百分点；0 = 正好压线。 */
  operatorShareHeadroomPercent: number
  metrics: NavMetrics
  navCurve: NavPoint[]
  snapshot: PoolSnapshot
  positions: PoolPosition[]
  orders: PoolOrder[]
  roundTrips: RoundTrip[]
  tradeComposition: TradeComposition
  primaryMarketId: number | null
}

export interface SizeFeasibility {
  capitalUsd: number
  marketId: number
  symbol: string
  poolExposureRatioPercent: number
  idealFollowNotional: number
  minOrderNotional: number
  canPlaceMinimumOrder: boolean
  requiredCapitalForMinOrder: number
  /** 硬凑到最小单时的敞口放大倍数。>1 表示你的单笔风险高于池子。 */
  amplificationFactor: number
}

export interface BacktestCurvePoint {
  at: string
  poolNav: number
  copyNav: number
  depositNav: number
}

export interface BacktestResult {
  accountIndex: number
  poolName: string
  poolTotalReturnPercent: number
  copyTotalReturnPercent: number
  depositTotalReturnPercent: number
  assumedRoundTripCostBps: number
  operatorFeePercent: number
  sampleDays: number
  curve: BacktestCurvePoint[]
}

export interface CopyFill {
  id: number
  accountIndex: number
  poolName: string
  marketId: number
  symbol: string
  side: 'buy' | 'sell'
  size: number
  poolPrice: number
  ourVwap: number
  /** 单腿漏损（基点）。正数表示我方更差。 */
  slippageBps: number
  depthSufficient: boolean
  detectionLatencyMs: number
  /** false = 该样本超出有效测量窗口，进入原始记录但被排除在报告统计之外。 */
  isWithinMeasurementWindow: boolean
  observedAt: string
}

export interface MarketSlippage {
  marketId: number
  symbol: string
  sampleCount: number
  medianSlippageBps: number
  spreadBps: number
  depthSufficient: boolean
}

export interface SlippageReport {
  accountIndex: number
  poolName: string
  sampleCount: number
  medianEntrySlippageBps: number
  p90EntrySlippageBps: number
  /** 往返 = 两条腿。与池子单轮收益直接可比。 */
  estimatedRoundTripBps: number
  averagePoolRoundTripReturnPercent: number
  byMarket: MarketSlippage[]
}

export interface CopyLedger {
  accountIndex: number
  poolName: string
  entries: CopyFill[]
  report: SlippageReport
}

export interface PoolEvent {
  accountIndex: number
  poolName: string
  kind: string
  marketId: number
  symbol: string
  side: string
  sizeBefore: number
  sizeAfter: number
  notionalDelta: number
  detectedAt: string
  isExposureIncreasing: boolean
}

export interface MonitorStatus {
  state: string
  monitoredAccounts: number[]
  subscribedChannels: string[]
  lastMessageAt: string | null
  detail: string
  isConnected: boolean
}

export interface MarketQuote {
  marketId: number
  symbol: string
  bestBid: number
  bestAsk: number
  midPrice: number
  spreadBps: number
  minOrderNotional: number
  minOrderSize: number
  takerFeePercent: number
}

export interface MonitorConfig {
  enabled: boolean
  accountIndexes: number[]
  orderBookCacheSeconds: number
  hubUrl: string
}

/// 请求失败。**带上状态码**——调用方常常需要区分"没登录"（401）与其他失败，
/// 而此前只有一句话，连 401 都只能靠文案去猜。
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

/// 未登录时的统一出口。由 `useSession` 在启动时挂上（清会话 + 跳登录页）。
///
/// 放在这里而不是让 client 直接 import 路由：请求层不该知道页面存在，
/// 而"401 之后去哪儿"是界面的事。**环形依赖**也由此避开（router → view → client → router）。
let onUnauthorized: (() => void) | null = null

export function setUnauthorizedHandler(handler: (() => void) | null): void {
  onUnauthorized = handler
}

/// 统一的失败处理：把后端的 { error } 翻成异常，并接上 401 的出口。
/// 抽出来是因为"不解析响应体"的请求也要走同一套——两条路径各写一遍，
/// 早晚会有一条忘了处理 401。
async function ensureOk(response: Response): Promise<void> {
  if (response.ok) {
    return
  }

  let detail = `HTTP ${response.status}`

  try {
    const body = (await response.json()) as { error?: string }
    if (body.error) {
      detail = body.error
    }
  } catch {
    // 响应不是 JSON（例如 204 或反向代理的 HTML 错误页），沿用状态码描述
  }

  if (response.status === 401) {
    onUnauthorized?.()
  }

  throw new ApiError(response.status, detail)
}

async function getJson<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    headers: { accept: 'application/json' },
    // 会话在 Cookie 里（HttpOnly，脚本读不到），所以每个请求都要带上它。
    // same-origin 是 fetch 的默认值，这里写明是因为"少了它整站 401"这种故障太难查
    credentials: 'same-origin',
    ...init,
  })

  await ensureOk(response)

  return (await response.json()) as T
}

/// 发一个**没有响应体**的请求（204 或空 200）。
///
/// 与 getJson 分开而不是给它加个开关：空体上去 `response.json()` 会抛
/// "Unexpected end of JSON input"——那句话既不提是哪个接口，也不提是空体的问题。
async function sendWithoutBody(path: string, init?: RequestInit): Promise<void> {
  const response = await fetch(path, {
    headers: { accept: 'application/json' },
    credentials: 'same-origin',
    ...init,
  })

  await ensureOk(response)
}

export const api = {
  /** 池列表（全量，含已冻结；冻结状态由 `isFrozen` 字段如实返回，界面负责标记）。 */
  listPools: (filter: 'user' | 'protocol' | 'stake' | 'all' = 'user') =>
    getJson<PoolSummary[]>(`/api/pools?filter=${filter}`),

  /** 池详情：一次请求给出观察该池所需的全部信息。 */
  poolDetail: (accountIndex: number | string) => getJson<PoolDetail>(`/api/pools/${accountIndex}`),

  /** 规模门槛：给定本金能否跟得动。 */
  feasibility: (accountIndex: number | string, capital: number) =>
    getJson<SizeFeasibility[]>(`/api/feasibility/${accountIndex}?capital=${capital}`),

  /** 回测：池子 / 跟单 / 直接入池三方对照。 */
  backtest: (accountIndex: number | string, roundTripCostBps = 5, tradesPerDay = 2) =>
    getJson<BacktestResult>(
      `/api/backtest/${accountIndex}?roundTripCostBps=${roundTripCostBps}&tradesPerDay=${tradesPerDay}`,
    ),

  /** 池子级的跟单对照与漏损报告（按池索引取，与跟单员无关）。 */
  ledger: (accountIndex: number | string, limit = 200) =>
    getJson<CopyLedger>(`/api/monitor/ledger/${accountIndex}?limit=${limit}`),

  /** 持仓变化事件。不传 accountIndex 则返回全部池的。 */
  events: (accountIndex?: number | string, limit = 100) =>
    getJson<PoolEvent[]>(
      accountIndex === undefined
        ? `/api/monitor/events?limit=${limit}`
        : `/api/monitor/events?accountIndex=${accountIndex}&limit=${limit}`,
    ),

  /** 全部带单员：配置状态、此刻是否在收推送、订阅数与信号产量。 */
  leadTraders: () => getJson<LeadTrader[]>('/api/lead-traders'),

  /** 创建或启停一个带单员。**创建即观察**——存在就在盯这个池。 */
  configureLeadTrader: (accountIndex: number | string, isEnabled: boolean) =>
    getJson<LeadTrader>(`/api/lead-traders/${accountIndex}`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ isEnabled }),
    }),

  /** 停止观察并删除带单员。还有跟单员订阅时后端会拒绝（400）。 */
  deleteLeadTrader: async (accountIndex: number | string): Promise<void> => {
    await fetch(`/api/lead-traders/${accountIndex}`, { method: 'DELETE' })
  },

  /** 全部跟单员的卡片摘要。 */
  followers: () => getJson<FollowerSummary[]>('/api/followers'),

  /** 读取一个跟单员。不存在时返回 null（后端 404）。 */
  follower: async (id: number | string): Promise<Follower | null> => {
    try {
      return await getJson<Follower>(`/api/followers/${id}`)
    } catch (error) {
      // 404 = 没有这个跟单员，属于正常状态而不是错误，界面据此显示引导
      if (error instanceof Error && error.message.includes('不存在')) {
        return null
      }
      throw error
    }
  },

  /** 开始跟单：新建一个跟单员订阅某个带单员。该池没有带单员时后端会自动补一个。 */
  /**
   * 新建跟单员。
   *
   * `exchangeConfigId` 只在**实盘**时给：下单必须落到某一个账号上。
   * 纸面不需要账号（它不碰任何交易所），传了后端也不会存。
   */
  createFollower: (
    leadAccountIndex: number | string,
    initialCapital: number,
    mode = 'paper',
    exchangeConfigId?: number,
  ) =>
    getJson<Follower>('/api/followers', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ leadAccountIndex, initialCapital, mode, exchangeConfigId }),
    }),

  /** 实盘流水：我发过什么、交易所怎么回的（真实持仓与盈亏以交易所为准，不在这里）。 */
  liveOrders: (id: number | string, limit = 200) =>
    getJson<LiveOrder[]>(`/api/followers/${id}/live-orders?limit=${limit}`),

  /** 实盘账户的真实状态。**现场读交易所**，读不到会抛。 */
  liveAccount: (id: number | string) => getJson<LiveAccount>(`/api/followers/${id}/live-account`),

  /**
   * **手动平掉**实盘账户上的一笔仓位（真实下单）。
   *
   * 只说明"平哪个标的"：数量由后端现场读交易所持仓得出——界面上的数字可能已经过期，
   * 照它下单会平不干净或反向开仓。
   */
  closeLivePosition: (id: number | string, payload: CloseLivePositionPayload) =>
    getJson<LiveOrder>(`/api/followers/${id}/live-positions/close`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ followerId: id, ...payload }),
    }),

  /** 改本金或启停。改动本金会清空该跟单员的历史（后端说明理由）。 */
  updateFollower: (id: number | string, initialCapital: number, isActive: boolean) =>
    getJson<Follower>(`/api/followers/${id}`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ initialCapital, isActive }),
    }),

  /** 删除跟单员及其流水。**不影响带单员是否继续观察**。 */
  deleteFollower: async (id: number | string): Promise<void> => {
    await fetch(`/api/followers/${id}`, { method: 'DELETE' })
  },

  marketQuote: (marketId: number) => getJson<MarketQuote>(`/api/markets/${marketId}/quote`),

  // ── 交易所配置与凭据 ────────────────────────────────────
  // 凭据按原样保存（明文），但**读取路径只回尾号**：那是减少屏幕上的暴露面，不是加密。

  /** 全部交易所配置（不含任何凭据字段）。 */
  exchanges: () => getJson<ExchangeConfigRow[]>('/api/exchanges'),

  /**
   * 连接测试：拿真实账户核对一遍（会真的调用交易所，不需要下单权限）。
   * 顺带返回余额与持仓——新建实盘跟单要靠它拿到"可用保证金"这个上限。
   */
  probeExchange: (id: number) => getJson<ExchangeProbe>(`/api/exchanges/${id}/probe`, { method: 'POST' }),

  createExchange: (payload: ExchangePayload) =>
    getJson<ExchangeConfigRow>('/api/exchanges', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(payload),
    }),

  /** 修改。凭据三态：不传字段 = 保持原样，传空串 = 清除，传值 = 换成新的。 */
  updateExchange: (id: number, payload: ExchangePayload & { isEnabled: boolean }) =>
    getJson<ExchangeConfigRow>(`/api/exchanges/${id}`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(payload),
    }),

  deleteExchange: async (id: number): Promise<void> => {
    await fetch(`/api/exchanges/${id}`, { method: 'DELETE' })
  },

  /** 系统支持的平台（映射页的选择器用它，**不是"你配了哪些交易所"**）。 */
  symbolPlatforms: () => getJson<SymbolPlatform[]>('/api/symbol-mappings/platforms'),

  /** 源标的清单：Lighter 的全部市场（登记映射时"源资产币"从这里选）。 */
  sourceSymbols: (search = '') =>
    getJson<SourceSymbol[]>(
      `/api/symbol-mappings/source-symbols${search ? `?search=${encodeURIComponent(search)}` : ''}`,
    ),

  /** 某平台的合约清单（人工登记映射时选目标合约用；公开数据，不需要凭据）。 */
  platformSymbols: (platform: string, search = '') =>
    getJson<ExchangeSymbol[]>(
      `/api/symbol-mappings/symbols?platform=${platform}${search ? `&search=${encodeURIComponent(search)}` : ''}`,
    ),

  // ── 标的映射表 ──────────────────────────────────────────
  // 跟单员收到信号时查的就是它：一次本地读，不打网络。

  /** 全部映射（可按平台符号过滤；不传就是全部平台）。 */
  symbolMappings: (platform?: string) =>
    getJson<SymbolMapping[]>(
      `/api/symbol-mappings${platform === undefined ? '' : `?platform=${platform}`}`,
    ),

  /** 扫描被观察的池，整理该平台的映射（人工行会被跳过）。 */
  scanSymbolMappings: (platform: string) =>
    getJson<SymbolMappingScanResult>(`/api/symbol-mappings/scan?platform=${platform}`, { method: 'POST' }),

  /** 人工登记或修正一条映射（之后不再被扫描覆盖）。 */
  saveSymbolMapping: (payload: SymbolMappingPayload) =>
    getJson<SymbolMapping>('/api/symbol-mappings', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(payload),
    }),

  deleteSymbolMapping: async (id: number): Promise<void> => {
    await fetch(`/api/symbol-mappings/${id}`, { method: 'DELETE' })
  },
}

// ─────────────────────────────────────────────────────────────
// 纸面跟单账户（账户会计，与上面的执行成本测量分工不同）
// ─────────────────────────────────────────────────────────────

/** 纸面持仓，含与池子的跟踪偏差。 */
export interface PaperPosition {
  marketId: number
  symbol: string
  /** 空仓时为 null。 */
  side: 'long' | 'short' | null
  /** 我方带符号数量。 */
  size: number
  entryVwap: number
  markPrice: number
  /** false 表示取不到盯市价，未实现盈亏不可得（不是 0）。 */
  hasMark: boolean
  notional: number
  unrealizedPnl: number
  realizedPnl: number
  /** 池子在市场的带符号仓位。 */
  poolSize: number
  /** 按比例复制并量化到交易所步长后，我方应该持有的数量。 */
  targetSize: number
  trackingDeviation: number
  trackingDeviationPercent: number
}

/** 一条纸面动作：成交或被跳过。 */
export interface PaperFill {
  id: number
  marketId: number
  symbol: string
  side: 'buy' | 'sell' | null
  size: number
  price: number
  notional: number
  fee: number
  realizedPnl: number
  isFilled: boolean
  skipReason: 'below_minimum_size' | 'below_minimum_notional' | 'no_liquidity' | null
  depthSufficient: boolean
  poolPositionAfter: number
  positionBefore: number
  positionAfter: number
  targetPosition: number
  observedAt: string
}

export interface PaperEquityPoint {
  at: string
  equity: number
  realizedPnl: number
  unrealizedPnl: number
  grossNotional: number
}

export interface Follower {
  /** 跟单员自身的标识（自增）。同一个带单员下可以有多个跟单员。 */
  id: number
  /** 订阅的带单员（池账户索引）。 */
  leadAccountIndex: number
  leadName: string
  /** 带单员是否在观察。false 时这个跟单员不会再有新动作。 */
  leadIsEnabled: boolean
  /** `paper` / `live`。 */
  mode: string
  /** 实盘用的交易所配置；纸面为 null。 */
  exchangeConfigId: number | null
  /** 交易所配置的显示名（"币安 主账号"）；纸面或配置已删时为 null。 */
  exchangeName: string | null
  initialCapital: number
  isActive: boolean
  createdAt: string
  equity: number
  realizedPnl: number
  unrealizedPnl: number
  fees: number
  returnPercent: number
  grossNotional: number
  poolEquity: number
  scale: number
  fillCount: number
  skipCount: number
  /** 取不到盯市价的市场，其未实现盈亏不可得。 */
  marksUnavailable: number[]
  positions: PaperPosition[]
  fills: PaperFill[]
  curve: PaperEquityPoint[]
}

/** 卡片上的持仓摘要。 */
export interface PaperPositionBrief {
  symbol: string
  side: 'long' | 'short' | null
  size: number
  notional: number
  unrealizedPnl: number
  trackingDeviationPercent: number
}

/** 跟单员的卡片摘要（不含流水与权益曲线）。 */
/**
 * 实盘流水的一行：我发了什么、多少钱、结果如何。
 *
 * 与纸面流水的区别必须记住：纸面流水是**账**（重放它才能算出持仓与盈亏）；
 * 这条是**日志**。实盘的真实持仓与盈亏以交易所的账户状态为准，本系统不另算一份。
 */
/** 交易所账户的真实状态。系统不重算——本地的第二份账只会带来"该信谁"的问题。 */
export interface LiveAccount {
  followerId: number
  exchangeConfigId: number
  exchangeName: string
  platform: string
  /** 钱包余额（含未实现盈亏）。 */
  walletBalance: number
  /** 可用保证金。 */
  availableBalance: number
  unrealizedPnl: number
  grossNotional: number
  /** 双向持仓模式。它决定下单参数形状。 */
  isHedgeMode: boolean
  initialCapital: number
  positions: LivePosition[]
}

/** 交易所账户里的一笔持仓。 */
export interface CloseLivePositionPayload {
  /** 资产币（系统标准标的的那一段）。 */
  baseAsset: string
  quoteAsset: string
  /** 平掉之后是否把跟单员一并暂停（否则下一次对账会按池子仓位把它重新建起来）。 */
  pauseFollower: boolean
}

export interface LivePosition {
  /** 交易所自己的合约写法（只用于在该交易所界面上找到它）。 */
  venueSymbol: string
  baseAsset: string | null
  quoteAsset: string | null
  /** 系统标准标的；该合约不在目录里时为 null。 */
  pair: string | null
  /** 交易所侧的原始字段（LONG / SHORT / BOTH）。 */
  positionSide: string
  /** 归一化的方向（long / short）——界面与平仓都用它：单向模式下 positionSide 恒为 BOTH。 */
  direction: string
  size: number
  entryPrice: number
  /** 交易所给的标记价（快照时刻的）：未实现盈亏的自变量。 */
  markPrice: number
  leverage: number
  liquidationPrice: number
  unrealizedPnl: number
  notional: number
}

export interface LiveOrder {
  id: number
  followerId: number
  exchangeConfigId: number
  exchangeName: string | null
  platform: string
  /** 源标的（Lighter 的市场名）——回答"这笔是从哪个信号来的"。 */
  sourceSymbol: string
  targetBase: string
  targetQuote: string
  /** 目标标的的系统标准形式（BTC/USDT）。 */
  targetPair: string
  /** open_long / close_long / open_short / close_short / none。 */
  intent: string
  /** buy / sell / none。 */
  side: string
  positionSide: string | null
  quantity: number
  /** 下单时的参考价（名义价值换算依据）。成交价看 entryPrice / closePrice。 */
  price: number
  /** 这一单所涉仓位的开仓均价（开仓单=自己的成交均价；平仓单=被平仓位的成本线）。0 = 未知（旧行）。 */
  entryPrice: number
  /** 平仓成交价；只有平仓单有。 */
  closePrice: number | null
  notionalUsd: number
  /** 池子在该市场的持仓名义价值（带符号）——信号本身有多大。 */
  poolNotional: number
  /** 按比例复制后算出的目标名义价值（带符号）。 */
  targetNotionalUsd: number
  poolEquity: number
  capital: number
  /** signal（信号驱动的自动跟单）/ manual（人在界面上手动下的）。 */
  trigger: string
  /** placed（已发出）/ skipped（我方主动没发）/ failed（发出被拒）。 */
  status: string
  reason: string | null
  venueOrderId: string | null
  clientOrderId: string | null
  createdAt: string
}

export interface FollowerSummary {
  id: number
  leadAccountIndex: number
  leadName: string
  leadIsEnabled: boolean
  mode: string
  /** 实盘所在的交易所账号名；纸面为 null。 */
  exchangeName: string | null
  /** 实盘所用的交易所配置 Id；纸面为 null。「交易所」页靠它回答"这条配置有没有人在用"——
   *  不按名字关联：名字由使用者起，后端不保证唯一。 */
  exchangeConfigId: number | null
  /** 实盘的可用保证金（交易所给的）；纸面为 null。 */
  availableBalance: number | null
  /** 实盘账户当前持有的仓位（交易所给的）；纸面跟单员为空。 */
  livePositions: LivePosition[]
  initialCapital: number
  isActive: boolean
  /** false 时下面的估值字段都没有意义，界面必须显示"估值不可得"而不是把它们当结果。 */
  isValuationAvailable: boolean
  equity: number
  returnPercent: number
  realizedPnl: number
  unrealizedPnl: number
  scale: number
  poolEquity: number
  grossNotional: number
  fillCount: number
  skipCount: number
  positions: PaperPositionBrief[]
  unpricedMarketCount: number
}

/** 带单员：被观察的池。存在即被观察，与有没有人跟无关。 */
export interface LeadTrader {
  accountIndex: number
  name: string
  /** 配置上是否在观察。 */
  isEnabled: boolean
  /** 此刻是否真的在收推送。与 isEnabled 不一致时说明连接断了或还在建订阅。 */
  isWatching: boolean
  followerCount: number
  activeFollowerCount: number
  /** 累计信号数（落库的仓位变化条数）。 */
  signalCount: number
  lastSignalAt: string | null
  createdAt: string
}

// ─────────────────────────────────────────────────────────────
// 交易所配置（凭据只进不出）
// ─────────────────────────────────────────────────────────────

/** 一条交易所配置。**既没有凭据字段，也没有接口地址**——前者只回"配没配"与尾号，
 *  后者由平台预设决定、不面向界面。 */
/** 连接测试的结果（真实账户快照）。 */
export interface ExchangeProbe {
  ok: boolean
  error: string | null
  platform: string | null
  canTrade: boolean
  isHedgeMode: boolean | null
  walletBalance: number
  /** 可用保证金——实盘跟单的"规模基准"上限就是它。 */
  availableBalance: number
  unrealizedPnl: number
  grossNotional: number
  positions: unknown[]
  checkedAt: string
}

export interface ExchangeConfigRow {
  id: number
  /** `lighter` / `binance` / `okx` / `gate` / `custom` */
  code: string
  /** `cex` / `dex` */
  kind: string
  displayName: string
  isTestnet: boolean
  /** 接口地址**不在读取结果里**：它由平台预设决定，界面不显示也不许改，
   *  所以后端不再把它发到前端（前端拿不到，也就不会有人想着去改它）。 */
  makerFeePercent: number
  takerFeePercent: number
  isEnabled: boolean
  notes: string | null
  createdAt: string
  hasCredentials: boolean
  /** API Key 尾号，用于确认配的是哪一把。 */
  apiKeyHint: string | null
  hasPassphrase: boolean
  /** 这个平台能不能扫合约清单 / 登记标的映射（界面据此提前禁用按钮）。 */
  supportsSymbolCatalog: boolean
}

/**
 * 新建/修改交易所配置的载荷。
 *
 * 凭据字段的三态：**省略**（不传这个键）= 保持原样、**空串** = 清除、**有值** = 换成新的。
 * 新建时不需要这一区分（省略即"先不配"）。
 */
export interface ExchangePayload {
  code?: string
  kind?: string
  displayName: string
  isTestnet?: boolean
  restBaseUrl?: string | null
  wsUrl?: string | null
  makerFeePercent?: number
  takerFeePercent?: number
  notes?: string | null
  apiKey?: string
  apiSecret?: string
  passphrase?: string
}

// ─────────────────────────────────────────────────────────────
// 标的映射（跨交易所）
// ─────────────────────────────────────────────────────────────

/** 源标的（Lighter 的一个市场）。 */
export interface SourceSymbol {
  symbol: string
  marketId: number
}

/** 平台的一个可交易合约。`contractType` 区分加密永续与传统金融（股票/商品/指数）永续。 */
export interface ExchangeSymbol {
  /**
   * **平台自己的写法**（`CLUSDT` / `CL-USDT-SWAP` / `CL_USDT`）。
   *
   * 只用于在这个平台的界面上找到那个合约；**不是本系统的标的表示**——
   * 系统内外一律用资产币 + 交易币两段，别把它存下来或当标的传。
   */
  symbol: string
  baseAsset: string
  /** 交易币。与资产币一起才是完整的标的。 */
  quoteAsset: string
  /** 平台自己的合约类型口径（`PERPETUAL`/`SWAP`/`stocks`…）。 */
  contractType: string
  /** 是不是传统金融（股票/商品/指数）。由各平台对接实现判定，界面不要自己猜。 */
  isTradFi: boolean
  minNotional: number
  stepSize: number
  quantityPrecision: number
}

/** 一条标的映射。 */
/** 系统支持的一个平台。`supported` = 有没有合约目录实现（没有就扫不了）。 */
export interface SymbolPlatform {
  platform: string
  supported: boolean
}

export interface SymbolMapping {
  id: number
  sourcePlatform: string
  /** 源资产币（如 WTI）。 */
  sourceBase: string
  /** 源交易币（Lighter 侧为 USDC）。 */
  sourceQuote: string
  /** 源标的的显示形式（WTI/USDC）。 */
  sourcePair: string
  /** 目标**平台符号**（binance 等），不是配置 Id——表格的「交易所」列显示它。 */
  targetPlatform: string
  /** 目标资产币（如 CL）。 */
  targetBase: string
  /** 目标交易币（币安为 USDT）。 */
  targetQuote: string
  /** 目标标的的系统标准形式（CL/USDT）。 */
  targetPair: string
  /** 平台自己的合约类型口径（`PERPETUAL`/`SWAP`/`stocks`…）。 */
  targetContractType: string
  /** 目标合约是不是传统金融（股票/商品/指数）。 */
  targetIsTradFi: boolean
  /** `scanned`（扫描出来的）/ `manual`（人工登记，扫描不再覆盖）。 */
  origin: string
  isEnabled: boolean
  sourcePrice: number | null
  targetPrice: number | null
  /** 两侧价格的偏离倍数（1.0 表示几乎一致）——"这条映射还可信吗"的量化依据。 */
  priceRatio: number | null
  verifiedAt: string | null
  note: string | null
  createdAt: string
}

/** 扫描结果。 */
export interface SymbolMappingScanResult {
  /** 扫描的目标平台符号。 */
  targetPlatform: string
  sourceMarkets: number
  /** 这次新登记的行数（**只会有与源不同的那些**：同名的直接用，不登记）。 */
  created: number
  updated: number
  /** 与源同名、直接用、不登记的个数。 */
  direct: number
  /** 这次收掉的同名冗余行数。 */
  deleted: number
  /** 同名但停用、因而保留的行数（那里面有一个"不要跟它"的决定）。 */
  kept: number
  skippedManual: number
  /** 没能映射上的源标的及原因。它们不会被写进表，所以这里必须说清是哪些、为什么。 */
  failures: SymbolMappingFailure[]
}

/** 一条没能映射上的源标的。 */
export interface SymbolMappingFailure {
  sourceBase: string
  sourceQuote: string
  reason: string
}

/** 人工登记/修正映射。 */
export interface SymbolMappingPayload {
  /** 目标平台符号（binance 等）。 */
  targetPlatform: string
  /** 源资产币。源交易币固定 USDC（Lighter 的逐市场 quote 字段恒为 0）。 */
  sourceBase: string
  /** 目标资产币（从平台合约清单里选）。 */
  targetBase: string
  /** 目标交易币。 */
  targetQuote: string
  isEnabled: boolean
  /** 源侧价格。**不要传 undefined**：项目开了 exactOptionalPropertyTypes，显式 undefined 与"不传"是两回事。 */
  sourcePrice?: number
  note?: string
}

// ── 会话与登录（MFA 强制）──────────────────────────────────
//
// 会话本身在 Cookie 里（HttpOnly），前端没有"令牌"这个东西要存——
// 这里只保留"当前是谁"这份用于渲染的信息，退出登录时清掉它即可。

export interface Session {
  userId: number
  email: string
}

/// 登录第一步的答复：下一步做什么，而不是"登录成功"。
export interface LoginChallenge {
  /** `mfaRequired` = 已绑令牌，直接输码；`mfaBindingRequired` = 还没绑，先扫码。 */
  status: 'mfaRequired' | 'mfaBindingRequired'
  challengeId: string
  /** 待绑定的密钥（Base32）；仅待绑定时有值。 */
  secret: string | null
  /** 前端据此画二维码；仅待绑定时有值。 */
  otpauthUri: string | null
}

/// 一次投递的结果。三种情形分开说：发了没有 / 是不是只进了日志 / 失败原因。
export interface NotificationDelivery {
  delivered: boolean
  logFallback: boolean
  error: string | null
  message: string
}

export interface NotificationProvider {
  id: number
  kind: string
  displayName: string
  isEnabled: boolean
  host: string | null
  port: number
  useStartTls: boolean
  userName: string | null
  /** 口令尾号；界面上只用来确认"配的是哪一条"。 */
  passwordHint: string | null
  fromAddress: string | null
  fromName: string
  isUsable: boolean
}

export interface NotificationProviderPayload {
  kind: string
  displayName: string
  isEnabled: boolean
  host: string | null
  port: number
  useStartTls: boolean
  userName: string | null
  /** 口令三态：不传 = 保持原样，空串 = 清除，传值 = 换新（与交易所凭据同一规则）。 */
  password?: string | null
  fromAddress: string | null
  fromName: string
}

export interface NotificationTemplate {
  purpose: string
  displayName: string
  subject: string
  body: string
  providerId: number | null
  /** false = 现在用的是代码里的默认样板（还没被改过）。 */
  isCustomized: boolean
  defaultSubject: string
  defaultBody: string
  /** 正文里必须保留的占位符。 */
  requiredPlaceholders: string[]
}

export interface NotificationLogEntry {
  id: number
  purpose: string
  purposeName: string
  providerId: number | null
  providerName: string
  kind: string
  recipient: string
  subject: string
  body: string
  delivered: boolean
  logFallback: boolean
  error: string | null
  sentAt: string
}

export const authApi = {
  /** 当前会话。未登录时抛 401 的 ApiError（由 useSession 接住）。 */
  me: () => getJson<Session>('/api/auth/me'),

  sendRegistrationCode: (email: string) =>
    getJson<NotificationDelivery>('/api/auth/register/code', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ email }),
    }),

  /// 注册成功没有响应体（后端回 204）——它不签发会话，也没什么可回的。
  register: (payload: { email: string; code: string; password: string; confirmPassword: string }) =>
    sendWithoutBody('/api/auth/register', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(payload),
    }),

  login: (payload: { email: string; password: string }) =>
    getJson<LoginChallenge>('/api/auth/login', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(payload),
    }),

  /** 已绑定：六位码换会话。 */
  verifyMfa: (payload: { challengeId: string; code: string }) =>
    getJson<Session>('/api/auth/mfa/verify', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(payload),
    }),

  /** 未绑定：验过码即完成绑定，并换到会话。 */
  bindMfa: (payload: { challengeId: string; code: string }) =>
    getJson<Session>('/api/auth/mfa/bind', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(payload),
    }),

  logout: async (): Promise<void> => {
    await fetch('/api/auth/logout', { method: 'POST' })
  },
}

export const notificationApi = {
  providers: () => getJson<NotificationProvider[]>('/api/notifications/providers'),

  createProvider: (payload: NotificationProviderPayload) =>
    getJson<NotificationProvider>('/api/notifications/providers', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(payload),
    }),

  updateProvider: (id: number, payload: NotificationProviderPayload) =>
    getJson<NotificationProvider>(`/api/notifications/providers/${id}`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(payload),
    }),

  deleteProvider: (id: number) =>
    sendWithoutBody(`/api/notifications/providers/${id}`, { method: 'DELETE' }),

  /** 用这条通道发一封测试邮件。发失败也是 200，结果里带着 SMTP 的原话。 */
  testProvider: (id: number) =>
    getJson<NotificationDelivery>(`/api/notifications/providers/${id}/test`, { method: 'POST' }),

  templates: () => getJson<NotificationTemplate[]>('/api/notifications/templates'),

  updateTemplate: (purpose: string, payload: { subject: string; body: string; providerId: number | null }) =>
    getJson<NotificationTemplate>(`/api/notifications/templates/${purpose}`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(payload),
    }),

  logs: () => getJson<NotificationLogEntry[]>('/api/notifications/logs'),
}
