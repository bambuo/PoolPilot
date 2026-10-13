import binanceIcon from '@/assets/exchanges/binance.png'
import gateIcon from '@/assets/exchanges/gate.png'
import lighterIcon from '@/assets/exchanges/lighter.png'
import okxIcon from '@/assets/exchanges/okx.png'

/// 交易所预设的唯一定义处。
///
/// 预设只做一件事：把**已知的公开信息**填好，省掉每次新建都要手敲接口地址。
/// 三件事刻意不做：
/// <list type="number">
///   <item>**不校验**。费率档位因账户而异（VIP 等级、做市商、活动），
///     所以这里填的是"常见标准档"，页面上明确写着请按实际档位改。</item>
///   <item>**不锁定**。凡是预填的值在表单里都能改——自建网关、代理地址都是常态。</item>
///   <item>**不猜凭据**。每个平台要几个字段、每个字段叫什么，只在预设里描述形状，
///     真正的内容永远由使用者输入。</item>
/// </list>
export type ExchangeCode = 'lighter' | 'binance' | 'okx' | 'gate' | 'custom'

export type ExchangeKind = 'cex' | 'dex'

export interface ExchangePreset {
  readonly code: ExchangeCode
  readonly label: string
  readonly kind: ExchangeKind
  readonly restBaseUrl: string
  readonly wsUrl: string
  /// 常见标准档的挂单费率（百分数）。实测过的只有 Lighter：其 Standard 档 maker/taker 均为 0。
  readonly makerFeePercent: number
  readonly takerFeePercent: number
  /// 平台标识图。**随包走，不是外链**：
  /// 各家的 CDN 会拦（Gate 的 apple-touch-icon 直接 403）、也会变，运行时外链等于把界面交给别人。
  /// 来源与尺寸写在 assets/exchanges 目录下的说明里；`null` 表示没有图标（兜底项）。
  readonly icon: string | null
  /// 一句话简介，用在"选择平台"那一步的卡片上：先让人认出这是哪个平台。
  readonly blurb: string
  /// 第二个凭据字段的标签。Lighter 放的是签名私钥，与 CEX 的 API Secret 语义不同。
  readonly secretLabel: string
  /// 该平台的凭据要多填一个 passphrase（OKX 用）。
  readonly needsPassphrase: boolean
  /// 凭据从哪来的提示——这一句能省掉一次"去哪找 key"的摸索。
  readonly credentialHint: string
}

/// 不在清单里的标识一律退回它。
///
/// 它**不是可选项**——平台下拉里只有下面那四家。它只需存在，用于读取：
/// 库里万一存着不认识的标识（手工改过库、或将来下线了某个平台），
/// 页面仍要能把它显示出来，而不是整页崩掉。
const CustomPreset: ExchangePreset = {
  code: 'custom',
  icon: null,
  blurb: '库里存着清单外的平台标识时的兜底显示项。',
  label: '其他平台',
  kind: 'cex',
  restBaseUrl: '',
  wsUrl: '',
  makerFeePercent: 0,
  takerFeePercent: 0,
  secretLabel: 'API Secret',
  needsPassphrase: false,
  credentialHint: '库里存着不在清单里的平台标识时的兜底显示项——新建配置只能从上面四家里选。',
}

export const EXCHANGE_PRESETS: readonly ExchangePreset[] = [
  {
    code: 'lighter',
    icon: lighterIcon,
    blurb: 'zk 永续 DEX。本项目的池数据全部来自这里。',
    label: 'Lighter',
    kind: 'dex',
    restBaseUrl: 'https://mainnet.zklighter.elliot.ai/api/v1',
    wsUrl: 'wss://mainnet.zklighter.elliot.ai/stream',
    makerFeePercent: 0,
    takerFeePercent: 0,
    secretLabel: '签名私钥',
    needsPassphrase: false,
    credentialHint:
      'Lighter 的 API Key 要在官网用 L1 钱包签名注册。本项目当前只读它的公开数据'
      + '（Lighter 侧不下单），这把 key 暂时没有消费方。',
  },
  {
    code: 'binance',
    icon: binanceIcon,
    blurb: 'U 本位永续，接口域名 fapi。',
    label: '币安',
    kind: 'cex',
    restBaseUrl: 'https://fapi.binance.com',
    wsUrl: 'wss://fstream.binance.com/ws',
    makerFeePercent: 0.02,
    takerFeePercent: 0.05,
    secretLabel: 'API Secret',
    needsPassphrase: false,
    credentialHint: '在币安「API 管理」创建，建议只勾选合约交易、并绑定 IP。',
  },
  {
    code: 'okx',
    icon: okxIcon,
    blurb: '统一账户，凭据需要一个 passphrase。',
    label: 'OKX',
    kind: 'cex',
    restBaseUrl: 'https://www.okx.com',
    wsUrl: 'wss://ws.okx.com:8443/ws/v5/public',
    makerFeePercent: 0.02,
    takerFeePercent: 0.05,
    secretLabel: 'API Secret',
    needsPassphrase: true,
    credentialHint: 'OKX 的 API Key 需要一个 passphrase（创建时自己设的），三项都要填。',
  },
  {
    code: 'gate',
    icon: gateIcon,
    blurb: 'USDT 永续，v4 接口。',
    label: 'Gate',
    kind: 'cex',
    restBaseUrl: 'https://api.gateio.ws/api/v4',
    wsUrl: 'wss://fx-ws.gateio.ws/v4/ws/usdt',
    makerFeePercent: 0.02,
    takerFeePercent: 0.05,
    secretLabel: 'API Secret',
    needsPassphrase: false,
    credentialHint: '在 Gate「API Keys」创建，注意合约与现货的权限是分开勾的。',
  },
]

/// 标识 → 显示名。<c>custom</c> 只用于历史/未知标识的读取兜底，不在下拉里出现。
export const EXCHANGE_CODE_LABELS: Readonly<Record<ExchangeCode, string>> = {
  lighter: 'Lighter',
  binance: '币安',
  okx: 'OKX',
  gate: 'Gate',
  custom: '其他平台',
}

export const EXCHANGE_KIND_LABELS: Readonly<Record<ExchangeKind, string>> = {
  cex: '中心化',
  dex: '去中心化',
}

/// 形态的胶囊标签：卡面上用大写缩写。
///
/// 与 <see cref="EXCHANGE_KIND_LABELS"/> 分工不同——中文全称用在配置卡片上（那里有宽度），
/// 缩写用在"选择平台"的选择卡片与详情头部（那里要与平台名挤在一行）。
export const EXCHANGE_KIND_BADGES: Readonly<Record<ExchangeKind, string>> = {
  cex: 'CEX',
  dex: 'DEX',
}

/// 选择平台那一步的一个分组。
export interface ExchangePresetGroup {
  readonly kind: ExchangeKind
  readonly title: string
  readonly presets: readonly ExchangePreset[]
}

/// 选择平台那一步的分组。**顺序固定：中心化在前**——执行侧目前落在币安，
/// 使用者多半先在这里找它；组内保持 <see cref="EXCHANGE_PRESETS"/> 的顺序。
export const EXCHANGE_PRESET_GROUPS: readonly ExchangePresetGroup[] = [
  {
    kind: 'cex',
    title: '中心化交易所 (CEX)',
    presets: EXCHANGE_PRESETS.filter((p) => p.kind === 'cex'),
  },
  {
    kind: 'dex',
    title: '去中心化交易所 (DEX)',
    presets: EXCHANGE_PRESETS.filter((p) => p.kind === 'dex'),
  },
]

export function presetOf(code: string): ExchangePreset {
  return EXCHANGE_PRESETS.find((p) => p.code === code) ?? CustomPreset
}

export function codeLabel(code: string): string {
  return EXCHANGE_CODE_LABELS[code as ExchangeCode] ?? code
}

export function kindLabel(kind: string): string {
  return EXCHANGE_KIND_LABELS[kind as ExchangeKind] ?? kind
}

export function kindBadge(kind: string): string {
  return EXCHANGE_KIND_BADGES[kind as ExchangeKind] ?? kind
}
