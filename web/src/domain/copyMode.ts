/// 跟单模式：文案与"新建时可选哪些"的唯一定义处。
///
/// 模式现在**是跟单员自己的属性**（后端按账户返回 `mode`），这里只负责两件事：
/// 把值翻译成界面文案，以及在新建时列出可选项。
/// 界面凡是显示"某个账户是什么模式"的地方都必须读账户上的字段，不能读这里的常量——
/// 那是数据，不是配置。
export type CopyMode = 'paper' | 'live'

export interface CopyModeOption {
  readonly value: CopyMode
  readonly label: string
  /// 不可选时为 `false`，并在界面上说明原因而不是静默禁用。
  readonly enabled: boolean
  readonly description: string
}

/// 实盘模式的说明。**必须说清它会真花钱**，而不是一句"真实下单"：
/// 选错模式的代价不对称——纸面多跑一天只是少赚，实盘跑起来是真的在下单。
/// 这段是**纯文本**插值渲染（FollowListView 的 {{ selectedMode.description }}），
/// 所以不能带 markdown 记号——** 会原样显示成四个星号。
export const LIVE_MODE_DESCRIPTION =
  '真在交易所下单，用你配好的那个账号。仓位按名义价值等比复制（池子该仓位值多少钱，' +
  '就开多少钱），跨交易所也成立。没映射上的标的不会硬跟——见「标的映射」页。'

export const COPY_MODES: readonly CopyModeOption[] = [
  {
    value: 'paper',
    label: '纸面模式',
    enabled: true,
    description: '不发送任何真实订单，按真实盘口模拟成交并完整记账盈亏。',
  },
  {
    value: 'live',
    label: '实盘模式',
    enabled: true,
    description: LIVE_MODE_DESCRIPTION,
  },
]

/// 默认模式。新增跟单一律从纸面开始——先量出跟单到底能不能赚，再谈下单。
export const DEFAULT_COPY_MODE: CopyMode = 'paper'

/// 后端返回的模式值 → 文案。认不出的值原样返回（宁可显示一个怪字符串，
/// 也不猜成"纸面"——猜错方向的话，实盘会被显示成纸面）。
export function copyModeLabel(mode: string): string {
  return COPY_MODES.find((m) => m.value === mode)?.label ?? mode
}
