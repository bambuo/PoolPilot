<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'

import { api, type PoolDetail, type PoolPosition } from '@/api/client'
import DirectionMark from '@/components/DirectionMark.vue'
import HeaderHint from '@/components/HeaderHint.vue'
import LineChart from '@/components/LineChart.vue'
import MetricTile from '@/components/MetricTile.vue'
import SymbolName from '@/components/SymbolName.vue'
import {
  formatCompact,
  formatDate,
  formatDateTime,
  formatNumber,
  formatPercent,
  formatUsd,
} from '@/utils/format'

/// 池详情。
///
/// 一次请求拿到观察该池所需的全部信息。页面顺序对应实际的分析顺序：
/// 净值与真实指标（值不值得跟）→ 敞口与保证金（危不危险）→ 挂单阶梯（怎么建仓）
/// → 成交构成与单轮盈亏（赚在哪）。
const route = useRoute()

const detail = ref<PoolDetail | null>(null)
const loading = ref(true)
const errorMessage = ref('')

/// 本次数据的读取时刻（ISO 串）。用于把「数据多新鲜」与「池子多久没动」分开表达——
/// 见模板里「最后交易 / 读取于」两个标签的说明。
const fetchedAt = ref<string | null>(null)

const accountIndex = computed(() => String(route.params.accountIndex))

const navSeries = computed(() => {
  if (!detail.value || detail.value.navCurve.length === 0) {
    return []
  }

  return [
    {
      label: '份额净值',
      color: 'rgb(var(--primary-6))',
      values: detail.value.navCurve.map((point) => point.sharePrice),
    },
  ]
})

/// 挂单阶梯，**按市场分组**。
///
/// 必须分组：阶梯是"某个市场的买卖档位分布"，而池子可以在多个市场同时挂单。
/// 不分组时两个市场的买单会被混进同一列，价格还互相穿插——那不是"不好读"，
/// 是读出来错的。只有一个市场时（当前实测就是）视觉上与不分组完全一致。
///
/// 每组内：买单按价格降序、卖单按价格升序，都从最接近成交的一档向外排。
/// slice() 是必需的——直接 sort 会改到响应式数组本身，导致原始表格顺序被改写。
const laddersByMarket = computed(() => {
  const orders = detail.value?.orders ?? []
  const markets = [...new Set(orders.map((o) => o.marketIndex))].sort((a, b) => a - b)

  return markets.map((marketIndex) => {
    const inMarket = orders.filter((o) => o.marketIndex === marketIndex)

    return {
      marketIndex,
      symbol: inMarket[0]?.symbol ?? `market-${marketIndex}`,
      buys: inMarket.filter((o) => o.side === 'buy').slice().sort((a, b) => b.price - a.price),
      sells: inMarket.filter((o) => o.side === 'sell').slice().sort((a, b) => a.price - b.price),
    }
  })
})

/// 清算价的悬浮说明。幅度用绝对值 + 方向词表达，而不是带符号的百分数——
/// "需上涨 1427.9%" 比 "-1427.9%" 少一层心算。
function liquidationHint(record: PoolPosition): string {
  const distance = record.liquidationDistancePercent

  if (distance === null) {
    return '该仓位不存在正的清算价'
  }

  return `距清算 ${formatPercent(Math.abs(distance), 1)}（${distance >= 0 ? '需上涨' : '需下跌'}）`
}

/// 阶梯条长度用「全部挂单里的最大名义价值」归一。跨市场共用一把尺是有意的：
/// 只有一个基准，买卖两侧、各市场之间的条长才可以直接比。
const maxLadderNotional = computed(() => {
  const notionals = (detail.value?.orders ?? []).map((order) => order.remainingNotional)
  return notionals.length === 0 ? 1 : Math.max(...notionals)
})

/// 买 / 卖合计（跨市场汇总，作为卡片右上角的总览）。
const buyNotionalTotal = computed(() =>
  (detail.value?.orders ?? [])
    .filter((order) => order.side === 'buy')
    .reduce((sum, order) => sum + order.remainingNotional, 0),
)

const sellNotionalTotal = computed(() =>
  (detail.value?.orders ?? [])
    .filter((order) => order.side === 'sell')
    .reduce((sum, order) => sum + order.remainingNotional, 0),
)

/// 敞口比例的语气。阈值取自实测样本区间，超出即提示；不用「危险」这类断言措辞——
/// 全仓保证金下这个比例本身不是爆仓判据，真正的判据是清算缓冲。
///
/// ⚠️ 返回值必须是 **Arco Tag 的预设色名**（green / orange / red 那一套），
/// 不是 Alert 那套语义名（success / warning / danger）。传后者的后果是"静默失效"：
/// Arco 当成自定义色、套上 `arco-tag-custom-color`，最终渲染成**白字 + 近白底**，
/// 在亮色主题下几乎不可读。实测过：文字 rgb(255,255,255) / 背景 rgb(242,243,245)。
const exposureTone = computed(() => {
  const ratio = detail.value?.snapshot.exposureRatioPercent ?? 0

  if (ratio > 400) return 'red'
  if (ratio > 200) return 'orange'
  return 'green'
})

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = ''

  try {
    detail.value = await api.poolDetail(accountIndex.value)
    fetchedAt.value = new Date().toISOString()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    loading.value = false
  }
}

onMounted(load)
</script>

<template>
  <a-space direction="vertical" fill style="width: 100%">
    <a-alert v-if="errorMessage" type="error" :title="errorMessage" />

    <a-spin :loading="loading" style="width: 100%">
      <template v-if="detail">
        <a-card :bordered="false" style="margin-bottom: 14px">
          <template #title>
            <a-space>
              <span>{{ detail.summary.name || '（未命名）' }}</span>
              <!--
                这条直接回答"我能不能入金"。判定与官方同源：份额触到下限 → 平台限制入金
                （官方原文 "Deposits are limited because the operator share has reached its minimum limit."）。
                曾经这里写的是相反的「有余量／贴着下限」，且基于自造的 1.5 倍阈值，
                把本池最该警示的状态显示成了最乐观的文案。
              -->
              <a-tooltip
                :content="
                  detail.isDepositLimited
                    ? '运营者份额已触到下限，平台不再接受新入金（新入金会把运营者压到下限之下）。跟单不受影响。'
                    : `份额比下限高 ${formatNumber(detail.operatorShareHeadroomPercent, 2)} 个百分点，可正常入金。`
                "
              >
                <a-tag size="small" :color="detail.isDepositLimited ? 'red' : 'green'">
                  运营者份额 {{ formatPercent(detail.operatorSharePercent) }} / 下限
                  {{ formatPercent(detail.minOperatorSharePercent) }}
                  {{ detail.isDepositLimited ? '（入金已受限）' : '（可入金）' }}
                </a-tag>
              </a-tooltip>
            </a-space>
          </template>

          <template #extra>
            <a-button size="small" :loading="loading" @click="load">刷新</a-button>
          </template>

          <a-descriptions :column="4" size="small">
            <a-descriptions-item label="总资产">
              {{ formatUsd(detail.summary.totalAssetValue) }}
            </a-descriptions-item>
            <a-descriptions-item label="份额总量">
              {{ formatCompact(detail.summary.totalShares) }}
            </a-descriptions-item>
            <a-descriptions-item label="创建时间">
              {{ formatDate(detail.summary.createdAt) }}
            </a-descriptions-item>
            <a-descriptions-item label="净值采样">
              {{ detail.metrics.totalPeriods }} 点 / {{ detail.metrics.spanDays }} 天
            </a-descriptions-item>
          </a-descriptions>
        </a-card>

        <a-card :bordered="false" style="margin-bottom: 14px">
          <template #title>收益与风险：上报值 vs 本机实算</template>

          <a-alert
            :type="detail.metrics.isStatisticallyMeaningful ? 'info' : 'warning'"
            :show-icon="true"
            style="margin-bottom: 12px"
          >
            {{
              detail.metrics.isStatisticallyMeaningful
                ? '样本量足以做统计判断（不少于 30 个观测点）。'
                : '样本不足 30 个观测点，实算指标只能当参考，不能当结论。'
            }}
          </a-alert>

          <a-grid :cols="6" :col-gap="12" :row-gap="14">
            <a-grid-item>
              <MetricTile
                label="年化利率（上报）"
                :value="formatPercent(detail.metrics.reportedApyPercent)"
                note="上游点估计"
              />
            </a-grid-item>
            <a-grid-item>
              <MetricTile
                label="实算年化"
                :value="formatPercent(detail.metrics.realAnnualizedReturnPercent)"
                note="净值曲线实算"
                tone="primary"
              />
            </a-grid-item>
            <a-grid-item>
              <MetricTile
                label="期间累计收益"
                :value="formatPercent(detail.metrics.realTotalReturnPercent)"
                :note="`${detail.metrics.spanDays} 天`"
              />
            </a-grid-item>
            <a-grid-item>
              <MetricTile
                label="最大回撤"
                :value="formatPercent(detail.metrics.realMaxDrawdownPercent)"
                note="上游不提供此项"
                tone="danger"
              />
            </a-grid-item>
            <a-grid-item>
              <MetricTile
                label="夏普比率（实算）"
                :value="formatNumber(detail.metrics.realSharpe, 2)"
                :note="`夏普比率（上报）${formatNumber(detail.metrics.reportedSharpe, 2)}`"
              />
            </a-grid-item>
            <a-grid-item>
              <MetricTile
                label="期间波动率"
                :value="formatPercent(detail.metrics.periodVolatilityPercent)"
                :note="`亏损 ${detail.metrics.lossPeriods}/${detail.metrics.totalPeriods} 期`"
              />
            </a-grid-item>
          </a-grid>
        </a-card>

        <a-card :bordered="false" style="margin-bottom: 14px">
          <template #title>份额净值曲线</template>
          <template #extra>
            <span style="font-size: 12px; color: var(--color-text-3)">
              首点 {{ formatDateTime(detail.navCurve[0]?.at ?? null) }} · 末点
              {{ formatDateTime(detail.navCurve.at(-1)?.at ?? null) }}
            </span>
          </template>

          <LineChart v-if="navSeries.length > 0" :series="navSeries" :height="280" />
          <a-empty v-else description="没有净值数据" />
        </a-card>

        <a-card :bordered="false" style="margin-bottom: 14px">
          <template #title>
            <a-space>
              <span>敞口与保证金</span>
              <a-tag size="small" :color="exposureTone">
                敞口率 {{ formatPercent(detail.snapshot.exposureRatioPercent) }}
              </a-tag>
            </a-space>
          </template>
          <template #extra>
            <!--
              两个时间必须分开显示，此前合成一个「快照」是误导：
              · 最后交易 = 上游 transaction_time，即账户<b>最后一次变动</b>的时刻。
                池子不交易时它一动不动（实测该池停在同一时刻数小时）。
              · 读取于 = 本次请求返回的时刻。总资产、未实现盈亏、名义价值都是
                上游按<b>当前价实时重估</b>的，所以数值每次都会变、而上面那个时间戳不变。
              合在一起会让人读成"这是几小时前的数据"，而事实正相反。
            -->
            <span style="font-size: 12px; color: var(--color-text-3)">
              最后交易 {{ formatDateTime(detail.snapshot.takenAt) }}
              <template v-if="fetchedAt"> · 读取于 {{ formatDateTime(fetchedAt) }}</template>
            </span>
          </template>

          <a-descriptions :column="4" size="small">
            <a-descriptions-item label="抵押品">
              {{ formatUsd(detail.snapshot.collateral) }}
            </a-descriptions-item>
            <a-descriptions-item label="可用余额">
              {{ formatUsd(detail.snapshot.availableBalance) }}
            </a-descriptions-item>
            <a-descriptions-item label="总敞口（毛）">
              {{ formatUsd(detail.snapshot.grossExposure) }}
            </a-descriptions-item>
            <a-descriptions-item label="净敞口">
              {{ formatUsd(detail.snapshot.netExposure) }}
            </a-descriptions-item>
            <a-descriptions-item label="初始保证金要求">
              {{ formatUsd(detail.snapshot.initialMarginRequirement) }}
            </a-descriptions-item>
            <a-descriptions-item label="维持保证金要求">
              {{ formatUsd(detail.snapshot.maintenanceMarginRequirement) }}
            </a-descriptions-item>
            <a-descriptions-item label="清算缓冲">
              {{ formatUsd(detail.snapshot.liquidationBuffer) }}
            </a-descriptions-item>
            <a-descriptions-item label="多空失衡度">
              {{ formatNumber(detail.snapshot.imbalanceRatio, 3) }}
            </a-descriptions-item>
          </a-descriptions>

          <a-alert type="info" :show-icon="true" style="margin-top: 12px">
            池子走的是<b>全仓保证金</b>，所以持仓表里的「清算价」<b>不是该仓位自己的爆仓价</b>，
            而是<b>该仓位单独亏掉整个账户清算缓冲</b>所需的单价（多头靠跌、空头靠涨）。
            两者差别可能极大：本机实测某池缓冲远大于单仓规模，算出的空头清算价是当前价的十几倍。
            <br />
            显示为 <b>-</b> 表示<b>不存在正的清算价</b>：多头要亏掉缓冲所需的跌幅会把价格推到 0 以下。
            所以<b>多头多为 -、空头总有值</b>——上涨没有上限、下跌有下限，这个不对称是结构性的。
            括号内的数字是<b>距清算的幅度</b>（还要走多少），比那个远离市价的绝对价格更便于判断。真正的账户级风险看上面的<b>清算缓冲</b>。
          </a-alert>
        </a-card>

        <a-card :bordered="false" style="margin-bottom: 14px">
          <template #title>
            当前持仓
            <a-tag size="small" style="margin-left: 6px">{{ detail.positions.length }}</a-tag>
          </template>

          <a-table
            :data="detail.positions"
            row-key="marketId"
            size="small"
            :bordered="false"
            :pagination="false"
          >
            <template #columns>
              <a-table-column title="市场" :width="120">
                <template #cell="{ record }">
                  <SymbolName :symbol="record.symbol" />
                </template>
              </a-table-column>
              <a-table-column title="方向" :width="70">
                <template #cell="{ record }">
                  <DirectionMark :side="record.side" />
                </template>
              </a-table-column>
              <a-table-column title="数量" :width="120" align="right">
                <template #cell="{ record }">{{ formatNumber(record.size, 4) }}</template>
              </a-table-column>
              <a-table-column title="开仓均价" :width="120" align="right">
                <template #cell="{ record }">{{ formatNumber(record.avgEntryPrice, 6) }}</template>
              </a-table-column>
              <a-table-column title="名义价值" :width="130" align="right">
                <template #cell="{ record }">{{ formatUsd(record.notional) }}</template>
              </a-table-column>
              <a-table-column title="未实现盈亏" :width="155" align="right">
                <template #title>
                  <HeaderHint
                    label="未实现盈亏"
                    hint="括号内为对保证金的收益率（未实现盈亏 ÷ 保证金），与 Lighter 官方前端同口径。注意不是对名义价值的收益率——两者相差一个杠杆倍数。"
                  />
                </template>
                <template #cell="{ record }">
                  <span
                    :style="{
                      color:
                        record.unrealizedPnl >= 0 ? 'rgb(var(--success-6))' : 'rgb(var(--danger-6))',
                    }"
                  >
                    {{ formatUsd(record.unrealizedPnl) }}
                    <template v-if="record.unrealizedPnlPercent !== null">
                      ({{ formatPercent(record.unrealizedPnlPercent) }})
                    </template>
                  </span>
                </template>
              </a-table-column>
              <a-table-column title="杠杆" :width="90" align="right">
                <template #cell="{ record }">
                  <a-tooltip
                    v-if="record.leverage !== null"
                    :content="`保证金率 ${(100 / record.leverage).toFixed(2)}%`"
                  >
                    <a-tag size="small">{{ formatNumber(record.leverage, 2) }}x</a-tag>
                  </a-tooltip>
                  <a-tooltip v-else content="上游此刻未给出保证金率（该仓位正在变动中）">
                    <span style="color: var(--color-text-3)">未设定</span>
                  </a-tooltip>
                </template>
              </a-table-column>
              <a-table-column title="资金费累计" :width="130" align="right">
                <template #cell="{ record }">{{ formatUsd(record.cumulativeFunding) }}</template>
              </a-table-column>
              <a-table-column title="清算价" :width="128" align="right">
                <template #title>
                  <HeaderHint
                    label="清算价"
                    hint="全仓口径：该仓位单独亏掉整个账户清算缓冲所需的单价（多头靠跌、空头靠涨），不是它自己的爆仓价，也与杠杆无关——爆仓由维持保证金触发。括号内是距清算的幅度（空头为正需上涨、多头为负需下跌）。显示为 - 表示不存在正的清算价。"
                  />
                </template>
                <template #cell="{ record }">
                  <!-- 无清算价时用 - 占位：全仓保证金下该字段不适用。
                       不能显示 0——那会被读成"价格跌到 0 才强平"这种荒谬结论。 -->
                  <span v-if="record.liquidationPrice === null" style="color: var(--color-text-3)">
                    -
                  </span>
                  <!-- 幅度放进悬浮：内联会让本列宽出一大截，而这一列多数行是 "-"，
                       为一个次要信息把整列撑宽不划算。cursor:help 提示可悬浮。 -->
                  <a-tooltip v-else :content="liquidationHint(record as PoolPosition)">
                    <span style="font-family: monospace; cursor: help">
                      {{ formatNumber(record.liquidationPrice, 6) }}
                    </span>
                  </a-tooltip>
                </template>
              </a-table-column>
            </template>

            <template #empty>
              <a-empty description="当前空仓" />
            </template>
          </a-table>
        </a-card>

        <a-card :bordered="false" style="margin-bottom: 14px">
          <template #title>
            挂单阶梯
            <a-tag size="small" style="margin-left: 6px">{{ detail.orders.length }}</a-tag>
          </template>
          <template #extra>
            <span style="font-size: 12px; color: var(--color-text-3)">
              买 {{ formatUsd(buyNotionalTotal) }} / 卖 {{ formatUsd(sellNotionalTotal) }}
            </span>
          </template>

          <!-- 按市场分组渲染。只有一个市场时（当前实测如此）与不分组视觉一致；
               多个市场时不会把两个市场的买单混进同一列。 -->
          <div v-if="detail.orders.length > 0">
            <div
              v-for="(ladder, index) in laddersByMarket"
              :key="ladder.marketIndex"
              :style="{ marginTop: index === 0 ? '0' : '20px' }"
            >
              <div
                v-if="laddersByMarket.length > 1"
                style="font-size: 13px; font-weight: 500; margin-bottom: 10px"
              >
                <SymbolName :symbol="ladder.symbol" compact />
              </div>

              <a-grid :cols="2" :col-gap="28">
                <a-grid-item>
                  <div style="font-size: 12px; color: var(--color-text-3); margin-bottom: 8px">
                    买单阶梯（价格降序）
                  </div>
                  <div v-for="order in ladder.buys" :key="order.orderIndex" style="margin-bottom: 9px">
                    <div style="display: flex; justify-content: space-between; font-size: 12px">
                      <span style="font-family: monospace">{{ formatNumber(order.price, 6) }}</span>
                      <span style="color: var(--color-text-3)">
                        {{ formatUsd(order.remainingNotional) }} ·
                        {{ formatNumber(order.remainingSize, 2) }}
                        <a-tag v-if="order.isPartiallyFilled" size="small" color="orange">部分成交</a-tag>
                      </span>
                    </div>
                    <div style="height: 6px; background: var(--color-fill-2); border-radius: 3px; margin-top: 3px">
                      <div
                        :style="{
                          width: `${(order.remainingNotional / maxLadderNotional) * 100}%`,
                          height: '6px',
                          background: 'rgb(var(--success-6))',
                          borderRadius: '3px',
                        }"
                      />
                    </div>
                  </div>
                  <a-empty v-if="ladder.buys.length === 0" description="无买单" />
                </a-grid-item>

                <a-grid-item>
                  <div style="font-size: 12px; color: var(--color-text-3); margin-bottom: 8px">
                    卖单阶梯（价格升序）
                  </div>
                  <div v-for="order in ladder.sells" :key="order.orderIndex" style="margin-bottom: 9px">
                    <div style="display: flex; justify-content: space-between; font-size: 12px">
                      <span style="font-family: monospace">{{ formatNumber(order.price, 6) }}</span>
                      <span style="color: var(--color-text-3)">
                        {{ formatUsd(order.remainingNotional) }} ·
                        {{ formatNumber(order.remainingSize, 2) }}
                        <a-tag v-if="order.isPartiallyFilled" size="small" color="orange">部分成交</a-tag>
                      </span>
                    </div>
                    <div style="height: 6px; background: var(--color-fill-2); border-radius: 3px; margin-top: 3px">
                      <div
                        :style="{
                          width: `${(order.remainingNotional / maxLadderNotional) * 100}%`,
                          height: '6px',
                          background: 'rgb(var(--danger-6))',
                          borderRadius: '3px',
                        }"
                      />
                    </div>
                  </div>
                  <a-empty v-if="ladder.sells.length === 0" description="无卖单" />
                </a-grid-item>
              </a-grid>
            </div>
          </div>

          <a-empty v-else description="当前无挂单" />

          <a-table
            v-if="detail.orders.length > 0"
            :data="detail.orders"
            row-key="orderIndex"
            size="small"
            :bordered="false"
            style="margin-top: 16px"
            :pagination="{ pageSize: 10, size: 'small' }"
          >
            <template #columns>
              <!-- 表头此前写作 "#"，容易被读成行号 1/2/3；实际是上游的订单 ID（17 位）。
                   实测该值到 1.3e16，远超 int32，后端用 long 承载。 -->
              <a-table-column title="市场" :width="120">
                <template #cell="{ record }">
                  <SymbolName :symbol="record.symbol" />
                </template>
              </a-table-column>
              <a-table-column title="订单 ID" :width="150">
                <template #title>
                  <HeaderHint
                    label="订单 ID"
                    hint="上游的 order_index，交易所侧的唯一订单标识（实测为 17 位数字，非行号）。同一订单被多次播报时按它去重。"
                  />
                </template>
                <template #cell="{ record }">
                  <span style="font-family: monospace; font-size: 12px">{{ record.orderIndex }}</span>
                </template>
              </a-table-column>
              <a-table-column title="方向" :width="70">
                <template #cell="{ record }">
                  <a-tag size="small" :color="record.side === 'buy' ? 'green' : 'red'">
                    {{ record.side === 'buy' ? '买' : '卖' }}
                  </a-tag>
                </template>
              </a-table-column>
              <a-table-column title="价格" :width="120" align="right">
                <template #cell="{ record }">{{ formatNumber(record.price, 6) }}</template>
              </a-table-column>
              <a-table-column title="剩余 / 原始" :width="170" align="right">
                <template #cell="{ record }">
                  {{ formatNumber(record.remainingSize, 2) }} /
                  {{ formatNumber(record.initialSize, 2) }}
                </template>
              </a-table-column>
              <a-table-column title="剩余名义价值" :width="130" align="right">
                <template #cell="{ record }">{{ formatUsd(record.remainingNotional) }}</template>
              </a-table-column>
              <a-table-column title="下单时间" :width="160">
                <template #cell="{ record }">{{ formatDateTime(record.createdAt) }}</template>
              </a-table-column>
              <a-table-column title="过期时间" :width="160">
                <template #cell="{ record }">{{ formatDateTime(record.expiresAt) }}</template>
              </a-table-column>
            </template>
          </a-table>
        </a-card>

        <a-card :bordered="false" style="margin-bottom: 14px">
          <template #title>成交构成（近端快照）</template>

          <a-descriptions :column="4" size="small">
            <a-descriptions-item label="成交笔数">
              {{ detail.tradeComposition.tradeCount }}
            </a-descriptions-item>
            <a-descriptions-item label="挂单占比">
              {{ formatPercent(detail.tradeComposition.makerSharePercent) }}
            </a-descriptions-item>
            <a-descriptions-item label="挂单 / 吃单">
              {{ detail.tradeComposition.makerCount }} / {{ detail.tradeComposition.takerCount }}
            </a-descriptions-item>
            <a-descriptions-item label="单笔名义价值中位数">
              {{ formatUsd(detail.tradeComposition.medianTradeNotional) }}
            </a-descriptions-item>
            <a-descriptions-item label="买入名义价值">
              {{ formatUsd(detail.tradeComposition.boughtNotional) }}
            </a-descriptions-item>
            <a-descriptions-item label="卖出名义价值">
              {{ formatUsd(detail.tradeComposition.soldNotional) }}
            </a-descriptions-item>
          </a-descriptions>

          <a-alert type="warning" :show-icon="true" style="margin-top: 12px">
            这是 <b>WebSocket 近端快照</b>的构成，不是历史全量，挂单占比会随取到的时间窗波动
            （实测同一池一次拿到 50 笔全是吃单、另一次只有 2 笔）。要看稳定构成得靠长期落库——
            该池有带单员时，服务会持续记录它的成交流水，跟单页的「执行成本测量」即由此累积
            （在「带单员」页可以为一相中的池开启持续观察）。
          </a-alert>
        </a-card>

        <a-card :bordered="false">
          <template #title>
            单轮盈亏重建
            <a-tag size="small" style="margin-left: 6px">{{ detail.roundTrips.length }}</a-tag>
          </template>
          <template #extra>
            <span style="font-size: 12px; color: var(--color-text-3)">由持仓差分重建，上游不提供</span>
          </template>

          <a-table
            :data="detail.roundTrips"
            row-key="openedAt"
            size="small"
            :bordered="false"
            :pagination="{ pageSize: 10, size: 'small' }"
          >
            <template #columns>
              <!-- 绑 symbol 而不是 marketId：编号 45/60 对人不具可读性，
                   符号由合约规格表解析（含池子已平掉的历史市场） -->
              <a-table-column title="市场" :width="120">
                <template #cell="{ record }">
                  <SymbolName :symbol="record.symbol" />
                </template>
              </a-table-column>
              <a-table-column title="方向" :width="70">
                <template #cell="{ record }">
                  <DirectionMark :side="record.side" />
                </template>
              </a-table-column>
              <a-table-column title="入场量" :width="115" align="right">
                <template #cell="{ record }">{{ formatNumber(record.entrySize, 4) }}</template>
              </a-table-column>
              <a-table-column title="入场均价" :width="115" align="right">
                <template #cell="{ record }">{{ formatNumber(record.entryVwap, 6) }}</template>
              </a-table-column>
              <a-table-column title="出场均价" :width="115" align="right">
                <template #cell="{ record }">
                  <span v-if="record.exitVwap === 0" style="color: var(--color-text-3)">持仓中</span>
                  <span v-else>{{ formatNumber(record.exitVwap, 6) }}</span>
                </template>
              </a-table-column>
              <a-table-column title="毛盈亏" :width="120" align="right">
                <template #cell="{ record }">
                  <span
                    :style="{
                      color: record.grossPnl >= 0 ? 'rgb(var(--success-6))' : 'rgb(var(--danger-6))',
                    }"
                  >
                    {{ formatUsd(record.grossPnl) }}
                  </span>
                </template>
              </a-table-column>
              <a-table-column title="收益率" :width="110" align="right">
                <template #cell="{ record }">{{ formatPercent(record.returnPercent) }}</template>
              </a-table-column>
              <a-table-column title="入场方式" :width="95" align="center">
                <template #cell="{ record }">
                  <a-tag size="small" :color="record.entryRole === 'maker' ? 'arcoblue' : 'gray'">
                    {{ record.entryRole === 'maker' ? '挂单' : '吃单' }}
                  </a-tag>
                </template>
              </a-table-column>
              <a-table-column title="出场方式" :width="95" align="center">
                <template #cell="{ record }">
                  <a-tag size="small" :color="record.exitRole === 'maker' ? 'arcoblue' : 'gray'">
                    {{ record.exitRole === 'maker' ? '挂单' : '吃单' }}
                  </a-tag>
                </template>
              </a-table-column>
              <a-table-column title="持有(分)" :width="95" align="right">
                <template #cell="{ record }">{{ formatNumber(record.holdingMinutes, 0) }}</template>
              </a-table-column>
              <a-table-column title="状态" :width="90" align="center">
                <template #cell="{ record }">
                  <a-tag size="small" :color="record.isClosed ? 'green' : 'orange'">
                    {{ record.isClosed ? '已平' : '持仓中' }}
                  </a-tag>
                </template>
              </a-table-column>
            </template>

            <template #empty>
              <a-empty description="这段时间窗内没有观测到完整单轮" />
            </template>
          </a-table>
        </a-card>
      </template>
    </a-spin>
  </a-space>
</template>
