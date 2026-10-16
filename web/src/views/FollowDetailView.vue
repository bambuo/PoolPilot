<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'

import {
  api,
  type CopyLedger,
  type Follower,
  type LiveAccount,
  type LiveOrder,
  type LivePosition,
} from '@/api/client'
import { useLiveAccount } from '@/composables/useMonitorHub'
import { usePermission } from '@/composables/usePermission'
import HeaderHint from '@/components/HeaderHint.vue'
import LineChart from '@/components/LineChart.vue'
import MetricTile from '@/components/MetricTile.vue'
import { copyModeLabel } from '@/domain/copyMode'
import { formatDateTime, formatNumber, formatPercent, formatUsd } from '@/utils/format'

/// 跟单详情 —— 单个跟单员的影子账户。
///
/// 从卡片列表点进来，跟单员由路由参数（自增 Id）确定，所以这里没有池选择器。
/// 注意这个 Id 是**跟单员自己的**，不是池索引：同一个池可以挂多个跟单员，
/// 用池索引当身份就区分不出它们。
///
/// 这一页回答的是项目的核心问题：**跟着它做，我的钱会变成多少**。
/// 做法是维护一个完整的影子账户——除了不发真实订单，其余一切照实记账：
/// 本金、仓位、开仓均价、已实现与未实现盈亏、权益曲线。
///
/// 规模口径是**比例复制**：我方目标仓位 = 池子仓位 × (我方本金 / 池子权益)。
/// 只有这个口径下"跟单能不能赚"才有确定答案，因为止盈止损藏在带单员手里，
/// 跟单者只能继承它的进出场。换成"每个信号固定下 N 单位"就必须自己发明一套
/// 出场规则，那测的就不是跟单，而是自己的策略了。

/// 本页自身的权限键，与「跟单」列表同一份（服务端按 `menu:{code}` 生成，前端只读不写）。
///
/// 详情页承载了改本金/启停/手动平仓（edit）与删除跟单员（delete）四类写口——
/// 手动平仓是**真实下单**，但语义上仍是"改这个跟单员的状态"，所以同样归 edit。
const PAGE_PERMISSION = 'menu:copy-list'

const { can, refresh: refreshPermissions } = usePermission()

const canEdit = computed(() => can(PAGE_PERMISSION, 'edit'))
const canDelete = computed(() => can(PAGE_PERMISSION, 'delete'))

const route = useRoute()
const router = useRouter()

/// 跟单员由路由决定，不在这里选——列表页负责选，详情页负责看。
const followerId = computed(() => String(route.params.id))
const capitalInput = ref(5000)

const follower = ref<Follower | null>(null)
const costReport = ref<CopyLedger | null>(null)

/// 实盘流水。只有实盘跟单员才有——纸面跟单员的账在下半部分的纸面流水里。
const liveOrders = ref<LiveOrder[]>([])

/// 实盘账户的真实状态（交易所给的）。读不到时为 null，界面据此说"读不到"，
/// 而不是退回纸面那套数字——那会让人以为账户一切正常。
const liveAccount = ref<LiveAccount | null>(null)
const liveAccountError = ref('')

/// 实时通道：交易所的用户数据流一有动静，后端就重读账户并推过来。
///
/// 订阅**本跟单员**的更新（事件里带 followerId）：同一个交易所账号下可能挂着多个跟单员，
/// 谁的本金、谁的规模基准都不一样，不能互相串。
/// 每秒钟推来的未实现盈亏：{ 标的 → 盈亏 } 与合计。
///
/// 这是**唯一**真正随价格跳动的部分：币安只在仓位真的变化时推账户，不推价格，
/// 所以后端订阅了每秒的标记价，用交易所自己的公式当场算（公式已对齐到 8 位小数）。
/// 账户快照（仓位、余额）仍是权威的那一份，两条路合起来用：
/// 快照负责"持有什么、有多少钱"，这里负责"现在赚亏多少"。
const livePnlByPair = ref<Record<string, number>>({})
const livePnlTotal = ref<number | null>(null)

/// 每秒推来的标记价（按系统标准标的）：与盈亏同源，都是后端订阅的每秒行情。
const liveMarksByPair = ref<Record<string, number>>({})

const liveChannel = useLiveAccount(
  followerId,
  (account) => {
    liveAccount.value = account as LiveAccount
    // 收到过推送就说明账户读得到，把上一次的读取错误清掉
    liveAccountError.value = ''
  },
  (healthy) => {
    // 后端用金丝雀判定的链路健康。判不出来时（还没跑过探针）保持 null，界面不表态
    liveLinkHealthy.value = healthy
  },
  (byPair, total, marksByPair) => {
    livePnlByPair.value = byPair
    livePnlTotal.value = total
    liveMarksByPair.value = marksByPair
  },
)

/// 持仓行上显示的未实现盈亏：优先用每秒推来的实时值，
/// 没有实时值时退回快照里的数（那是交易所自己报的）
function positionPnl(position: LivePosition): number {
  const live = position.pair === null ? undefined : livePnlByPair.value[position.pair]

  return live ?? position.unrealizedPnl
}

/// 账户级的未实现盈亏：实盘优先用每秒推送的实时值；纸面读模拟账
const accountPnl = computed(() => {
  if (follower.value?.mode === 'live') {
    return livePnlTotal.value ?? liveAccount.value?.unrealizedPnl ?? 0
  }

  return follower.value?.unrealizedPnl ?? 0
})

/// 账户级的持仓名义价值：同一槽位、两种来源
const accountNotional = computed(() =>
  follower.value?.mode === 'live'
    ? liveAccount.value?.grossNotional ?? 0
    : follower.value?.grossNotional ?? 0,
)

/// 实盘流水的两档计数（概览格与流水表同源）
const liveOrderCounts = computed(() => {
  const placed = liveOrders.value.filter((o) => o.status === 'placed').length
  const notFollowed = liveOrders.value.filter((o) => o.status !== 'placed').length

  return { placed, notFollowed }
})

/// 纸面流水行的开/平判定：仓位变厚是开、变薄是平（流水记了前后仓位，可判定）
function fillOpens(fill: { positionBefore: number; positionAfter: number }): boolean {
  return Math.abs(fill.positionAfter) > Math.abs(fill.positionBefore)
}

/// 纸面平仓行的开仓价：该标的我方当前持仓的加权开仓均价。
/// "当时"的精确成本纸面流水没记，用现在的成本线近似——纸面是模拟账，
/// 这个误差远小于它要回答的问题（"这一趟是赚是亏"）
function paperEntryOf(symbol: string): number | null {
  return follower.value?.positions.find((p) => p.symbol === symbol)?.entryVwap ?? null
}

/// 流水行的一句注解：实盘用被记下的原因（没留原因的就是订单号），纸面用跳过原因。
/// 空串表示这一行没有注解（成交的行、以及实盘里没留话的动作）
function fillNote(record: UnifiedFillRow): string {
  if (record.live) {
    return record.live.reason ?? (record.live.venueOrderId ? `订单 ${record.live.venueOrderId}` : '')
  }

  if (record.paper && !record.paper.isFilled) {
    return skipReasonLabel(record.paper.skipReason as PaperFillReason)
  }

  return ''
}

/// 统一持仓行：两种模式合到**同一张表**。共享列（数量/开仓均价/标记价/名义价值/未实现盈亏）
/// 读同一组字段；模式专属的原始记录挂在 paper / live 上，专属列从那里取。
/// 这是"布局与字段统一"的落点：两种模式看同一组列，差异只在尾部追加。
interface UnifiedPositionRow {
  key: string
  symbol: string
  side: 'long' | 'short' | null
  size: number
  entry: number
  mark: number | null
  notional: number
  unrealized: number
  /** 实盘才有：相对占用保证金的回报率。纸面没有杠杆概念，不算。 */
  liveReturn: number | null
  paper: Follower['positions'][number] | null
  live: LivePosition | null
}

const unifiedPositions = computed<UnifiedPositionRow[]>(() => {
  const f = follower.value
  if (!f) return []

  if (f.mode === 'live') {
    return (liveAccount.value?.positions ?? []).map((p) => ({
      key: p.venueSymbol,
      symbol: p.pair ?? p.venueSymbol,
      side: p.direction === 'short' ? 'short' : 'long',
      size: p.size,
      entry: p.entryPrice,
      mark: positionMark(p),
      notional: p.notional,
      unrealized: (p.pair === null ? undefined : livePnlByPair.value[p.pair]) ?? p.unrealizedPnl,
      liveReturn: positionReturn(p),
      paper: null,
      live: p,
    }))
  }

  return f.positions.map((p) => ({
    key: String(p.marketId),
    symbol: p.symbol,
    side: p.side,
    size: p.size,
    entry: p.entryVwap,
    mark: p.hasMark ? p.markPrice : null,
    notional: p.notional,
    unrealized: p.unrealizedPnl,
    liveReturn: null,
    paper: p,
    live: null,
  }))
})

/// 统一流水行：同理合到一张表。行带模式与原始记录，单元格按模式分支
interface UnifiedFillRow {
  key: string
  mode: 'paper' | 'live'
  paper: Follower['fills'][number] | null
  live: LiveOrder | null
}

const unifiedRows = computed<UnifiedFillRow[]>(() => {
  const f = follower.value
  if (!f) return []

  if (f.mode === 'live') {
    return liveOrders.value.map((o) => ({ key: `l${o.id}`, mode: 'live' as const, paper: null, live: o }))
  }

  return f.fills.map((x) => ({ key: `p${x.id}`, mode: 'paper' as const, paper: x, live: null }))
})

/// 持仓行上的标记价：优先用每秒推来的，没有就退回快照里的那个（交易所给的）
function positionMark(position: LivePosition): number {
  return (position.pair === null ? undefined : liveMarksByPair.value[position.pair]) ?? position.markPrice
}

/// 一笔仓位的回报率：未实现盈亏 ÷ **这笔占用的保证金**（名义价值 ÷ 杠杆），不是 ÷ 名义价值。
///
/// 为什么用保证金：名义价值作分母得到的是**价格涨跌**，那与"我投进去的钱赚了多少"是两回事——
/// 10 倍杠杆下价格动 1%，我占用的保证金就动 10%。交易员看的是后者。
function positionReturn(position: LivePosition): number | null {
  const margin = position.leverage > 0 ? position.notional / position.leverage : 0

  return margin > 0 ? (positionPnl(position) / margin) * 100 : null
}

/// 账户级的回报率：合计未实现盈亏 ÷ **钱包余额**（账户里属于这次跟单交易的钱）。
/// 不用持仓名义价值做分母：那是价格涨跌；也不该用规模基准——那是跟单规模的基准，不是账户里的钱。
const accountReturn = computed(() => {
  const wallet = liveAccount.value?.walletBalance ?? 0

  return wallet > 0 ? (accountPnl.value / wallet) * 100 : null
})

/// 后端判定的链路健康：true 通 / false 被网络挡住 / null 未知。
/// **三者必须分开显示**：把"未知"显示成"实时"是在说假话，而实测存在"握手通、数据一条不来"的链路
const liveLinkHealthy = ref<boolean | null>(null)

/// 随**价格**变的那几个数字（未实现盈亏、持仓名义价值）靠这个定时器跟上：
/// 交易所的用户数据流只在**账户发生变化**时推消息（下单、成交、强平、划转、资金费），
/// **价格变动不推**——所以只靠推送的话，"未实现盈亏"会在两次动作之间一直冻着，
/// 而那正是使用者盯着看的那个数。
///
/// 10 秒是刻意的折中：账户接口每次占用权重 5+5，10 秒一次在币安的分钟内权重限额下很宽裕；
/// 再快只是让数字抖得更勤，不会更准（交易所自己也是按标记价算的）。
/// 只在**页面可见**时跑：后台标签页里刷数是白花权重，而人看不到。
const LIVE_REFRESH_MS = 10_000

async function refreshLiveAccount(): Promise<void> {
  if (follower.value?.mode !== 'live') return

  try {
    liveAccount.value = await api.liveAccount(followerId.value)
    liveAccountError.value = ''
  } catch (error) {
    // 定时刷新失败**不弹错**：它每 10 秒跑一次，弹错会把页面刷满。
    // 真正的读取失败由首屏那次读取负责说清（那时才谈得上"读不到"）
  }
}

let liveTimer: number | null = null

/// 离开页面就停掉定时器：不定时器的组件会在导航之后继续跑，
/// 那就是一个永远不会被看见、也不会停的后台轮询
onUnmounted(stopLiveRefresh)

function startLiveRefresh(): void {
  stopLiveRefresh()

  if (follower.value?.mode !== 'live') return

  liveTimer = window.setInterval(() => {
    if (document.visibilityState === 'visible') {
      void refreshLiveAccount()
    }
  }, LIVE_REFRESH_MS)
}

function stopLiveRefresh(): void {
  if (liveTimer !== null) {
    window.clearInterval(liveTimer)
    liveTimer = null
  }
}

/// 实时通道的文案。连接断了、链路不通都必须说出来——否则"没有新数据"与"通道坏了"看起来一样。
///
/// 措辞是「实时推送」而不是「实时」：交易所推的是**账户事件**（下单、成交、强平、划转），
/// 不是价格；把"推送实时"说成"数字实时"是过度承诺——使用者会盯着未实现盈亏等它自己跳，
/// 而它只会在账户下次变化时才动。随价格变的数字由页面的定时刷新负责（见 LIVE_REFRESH_MS）。
const liveChannelLabel = computed(() => {
  if (liveLinkHealthy.value === false) return '通道不可用'
  if (liveChannel.state.value === 'connected') return '实时推送'
  if (liveChannel.state.value === 'connecting') return '连接中'

  return liveChannel.state.value === 'reconnecting' ? '重连中' : '实时通道未连接'
})

const liveChannelColor = computed(() => {
  if (liveLinkHealthy.value === false) return 'red'

  return liveChannel.state.value === 'connected' ? 'green' : 'gray'
})

const liveChannelHint = computed(() => {
  if (liveLinkHealthy.value === false) {
    return '交易所的行情链路推不出数据（握手能过、数据不来）：数字只在打开页面或点刷新时更新'
  }

  return (
    '账户事件是交易所推过来的：下单、成交、强平、划转、资金费都会立刻反映。' +
    '未实现盈亏随「价格」变动，而交易所不推价格——所以页面每 10 秒自己刷一次（仅当这个标签页在前台）'
  )
})

/// 手动平仓的确认弹窗状态。
///
/// **必须有这一步**：这是真实下单、不可撤销。确认框里摆出数量与名义价值，
/// 并说清"平掉后会不会被下一次对账重新建起来"——那个后果不看说明是想不到的。
const closeTarget = ref<LivePosition | null>(null)
const closePauseFollower = ref(true)
const closing = ref(false)
const closeError = ref('')

function openClose(position: LivePosition): void {
  closeTarget.value = position
  closePauseFollower.value = true
  closeError.value = ''
}

async function confirmClose(): Promise<void> {
  const target = closeTarget.value
  if (!target || !target.baseAsset || !target.quoteAsset) return

  closing.value = true
  closeError.value = ''

  try {
    await api.closeLivePosition(followerId.value, {
      baseAsset: target.baseAsset,
      quoteAsset: target.quoteAsset,
      pauseFollower: closePauseFollower.value,
    })

    closeTarget.value = null
    await load()
  } catch (error) {
    // 报错留在弹窗里：交易所的拒单原因（保证金不足之类）是这里的重点，
    // 丢到页面顶部会被弹窗挡掉
    closeError.value = error instanceof Error ? error.message : String(error)
  } finally {
    closing.value = false
  }
}


/// 实盘流水的三档结局 → 界面文案与颜色。
///
/// `skipped` 与 `failed` 分开显示不是修饰：前者是我方主动没发（去看映射表或本金规模），
/// 后者是发出去了被交易所拒（去看交易所的保证金与规格）。混成一个"失败"，
/// 排查的人会在两个完全不同的地方之间乱找。
function liveStatusLabel(status: string): string {
  if (status === 'placed') return '已发出'
  if (status === 'skipped') return '未发出'

  return status === 'failed' ? '被拒' : status
}

function liveStatusColor(status: string): string {
  if (status === 'placed') return 'green'
  if (status === 'skipped') return 'gray'

  return status === 'failed' ? 'red' : 'gray'
}

/// 意图 → 文案。四种意图逐一列出，不去推"方向"：双向持仓下平多与开空都是卖出，
/// 只差 positionSide，用方向表达会让人以为它们是一回事。
function liveIntentLabel(intent: string): string {
  if (intent === 'open_long') return '开多'
  if (intent === 'close_long') return '平多'
  if (intent === 'open_short') return '开空'

  return intent === 'close_short' ? '平空' : intent
}
/// 404 与"网络错误"必须分开：前者是"没有这个跟单员"，后者是"暂时取不到"。
const notFound = ref(false)

const loading = ref(false)
const saving = ref(false)
const errorMessage = ref('')

/// 权益曲线。基准线画在初始本金上——高于它才是赚的。
const equitySeries = computed(() => {
  if (!follower.value || follower.value.curve.length === 0) return []

  return [
    {
      label: '账户权益',
      color: 'rgb(var(--primary-6))',
      values: follower.value.curve.map((point) => point.equity),
    },
  ]
})

/// 有持仓却取不到盯市价时，未实现盈亏是不可得的，不是 0。
const hasUnpricedPosition = computed(() =>
  (follower.value?.positions ?? []).some((p) => !p.hasMark && p.size !== 0),
)

const skipReasonLabel = (reason: PaperFillReason): string => {
  switch (reason) {
    case 'below_minimum_size':
      return '低于最小下单数量'
    case 'below_minimum_notional':
      return '低于最小下单金额'
    case 'no_liquidity':
      return '盘口无深度'
    default:
      return reason
  }
}

type PaperFillReason = 'below_minimum_size' | 'below_minimum_notional' | 'no_liquidity'

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = ''
  notFound.value = false

  try {
    follower.value = await api.follower(followerId.value)

    if (follower.value === null) {
      notFound.value = true
      costReport.value = null
      return
    }

    capitalInput.value = follower.value.initialCapital

    // 执行成本测量是**池子级**的（逐腿对照池子成交），所以按订阅的带单员取
    costReport.value = await api.ledger(follower.value.leadAccountIndex)

    // 实盘流水单独取：纸面流水那张表是"跟着它会变成多少"，与真实发出的单是两回事。
    // 取不到也不该让整页失败——页面上的配置与纸面部分依然有意义
    if (follower.value.mode === 'live') {
      try {
        liveOrders.value = await api.liveOrders(followerId.value)
      } catch {
        liveOrders.value = []
      }

      try {
        liveAccount.value = await api.liveAccount(followerId.value)
        liveAccountError.value = ''
      } catch (error) {
        liveAccount.value = null
        liveAccountError.value = error instanceof Error ? error.message : String(error)
      }

      startLiveRefresh()
    } else {
      liveOrders.value = []
      liveAccount.value = null
      liveAccountError.value = ''
      stopLiveRefresh()
    }
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    loading.value = false
  }
}

/// 改本金前的确认弹窗状态。**这是必需的**：改本金会清空该跟单员的全部历史，
/// 而这个按钮就挨着「刷新」——一次误点会静默销毁几个小时的账面记录。
/// 确认框里列出具体会丢什么，而不是笼统地问"确定吗"。
const confirmOpen = ref(false)
const pendingActive = ref(true)

/// 有历史才需要确认；空账户改本金没有任何损失。
const historyToLose = computed(() => {
  if (!follower.value) return null

  const fills = follower.value.fillCount
  const skips = follower.value.skipCount
  const points = follower.value.curve.length

  return fills + skips + points === 0 ? null : { fills, skips, points }
})

/// 实盘的本金上限：可用保证金。读不到账户时为 null（那时不设上限，交给后端把关）。
const capitalMax = computed(() =>
  follower.value?.mode === 'live' && liveAccount.value
    // 向下取到分：可用保证金是交易所给的浮点数（179.59496361），
    // 拿它当上限时输入框会自动收敛成那个原值——一个没人会去填的怪数字。
    // 向下取整保证"不超过"这条仍然成立，只是把上限变成一个能读的数
    ? Math.floor(liveAccount.value.availableBalance * 100) / 100
    : null,
)

/// 输入框的上下限。**没有上限时不能传 `max: undefined`**：本项目的
/// `exactOptionalPropertyTypes` 会把"显式 undefined"与"这个键不存在"当成两回事，
/// 所以按需拼出这个对象，而不是写 `:max="capitalMax ?? undefined"`
///
/// 上限**低于当前本金时不挂 max**：Arco 的输入框会把显示值收敛到上限——
/// "当前本金 50、账号可用保证金 0"会被显示成 0，与同一页概览里的「规模基准 $50.00」
/// 自相矛盾；更糟的是顺手点一下「重设本金并继续」就会把这个假的 0 真写进库里。
/// 这一档改由提交前那道带数字的校验拦住（后端也是同一条规则）。
const capitalInputLimits = computed(() => {
  const max = capitalMax.value
  const current = follower.value?.initialCapital ?? 0

  return max === null || max < current
    ? { min: 1, step: 500 }
    : { min: 1, step: 500, max }
})

function requestConfigure(isActive: boolean): void {
  // 空本金不能提交：后端会回一句框架级的参数错误，而这里能直接说清
  if (!(capitalInput.value > 0)) {
    errorMessage.value = '请填入大于 0 的本金：它是规模比例的分母，为 0 算不出任何仓位。'
    return
  }

  // 上限就地先说一遍：等后端回错误多一次往返，而且那句话里的数字此刻就在页面上
  if (capitalMax.value !== null && capitalInput.value > capitalMax.value) {
    errorMessage.value =
      `本金不能超过该账号的可用保证金 ${formatUsd(capitalMax.value)}——` +
      '实盘按这个本金算出来的仓位，账户里的钱开不出来。'
    return
  }

  pendingActive.value = isActive

  const changingCapital =
    follower.value !== null && capitalInput.value !== follower.value.initialCapital

  if (changingCapital && historyToLose.value !== null) {
    confirmOpen.value = true
    return
  }

  void configure(isActive)
}

async function configure(isActive: boolean): Promise<void> {
  saving.value = true
  errorMessage.value = ''

  try {
    await api.updateFollower(followerId.value, capitalInput.value, isActive)
    confirmOpen.value = false
    await load()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    saving.value = false
  }
}

/// 删除跟单员。**不影响带单员是否继续观察**——池子照旧被盯着，只是少了一个跟随者。
const deleteOpen = ref(false)
const deleting = ref(false)

async function remove(): Promise<void> {
  deleting.value = true
  errorMessage.value = ''

  try {
    await api.deleteFollower(followerId.value)
    deleteOpen.value = false
    await router.push({ name: 'copy-list' })
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    deleting.value = false
  }
}

onMounted(async () => {
  // 权限缓存按会话共享：进来先拉一次，按钮状态才与当前会话一致
  try {
    await refreshPermissions()
  } catch (error) {
    console.warn('读取权限失败，本页按钮将按无权限处理', error)
  }

  await load()
})

// 从卡片 A 跳到卡片 B 时组件不会重建，得跟着路由参数重新取数
watch(followerId, load)
</script>

<template>
  <a-space direction="vertical" fill style="width: 100%">
    <a-card :bordered="false">
      <template #title>
        <a-space :size="12">
          <!-- 返回入口。用 RouterLink 而非按钮：这是导航，应该能新标签页打开、能被键盘聚焦 -->
          <RouterLink :to="{ name: 'copy-list' }" class="back-link">← 全部跟单</RouterLink>
          <span style="color: var(--color-text-3); font-weight: 400">|</span>
          <span>跟单员 #{{ followerId }}</span>
          <template v-if="follower">
            <a-tag size="small" :color="follower.mode === 'live' ? 'orange' : 'arcoblue'">
              {{ copyModeLabel(follower.mode) }}
            </a-tag>
            <!-- 实时与否必须看得见：断了的通道与"账户一直没变化"在界面上长得一样 -->
            <template v-if="follower.mode === 'live'">
              <a-tooltip :content="liveChannelHint">
                <a-tag size="small" :color="liveChannelColor">{{ liveChannelLabel }}</a-tag>
              </a-tooltip>
              <a-tag v-if="liveAccount?.isHedgeMode" size="small">双向持仓</a-tag>
            </template>
            <span v-if="follower.mode === 'live'" style="font-size: 13px; font-weight: 400">
              下单账号 <b>{{ follower.exchangeName ?? '未指定' }}</b>
            </span>
            <span style="font-size: 13px; font-weight: 400; color: var(--color-text-3)">
              订阅
              <RouterLink
                :to="{ name: 'pool-detail', params: { accountIndex: String(follower.leadAccountIndex) } }"
                class="back-link"
              >
                {{ follower.leadName }}
              </RouterLink>
            </span>
          </template>
        </a-space>
      </template>

      <template v-if="follower">
        <a-space wrap :size="16" align="center">
          <!-- 实盘的本金（规模基准）有硬上限：账号的可用保证金。
               把上限摆在输入框上，比按下去再看错误提示少一次往返 -->
          <a-input-number v-model="capitalInput" v-bind="capitalInputLimits" :style="{ width: '190px' }">
            <template #prefix>{{ follower.mode === 'live' ? '规模基准' : '本金' }}</template>
            <template #suffix>USDC</template>
          </a-input-number>

          <!-- 上限的来源写在旁边：数字不解释来源，使用者会以为是随便定的 -->
          <span
            v-if="capitalMax !== null"
            style="font-size: 12px; color: var(--color-text-3)"
          >
            上限 {{ formatUsd(capitalMax) }}（该账号的可用保证金）
          </span>

          <a-button
            type="primary"
            :disabled="!canEdit"
            :title="canEdit ? '' : '需要「跟单」的管理权限（编辑）'"
            :loading="saving"
            @click="requestConfigure(true)"
          >
            重设本金并继续
          </a-button>

          <a-button
            v-if="follower.isActive"
            :disabled="!canEdit"
            :title="canEdit ? '' : '需要「跟单」的管理权限（编辑）'"
            :loading="saving"
            @click="requestConfigure(false)"
          >
            暂停
          </a-button>
          <a-button
            v-else
            :disabled="!canEdit"
            :title="canEdit ? '' : '需要「跟单」的管理权限（编辑）'"
            :loading="saving"
            @click="requestConfigure(true)"
          >
            继续
          </a-button>

          <a-button :loading="loading" @click="load">刷新</a-button>

          <a-link
            status="danger"
            :disabled="!canDelete"
            :title="canDelete ? '' : '需要「跟单」的管理权限（删除）'"
            @click="deleteOpen = true"
          >
            删除跟单员
          </a-link>
        </a-space>

        <a-alert v-if="errorMessage" type="error" :title="errorMessage" style="margin-top: 12px" />

        <div v-if="!canEdit && !canDelete" class="readonly-line">
          当前账号对本页只有查看权限——不能改本金、启停、平仓或删除跟单员。
        </div>

        <a-alert
          v-if="!follower.leadIsEnabled"
          type="warning"
          :show-icon="true"
          style="margin-top: 12px"
          title="订阅的带单员已暂停"
        >
          带单员暂停后不再产生新信号，这个跟单员因此不会再有新动作。
          已有仓位保持不动——是否平仓是交易决策，需要你自己决定。
          到「带单员」页可以恢复它的观察。
        </a-alert>

        <div style="margin-top: 10px; font-size: 12px; color: var(--color-text-3); line-height: 1.7">
          本金是规模比例的分母：<b>我方目标仓位 = 池子仓位 × (本金 ÷ 池子权益)</b>。
          因此<b>改动本金会清空该跟单员的历史</b>——两种本金下的流水混在一条权益曲线里，
          不对应任何一个真实实验，却会被读成"跟单业绩"。
        </div>
      </template>
    </a-card>

    <!-- 跟单员不存在：给出事实与出口，而不是一个空页面 -->
    <a-card v-if="notFound && !loading" :bordered="false">
      <a-empty :description="`跟单员 #${followerId} 不存在`">
        <div style="margin-top: 8px; font-size: 13px; color: var(--color-text-3)">
          它可能已被删除。删除跟单员不影响带单员是否继续观察那个池。
        </div>
        <a-button type="primary" style="margin-top: 12px" @click="router.push({ name: 'copy-list' })">
          返回跟单列表
        </a-button>
      </a-empty>
    </a-card>

    <template v-if="follower">
      <!-- 账户总览 -->
      <a-card :bordered="false">
        <template #title>
          <a-space>
            <span>{{ follower.leadName }}</span>
            <a-tag size="small" :color="follower.isActive ? 'green' : 'gray'">
              {{ follower.isActive ? '跟随中' : '已暂停' }}
            </a-tag>
          </a-space>
        </template>
        <template #extra>
          <span style="font-size: 12px; color: var(--color-text-3)">
            比例 {{ formatPercent(follower.scale * 100, 4) }} · 池子权益
            {{ formatUsd(follower.poolEquity) }} · 成立于
            {{ formatDateTime(follower.createdAt) }}
          </span>
        </template>

        <!-- 概览：两种模式同一张网格、同一组槽位——纸面的数来自模拟账（重放流水），
     实盘的数来自交易所（用户数据流推送 + 定时刷新）。槽位一样，
     扫一眼就知道每个数在哪；标签按模式各叫各的（权益 vs 余额）。 -->
        <a-alert
          v-if="follower.mode === 'live' && liveAccountError"
          type="warning"
          :show-icon="true"
          :title="`读不到交易所账户：${liveAccountError}`"
        />

        <a-grid :cols="6" :col-gap="12" :row-gap="14">
          <a-grid-item v-if="follower.mode === 'live'">
            <MetricTile
              label="钱包余额"
              :value="formatUsd(liveAccount?.walletBalance ?? 0, 4)"
              :note="`规模基准 ${formatUsd(follower.initialCapital)}`"
            />
          </a-grid-item>
          <a-grid-item v-else>
            <MetricTile
              label="账户权益"
              :value="formatUsd(follower.equity, 4)"
              :note="`本金 ${formatUsd(follower.initialCapital)}`"
              :tone="follower.equity >= follower.initialCapital ? 'success' : 'danger'"
            />
          </a-grid-item>

          <a-grid-item v-if="follower.mode === 'live'">
            <MetricTile
              label="可用保证金"
              :value="formatUsd(liveAccount?.availableBalance ?? 0, 4)"
              note="此刻能开仓的钱"
            />
          </a-grid-item>
          <a-grid-item v-else>
            <MetricTile
              label="收益率"
              :value="formatPercent(follower.returnPercent, 4)"
              :note="`${formatUsd(follower.equity - follower.initialCapital, 4)}`"
              :tone="follower.returnPercent >= 0 ? 'success' : 'danger'"
            />
          </a-grid-item>

          <a-grid-item>
            <MetricTile
              label="未实现盈亏"
              :value="
                follower.mode === 'live' && accountReturn !== null
                  ? `${formatUsd(accountPnl, 4)}（${formatPercent(accountReturn, 2)}）`
                  : formatUsd(accountPnl, 4)
              "
              :note="
                follower.mode === 'live'
                  ? '按每秒标记价实时计算'
                  : hasUnpricedPosition
                    ? '含取不到价的标的（不可得）'
                    : '按盘口中间价盯市'
              "
              :tone="
                follower.mode === 'live'
                  ? accountPnl >= 0
                    ? 'success'
                    : 'danger'
                  : hasUnpricedPosition
                    ? 'warning'
                    : 'default'
              "
            />
          </a-grid-item>

          <a-grid-item>
            <MetricTile label="持仓名义价值" :value="formatUsd(accountNotional)" note="总敞口" />
          </a-grid-item>

          <a-grid-item v-if="follower.mode === 'live'">
            <MetricTile
              label="下单 / 未跟上"
              :value="`${liveOrderCounts.placed} / ${liveOrderCounts.notFollowed}`"
              :note="
                liveOrderCounts.notFollowed > 0
                  ? '未跟上 = 没发或被拒（见流水）'
                  : '全部跟上'
              "
              :tone="liveOrderCounts.notFollowed > 0 ? 'warning' : 'success'"
            />
          </a-grid-item>
          <a-grid-item v-else>
            <MetricTile
              label="已实现盈亏"
              :value="formatUsd(follower.realizedPnl, 4)"
              note="平仓部分结转"
            />
          </a-grid-item>

          <a-grid-item v-if="follower.mode === 'live'">
            <MetricTile
              label="规模基准"
              :value="formatUsd(follower.initialCapital)"
              note="比例的分母"
            />
          </a-grid-item>
          <a-grid-item v-else>
            <MetricTile
              label="成交 / 跳过"
              :value="`${follower.fillCount} / ${follower.skipCount}`"
              :note="follower.skipCount > 0 ? '跳过 = 带单员动了但我没跟上' : '全部跟上'"
              :tone="follower.skipCount > 0 ? 'warning' : 'success'"
            />
          </a-grid-item>
        </a-grid>

        <a-alert
          v-if="follower.fees > 0 && follower.mode !== 'live'"
          type="info"
          :show-icon="true"
          style="margin-top: 12px"
          :title="`累计手续费 ${formatUsd(follower.fees, 4)}`"
        />
      </a-card>

      <!-- 持仓：两种模式同一张表。共享列在前（数量/开仓均价/标记价/名义价值/未实现盈亏），
           模式专属列追加在尾部：纸面只剩已实现盈亏，实盘是账户事实（杠杆/强平价）与操作（平仓）。 -->
      <a-card :bordered="false">
        <template #title>
          <a-space :size="12">
            <span>持仓</span>
            <a-tag size="small" style="margin-left: 6px">{{ unifiedPositions.length }}</a-tag>
            <a-tag v-if="follower.mode === 'live' && liveAccount?.isHedgeMode" size="small">
              双向持仓
            </a-tag>
          </a-space>
        </template>
        <!-- 表头右侧的话只在实盘说：纸面这边原先那句是解释"跟踪偏差"列的，列已删 -->
        <template #extra>
          <a-tooltip
            v-if="follower.mode === 'live'"
            content="仓位由交易所推来（下单、成交、强平时更新）；未实现盈亏按每秒标记价实时计算——公式已与交易所报的数对齐到小数第 8 位"
          >
            <span style="font-size: 12px; color: var(--color-text-3)">
              数字由交易所推来，盈亏按每秒标记价实时计算
            </span>
          </a-tooltip>
        </template>

        <a-table
          :data="unifiedPositions"
          row-key="key"
          size="small"
          :bordered="false"
          :pagination="false"
        >
          <template #columns>
            <a-table-column title="标的" :width="130">
              <template #cell="{ record }">
                <a-tooltip
                  v-if="record.live && !record.live.pair"
                  :content="`${record.live.venueSymbol} 不在合约目录里，认不出它是哪个标的`"
                >
                  <span class="mono">{{ record.symbol }}</span>
                </a-tooltip>
                <span v-else class="mono">{{ record.symbol }}</span>
              </template>
            </a-table-column>
            <a-table-column title="方向" :width="70" align="center">
              <template #cell="{ record }">
                <a-tag v-if="record.side" size="small" :color="record.side === 'long' ? 'green' : 'red'">
                  {{ record.side === 'long' ? '多' : '空' }}
                </a-tag>
                <span v-else style="color: var(--color-text-3)">空仓</span>
              </template>
            </a-table-column>
            <!-- 数量列写 width，不写 minWidth：两种写法在这张表上不是一个效果——
                 表格比各列之和宽出五百多 px，没有固定宽度的列会把这部分**全部**吞掉
                 （实测 minWidth 让数量列涨到 292px，而写死宽度时浏览器按比例均摊，
                 列与列的比例被保住）。数据变长不会把数字挤折行：列宽不够时表格整体横向滚动 -->
            <a-table-column title="数量" :width="110" align="right">
              <template #cell="{ record }">{{ formatNumber(record.size, 6) }}</template>
            </a-table-column>
            <a-table-column title="开仓均价" :width="140" align="right">
              <template #cell="{ record }">{{ formatNumber(record.entry, 6) }}</template>
            </a-table-column>
            <a-table-column title="标记价" :width="140" align="right">
              <template #cell="{ record }">
                <span v-if="record.mark !== null">{{ formatNumber(record.mark, 6) }}</span>
                <a-tooltip v-else content="取不到标记价，该标的的未实现盈亏不可得">
                  <span style="color: rgb(var(--warning-6))">取价失败</span>
                </a-tooltip>
              </template>
            </a-table-column>
            <a-table-column title="名义价值" :width="110" align="right">
              <template #cell="{ record }">${{ formatNumber(record.notional, 2) }}</template>
            </a-table-column>
            <a-table-column title="未实现盈亏" :width="160" align="right">
              <template #cell="{ record }">
                <span
                  v-if="record.mark === null && record.size !== 0"
                  style="color: var(--color-text-3)"
                >
                  不可得
                </span>
                <span
                  v-else
                  :style="{
                    color: record.unrealized >= 0 ? 'rgb(var(--success-6))' : 'rgb(var(--danger-6))',
                  }"
                >
                  ${{ formatNumber(record.unrealized, 4) }}
                  <template v-if="record.liveReturn !== null">
                    （{{ formatPercent(record.liveReturn, 2) }}）
                  </template>
                </span>
              </template>
            </a-table-column>

            <!-- 纸面专属：只剩已实现盈亏（先前的目标数量/池子仓位/跟踪偏差三列已删：
                 它们是同一件事的三种换算——数量列是本方仓位、池子的仓位在池详情页，
                 偏差则要读者自己做一次减法才有意义） -->
            <a-table-column v-if="follower.mode !== 'live'" title="已实现盈亏" :width="120" align="right">
              <template #cell="{ record }">
                <span v-if="record.paper">${{ formatNumber(record.paper.realizedPnl, 4) }}</span>
              </template>
            </a-table-column>

            <!-- 实盘专属：账户事实与操作 -->
            <a-table-column v-if="follower.mode === 'live'" title="杠杆" :width="70" align="right">
              <template #cell="{ record }">{{ record.live ? `${record.live.leverage}×` : '—' }}</template>
            </a-table-column>
            <a-table-column v-if="follower.mode === 'live'" title="强平价" :width="120" align="right">
              <template #cell="{ record }">
                {{
                  record.live && record.live.liquidationPrice !== 0
                    ? formatNumber(record.live.liquidationPrice, 6)
                    : '—'
                }}
              </template>
            </a-table-column>
            <a-table-column v-if="follower.mode === 'live'" title="操作" :width="90" align="center">
              <template #cell="{ record }">
                <!-- 认不出标的的仓位不给平：那说明这个合约不在目录里，
                     我们连它是哪个标的都不知道，不该对着它下单 -->
                <a-button
                  v-if="record.live && record.live.pair"
                  size="mini"
                  status="danger"
                  :disabled="!canEdit"
                  :title="canEdit ? '' : '需要「跟单」的管理权限（编辑）'"
                  @click="record.live && openClose(record.live)"
                >
                  平仓
                </a-button>
                <a-tooltip v-else content="这个合约不在合约目录里，认不出是哪个标的，不能对着它下单">
                  <span style="color: var(--color-text-3); font-size: 12px">—</span>
                </a-tooltip>
              </template>
            </a-table-column>
          </template>

          <template #empty>
            <a-empty
              :description="
                follower.mode === 'live' ? '交易所账号当前没有持仓' : '我方与池子当前都是空仓'
              "
            />
          </template>
        </a-table>
      </a-card>

      <!-- 流水：两种模式同一张表。共享列（时间/标的/动作/数量/开仓价/平仓价/名义价值/结果），
           纸面在尾部追加跟踪两列（实现盈亏/深度）——实盘没有专属列；
           「说明」是注解列，两种模式下都排在最右。 -->
      <a-card :bordered="false">
        <template #title>
          <a-space :size="12">
            <span>流水</span>
            <a-tag size="small" style="margin-left: 6px">{{ unifiedRows.length }}</a-tag>
          </a-space>
        </template>
        <template #extra>
          <span v-if="follower.mode !== 'live'" style="font-size: 12px; color: var(--color-text-3)">
            每次带单员仓位变动，我方的处置与结果
          </span>
          <span v-else style="font-size: 12px; color: var(--color-text-3)">
            真实持仓与盈亏以交易所账号为准，这里记"发了什么、结果如何"
          </span>
        </template>

        <a-table
          :data="unifiedRows"
          row-key="key"
          size="small"
          :bordered="false"
          :pagination="{ pageSize: 20, size: 'small' }"
        >
          <template #columns>
            <a-table-column title="时间" :width="165">
              <template #cell="{ record }">
                {{ formatDateTime(record.live ? record.live.createdAt : record.paper.observedAt) }}
              </template>
            </a-table-column>

            <a-table-column title="标的" :width="185">
              <template #cell="{ record }">
                <!-- 目标未知（解析不出合约）：只剩源名字，如实显示 -->
                <span
                  v-if="record.live && record.live.targetBase === '—'"
                  class="mono"
                  style="color: var(--color-text-3)"
                >
                  {{ record.live.sourceSymbol }}<span style="font-size: 11px">（目标未知）</span>
                </span>
                <span v-else-if="record.live" class="mono">
                  <template
                    v-if="record.live.sourceSymbol !== record.live.targetBase && record.live.sourceSymbol !== '—'"
                  >
                    {{ record.live.targetPair
                    }}<span style="color: var(--color-text-3)">（源 {{ record.live.sourceSymbol }}）</span>
                  </template>
                  <template v-else>{{ record.live.targetPair }}</template>
                </span>
                <span v-else class="mono">{{ record.paper.symbol }}</span>
              </template>
            </a-table-column>

            <a-table-column title="动作" :width="150">
              <template #cell="{ record }">
                <!-- 纸面：买入/卖出；实盘：四种意图之一，手动/对齐要标出来。
                     两列必须各说各的：跳过的行"没有动作"，写「未跟上」是把右边「结果」「说明」
                     两列又说一遍——动作列只回答"做了什么" -->
                <template v-if="record.paper">
                  <a-tag
                    v-if="record.paper.isFilled"
                    size="small"
                    :color="record.paper.side === 'buy' ? 'green' : 'red'"
                  >
                    {{ record.paper.side === 'buy' ? '买入' : '卖出' }}
                  </a-tag>
                  <span v-else style="color: var(--color-text-3)">—</span>
                </template>
                <template v-else-if="record.live">
                  <span v-if="record.live.intent === 'none'" style="color: var(--color-text-3)">—</span>
                  <span v-else>
                    {{ liveIntentLabel(record.live.intent) }}
                    <span style="color: var(--color-text-3)">{{ record.live.side }}</span>
                    <a-tag v-if="record.live.trigger === 'manual'" size="small" color="orange">手动</a-tag>
                    <a-tag v-else-if="record.live.trigger === 'align'" size="small" color="arcoblue">对齐</a-tag>
                  </span>
                </template>
              </template>
            </a-table-column>

            <!-- 数量列与持仓表同宽同写法：写 width 不写 minWidth。这张表里没有固定宽度的
                 列会吞掉全部剩余宽度（实盘删掉两列后实测被撑到 276px）；写死宽度时
                 浏览器按比例均摊，只剩「说明」一列自适应、在右端吸收零头 -->
            <a-table-column title="数量" :width="110" align="right">
              <template #cell="{ record }">
                {{
                  record.live
                    ? record.live.quantity === 0
                      ? '—'
                      : formatNumber(record.live.quantity, 6)
                    : record.paper && record.paper.isFilled
                      ? formatNumber(record.paper.size, 6)
                      : '—'
                }}
              </template>
            </a-table-column>

            <!-- 开仓价/平仓价：按行的身份各就各位。
                 纸面的平仓行用该标的当前的成本线近似（流水里没记当时的 vwap） -->
            <a-table-column :width="110" align="right">
              <template #title>
                <HeaderHint
                  label="开仓价"
                  hint="这笔仓位是多少钱开的：开仓行=本单成交均价；平仓行=被平仓位的开仓均价。旧数据用下单时的参考价近似，误差不到一个基点"
                />
              </template>
              <template #cell="{ record }">
                <template v-if="record.live">
                  {{ record.live.entryPrice > 0 ? formatNumber(record.live.entryPrice, 4) : '—' }}
                </template>
                <template v-else-if="record.paper && record.paper.isFilled">
                  {{
                    fillOpens(record.paper)
                      ? formatNumber(record.paper.price, 4)
                      : paperEntryOf(record.paper.symbol) !== null
                        ? formatNumber(paperEntryOf(record.paper.symbol) ?? 0, 4)
                        : '—'
                  }}
                </template>
                <template v-else>—</template>
              </template>
            </a-table-column>

            <a-table-column :width="110" align="right">
              <template #title>
                <HeaderHint
                  label="平仓价"
                  hint="这笔仓位是多少钱平的（本单的成交均价）。只有平仓行有——开仓行与未发出的行是空的"
                />
              </template>
              <template #cell="{ record }">
                <template v-if="record.live">
                  {{ record.live.closePrice !== null ? formatNumber(record.live.closePrice, 4) : '—' }}
                </template>
                <template v-else-if="record.paper && record.paper.isFilled && !fillOpens(record.paper)">
                  {{ formatNumber(record.paper.price, 4) }}
                </template>
                <template v-else>—</template>
              </template>
            </a-table-column>

            <a-table-column title="名义价值" :width="110" align="right">
              <template #cell="{ record }">
                <template v-if="record.live">
                  {{ record.live.notionalUsd === 0 ? '—' : formatUsd(record.live.notionalUsd) }}
                </template>
                <template v-else-if="record.paper && record.paper.isFilled">
                  {{ formatUsd(record.paper.notional) }}
                </template>
                <template v-else>—</template>
              </template>
            </a-table-column>

            <a-table-column title="结果" :width="90" align="center">
              <template #cell="{ record }">
                <template v-if="record.live">
                  <a-tag size="small" :color="liveStatusColor(record.live.status)">
                    {{ liveStatusLabel(record.live.status) }}
                  </a-tag>
                </template>
                <template v-else-if="record.paper">
                  <a-tag size="small" :color="record.paper.isFilled ? 'green' : 'orange'">
                    {{ record.paper.isFilled ? '已成交' : '未跟上' }}
                  </a-tag>
                </template>
              </template>
            </a-table-column>

            <!-- 实盘没有专属列：池仓名义价值/目标名义价值已删——读流水的人要的是"这一笔
                 实际下了多少"（就在「名义价值」列），另外两个是算这笔时的中间量；
                 真要追溯，它们仍记在库里的订单行上（LiveOrder.PoolNotional/TargetNotionalUsd） -->

            <!-- 纸面专属：只剩实现盈亏。
                 仓位的前后变化不单列：本单数量在「数量」、方向在「动作」，
                 池子的仓位在池详情页——再画两列只是把同一件事说第二遍 -->
            <a-table-column v-if="follower.mode !== 'live'" title="实现盈亏" :width="110" align="right">
              <template #cell="{ record }">
                <span
                  v-if="record.paper && record.paper.realizedPnl !== 0"
                  :style="{
                    color:
                      record.paper.realizedPnl >= 0 ? 'rgb(var(--success-6))' : 'rgb(var(--danger-6))',
                  }"
                >
                  ${{ formatNumber(record.paper.realizedPnl, 4) }}
                </span>
                <span v-else style="color: var(--color-text-3)">—</span>
              </template>
            </a-table-column>
            <a-table-column v-if="follower.mode !== 'live'" title="深度" :width="85" align="center">
              <template #cell="{ record }">
                <span
                  v-if="record.paper && !record.paper.isFilled"
                  style="color: var(--color-text-3)"
                >
                  —
                </span>
                <a-tag
                  v-else-if="record.paper"
                  size="small"
                  :color="record.paper.depthSufficient ? 'green' : 'orange'"
                >
                  {{ record.paper.depthSufficient ? '够' : '缺' }}
                </a-tag>
              </template>
            </a-table-column>

            <!-- 说明排最右：它是对整行的一句注解，夹在数字列中间会把"这一行发生了什么"
                 割成两半。
                 宽度**不给列**：Arco 把表格的剩余宽度分给最后一列，给列写宽度挡不住它被撑开；
                 真正封顶的是单元格里的 .reason-text（超出省略号、悬停看全文） -->
            <a-table-column title="说明">
              <template #cell="{ record }">
                <a-tooltip v-if="fillNote(record)" :content="fillNote(record)">
                  <span class="reason-text">{{ fillNote(record) }}</span>
                </a-tooltip>
                <span v-else style="color: var(--color-text-3)">—</span>
              </template>
            </a-table-column>
          </template>

          <template #empty>
            <a-empty
              :description="
                follower.mode === 'live' ? '还没有实盘动作' : '还没有任何流水'
              "
            />
          </template>
        </a-table>
      </a-card>

<!-- 权益曲线 -->
      <!-- 权益曲线是**纸面账**的产物（由模拟成交重放出来）。实盘没有这条曲线：
           它的"现在值多少"以交易所余额为准，而历史权益我们不去编一份 -->
      <a-card v-if="follower.mode !== 'live'" :bordered="false">
        <template #title>权益曲线</template>
        <template #extra>
          <span style="font-size: 12px; color: var(--color-text-3)">
            虚线为初始本金；有成交或每 5 分钟记一个点
          </span>
        </template>

        <!-- 判据是<b>点数</b>而不是序列条数：这里只有一条序列，
             写成 equitySeries.length > 1 会恒为假，有数据也永远画不出曲线 -->
        <LineChart
          v-if="follower.curve.length > 1"
          :series="equitySeries"
          :baseline="follower.initialCapital"
          :height="260"
        />
        <a-empty
          v-else
          :description="
            follower.curve.length === 0
              ? '还没有权益点。账户刚建立时，等第一次对账完成后就会出现。'
              : `目前只有 ${follower.curve.length} 个权益点，画不出趋势。有成交或每 5 分钟会补一个点。`
          "
        />
      </a-card>

<!-- 平仓确认。三段：要平什么（数量/名义价值）、会发生什么（可能被重新建仓）、
           以及一个默认勾上的"同时暂停"——这是让"平仓"这个动作真的算数的那一步 -->
      <a-modal
        :visible="closeTarget !== null"
        title="平掉这笔实盘仓位"
        :footer="false"
        @cancel="closeTarget = null"
      >
        <a-space v-if="closeTarget" direction="vertical" fill :size="14">
          <div>
            <div style="font-size: 13px; margin-bottom: 6px">这一笔</div>
            <a-descriptions :column="2" size="small" bordered>
              <a-descriptions-item label="标的">
                <span class="mono">{{ closeTarget.pair }}</span>
              </a-descriptions-item>
              <a-descriptions-item label="方向">
                {{ closeTarget.direction === 'short' ? '空' : '多' }}
              </a-descriptions-item>
              <a-descriptions-item label="数量">
                {{ formatNumber(closeTarget.size, 6) }}
              </a-descriptions-item>
              <a-descriptions-item label="名义价值">
                ${{ formatNumber(closeTarget.notional, 2) }}
              </a-descriptions-item>
            </a-descriptions>
          </div>

          <a-alert type="warning" :show-icon="true">
            这是<b>真实下单</b>：按<b>市价</b>平掉，价格以成交那一刻为准。
            实际数量由后端现场读交易所持仓得出（界面上的数字可能已经过期）。
          </a-alert>

          <a-checkbox v-model="closePauseFollower">
            同时暂停这个跟单员
            <div style="font-size: 12px; color: var(--color-text-3); line-height: 1.7">
              不暂停的话，下一次对账会按池子的仓位把这笔<b>重新建起来</b>——
              点了平仓却看到仓位自己回来了。暂停后想继续跟，在页面上点「继续」即可。
            </div>
          </a-checkbox>

          <a-alert v-if="closeError" type="error" :title="closeError" />

          <div style="display: flex; justify-content: flex-end; gap: 8px">
            <a-button @click="closeTarget = null">取消</a-button>
            <a-button type="primary" status="danger" :loading="closing" @click="confirmClose">
              确认平仓
            </a-button>
          </div>
        </a-space>
      </a-modal>

      <!-- 改本金确认。**这是必需的**：改本金会清空该跟单员的全部历史，
           而按钮就挨着「刷新」——一次误点会静默销毁几个小时的账面记录。
           确认框列出具体会丢什么，而不是笼统地问"确定吗" -->
      <a-modal :visible="confirmOpen" title="改动本金会清空该跟单员的历史" :footer="false" @cancel="confirmOpen = false">
        <a-space direction="vertical" fill :size="14">
          <div style="font-size: 13px; line-height: 1.8">
            本金是规模比例的分母：我方目标仓位 = 池子仓位 × (本金 ÷ 池子权益)。
            因此<b>改动本金会清空该跟单员的历史</b>——两种本金下的流水混在一条权益曲线里，
            不对应任何一个真实实验，却会被读成"跟单业绩"。
          </div>

          <div v-if="historyToLose" style="font-size: 13px; line-height: 1.8">
            将清除：<b>{{ historyToLose.fills }}</b> 条流水、<b>{{ historyToLose.skips }}</b> 条跳过记录、
            <b>{{ historyToLose.points }}</b> 个权益点。
          </div>

          <div style="display: flex; justify-content: flex-end; gap: 8px">
            <a-button @click="confirmOpen = false">取消</a-button>
            <a-button type="primary" :loading="saving" @click="configure(pendingActive)">
              清空并重设
            </a-button>
          </div>
        </a-space>
      </a-modal>

      <!-- 删除确认。删除的是**跟单员**（含流水与曲线），不是带单员的观察 -->
      <a-modal :visible="deleteOpen" title="删除这个跟单员？" :footer="false" @cancel="deleteOpen = false">
        <a-space direction="vertical" fill :size="12">
          <div style="font-size: 13px; line-height: 1.8">
            将删除该跟单员及其全部流水与权益曲线，<b>无法恢复</b>。
          </div>
          <div style="font-size: 13px; color: var(--color-text-3); line-height: 1.8">
            它订阅的带单员<b>不受影响</b>：池子仍然被持续观察，信号照旧记录。
            想连观察一起停掉，请到「带单员」页操作。
          </div>
        </a-space>
        <div style="display: flex; justify-content: flex-end; gap: 8px; margin-top: 14px">
          <a-button @click="deleteOpen = false">取消</a-button>
          <a-button type="primary" status="danger" :loading="deleting" @click="remove">删除</a-button>
        </div>
      </a-modal>
    </template>

    <!-- 执行成本测量：与账户会计是两件事，放在同一页但分开呈现 -->
    <a-card v-if="costReport" :bordered="false">
      <template #title>
        <a-space>
          <span>执行成本测量</span>
          <a-tag size="small">独立于上面的账户会计</a-tag>
        </a-space>
      </template>
      <template #extra>
        <span style="font-size: 12px; color: var(--color-text-3)">
          逐腿对照，回答"每一腿比池子差几个基点"
        </span>
      </template>

      <a-alert type="info" :show-icon="true" style="margin-bottom: 12px">
        这一节与上面的账户盈亏<b>不是同一件事</b>：它不建仓位、不产生权益，
        只用当时的真实盘口量出跟单的执行摩擦（点差 + 滑点）。
        漏损为负（我方更优）最常见的原因是池子那笔是挂单成交——它赚点差、我方付点差，
        两者不可比，因此这个数值要对着成交方式一起读。
        <b>它是按池子统计的</b>，同一个池下的多个跟单员看到的是同一份样本。
      </a-alert>

      <a-grid :cols="5" :col-gap="12" :row-gap="14" style="margin-bottom: 14px">
        <a-grid-item>
          <MetricTile
            label="有效样本"
            :value="`${costReport.report.sampleCount} 笔`"
            note="60 秒窗口内"
          />
        </a-grid-item>
        <a-grid-item>
          <MetricTile
            label="单腿漏损中位数"
            :value="`${formatNumber(costReport.report.medianEntrySlippageBps, 2)} bps`"
            note="正值 = 我方更差"
          />
        </a-grid-item>
        <a-grid-item>
          <MetricTile
            label="往返漏损"
            :value="`${formatNumber(costReport.report.estimatedRoundTripBps, 2)} bps`"
            note="两条腿之和"
          />
        </a-grid-item>
        <a-grid-item>
          <MetricTile
            label="池子单轮平均收益"
            :value="formatPercent(costReport.report.averagePoolRoundTripReturnPercent, 3)"
            note="与之直接可比"
          />
        </a-grid-item>
        <a-grid-item>
          <MetricTile
            label="台账记录"
            :value="`${costReport.entries.length} 条`"
            note="含窗口外的历史样本"
          />
        </a-grid-item>
      </a-grid>

      <a-table
        :data="costReport.entries"
        row-key="id"
        size="small"
        :bordered="false"
        :pagination="{ pageSize: 10, size: 'small' }"
      >
        <template #columns>
          <a-table-column title="时间" :width="165">
            <template #cell="{ record }">{{ formatDateTime(record.observedAt) }}</template>
          </a-table-column>
          <a-table-column title="标的" data-index="symbol" :width="105" />
          <a-table-column title="方向" :width="70" align="center">
            <template #cell="{ record }">
              <a-tag size="small" :color="record.side === 'buy' ? 'green' : 'red'">
                {{ record.side === 'buy' ? '买' : '卖' }}
              </a-tag>
            </template>
          </a-table-column>
          <a-table-column title="池成交价" :width="120" align="right">
            <template #cell="{ record }">{{ formatNumber(record.poolPrice, 6) }}</template>
          </a-table-column>
          <a-table-column title="我方模拟价" :width="125" align="right">
            <template #cell="{ record }">{{ formatNumber(record.ourVwap, 6) }}</template>
          </a-table-column>
          <a-table-column title="漏损 (bps)" :width="115" align="right">
            <template #cell="{ record }">
              <span
                :style="{
                  color: !record.isWithinMeasurementWindow
                    ? 'var(--color-text-4)'
                    : record.slippageBps > 20
                      ? 'rgb(var(--danger-6))'
                      : record.slippageBps > 5
                        ? 'rgb(var(--warning-6))'
                        : 'rgb(var(--success-6))',
                }"
              >
                {{ formatNumber(record.slippageBps, 2) }}
              </span>
            </template>
          </a-table-column>
          <a-table-column title="计入统计" :width="105" align="center">
            <template #cell="{ record }">
              <a-tag size="small" :color="record.isWithinMeasurementWindow ? 'green' : 'gray'">
                {{ record.isWithinMeasurementWindow ? '已计入' : '不计数' }}
              </a-tag>
            </template>
          </a-table-column>
        </template>

        <template #empty>
          <a-empty description="还没有执行成本样本" />
        </template>
      </a-table>
    </a-card>
  </a-space>
</template>

<style scoped>
/* 只在该页确实有被禁用的按钮时才显示（见模板里的判断） */
.readonly-line {
  margin-top: 10px;
  font-size: 12px;
  color: rgb(var(--warning-6));
}

/* 原因压成一行：超出就用省略号，全文在悬停提示里。
   为什么不用整段换行：原因里带着价与步长，完整摊开会把这一列撑成三行，
   整张表被它带着一起变高，而它并不是每行都要读的字段 */
.reason-text {
  display: block;
  overflow: hidden;

  /* 宽度在这里封顶，而不是靠列的 :width —— Arco 的表会把剩余宽度分给最后一列，
     给列设 220 实测被撑到 732，结果一个字都没截断 */
  max-width: 220px;
  font-size: 12px;
  color: var(--color-text-3);
  text-overflow: ellipsis;
  white-space: nowrap;
  cursor: help;
}

.mono {
  font-family: var(--font-mono, ui-monospace, SFMono-Regular, Menlo, monospace);
}

/* 返回入口。做成链接而不是按钮：它是导航，应当能新标签页打开、也能被键盘聚焦 */
.back-link {
  color: rgb(var(--primary-6));
  text-decoration: none;
  font-size: 14px;
  font-weight: 400;
}

.back-link:hover {
  text-decoration: underline;
}
</style>
