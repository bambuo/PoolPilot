/// 池选择框的搜索过滤。
///
/// Arco 的 `allow-search` 默认只匹配选项的 label（池名），而使用者手里
/// 常有的恰恰是**账户索引**——比如从池详情页地址栏或别人发来的链接里复制来的 20 位数字。
/// 搜索命中不了索引，选择框就白瞎了它最重要的入口。
///
/// 配套要求：选项必须带 `:label`（池名），否则默认显示会退化成索引。
import type { PoolSummary } from '@/api/client'

export interface PoolOptionLike {
  readonly value?: unknown
  readonly label?: unknown
}

/** 选项的展示标签：池名优先，未命名时用索引兜底。 */
export function poolOptionLabel(pool: PoolSummary): string {
  return pool.name || '（未命名）'
}

/** 同时匹配池名与账户索引（都是子串匹配，忽略大小写）。 */
export function filterPoolOption(inputValue: string, option: PoolOptionLike): boolean {
  const keyword = inputValue.trim().toLowerCase()

  if (keyword === '') return true

  // Arco 的选项 value 类型很宽（还能传对象/布尔），这里只认得出来是字符串的部分；
  // 本项目所有池选项的 value 都是 String(accountIndex)
  const label = typeof option.label === 'string' ? option.label.toLowerCase() : ''
  const value = typeof option.value === 'string' ? option.value.toLowerCase() : ''

  return label.includes(keyword) || value.includes(keyword)
}
