/** 数值格式化。集中一处，避免各页面各写一份精度规则。 */

/**
 * 金额（含美元符号）。
 *
 * 符号与负号的顺序由这里唯一决定：`$857.05`、`-$14.25`。
 * 曾经把 `$` 粘在模板里（`${{ formatUsd(x) }}`），于是负数渲染成 `$-14.25`——
 * 负号落在货币符号之后，读起来像"负的美元符号"。格式化规则只该有一份，
 * 符号位置也不例外。
 *
 * 量级 >= 1000 时缩写为 K/M（总资产 72197 → `$72.2K`），
 * 否则按 digits 位定点（857.05 → `$857.05`）。这个阈值行为是有意的：
 * 概览类数字要短，明细类数字要准。
 */
export function formatUsd(value: number, digits = 2): string {
  const sign = value < 0 ? '-' : ''
  const abs = Math.abs(value)

  if (abs >= 1_000_000) return `${sign}$${(abs / 1_000_000).toFixed(2)}M`
  if (abs >= 1_000) return `${sign}$${(abs / 1_000).toFixed(1)}K`

  return `${sign}$${abs.toFixed(digits)}`
}

/**
 * 紧凑数字（**不带**货币符号）。用于份额、数量这类"计数"而非金额的值。
 *
 * 与 formatUsd 分开是刻意的：份额总量 1192M 不是 1192 美元，
 * 之前两者共用 formatUsd，一旦给金额加上符号，份额就会被标成美元。
 */
export function formatCompact(value: number, digits = 2): string {
  const sign = value < 0 ? '-' : ''
  const abs = Math.abs(value)

  if (abs >= 1_000_000) return `${sign}${(abs / 1_000_000).toFixed(2)}M`
  if (abs >= 1_000) return `${sign}${(abs / 1_000).toFixed(1)}K`

  return `${sign}${abs.toFixed(digits)}`
}

export function formatNumber(value: number, digits = 2): string {
  return value.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits })
}

export function formatPercent(value: number, digits = 2): string {
  return `${value.toFixed(digits)}%`
}

/** 基点。用于漏损这类小量级数值——用百分数表示会全是 0.0x%。 */
export function formatBps(value: number, digits = 1): string {
  return `${value.toFixed(digits)} bps`
}

export function formatTime(iso: string | null): string {
  if (!iso) return '—'

  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? '—' : date.toISOString().slice(11, 19)
}

export function formatDateTime(iso: string | null): string {
  if (!iso) return '—'

  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? '—' : date.toISOString().slice(0, 16).replace('T', ' ')
}

export function formatDate(iso: string | null): string {
  if (!iso) return '—'

  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? '—' : date.toISOString().slice(0, 10)
}

/**
 * 距今的运营时长（"1 年 2 个月" / "13 天"）。
 *
 * 按日历精确计算而不是天数 ÷30：月份长短不一，近似值会让"13 个月"显示成"1 年 1 个月"
 * 或反过来，用的人拿它判断池子资历，误差必须避免。不足一个月按天，不足一天按"今天"。
 */
export function formatAgeSince(iso: string | null, now = new Date()): string {
  if (!iso) return '—'

  const created = new Date(iso)
  if (Number.isNaN(created.getTime())) return '—'

  let months = (now.getFullYear() - created.getFullYear()) * 12 + (now.getMonth() - created.getMonth())
  if (now.getDate() < created.getDate()) months -= 1

  if (months < 1) {
    const days = Math.max(0, Math.floor((now.getTime() - created.getTime()) / 86_400_000))
    return days <= 0 ? '今天' : `${days} 天`
  }

  const years = Math.floor(months / 12)
  const remainder = months % 12

  if (years === 0) return `${months} 个月`
  return remainder === 0 ? `${years} 年` : `${years} 年 ${remainder} 个月`
}

/**
 * 距今多久（"刚刚" / "12 分钟前" / "3 小时前" / "2 天前"）。
 *
 * 与 <see cref="formatAgeSince"/> 的分工必须分清，否则会写出"今天前"这种句子：
 * 那个给的是**时长**（这个东西存在多久了），用在"运营时长"这类列上；
 * 这个给的是**某个时刻距今多远**，用在"最近信号"这类时刻列上。
 * 两者名字相近但回答的是不同的问题，混用不会报错，只会让文案变得别扭。
 */
export function formatSince(iso: string | null, now = new Date()): string {
  if (!iso) return '—'

  const at = new Date(iso)
  if (Number.isNaN(at.getTime())) return '—'

  const seconds = Math.floor((now.getTime() - at.getTime()) / 1000)

  // 负值来自两台机器之间的时钟偏差。显示成"-3 分钟前"会让人以为数据坏了
  if (seconds < 60) return '刚刚'

  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes} 分钟前`

  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours} 小时前`

  return `${Math.floor(hours / 24)} 天前`
}

/** 持仓方向的中文标签。 */
export function sideLabel(side: 'long' | 'short' | string): string {
  return side === 'long' ? '多' : side === 'short' ? '空' : side
}

/** 事件种类的中文标签。 */
export function eventKindLabel(kind: string): string {
  const map: Record<string, string> = {
    open: '开仓',
    increase: '加仓',
    reduce: '减仓',
    close: '平仓',
    flip: '反手',
  }

  return map[kind] ?? kind
}

/** 事件种类的标签颜色（Arco 语义色）。 */
export function eventKindColor(kind: string): string {
  const map: Record<string, string> = {
    open: 'green',
    increase: 'arcoblue',
    reduce: 'orange',
    close: 'red',
    flip: 'purple',
  }

  return map[kind] ?? 'gray'
}
