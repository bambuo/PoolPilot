<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'

import { api, type ExchangeConfigRow, type FollowerSummary, type LeadTrader } from '@/api/client'
import DirectionMark from '@/components/DirectionMark.vue'
import SymbolName from '@/components/SymbolName.vue'
import { useFollowerLive, subscribeFollowerMarkets } from '@/composables/useFollowerLive'
import { usePermission } from '@/composables/usePermission'
import { COPY_MODES, DEFAULT_COPY_MODE, copyModeLabel, type CopyMode } from '@/domain/copyMode'
import { formatPercent, formatNumber, formatUsd } from '@/utils/format'

/// 跟单 —— 跟单员卡片列表。
///
/// 一屏列出所有跟单员，每张卡片只放**决定"这个跟单好不好"的几个数**：
/// 权益与收益率（赚没赚）、持仓（在赌什么）、成交/跳过（跟得紧不紧）。
/// 流水与权益曲线属于细节，点进卡片再看。
///
/// 与「带单管理」页的分工：那边是**被观察的池**，这里是**下场的账户**。
/// 一个池可以挂多个跟单员（各自一份本金、各记一套账），所以卡片的身份是跟单员编号，
/// 池名只是它订阅的对象。

/// 本页自身的权限键。服务端按 `menu:{code}` 生成，前端只读不写。
///
/// 列表页只有一个写口（新建跟单，归 edit）；改本金/启停/平仓/删除都在详情页，
/// 但那条路由与「跟单」是同一个权限键，所以 canDelete 也用在这里的只读提示上。
const PAGE_PERMISSION = 'menu:copy-list'

const { can, refresh: refreshPermissions } = usePermission()

const canEdit = computed(() => can(PAGE_PERMISSION, 'edit'))
const canDelete = computed(() => can(PAGE_PERMISSION, 'delete'))

/// 持仓区固定显示的行数。超过它就得滚动——高度随标的多少变，一排卡片就对不齐
const POSITION_ROWS_VISIBLE = 2

/// 这张卡此刻在显示几行持仓。
///
/// 实盘读**交易所的持仓**、纸面读**纸面账算出来的仓位**——两边口径不同（详情见下面模板里的说明），
/// 行数也各算各的。抽成一处是为了让"要不要滚动"与"渲染了哪一份列表"永远是同一个判断。
function positionRowCount(follower: FollowerSummary): number {
  return follower.mode === 'live' ? follower.livePositions.length : follower.positions.length
}

const followers = ref<FollowerSummary[]>([])
const leads = ref<LeadTrader[]>([])
const loading = ref(false)
const errorMessage = ref('')

/// 实时通道：账本变化 → 重取列表；行情 tick → 本地重算卡片估值，不打 REST。
const { state: liveState } = useFollowerLive({
  onChanged: scheduleReload,
  onMarketPrice: applyMarketPrice,
})

let reloadTimer: number | null = null

const liveNote = computed(() => {
  if (liveState.value === 'reconnecting') return '实时连接已断开，正在重连'
  if (liveState.value === 'connected') return '实时更新'
  if (liveState.value === 'connecting') return '正在连接实时更新'

  return '实时通道未连接'
})

function scheduleReload(): void {
  if (reloadTimer !== null) {
    window.clearTimeout(reloadTimer)
  }

  reloadTimer = window.setTimeout(() => {
    reloadTimer = null
    void load(true)
  }, 250)
}

/// 行情 tick 的本地补丁：口径与后端盯市一致（未实现 = 数量 ×（现价 − 开仓价））。
function applyMarketPrice(marketId: number, price: number): void {
  followers.value = followers.value.map((follower) => {
    if (follower.mode === 'live' || !follower.isValuationAvailable) {
      return follower
    }

    let changed = false
    const positions = follower.positions.map((position) => {
      if (position.marketId !== marketId) return position

      changed = true
      const notional = Math.abs(position.size) * price
      return {
        ...position,
        notional,
        unrealizedPnl: position.size * (price - position.entryVwap),
      }
    })

    if (!changed) return follower

    const unrealizedPnl = positions.reduce((sum, p) => sum + p.unrealizedPnl, 0)
    const grossNotional = positions.reduce((sum, p) => sum + p.notional, 0)
    const equity = follower.initialCapital + follower.realizedPnl + unrealizedPnl - follower.fees

    return {
      ...follower,
      positions,
      unrealizedPnl,
      grossNotional,
      equity,
      returnPercent:
        follower.initialCapital > 0
          ? ((equity - follower.initialCapital) / follower.initialCapital) * 100
          : 0,
    }
  })
}

onUnmounted(() => {
  if (reloadTimer !== null) {
    window.clearTimeout(reloadTimer)
    reloadTimer = null
  }
})

/// 带单员列表取失败的原因。**不能静默吞掉**：吞掉的后果是"开始跟单"弹窗里
/// 空空一个"暂无数据"，看起来像"没有任何带单员"，而事实是这次请求失败了。
const leadListError = ref('')

/// 新建跟单弹窗
const createOpen = ref(false)
const newLeadIndex = ref('')
const newCapital = ref(5000)
const creating = ref(false)

/// 跟单模式。默认纸面（先量出跟单能不能赚，再谈真下单）。
/// 这只是**新建时的选择**；已有账户的模式一律读后端返回的字段。
const newMode = ref<CopyMode>(DEFAULT_COPY_MODE)

/// 实盘要落到某一个账号上，所以选了实盘就必须指定交易所配置。
/// 0 = 还没选（Arco 的 select 不接受 null 作 v-model）。
/// 配置 Id 由数据库自增，从 1 起，所以 0 不会与真实配置撞上
const newExchangeId = ref(0)

/// 目前能选的交易所账号。**只列能下单的**（后端会拒掉没有执行侧实现的平台），
/// 但界面不自己筛：筛错了会让人以为"我配的账号不见了"
const exchangeOptions = ref<ExchangeConfigRow[]>([])

/// 选中账号的可用保证金：实盘本金的上限。
/// 选账号时现场问一次交易所（就是"连接测试"那条接口）——上限得是真的，
/// 不能拿本地记录的数字估。读不到时为 null，那时不设上限，交给后端把关。
const liveAvailable = ref<number | null>(null)
const availableLoading = ref(false)

watch([newMode, newExchangeId], async ([mode, id]: [CopyMode, number]) => {
  liveAvailable.value = null

  if (mode !== 'live' || !id) return

  availableLoading.value = true

  try {
    const probe = await api.probeExchange(id)

    // 同样向下取到分：上限要是个能读的数（见详情页同名处理的说明）
    liveAvailable.value = probe.ok ? Math.floor(probe.availableBalance * 100) / 100 : null
  } catch {
    // 读不到不是错误：那时只是不给上限，后端在提交时仍会把关
    liveAvailable.value = null
  } finally {
    availableLoading.value = false
  }
})

const selectedMode = computed(() => COPY_MODES.find((m) => m.value === newMode.value))

/// 本金输入框的上下限。没有上限时不传 `max`（本项目的 exactOptionalPropertyTypes
/// 把"显式 undefined"与"键不存在"当两回事）
const newCapitalLimits = computed(() =>
  liveAvailable.value === null
    ? { min: 1, step: 500 }
    : { min: 1, step: 500, max: liveAvailable.value },
)

/// 该带单员现有的跟单员数量。一个带单员可以挂多个跟单员（各自一份本金），
/// 所以这里**不排除已有跟单员的带单员**——排除等于宣称"一个带单员只能有一个跟单员"，
/// 那已经不是现在的模型了。
const followerCountOf = (leadIndex: number | string): number => {
  const key = String(leadIndex)
  return followers.value.filter((f) => String(f.leadAccountIndex) === key).length
}

function openCreate(): void {
  newLeadIndex.value = leads.value[0] ? String(leads.value[0].accountIndex) : ''
  newCapital.value = 5000
  newMode.value = DEFAULT_COPY_MODE
  newExchangeId.value = exchangeOptions.value[0]?.id ?? 0
  createOpen.value = true
}

/// 交易所账号清单：进页面就取。失败只影响实盘选项的显示（不给实盘选账号），
/// 不该让跟单列表本身打不开——纸面跟单与交易所配置毫无关系
async function loadExchanges(): Promise<void> {
  try {
    exchangeOptions.value = await api.exchanges()
  } catch {
    exchangeOptions.value = []
  }
}

async function load(silent = false): Promise<void> {
  if (!silent) {
    loading.value = true
    errorMessage.value = ''
  }

  try {
    // 跟单员先到就先渲染卡片。池名来自后端（带单员表里存了名字快照），
    // 所以这里不再需要为了显示名字去拉一次 90KB 的池列表。
    followers.value = await api.followers()

    // 行情按市场分组推送：把卡片上正在展示的市场都登记进来。
    subscribeFollowerMarkets(
      followers.value.flatMap((follower) => follower.positions.map((position) => position.marketId)),
    )
  } catch (error) {
    // 静默重取失败时保留上一次的数据：实时事件是一次补刷新，不该把已经能看的页面清空
    if (!silent) {
      errorMessage.value = error instanceof Error ? error.message : String(error)
    }
  } finally {
    loading.value = false
  }

  void loadLeads()
}

/// 带单员名单：跟单员订阅的对象。
///
/// 跟单不再从"池"出发：要跟的池必须先有带单员（在「带单管理」页创建）。
/// 这样订阅关系是明确挑出来的，而不是"顺手替你建一个观察者"造出来的——
/// 后者会让界面上冒出一个你没创建过的带单员，也说不清你到底在跟谁。
async function loadLeads(): Promise<void> {
  leadListError.value = ''

  try {
    leads.value = await api.leadTraders()
  } catch (error) {
    leadListError.value = error instanceof Error ? error.message : String(error)
  }
}

async function create(): Promise<void> {
  if (!newLeadIndex.value) return

  // 空本金不能提交：放行的话请求会带着空值打到后端，回一句框架级的参数错误——
  // 使用者看到的是一句他无从下手的话，而这里本来就能直接说清
  if (!(newCapital.value > 0)) {
    errorMessage.value = '请填入大于 0 的本金：它是规模比例的分母，为 0 算不出任何仓位。'
    return
  }

  // 实盘必须指定账号：钱要落到哪本账上都还不知道，后端也会拒（这里先说清，省一次往返）
  if (newMode.value === 'live' && newExchangeId.value === 0) {
    errorMessage.value = '实盘跟单要指定交易所账号：下单必须落到某一个账号上。到「交易所」页配好钥匙再来。'
    return
  }

  // 本金不得超过可用保证金：后端是最终关卡，这里先说一遍省一次往返
  if (newMode.value === 'live' && liveAvailable.value !== null && newCapital.value > liveAvailable.value) {
    errorMessage.value =
      `本金不能超过该账号的可用保证金 ${formatUsd(liveAvailable.value)}——` +
      '实盘按这个本金算出来的仓位，账户里的钱开不出来。'
    return
  }

  creating.value = true
  errorMessage.value = ''

  try {
    await api.createFollower(
      newLeadIndex.value,
      newCapital.value,
      newMode.value,
      newMode.value === 'live' ? newExchangeId.value : undefined,
    )
    createOpen.value = false
    await load()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    creating.value = false
  }
}

onMounted(async () => {
  // 权限缓存按会话共享：进来先拉一次，按钮状态才与当前会话一致
  try {
    await refreshPermissions()
  } catch (error) {
    console.warn('读取权限失败，本页按钮将按无权限处理', error)
  }

  void load()
  void loadExchanges()
})
</script>

<template>
  <a-space direction="vertical" fill style="width: 100%">
    <a-card :bordered="false">
      <template #title>
        <a-space>
          <span>跟单管理</span>
          <a-tag v-if="!loading" size="small">{{ followers.length }} 个</a-tag>
        </a-space>
      </template>
      <!-- 右上角只留「刷新」：开始跟单的入口**只有**列表里那张虚线卡片一处
           （与「交易所」页同一口径）。两处入口含义完全一样，多一个只会让人犹豫"点哪个" -->
      <template #extra>
        <a-space :size="8">
          <span class="live-note">{{ liveNote }}</span>
          <a-button size="small" :loading="loading" @click="load()">刷新</a-button>
        </a-space>
      </template>

      <a-alert v-if="errorMessage" type="error" :title="errorMessage" />

      <div style="font-size: 12px; color: var(--color-text-3); line-height: 1.7">
        每个跟单员订阅一个<b>带单员</b>，各自一个独立账户：本金、仓位、盈亏、权益曲线各自记账，
        规模口径为<b>比例复制</b>（目标仓位 = 池子仓位 × 本金 ÷ 池子权益）。
        同一个带单员可以挂多个跟单员，用不同本金各跟一份。点任意卡片查看该跟单的详情。
      </div>

      <div v-if="!canEdit && !canDelete" class="readonly-line">
        当前账号对本页只有查看权限——不能新建跟单。
      </div>
    </a-card>

    <a-spin :loading="loading" style="width: 100%">
      <div v-if="!loading && followers.length === 0" class="empty-line">还没有任何跟单</div>

      <div v-if="!loading" class="card-grid">
        <RouterLink
          v-for="follower in followers"
          :key="follower.id"
          class="account-card"
          :to="{ name: 'copy-detail', params: { id: String(follower.id) } }"
        >
          <div class="card-head">
            <span class="card-title">{{ follower.leadName }}</span>
            <a-space :size="4">
              <!-- 模式读账户自己的字段：纸面还是实盘，是使用者最需要一眼确认的信息 -->
              <a-tag size="small" :color="follower.mode === 'live' ? 'orange' : 'arcoblue'">
                {{ copyModeLabel(follower.mode) }}
              </a-tag>
              <a-tag size="small" :color="follower.isActive ? 'green' : 'gray'">
                {{ follower.isActive ? '跟随中' : '已暂停' }}
              </a-tag>
            </a-space>
          </div>

          <template v-if="follower.isValuationAvailable">
            <!-- 实盘的头条是**交易所账户的余额**，不报收益率：
                 那个账号里可能还放着与这次跟单无关的资金，
                 拿"余额 ÷ 本金"当收益率等于宣称整个账户都是这次跟单赚的 -->
            <div v-if="follower.mode === 'live'" class="card-equity">
              <span class="equity-value">${{ formatNumber(follower.equity, 2) }}</span>
              <span class="equity-abs">
                账户余额
                <template v-if="follower.availableBalance !== null">
                  · 可用 ${{ formatNumber(follower.availableBalance, 2) }}
                </template>
              </span>
            </div>
            <div v-else class="card-equity">
              <span
                class="equity-value"
                :style="{
                  color:
                    follower.returnPercent >= 0 ? 'rgb(var(--success-6))' : 'rgb(var(--danger-6))',
                }"
              >
                {{ formatPercent(follower.returnPercent, 3) }}
              </span>
              <!-- 净值是**金额**，不是说明文字：给它数值档（深一档），别与旁边的单位、标签同色 -->
              <span class="equity-abs is-value">${{ formatNumber(follower.equity, 2) }}</span>
            </div>

            <div class="card-meta">
              跟单员 #{{ follower.id }} ·
              <!-- 实盘的本金是**规模基准**（跟多大仓），与账户里有多少钱是两回事 -->
              {{ follower.mode === 'live' ? '规模基准' : '本金' }}
              <span class="meta-value">{{ formatUsd(follower.initialCapital) }}</span>
              <!-- 实盘的比例要池子权益才算得出，列表不取池子快照，所以不在这里显示一个
                   分母不同的"比例"——详情页上那个是准的 -->
              <template v-if="follower.mode !== 'live'">
                · 比例 <span class="meta-value">{{ formatPercent(follower.scale * 100, 3) }}</span>
              </template>
              <!-- 实盘必须显示落在哪个账号上：同一个平台可以有两个账号，
                   不写出来的话两个实盘跟单员在界面上长得一模一样 -->
              <template v-if="follower.mode === 'live'">
                · 下单账号 <b>{{ follower.exchangeName ?? '未指定' }}</b>
              </template>
            </div>

            <!-- 持仓：纸面读纸面账算出来的仓位，实盘读**交易所的持仓**——
                 两边口径不同，不能共用一处渲染：曾经实盘这段是空的，卡片上就写着
                 「当前空仓」，而旁边正是「持仓名义价值 $17.75」，一句明显的假话 -->
            <!-- 持仓区**固定两行的高度**：高度随标的多少变的话，一排卡片会参差不齐，
                 卡片脚（成交/跳过/名义价值）对不齐同一条水平线。超过两行由轨道自动上下滚动 -->
            <div
              class="card-positions"
              :class="{ 'is-scrolling': positionRowCount(follower) > POSITION_ROWS_VISIBLE }"
            >
              <div class="positions-track">
                <template v-if="follower.mode === 'live'">
                  <template v-if="follower.livePositions.length > 0">
                    <div
                      v-for="position in follower.livePositions"
                      :key="position.venueSymbol"
                      class="position-row"
                    >
                      <span class="position-main">
                        <DirectionMark
                          :side="position.positionSide === 'SHORT' ? 'short' : 'long'"
                          :size="12"
                        />
                        <SymbolName
                          :symbol="(position.pair ?? position.venueSymbol).split('/')[0] ?? position.venueSymbol"
                          compact
                        />
                        <span class="position-size">{{ formatNumber(position.size, 4) }}</span>
                      </span>
                      <span
                        :style="{
                          color:
                            position.unrealizedPnl >= 0
                              ? 'rgb(var(--success-6))'
                              : 'rgb(var(--danger-6))',
                        }"
                      >
                        ${{ formatNumber(position.unrealizedPnl, 2) }}
                      </span>
                    </div>
                  </template>
                  <span v-else class="card-idle">交易所账号当前空仓</span>
                </template>

                <template v-else>
                  <template v-if="follower.positions.length > 0">
                    <div v-for="position in follower.positions" :key="position.symbol" class="position-row">
                      <span class="position-main">
                        <DirectionMark :side="position.side" :size="12" />
                        <SymbolName :symbol="position.symbol" compact />
                        <span class="position-size">{{ formatNumber(position.size, 1) }}</span>
                      </span>
                      <span
                        :style="{
                          color:
                            position.unrealizedPnl >= 0
                              ? 'rgb(var(--success-6))'
                              : 'rgb(var(--danger-6))',
                        }"
                      >
                        ${{ formatNumber(position.unrealizedPnl, 2) }}
                      </span>
                    </div>
                  </template>
                  <span v-else class="card-idle">当前空仓</span>
                </template>
              </div>
            </div>

            <!-- 最后一行两种模式共用一套骨架：
                 纸面的成交/跳过来自纸面台账，实盘的来自实盘流水（发出去了多少 / 没跟上多少）。
                 同一件事占同一个位置，扫一眼列表不用每张卡重新找数 -->
            <div class="card-foot">
              <!-- 骨架两种模式共用（三个格子、同样的位置），标签按概念各叫各的：
                   纸面是"成交/跳过"（模拟账的两档），实盘是"下单/未跟上"
                   （发出去了多少、多少笔没跟上——含被交易所拒的） -->
              <span>
                {{ follower.mode === 'live' ? '下单' : '成交' }} {{ follower.fillCount }}
              </span>
              <span :style="{ color: follower.skipCount > 0 ? 'rgb(var(--warning-6))' : undefined }">
                {{ follower.mode === 'live' ? '未跟上' : '跳过' }} {{ follower.skipCount }}
              </span>

              <!-- 名义价值是金额：与带单员卡片的「池子权益」同款处理——
                   标签留灰、数值用最深一档。它原先写死在 text-4（全卡最浅），
                   比左边两个计数还浅，读起来像附注而不是一个要看的数 -->
              <span>名义价值 <span class="meta-value">{{ formatUsd(follower.grossNotional) }}</span></span>
            </div>
          </template>

          <!-- 取不到数据：把"不知道"如实说出来，不拿 0 冒充结果。
               而且要说清缺的是哪一侧：纸面缺的是池子快照，实盘缺的是交易所账户，
               两者的排查方向完全不同（一个去查池子/RPC，一个去查 key 与网络） -->
          <div v-else class="card-meta">
            跟单员 #{{ follower.id }} ·
            {{ follower.mode === 'live' ? '规模基准' : '本金' }}
            {{ formatUsd(follower.initialCapital) }}
            <div class="card-warn" style="margin-top: 6px">
              <template v-if="follower.mode === 'live'">
                暂取不到交易所账户（网络或 key 的问题），余额与持仓不可得
              </template>
              <template v-else>暂取不到池子数据，估值不可得</template>
              （账户配置仍可查看与修改）
            </div>
          </div>

          <div v-if="follower.isValuationAvailable && !follower.leadIsEnabled" class="card-warn">
            订阅的带单员已暂停，不会再有新动作
          </div>

          <div v-if="follower.unpricedMarketCount > 0" class="card-warn">
            有 {{ follower.unpricedMarketCount }} 个标的取不到价，未实现盈亏不完整
          </div>
        </RouterLink>

        <button
          type="button"
          class="account-card add-card"
          :disabled="!canEdit"
          :title="canEdit ? '' : '需要「跟单管理」的管理权限（编辑）'"
          @click="openCreate()"
        >
          <span style="font-size: 26px; line-height: 1">+</span>
          <span style="margin-top: 6px">开始跟单</span>
        </button>
      </div>
    </a-spin>

    <a-modal v-model:visible="createOpen" title="开始跟单" :ok-loading="creating" @ok="create">
      <a-space direction="vertical" fill :size="14">
        <a-alert v-if="leadListError" type="warning" :show-icon="true">
          带单员列表这次没取到（{{ leadListError }}），下面的选择框会是空的。
          <a-link @click="loadLeads">重试</a-link>
        </a-alert>

        <div>
          <div style="font-size: 13px; margin-bottom: 6px">选择要跟随的带单员</div>
          <a-select
            v-model="newLeadIndex"
            :style="{ width: '100%' }"
            placeholder="选择带单员（可按池名或账户索引搜索）"
            allow-search
            :filter-option="(value: string, option: { label?: unknown; value?: unknown }) => {
              const keyword = value.trim().toLowerCase()
              if (keyword === '') return true
              const label = typeof option.label === 'string' ? option.label.toLowerCase() : ''
              const val = typeof option.value === 'string' ? option.value.toLowerCase() : ''
              return label.includes(keyword) || val.includes(keyword)
            }"
          >
            <a-option
              v-for="lead in leads"
              :key="lead.accountIndex"
              :value="String(lead.accountIndex)"
              :label="lead.name"
            >
              {{ lead.name }} ·
              {{ lead.isEnabled ? (lead.isWatching ? '监控中' : '未连接') : '已暂停' }}
              <span v-if="followerCountOf(lead.accountIndex) > 0">
                · 已有 {{ followerCountOf(lead.accountIndex) }} 个跟单员
              </span>
              <span v-else>· 暂无跟单员</span>
            </a-option>
          </a-select>

          <!-- 一个带单员都没有时，这里不能只是一句"暂无数据"——要说清下一步去哪做 -->
          <div v-if="!leadListError && leads.length === 0" style="font-size: 12px; color: var(--color-text-3); margin-top: 6px">
            还没有任何带单员。跟单员订阅的是带单员，所以先到
            <RouterLink :to="{ name: 'leads' }" style="color: rgb(var(--primary-6))">「带单管理」页</RouterLink>
            为你想跟的池创建观察者。
          </div>
        </div>

        <div>
          <div style="font-size: 13px; margin-bottom: 6px">跟单模式</div>
          <a-radio-group v-model="newMode" direction="vertical">
            <a-radio v-for="mode in COPY_MODES" :key="mode.value" :value="mode.value">
              {{ mode.label }}
            </a-radio>
          </a-radio-group>
          <!-- 选中的模式要当场说清它的后果，而不是只给一个词让人猜 -->
          <div
            v-if="selectedMode"
            style="font-size: 12px; color: var(--color-text-3); line-height: 1.7; margin-top: 6px"
          >
            {{ selectedMode.description }}
          </div>
        </div>

        <!-- 实盘要落到一个具体账号上：这是账户事实，与"映射按平台存"是两回事 -->
        <div v-if="newMode === 'live'">
          <div style="font-size: 13px; margin-bottom: 6px">用哪个交易所账号下单</div>
          <a-select
            v-if="exchangeOptions.length > 0"
            v-model="newExchangeId"
            :style="{ width: '100%' }"
            placeholder="选择交易所账号"
          >
            <a-option v-for="e in exchangeOptions" :key="e.id" :value="e.id" :label="e.displayName">
              {{ e.displayName }}
              <span style="color: var(--color-text-3)">· {{ e.code }}</span>
            </a-option>
          </a-select>
          <div v-else style="font-size: 12px; color: var(--color-text-3); line-height: 1.7">
            还没有配置任何交易所账号。到
            <RouterLink :to="{ name: 'exchanges' }" style="color: rgb(var(--primary-6))">「交易所」页</RouterLink>
            填一次 key，实盘才能下单。
          </div>
        </div>

        <div>
          <div style="font-size: 13px; margin-bottom: 6px">
            投入的本金
            <span v-if="newMode === 'live'" style="color: var(--color-text-3)">（实盘叫「规模基准」：决定跟多大仓）</span>
          </div>
          <a-input-number
            v-model="newCapital"
            v-bind="newCapitalLimits"
            :style="{ width: '100%' }"
            :loading="availableLoading"
          >
            <template #suffix>USDC</template>
          </a-input-number>
          <!-- 上限的来源要写出来：一个说不出来源的数字，人会以为是随便定的 -->
          <div
            v-if="newMode === 'live'"
            style="font-size: 12px; color: var(--color-text-3); line-height: 1.7; margin-top: 6px"
          >
            <template v-if="availableLoading">正在读该账号的可用保证金……</template>
            <template v-else-if="liveAvailable !== null">
              上限 {{ formatUsd(liveAvailable) }}（该账号的可用保证金）。
              实盘按本金算出来的仓位，账户里的钱要开得出来。
            </template>
            <template v-else>
              没能读到该账号的可用保证金，本金上限会在提交时由后端核对。
            </template>
          </div>
        </div>

        <a-alert type="info" :show-icon="true">
          本金太小时，等比仓位会低于市场的<b>最小下单额</b>，账户建不出仓，
          只会记录一串「未跟上」。每个市场的最小下单额不同，
          要跟的池与市场需要多少本金，先用「规模门槛」页算一遍再定。
        </a-alert>
      </a-space>
    </a-modal>
  </a-space>
</template>

<style scoped>
.live-note {
  font-size: 12px;
  color: var(--color-text-3);
}

/* 只在该页确实有被禁用的按钮时才显示（见模板里的判断） */
.readonly-line {
  margin-top: 10px;
  font-size: 12px;
  color: rgb(var(--warning-6));
}

.card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
  gap: 14px;
}

/* 空态只用一行提示，下面照旧是那张虚线卡片——"怎么添加"的入口在空列表里
   不该变成一个长得不一样的普通按钮 */
.empty-line {
  margin-bottom: 12px;
  font-size: 12px;
  color: var(--color-text-3);
}

.account-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 16px;
  background: var(--color-bg-2);
  border: 1px solid var(--color-border-1);
  border-radius: 6px;
  color: inherit;
  text-decoration: none;
  transition: border-color 0.15s, box-shadow 0.15s;
}

.account-card:hover {
  border-color: rgb(var(--primary-6));
  box-shadow: 0 2px 10px rgb(0 0 0 / 8%);
}

.card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.card-title {
  font-size: 15px;
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.card-equity {
  display: flex;
  align-items: baseline;
  gap: 10px;
}

/* 大字：纸面的收益率（内联覆盖成红绿）与实盘的账户余额共用它。
   显式给最深一档而不是继承——它是卡片的视觉锚点，颜色不该跟着父级飘。
   等宽数字：一排卡片横扫过去时，位数不同的金额也能对齐。 */
.equity-value {
  font-size: 24px;
  font-weight: 500;
  color: var(--color-text-1);
  font-variant-numeric: tabular-nums;
}

.equity-abs {
  font-size: 13px;
  color: var(--color-text-3);
}

/* 纸面模式的这一格放的是**净值金额**（不是说明文字）：与上面的收益率同属"要看的数"，
   给它数值档。实盘模式那格是"账户余额 · 可用 …"这类描述句，仍留在说明档 */
.equity-abs.is-value {
  color: var(--color-text-1);
  font-variant-numeric: tabular-nums;
}

/* 行内的金额与比例。规则与带单员卡片一致：标签浅（text-3）、数值深（text-1）——
   原先整行同一个灰，"本金 $500.00 · 比例 8.350%" 里的数要费劲才找到 */
.meta-value {
  color: var(--color-text-1);
  font-variant-numeric: tabular-nums;
}

.card-meta,
.card-foot {
  font-size: 12px;
  color: var(--color-text-3);
}

/* 持仓区 = 固定高度的**视口**，高度按"两行"算出来而不是写死像素：
   行高与行距是变量，改一处两边都跟着变（视口写死、行高改了就会出现半行）。 */
.card-positions {
  --position-row-height: 24px;
  --position-row-gap: 4px;
  --position-viewport: calc(
    (var(--position-row-height) * 2) + var(--position-row-gap)
  );

  height: var(--position-viewport);

  /* 超出部分由轨道动画带出来，不给原生滚动条——列表里十几张卡片各挂一条滚动条太吵。
     但 prefers-reduced-motion 下会退回可滚动（见文件末尾），保证第三行之后够得着 */
  overflow: hidden;
  font-size: 13px;
  padding: 8px 0;
  border-top: 1px solid var(--color-border-1);
  border-bottom: 1px solid var(--color-border-1);
}

/* 会动的只有这一条轨道：视口（边框与内边距）不动，动的是里面的行 */
.positions-track {
  display: flex;
  flex-direction: column;
  gap: var(--position-row-gap);
}

.position-row {
  display: flex;
  align-items: center;
  justify-content: space-between;

  /* 行高固定：否则"两行的高度"这种算法不成立，标签换行还会把视口撑破 */
  height: var(--position-row-height);
}

/* 方向三角、标的图标、数量放在同一条水平线上；
   三者间距由 gap 统一给，不再靠文本空格（空格在不同字体下宽度不一致） */
.position-main {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.position-size {
  font-variant-numeric: tabular-nums;
}

/* 超过两行：自动上下滚动（跑马灯）。
   用 alternate 来回走，而不是单向循环——单向循环要复制一份内容才能无缝，
   而这里只有几行，"来回翻看"也更贴近它表达的东西。悬停暂停：鼠标压在上面时不该继续动 */
.card-positions.is-scrolling .positions-track {
  animation: positions-pingpong 4s ease-in-out infinite alternate;
}

.card-positions.is-scrolling:hover .positions-track {
  animation-play-state: paused;
}

@keyframes positions-pingpong {
  from {
    transform: translateY(0);
  }

  /* 百分比相对**轨道自身**高度：-100% 是滚到底，再加回一个视口高度，
     正好让最后一行停在视口底部（多滚一点就会露出空白） */
  to {
    transform: translateY(calc(-100% + var(--position-viewport)));
  }
}

/* 声明"减少动效"的用户：不做动画，改成可手动滚动 */
@media (prefers-reduced-motion: reduce) {
  .card-positions.is-scrolling {
    overflow-y: auto;
  }

  .card-positions.is-scrolling .positions-track {
    animation: none;
  }
}

.card-idle {
  color: var(--color-text-3);
}

.card-foot {
  display: flex;
  gap: 14px;
}

.card-warn {
  font-size: 12px;
  color: rgb(var(--warning-6));
}

/* 新建卡：虚线边框，明显不是数据卡 */
.add-card {
  align-items: center;
  justify-content: center;
  min-height: 180px;
  color: var(--color-text-3);
  background: transparent;

  /* 虚线描边比数据卡深一档：它是"这里可以加一条"的唯一提示，
     用与数据卡同一档的浅灰时，在空列表里会淡得只剩一个悬空的加号 */
  border-style: dashed;
  border-color: var(--color-border-3);
  cursor: pointer;
  font-size: 13px;
}

.add-card:hover {
  border-color: rgb(var(--primary-6));
  box-shadow: none;
}

/* 无权限时"开始跟单"卡不该还是可点的样子：native 按钮的 disabled 不换光标，
   悬停也不该再亮起来——否则它会与"这里可以开始"的暗示自相矛盾 */
.add-card:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}

.add-card:disabled:hover {
  border-color: var(--color-border-3);
  box-shadow: none;
}
</style>
