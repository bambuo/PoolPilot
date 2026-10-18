import { formatNumber } from '@/utils/format'

/// 标的杠杆的展示口径，工作台卡片与公开跟单卡片共用一份。
///
/// 两个模式的来源不同：
///   · 实盘直接用交易所给该合约的杠杆（账户事实）；
///   · 纸面用源池初始保证金率反推（杠杆 = 100 ÷ 保证金率），沿来源池的杠杆口径。
///
/// 取不到、为 0 或负数都显示 —，并给出悬停原因。**不拿 1× 冒充"没有杠杆"**：
/// 那会把"不知道"画成一个具体的风险数字。
export function formatLeverage(leverage: number): string {
  return `${formatNumber(leverage, Number.isInteger(leverage) ? 0 : 2)}×`
}

export function paperLeverage(initialMarginPercent: number | null): number | null {
  return initialMarginPercent !== null && initialMarginPercent > 0
    ? 100 / initialMarginPercent
    : null
}

export function leverageText(leverage: number | null): string {
  return leverage !== null && leverage > 0 ? formatLeverage(leverage) : '—'
}

export function leverageHint(leverage: number | null): string {
  return leverage !== null && leverage > 0
    ? ''
    : '这个持仓没有可用的保证金率或交易所杠杆，无法展示标的杠杆'
}
