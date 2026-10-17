<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import type { TableData } from '@arco-design/web-vue'

import { api, type LeadTrader, type PoolDetail, type PoolOrder } from '@/api/client'
import LineChart from '@/components/LineChart.vue'
import MetricTile from '@/components/MetricTile.vue'
import { usePermission } from '@/composables/usePermission'
import {
  formatDateTime,
  formatNumber,
  formatPercent,
  formatSince,
  formatUsd,
} from '@/utils/format'

/// 带单员详情 —— 单个被观察池的观察关系 + 按带单广场术语展示的观察视图。
///
/// 术语与公开面的「带单广场」保持一致（跟随者、仓位变动、总收益率、AUM、
/// 收益分成、带单员跟投、当前风险/持仓），避免同一个数在工作台与公开面叫两个名字。
/// 工作台额外提供暂停/恢复/停止观察三个管理动作与「完整池详情」入口。
///
/// 两个请求分开取：带单员配置（leads view）与池详情（pools view）。
/// 池详情取不到时仍然显示带单员配置与动作，不把整页判死——上游抖动或当前账号
/// 没有池列表权限时，至少还能暂停/恢复观察。
const PAGE_PERMISSION = 'menu:leads'

const route = useRoute()
const router = useRouter()
const { can, refresh: refreshPermissions } = usePermission()

const accountIndex = computed(() => String(route.params.accountIndex))
const canEdit = computed(() => can(PAGE_PERMISSION, 'edit'))
const canDelete = computed(() => can(PAGE_PERMISSION, 'delete'))

const lead = ref<LeadTrader | null>(null)
const pool = ref<PoolDetail | null>(null)
const loading = ref(true)
const actionLoading = ref(false)
const errorMessage = ref('')
const leadError = ref('')
const poolError = ref('')

/// 持仓行的展开状态。由表格的图标与“点击整行”两条入口共同维护；
/// 图标自己会 stopPropagation，所以不会与 row-click 互相抵消。
const expandedKeys = ref<(string | number)[]>([])

const statusLabel = computed(() => {
  if (lead.value === null) return ''
  if (!lead.value.isEnabled) return '已暂停'
  return lead.value.isWatching ? '监控中' : '未连接'
})

const statusColor = computed(() => {
  if (lead.value === null) return 'gray'
  if (!lead.value.isEnabled) return 'gray'
  return lead.value.isWatching ? 'green' : 'orange'
})

const statusHint = computed(() => {
  if (lead.value === null) return ''
  if (!lead.value.isEnabled) return '配置上是暂停的，不会订阅这个池的推送'
  return lead.value.isWatching
    ? '当前正在接收这个池的持仓与成交推送'
    : '已启用，但此刻没有收到推送——连接还没建立或已经断开'
})

/// 收益曲线与带单广场同口径：把净值换算成区间收益率（窗口首点为 0%）。
/// 跟单产品看的是"跟着它能赚多少"，不是"一份份额值多少钱"。
const navSeries = computed(() => {
  const points = pool.value?.navCurve ?? []
  const first = points[0]

  if (points.length < 2 || first === undefined || first.sharePrice <= 0) return []

  return [
    {
      label: '收益率（%）',
      color: 'rgb(var(--primary-6))',
      values: points.map((point) => (point.sharePrice / first.sharePrice - 1) * 100),
    },
  ]
})

/// 曲线窗口的累计收益率，用于标题右侧的说明（与图上末点一致）。
const windowReturnPercent = computed(() => {
  const points = pool.value?.navCurve ?? []
  const first = points[0]
  const last = points[points.length - 1]

  if (points.length < 2 || first === undefined || last === undefined || first.sharePrice <= 0) {
    return null
  }

  return (last.sharePrice / first.sharePrice - 1) * 100
})

const chartLastAt = computed(() => pool.value?.navCurve.at(-1)?.at ?? null)

const trackingHint = computed(() =>
  lead.value === null ? '' : `本机自 ${formatDateTime(lead.value.createdAt)} 开始跟踪`,
)

/// 持仓按持仓价值降序；占比是该仓位占全部持仓价值的比例。
const positions = computed(() =>
  [...(pool.value?.positions ?? [])].sort((a, b) => Math.abs(b.notional) - Math.abs(a.notional)),
)

function positionWeight(notional: number): number {
  const gross = pool.value?.snapshot.grossExposure ?? 0
  return gross <= 0 ? 0 : (Math.abs(notional) / gross) * 100
}

/// 按市场归集挂单。买入价格降序、卖出价格升序，与池详情页的阶梯顺序一致：
/// 每一侧都从最接近成交的档位向外排。
const ordersByMarket = computed(() => {
  const result = new Map<number, { buy: PoolOrder[]; sell: PoolOrder[] }>()

  for (const order of pool.value?.orders ?? []) {
    let bucket = result.get(order.marketIndex)

    if (bucket === undefined) {
      bucket = { buy: [], sell: [] }
      result.set(order.marketIndex, bucket)
    }

    if (order.side === 'buy') {
      bucket.buy.push(order)
    } else {
      bucket.sell.push(order)
    }
  }

  for (const bucket of result.values()) {
    bucket.buy.sort((a, b) => b.price - a.price)
    bucket.sell.sort((a, b) => a.price - b.price)
  }

  return result
})

function ordersFor(marketId: number, side: 'buy' | 'sell'): PoolOrder[] {
  return ordersByMarket.value.get(marketId)?.[side] ?? []
}

function orderCount(marketId: number): number {
  const bucket = ordersByMarket.value.get(marketId)
  return bucket === undefined ? 0 : bucket.buy.length + bucket.sell.length
}

/// 点击整行展开/收起；展开图标有自己的 stopPropagation，不会重复触发。
function toggleExpanded(record: TableData): void {
  const key = record.marketId as number

  expandedKeys.value = expandedKeys.value.includes(key)
    ? expandedKeys.value.filter((current) => current !== key)
    : [...expandedKeys.value, key]
}

/// 动作失败只影响当前动作，不清掉已经加载的详情。
async function runAction(action: () => Promise<void>): Promise<void> {
  actionLoading.value = true
  errorMessage.value = ''

  try {
    await action()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    actionLoading.value = false
  }
}

async function setEnabled(isEnabled: boolean): Promise<void> {
  await runAction(async () => {
    await api.configureLeadTrader(accountIndex.value, isEnabled)
    await load()
  })
}

async function stop(): Promise<void> {
  await runAction(async () => {
    await api.deleteLeadTrader(accountIndex.value)
    await router.push({ name: 'leads' })
  })
}

async function load(): Promise<void> {
  loading.value = true
  leadError.value = ''
  poolError.value = ''

  const [leadResult, poolResult] = await Promise.allSettled([
    api.leadTrader(accountIndex.value),
    api.poolDetail(accountIndex.value),
  ])

  if (leadResult.status === 'fulfilled') {
    lead.value = leadResult.value
  } else {
    lead.value = null
    leadError.value = leadResult.reason instanceof Error
      ? leadResult.reason.message
      : String(leadResult.reason)
  }

  if (poolResult.status === 'fulfilled') {
    pool.value = poolResult.value
  } else {
    pool.value = null
    poolError.value = poolResult.reason instanceof Error
      ? poolResult.reason.message
      : String(poolResult.reason)
  }

  loading.value = false
}

onMounted(async () => {
  try {
    await refreshPermissions()
  } catch (error) {
    console.warn('读取权限失败，本页按钮将按无权限处理', error)
  }

  await load()
})
</script>

<template>
  <a-space direction="vertical" fill style="width: 100%">
    <a-card :bordered="false">
      <template #title>
        <a-space :size="10" wrap>
          <RouterLink :to="{ name: 'leads' }" class="back-link">← 全部带单员</RouterLink>
          <span class="divider">|</span>
          <span>{{ lead?.name || `带单员 #${accountIndex}` }}</span>
          <a-tooltip v-if="lead" :content="statusHint">
            <a-tag size="small" :color="statusColor">{{ statusLabel }}</a-tag>
          </a-tooltip>
        </a-space>
      </template>

      <template #extra>
        <a-space :size="8">
          <RouterLink
            v-if="pool"
            :to="{ name: 'pool-detail', params: { accountIndex } }"
            class="back-link"
          >
            完整池详情
          </RouterLink>
          <a-button size="small" :loading="loading" @click="load">刷新</a-button>
          <a-button
            v-if="lead"
            size="small"
            :loading="actionLoading"
            :disabled="!canEdit"
            :title="canEdit ? '' : '需要「带单员」的管理权限（编辑）'"
            @click="setEnabled(!lead.isEnabled)"
          >
            {{ lead.isEnabled ? '暂停观察' : '恢复观察' }}
          </a-button>
          <a-popconfirm
            v-if="lead"
            content="停止观察会删除这个带单员；仍有跟随者订阅时会被后端拒绝。"
            @ok="stop"
          >
            <a-button
              size="small"
              status="danger"
              :loading="actionLoading"
              :disabled="!canDelete"
              :title="canDelete ? '' : '需要「带单员」的管理权限（删除）'"
            >
              停止观察
            </a-button>
          </a-popconfirm>
        </a-space>
      </template>

      <a-spin :loading="loading" style="width: 100%">
        <a-alert v-if="errorMessage" type="error" :title="errorMessage" />
        <a-alert v-if="leadError" type="error" :title="leadError" style="margin-top: 10px" />

        <template v-if="lead">
          <div class="meta-line">
            带单员 #{{ lead.accountIndex }} · 跟随者 <b>{{ lead.followerCount }}</b>
            <template v-if="lead.activeFollowerCount !== lead.followerCount">
              （{{ lead.activeFollowerCount }} 活跃）
            </template>
            · 仓位变动 <b>{{ lead.signalCount }}</b> 次
            <template v-if="lead.lastSignalAt">
              · 最近变动 {{ formatSince(lead.lastSignalAt) }}
            </template>
          </div>

          <a-alert type="info" :show-icon="true" style="margin-top: 12px">
            带单员只要存在就在观察，与有没有跟随者无关。暂停只停推送；停止观察会删除这条记录。
          </a-alert>
        </template>
      </a-spin>
    </a-card>

    <a-alert v-if="poolError" type="warning" :show-icon="true" :title="`池详情暂时不可得：${poolError}`">
      带单员配置仍可操作；如果当前账号没有「池列表」的查看权限，也会看到这条提示。
    </a-alert>

    <template v-if="pool && lead">
      <a-card :bordered="false">
        <template #title>
          带单表现
          <span class="section-note">收益率与风险指标按净值记录计算</span>
        </template>

        <a-alert
          v-if="!pool.metrics.isStatisticallyMeaningful"
          type="warning"
          :show-icon="true"
          style="margin-bottom: 12px"
        >
          该带单员的业绩记录只有 {{ pool.metrics.spanDays }} 天，收益与风险指标仅供参考。
        </a-alert>

        <a-grid :cols="6" :col-gap="12" :row-gap="14">
          <a-grid-item>
            <MetricTile
              label="总收益率"
              :value="formatPercent(pool.metrics.realTotalReturnPercent)"
              note="按净值计算"
            />
          </a-grid-item>
          <a-grid-item>
            <MetricTile
              label="年化收益率"
              :value="formatPercent(pool.metrics.realAnnualizedReturnPercent)"
              note="按业绩区间折算"
              tone="primary"
            />
          </a-grid-item>
          <a-grid-item>
            <MetricTile
              label="最大回撤"
              :value="formatPercent(pool.metrics.realMaxDrawdownPercent)"
              note="区间内最深"
              tone="danger"
            />
          </a-grid-item>
          <a-grid-item>
            <MetricTile
              label="夏普比率"
              :value="formatNumber(pool.metrics.realSharpe, 2)"
              :note="`波动率 ${formatPercent(pool.metrics.periodVolatilityPercent)}`"
            />
          </a-grid-item>
          <a-grid-item>
            <MetricTile
              label="资产管理规模（AUM）"
              :value="formatUsd(pool.summary.totalAssetValue)"
              note="带单员管理的资金"
            />
          </a-grid-item>
          <a-grid-item>
            <MetricTile label="跟随者" :value="`${lead.followerCount} 人`" />
          </a-grid-item>
        </a-grid>
      </a-card>

      <a-card :bordered="false">
        <template #title>带单概况</template>
        <a-descriptions :column="4" size="small">
          <a-descriptions-item label="带单天数">
            <a-tooltip :content="trackingHint">
              <span>{{ pool.metrics.spanDays }} 天</span>
            </a-tooltip>
          </a-descriptions-item>
          <a-descriptions-item label="收益分成">
            <a-tooltip content="带单员从跟随者收益中提取的比例。">
              <span>{{ formatPercent(pool.summary.operatorFeePercent) }}</span>
            </a-tooltip>
          </a-descriptions-item>
          <a-descriptions-item label="带单员跟投">
            <a-tooltip
              :content="
                pool.isDepositLimited
                  ? `带单员自有资金占资产管理规模的 ${formatPercent(pool.operatorSharePercent)}；已到平台下限 ${formatPercent(pool.minOperatorSharePercent)}，不再接受新入金（跟随不受影响）。`
                  : `带单员自有资金占资产管理规模的 ${formatPercent(pool.operatorSharePercent)}；平台要求不低于 ${formatPercent(pool.minOperatorSharePercent)}。`
              "
            >
              <a-tag size="small" color="arcoblue">
                {{ formatPercent(pool.operatorSharePercent) }}
              </a-tag>
            </a-tooltip>
          </a-descriptions-item>
          <a-descriptions-item label="仓位变动">
            <a-tooltip content="系统记录的该带单员仓位变化次数。">
              <span>{{ lead.signalCount }} 次</span>
            </a-tooltip>
          </a-descriptions-item>
        </a-descriptions>
      </a-card>

      <a-card :bordered="false">
        <template #title>
          当前风险
          <span class="section-note">全仓保证金 · 强平缓冲越厚越安全</span>
        </template>
        <a-descriptions :column="4" size="small">
          <a-descriptions-item label="总持仓">
            {{ formatUsd(pool.snapshot.grossExposure) }}
          </a-descriptions-item>
          <a-descriptions-item label="净持仓">
            {{ formatUsd(pool.snapshot.netExposure) }}
          </a-descriptions-item>
          <a-descriptions-item label="仓位比例">
            <a-tooltip content="总持仓 ÷ 资产管理规模（AUM）。">
              <a-tag
                size="small"
                :color="
                  pool.snapshot.exposureRatioPercent > 400
                    ? 'red'
                    : pool.snapshot.exposureRatioPercent > 200
                      ? 'orange'
                      : 'green'
                "
              >
                {{ formatPercent(pool.snapshot.exposureRatioPercent) }}
              </a-tag>
            </a-tooltip>
          </a-descriptions-item>
          <a-descriptions-item label="强平缓冲">
            <a-tooltip content="抵押品减去维持保证金要求；数值越厚，离强平越远。">
              <span>{{ formatUsd(pool.snapshot.liquidationBuffer) }}</span>
            </a-tooltip>
          </a-descriptions-item>
          <a-descriptions-item label="持仓数量">
            {{ positions.length }} 个
          </a-descriptions-item>
        </a-descriptions>
      </a-card>

      <a-card :bordered="false">
        <template #title>
          当前持仓
          <a-tag size="small" style="margin-left: 6px">{{ positions.length }}</a-tag>
          <span class="section-note">按持仓价值排序</span>
        </template>
        <a-table
          v-model:expanded-keys="expandedKeys"
          :data="positions"
          row-key="marketId"
          size="small"
          :bordered="false"
          :pagination="false"
          :expandable="{ width: 44 }"
          row-class="expandable-row"
          @row-click="toggleExpanded"
        >
          <template #columns>
            <a-table-column title="币种" data-index="symbol" :width="140" />
            <a-table-column title="方向" :width="80" align="center">
              <template #cell="{ record }">
                <a-tooltip :content="record.side === 'long' ? '多' : '空'">
                  <span
                    class="direction"
                    :class="record.side === 'long' ? 'direction-long' : 'direction-short'"
                    role="img"
                    :aria-label="record.side === 'long' ? '多' : '空'"
                  >
                    {{ record.side === 'long' ? '▲' : '▼' }}
                  </span>
                </a-tooltip>
              </template>
            </a-table-column>
            <a-table-column
              title="标记价格"
              align="right"
              :header-cell-style="{ width: '1%', whiteSpace: 'nowrap' }"
              :body-cell-style="{ width: '1%', whiteSpace: 'nowrap' }"
            >
              <template #cell="{ record }">
                <span class="price-cell">{{ formatNumber(record.markPrice, 6) }}</span>
              </template>
            </a-table-column>
            <a-table-column
              title="清算价格"
              align="right"
              :header-cell-style="{ width: '1%', whiteSpace: 'nowrap' }"
              :body-cell-style="{ width: '1%', whiteSpace: 'nowrap' }"
            >
              <template #cell="{ record }">
                <span v-if="record.liquidationPrice === null" class="price-cell" style="color: var(--color-text-3)">
                  —
                </span>
                <span v-else class="price-cell">{{ formatNumber(record.liquidationPrice, 6) }}</span>
              </template>
            </a-table-column>
            <a-table-column
              title="持仓价值"
              align="right"
              :header-cell-style="{ width: '1%', whiteSpace: 'nowrap' }"
              :body-cell-style="{ width: '1%', whiteSpace: 'nowrap' }"
            >
              <template #cell="{ record }">
                <span class="price-cell">{{ formatUsd(record.notional) }}</span>
              </template>
            </a-table-column>
            <a-table-column title="持仓占比">
              <template #cell="{ record }">
                <div class="weight-cell">
                  <div class="weight-track">
                    <div
                      class="weight-fill"
                      :style="{ width: `${Math.min(100, positionWeight(record.notional))}%` }"
                    />
                  </div>
                  <span class="weight-value">{{ formatPercent(positionWeight(record.notional), 1) }}</span>
                </div>
              </template>
            </a-table-column>
          </template>

          <template #expand-row="{ record }">
            <div v-if="orderCount(record.marketId) === 0" class="order-empty-all">
              {{ record.symbol }} 当前没有挂单
            </div>

            <div v-else class="order-ladder">
              <div class="order-side">
                <div class="order-side-head">
                  <a-tag size="small" color="green">买入</a-tag>
                  <span>{{ ordersFor(record.marketId, 'buy').length }} 笔</span>
                </div>
                <div class="order-columns">
                  <span>价格</span>
                  <span class="order-number">数量</span>
                  <span class="order-number">名义价值</span>
                  <span />
                </div>
                <div
                  v-for="order in ordersFor(record.marketId, 'buy')"
                  :key="order.orderIndex"
                  class="order-row"
                >
                  <span class="order-price">{{ formatNumber(order.price, 6) }}</span>
                  <span class="order-number">{{ formatNumber(order.remainingSize, 2) }}</span>
                  <span class="order-number">{{ formatUsd(order.remainingNotional) }}</span>
                  <span class="order-flag">
                    <a-tag v-if="order.isPartiallyFilled" size="small" color="orange">部分</a-tag>
                  </span>
                </div>
                <div v-if="ordersFor(record.marketId, 'buy').length === 0" class="order-empty">
                  无买入挂单
                </div>
              </div>

              <div class="order-side">
                <div class="order-side-head">
                  <a-tag size="small" color="red">卖出</a-tag>
                  <span>{{ ordersFor(record.marketId, 'sell').length }} 笔</span>
                </div>
                <div class="order-columns">
                  <span>价格</span>
                  <span class="order-number">数量</span>
                  <span class="order-number">名义价值</span>
                  <span />
                </div>
                <div
                  v-for="order in ordersFor(record.marketId, 'sell')"
                  :key="order.orderIndex"
                  class="order-row"
                >
                  <span class="order-price">{{ formatNumber(order.price, 6) }}</span>
                  <span class="order-number">{{ formatNumber(order.remainingSize, 2) }}</span>
                  <span class="order-number">{{ formatUsd(order.remainingNotional) }}</span>
                  <span class="order-flag">
                    <a-tag v-if="order.isPartiallyFilled" size="small" color="orange">部分</a-tag>
                  </span>
                </div>
                <div v-if="ordersFor(record.marketId, 'sell').length === 0" class="order-empty">
                  无卖出挂单
                </div>
              </div>
            </div>
          </template>

          <template #empty>
            <a-empty description="当前空仓" />
          </template>
        </a-table>
      </a-card>

      <a-card :bordered="false">
        <template #title>
          收益曲线
          <span class="section-note">
            最近 {{ pool.navCurve.length }} 个点 · 截至 {{ formatDateTime(chartLastAt) }} · 区间累计
            {{ windowReturnPercent === null ? '—' : formatPercent(windowReturnPercent) }}
          </span>
        </template>
        <LineChart v-if="navSeries.length > 0" :series="navSeries" :baseline="0" :height="260" />
        <a-empty v-else description="没有足够的净值点画出曲线" />
      </a-card>
    </template>
  </a-space>
</template>

<style scoped>
.back-link {
  font-size: 12px;
  font-weight: 400;
  color: rgb(var(--primary-6));
  text-decoration: none;
}

.divider {
  color: var(--color-text-4);
  font-weight: 400;
}

.meta-line {
  font-size: 12px;
  color: var(--color-text-3);
  line-height: 1.7;
}

.section-note {
  margin-left: 8px;
  font-size: 12px;
  font-weight: 400;
  color: var(--color-text-3);
}

/* 持仓占比条：长度与数字表达同一个量，数字仍以文字为准 */
.weight-cell {
  display: flex;
  align-items: center;
  gap: 10px;
}

.weight-track {
  flex: 1 1 auto;
  height: 6px;
  background: var(--color-fill-2);
  border-radius: 3px;
  overflow: hidden;
}

.weight-fill {
  height: 100%;
  background: rgb(var(--primary-6));
  border-radius: 3px;
}

.weight-value {
  flex: 0 0 auto;
  font-variant-numeric: tabular-nums;
}

.direction {
  display: inline-block;
  font-size: 14px;
  line-height: 1;
}

.direction-long {
  color: rgb(var(--success-6));
}

.direction-short {
  color: rgb(var(--danger-6));
}

/* 价格/金额列不设固定宽度，让表格按内容撑开；只保证数字不折行 */
.price-cell {
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

/* 展开行：买入在左、卖出在右；两侧各自按接近成交的价格向外排 */
.order-ladder {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  gap: 20px;
  padding: 6px 8px 10px 48px;
}

.order-side {
  min-width: 0;
}

.order-side-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
  font-size: 12px;
  color: var(--color-text-3);
}

.order-columns,
.order-row {
  display: grid;
  grid-template-columns: minmax(80px, 1fr) minmax(88px, 1fr) minmax(84px, 1fr) 48px;
  align-items: center;
  column-gap: 10px;
}

.order-columns {
  padding-bottom: 4px;
  margin-bottom: 2px;
  border-bottom: 1px solid var(--color-border-1);
  font-size: 11px;
  color: var(--color-text-4);
}

.order-row {
  min-height: 24px;
  font-size: 12px;
  line-height: 24px;
}

.order-price {
  font-family: monospace;
  font-variant-numeric: tabular-nums;
  color: var(--color-text-1);
  white-space: nowrap;
}

.order-number {
  text-align: right;
  font-variant-numeric: tabular-nums;
  color: var(--color-text-3);
  white-space: nowrap;
}

.order-flag {
  text-align: right;
}

:deep(.expandable-row) {
  cursor: pointer;
}

.order-empty,
.order-empty-all {
  font-size: 12px;
  color: var(--color-text-3);
}

.order-empty-all {
  padding: 10px 8px 10px 48px;
}
</style>
