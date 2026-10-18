<script setup lang="ts">
import { computed, ref } from 'vue'
import type { TableData } from '@arco-design/web-vue'

import DirectionMark from '@/components/DirectionMark.vue'
import LineChart from '@/components/LineChart.vue'
import MetricTile from '@/components/MetricTile.vue'
import SymbolIcon from '@/components/SymbolIcon.vue'
import type {
  LeadDetailOrder,
  LeadDetailPosition,
  LeadDetailViewData,
} from '@/domain/leadDetail'
import {
  formatDateTime,
  formatNumber,
  formatPercent,
  formatUsd,
} from '@/utils/format'
import { leverageText } from '@/utils/leverage'

/// 带单员详情的共享展示区。
///
/// 工作台详情页与公开带单广场弹窗共用这一份：术语、指标顺序、持仓列、
/// 挂单展开行为都只在这里维护。两个页面各自负责取数、标题与操作按钮，
/// 并把数据映射成 `LeadDetailViewData`。
const props = defineProps<{ data: LeadDetailViewData }>()

/// 数值列统一不换行。持仓表固定 1180px 宽：六位小数的大价格（如 BTC）
/// 需要 120px 以上的列宽；容器更窄时横向滚动，左右固定列保持可见。
const numericCellStyle = {
  whiteSpace: 'nowrap',
}

const expandedKeys = ref<(string | number)[]>([])

const positions = computed<LeadDetailPosition[]>(() =>
  [...props.data.positions].sort((a, b) => Math.abs(b.notional) - Math.abs(a.notional)),
)

const navSeries = computed(() => {
  const points = props.data.navCurve
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

const windowReturnPercent = computed(() => {
  const points = props.data.navCurve
  const first = points[0]
  const last = points[points.length - 1]

  if (points.length < 2 || first === undefined || last === undefined || first.sharePrice <= 0) {
    return null
  }

  return (last.sharePrice / first.sharePrice - 1) * 100
})

const chartLastAt = computed(() => props.data.navCurve.at(-1)?.at ?? null)

/// 按市场归集挂单。买入价格降序、卖出价格升序，每一侧都从最接近成交的档位向外排。
const ordersByMarket = computed(() => {
  const result = new Map<number, { buy: LeadDetailOrder[]; sell: LeadDetailOrder[] }>()

  for (const order of props.data.orders) {
    let bucket = result.get(order.marketId)

    if (bucket === undefined) {
      bucket = { buy: [], sell: [] }
      result.set(order.marketId, bucket)
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

function ordersFor(marketId: number, side: 'buy' | 'sell'): LeadDetailOrder[] {
  return ordersByMarket.value.get(marketId)?.[side] ?? []
}

function orderCount(marketId: number): number {
  const bucket = ordersByMarket.value.get(marketId)
  return bucket === undefined ? 0 : bucket.buy.length + bucket.sell.length
}

/// 点击整行展开/收起；展开图标自己会 stopPropagation，不会重复触发。
function toggleExpanded(record: TableData): void {
  const key = record.marketId as number

  expandedKeys.value = expandedKeys.value.includes(key)
    ? expandedKeys.value.filter((current) => current !== key)
    : [...expandedKeys.value, key]
}

function pnlColor(value: number): string {
  if (value > 0) return 'rgb(var(--success-6))'
  if (value < 0) return 'rgb(var(--danger-6))'
  return 'var(--color-text-3)'
}
</script>

<template>
  <div class="lead-detail-content">
    <section>
      <div class="section-title">
        带单表现
        <span class="section-note">收益率与风险指标按净值记录计算</span>
      </div>

      <a-alert
        v-if="!data.metrics.isStatisticallyMeaningful"
        type="warning"
        :show-icon="true"
        style="margin-bottom: 12px"
      >
        该带单员的业绩记录只有 {{ data.metrics.spanDays }} 天，收益与风险指标仅供参考。
      </a-alert>

      <a-grid :cols="6" :col-gap="12" :row-gap="14">
        <a-grid-item>
          <MetricTile
            label="总收益率"
            :value="formatPercent(data.metrics.realTotalReturnPercent)"
            note="按净值计算"
          />
        </a-grid-item>
        <a-grid-item>
          <MetricTile
            label="年化收益率"
            :value="formatPercent(data.metrics.realAnnualizedReturnPercent)"
            note="按业绩区间折算"
            tone="primary"
          />
        </a-grid-item>
        <a-grid-item>
          <MetricTile
            label="最大回撤"
            :value="formatPercent(data.metrics.realMaxDrawdownPercent)"
            note="区间内最深"
            tone="danger"
          />
        </a-grid-item>
        <a-grid-item>
          <MetricTile
            label="夏普比率"
            :value="formatNumber(data.metrics.realSharpe, 2)"
            :note="`波动率 ${formatPercent(data.metrics.periodVolatilityPercent)}`"
          />
        </a-grid-item>
        <a-grid-item>
          <MetricTile
            label="资产管理规模（AUM）"
            :value="formatUsd(data.totalAssetValue)"
            note="带单员管理的资金"
          />
        </a-grid-item>
        <a-grid-item>
          <MetricTile label="跟随者" :value="`${data.followerCount} 人`" />
        </a-grid-item>
      </a-grid>
    </section>

    <section>
      <div class="section-title">带单概况</div>
      <a-descriptions :column="4" size="small">
        <a-descriptions-item label="带单天数">
          <a-tooltip :content="data.trackingHint">
            <span>{{ data.metrics.spanDays }} 天</span>
          </a-tooltip>
        </a-descriptions-item>
        <a-descriptions-item label="收益分成">
          <a-tooltip content="带单员从跟随者收益中提取的比例。">
            <span>{{ formatPercent(data.operatorFeePercent) }}</span>
          </a-tooltip>
        </a-descriptions-item>
        <a-descriptions-item label="带单员跟投">
          <a-tooltip
            :content="
              data.isDepositLimited
                ? `带单员自有资金占资产管理规模的 ${formatPercent(data.operatorSharePercent)}；已到平台下限 ${formatPercent(data.minOperatorSharePercent)}，不再接受新入金（跟随不受影响）。`
                : `带单员自有资金占资产管理规模的 ${formatPercent(data.operatorSharePercent)}；平台要求不低于 ${formatPercent(data.minOperatorSharePercent)}。`
            "
          >
            <a-tag size="small" color="arcoblue">
              {{ formatPercent(data.operatorSharePercent) }}
            </a-tag>
          </a-tooltip>
        </a-descriptions-item>
        <a-descriptions-item label="仓位变动">
          <a-tooltip content="系统记录的该带单员仓位变化次数。">
            <span>{{ data.signalCount }} 次</span>
          </a-tooltip>
        </a-descriptions-item>
      </a-descriptions>
    </section>

    <section>
      <div class="section-title">
        当前风险
        <span class="section-note">全仓保证金 · 强平缓冲越厚越安全</span>
      </div>
      <a-descriptions :column="4" size="small">
        <a-descriptions-item label="总持仓">
          {{ formatUsd(data.exposure.grossExposure) }}
        </a-descriptions-item>
        <a-descriptions-item label="净持仓">
          {{ formatUsd(data.exposure.netExposure) }}
        </a-descriptions-item>
        <a-descriptions-item label="仓位比例">
          <a-tooltip content="总持仓 ÷ 资产管理规模（AUM）。">
            <a-tag
              size="small"
              :color="
                data.exposure.exposureRatioPercent > 400
                  ? 'red'
                  : data.exposure.exposureRatioPercent > 200
                    ? 'orange'
                    : 'green'
              "
            >
              {{ formatPercent(data.exposure.exposureRatioPercent) }}
            </a-tag>
          </a-tooltip>
        </a-descriptions-item>
        <a-descriptions-item label="强平缓冲">
          <a-tooltip content="抵押品减去维持保证金要求；数值越厚，离强平越远。">
            <span>{{ formatUsd(data.exposure.liquidationBuffer) }}</span>
          </a-tooltip>
        </a-descriptions-item>
        <a-descriptions-item label="持仓数量">
          {{ positions.length }} 个
        </a-descriptions-item>
      </a-descriptions>
    </section>

    <section>
      <div class="section-title">
        当前持仓
        <a-tag size="small" style="margin-left: 6px">{{ positions.length }}</a-tag>
        <span class="section-note">按持仓价值排序 · 点击行展开挂单</span>
      </div>
      <a-table
        v-model:expanded-keys="expandedKeys"
        :data="positions"
        row-key="marketId"
        size="small"
        :bordered="false"
        :pagination="false"
        :expandable="{ width: 44 }"
        :scroll="{ x: 1180 }"
        row-class="expandable-row"
        @row-click="toggleExpanded"
      >
        <template #columns>
          <a-table-column title="标的" :width="100" fixed="left">
            <template #cell="{ record }">
              <div class="symbol-cell">
                <SymbolIcon :symbol="record.symbol" />
                <span class="symbol-name" :title="record.symbol">{{ record.symbol }}</span>
              </div>
            </template>
          </a-table-column>
          <a-table-column title="方向" :width="60" align="center" fixed="left">
            <template #cell="{ record }">
              <DirectionMark :side="record.side" />
            </template>
          </a-table-column>
          <a-table-column
            title="杠杆"
            align="right"
            :width="80"
            :header-cell-style="numericCellStyle"
            :body-cell-style="numericCellStyle"
          >
            <template #title>
              <a-tooltip
                content="该仓位当前的最大杠杆倍数（1 ÷ 保证金率）；上游未给有效保证金率时为 —。"
              >
                <span>杠杆</span>
              </a-tooltip>
            </template>
            <template #cell="{ record }">
              <span
                class="price-cell"
                :style="{ color: record.leverage === null ? 'var(--color-text-3)' : undefined }"
              >
                {{ leverageText(record.leverage) }}
              </span>
            </template>
          </a-table-column>
          <a-table-column
            title="开仓价格"
            align="right"
            :width="128"
            :header-cell-style="numericCellStyle"
            :body-cell-style="numericCellStyle"
          >
            <template #cell="{ record }">
              <span class="price-cell">{{ formatNumber(record.avgEntryPrice, 6) }}</span>
            </template>
          </a-table-column>
          <a-table-column
            title="标记价格"
            align="right"
            :width="128"
            :header-cell-style="numericCellStyle"
            :body-cell-style="numericCellStyle"
          >
            <template #cell="{ record }">
              <span class="price-cell">{{ formatNumber(record.markPrice, 6) }}</span>
            </template>
          </a-table-column>
          <a-table-column
            title="清算价格"
            align="right"
            :width="120"
            :header-cell-style="numericCellStyle"
            :body-cell-style="numericCellStyle"
          >
            <template #cell="{ record }">
              <span
                v-if="record.liquidationPrice === null"
                class="price-cell"
                style="color: var(--color-text-3)"
              >
                —
              </span>
              <span v-else class="price-cell">{{ formatNumber(record.liquidationPrice, 6) }}</span>
            </template>
          </a-table-column>
          <a-table-column
            title="持仓价值"
            align="right"
            :width="92"
            :header-cell-style="numericCellStyle"
            :body-cell-style="numericCellStyle"
          >
            <template #cell="{ record }">
              <span class="price-cell">{{ formatUsd(record.notional) }}</span>
            </template>
          </a-table-column>
          <a-table-column
            title="已实现盈亏"
            align="right"
            :width="96"
            :header-cell-style="numericCellStyle"
            :body-cell-style="numericCellStyle"
          >
            <template #title>
              <a-tooltip
                content="由已平仓轮次重建的毛利（未扣费），只覆盖近端成交窗口；上游持仓不提供已实现盈亏。"
              >
                <span>已实现盈亏</span>
              </a-tooltip>
            </template>
            <template #cell="{ record }">
              <span class="price-cell" :style="{ color: pnlColor(record.realizedPnl) }">
                {{ formatUsd(record.realizedPnl, 4) }}
              </span>
            </template>
          </a-table-column>
          <a-table-column
            title="未实现盈亏"
            align="right"
            fixed="right"
            :width="104"
            :header-cell-style="numericCellStyle"
            :body-cell-style="numericCellStyle"
          >
            <template #title>
              <a-tooltip content="按标记价计算的浮动盈亏。">
                <span>未实现盈亏</span>
              </a-tooltip>
            </template>
            <template #cell="{ record }">
              <span class="price-cell" :style="{ color: pnlColor(record.unrealizedPnl) }">
                {{ formatUsd(record.unrealizedPnl, 4) }}
              </span>
            </template>
          </a-table-column>
          <a-table-column
            title="投资回报率"
            align="right"
            fixed="right"
            :width="104"
            :header-cell-style="numericCellStyle"
            :body-cell-style="numericCellStyle"
          >
            <template #title>
              <a-tooltip content="未实现盈亏 ÷ 保证金，与 Lighter 官方前端同口径。">
                <span>投资回报率</span>
              </a-tooltip>
            </template>
            <template #cell="{ record }">
              <span
                v-if="record.unrealizedPnlPercent === null"
                class="price-cell"
                style="color: var(--color-text-3)"
              >
                —
              </span>
              <span
                v-else
                class="price-cell"
                :style="{ color: pnlColor(record.unrealizedPnlPercent) }"
              >
                {{ formatPercent(record.unrealizedPnlPercent) }}
              </span>
            </template>
          </a-table-column>
          <a-table-column
            title="持仓占比"
            fixed="right"
            :width="124"
            :header-cell-style="numericCellStyle"
            :body-cell-style="numericCellStyle"
          >
            <template #cell="{ record }">
              <div class="weight-cell">
                <div class="weight-track">
                  <div
                    class="weight-fill"
                    :style="{ width: `${Math.min(100, record.weightPercent)}%` }"
                  />
                </div>
                <span class="weight-value">{{ formatPercent(record.weightPercent, 1) }}</span>
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
    </section>

    <section>
      <div class="section-title">
        收益曲线
        <span class="section-note">
          最近 {{ data.navCurve.length }} 个点 · 截至 {{ formatDateTime(chartLastAt) }} · 区间累计
          {{ windowReturnPercent === null ? '—' : formatPercent(windowReturnPercent) }}
        </span>
      </div>
      <LineChart v-if="navSeries.length > 0" :series="navSeries" :baseline="0" :height="240" />
      <a-empty
        v-else
        :description="
          data.navCurve.length === 0 ? '没有业绩数据' : '目前只有 1 个净值点，画不出曲线'
        "
      />
    </section>
  </div>
</template>

<style scoped>
.lead-detail-content {
  display: flex;
  flex-direction: column;
  gap: 24px;
}

.section-title {
  margin-bottom: 8px;
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-1);
}

.section-note {
  margin-left: 8px;
  font-size: 12px;
  font-weight: 400;
  color: var(--color-text-3);
}

.price-cell {
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

:deep(.expandable-row) {
  cursor: pointer;
}

.weight-cell {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 90px;
}

.symbol-cell {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.symbol-name {
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.weight-track {
  flex: 1 1 30px;
  min-width: 30px;
  height: 6px;
  border-radius: 3px;
  background: var(--color-fill-2);
  overflow: hidden;
}

.weight-fill {
  height: 100%;
  border-radius: 3px;
  background: rgb(var(--primary-6));
}

.weight-value {
  flex: 0 0 auto;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

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

.order-empty,
.order-empty-all {
  font-size: 12px;
  color: var(--color-text-3);
}

.order-empty-all {
  padding: 10px 8px 10px 48px;
}
</style>
