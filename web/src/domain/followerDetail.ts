/// 跟单员详情的展示模型。
///
/// 公开页与工作台从不同契约取数（窄 DTO / 完整 Follower + 实盘账户），
/// 但总览、持仓表、流水表、权益曲线的排版与术语必须只有一套。两边都先映射成
/// 这里的模型，再交给共享组件渲染；格式化在各自的 mapper 里完成，
/// 共享组件只负责排版，避免同一个数字在两个页面上口径不同。
export type MetricTone = 'default' | 'primary' | 'success' | 'danger' | 'warning'

export interface FollowerMetric {
  label: string
  value: string
  note?: string | undefined
  tone?: MetricTone | undefined
}

export type PnlTone = 'profit' | 'loss' | 'neutral'

export interface FollowerPositionRow {
  key: string
  symbol: string
  /** 悬停提示：实盘里认不出的合约用 venueSymbol 展示。 */
  symbolHint?: string | undefined
  side: 'long' | 'short' | null
  sizeText: string
  entryText: string
  /** null = 取不到标记价，未实现盈亏不可得（不是 0）。 */
  markText: string | null
  markHint?: string | undefined
  /** 清算价；没有正数解时为 null。 */
  liquidationText: string | null
  notionalText: string
  unrealizedText: string
  unrealizedTone: PnlTone
  /** 未实现盈亏 ÷ 保证金（百分数）；保证金不可得时为 null。 */
  returnText: string | null
  returnTone?: PnlTone | undefined
  realizedText?: string | undefined
  /** 实盘专属列。 */
  leverageText?: string | undefined
  /** 实盘原始行，只供工作台的平仓按钮使用；共享表不读。 */
  live?: unknown | undefined
}

export interface FollowerFillAction {
  /** 纸面：买入/卖出；实盘：开多/平多/开空/平空。 */
  text?: string | undefined
  side?: 'buy' | 'sell' | undefined
  /** 实盘动作后面的方向词（BUY/SELL）。 */
  subtext?: string | undefined
  tags?: { label: string; color: string }[] | undefined
}

export interface FollowerFillResult {
  label: string
  color: string
}

export interface FollowerFillRow {
  key: string
  atText: string
  symbol: string
  /** 源标的与目标不一致时的旁注。 */
  symbolNote?: string | undefined
  symbolHint?: string | undefined
  action: FollowerFillAction | null
  quantityText: string
  /** 单价格模式（公开页）用这个。 */
  priceText?: string | undefined
  /** 开/平两列模式（工作台）用这两个。 */
  entryPriceText?: string | undefined
  closePriceText?: string | undefined
  notionalText: string
  result: FollowerFillResult
  realizedText?: string | undefined
  realizedTone?: PnlTone | undefined
  /** 平仓行的投资回报率（已实现盈亏 ÷ 该笔保证金）；开仓行/无保证金为 null。 */
  fillReturnText?: string | undefined
  fillReturnTone?: PnlTone | undefined
  note?: string | undefined
}
