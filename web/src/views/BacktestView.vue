<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'

import { api, type BacktestResult, type PoolSummary } from '@/api/client'
import LineChart from '@/components/LineChart.vue'
import MetricTile from '@/components/MetricTile.vue'
import { filterPoolOption, poolOptionLabel } from '@/utils/poolOptions'
import { formatNumber, formatPercent, formatUsd } from '@/utils/format'

/// 回测面板。
///
/// 三条曲线对照，回答「跟单到底值不值」：
/// <list type="bullet">
///   <item>池净值 —— 池子本身的业绩，基准线</item>
///   <item>跟单净值 —— 池净值减去跟单摩擦（每轮往返成本 × 交易频次）</item>
///   <item>直接入池净值 —— 池净值扣掉运营费分成，也就是「不跟单，直接把钱存进池」</item>
/// </list>
///
/// 诚实声明：上游**不提供历史成交明细**（实测交易频道只能取到近端快照），
/// 所以跟单摩擦是参数化的情景假设，不是历史回放。这也是这个页面最重要的标注。
const route = useRoute()

const accountIndex = ref(String(route.query.pool ?? '281474976552443'))
const roundTripCostBps = ref(5)
const tradesPerDay = ref(2)
const result = ref<BacktestResult | null>(null)
const loading = ref(false)
const errorMessage = ref('')

const pools = ref<PoolSummary[]>([])
const poolsLoading = ref(false)

const series = computed(() => {
  if (!result.value) return []

  return [
    {
      label: '池净值',
      color: 'var(--color-text-3)',
      values: result.value.curve.map((p) => p.poolNav),
    },
    {
      label: '跟单净值（扣摩擦）',
      color: 'rgb(var(--primary-6))',
      values: result.value.curve.map((p) => p.copyNav),
    },
    {
      label: '直接入池（扣运营费）',
      color: 'rgb(var(--success-6))',
      values: result.value.curve.map((p) => p.depositNav),
    },
  ]
})

/// 跟单相对直接入池的超额。这是最终结论：为正才值得跟。
const copyOverDeposit = computed(() => {
  if (!result.value) return 0
  return result.value.copyTotalReturnPercent - result.value.depositTotalReturnPercent
})

/// 跟单摩擦把池业绩吃掉了多少个百分点。
const totalDrag = computed(() => {
  if (!result.value) return 0
  return result.value.poolTotalReturnPercent - result.value.copyTotalReturnPercent
})

const verdict = computed(() => {
  if (!result.value) return null

  // 字段名用 color 而不是 type，值用 Arco Tag 的预设色名——
  // 叫 type 会诱导填 success/warning/danger，那套名字在 Tag 上会静默失效成白字白底
  if (copyOverDeposit.value > 1) {
    return { color: 'green' as const, text: '跟单优于直接入池' }
  }

  if (copyOverDeposit.value > -1) {
    return { color: 'orange' as const, text: '跟单与直接入池基本持平' }
  }

  return { color: 'red' as const, text: '跟单不如直接入池' }
})

async function loadPools(): Promise<void> {
  poolsLoading.value = true

  try {
    pools.value = await api.listPools('user')
  } catch {
    // 下拉框是便利功能，失败不阻塞主流程
  } finally {
    poolsLoading.value = false
  }
}

async function run(): Promise<void> {
  loading.value = true
  errorMessage.value = ''

  try {
    result.value = await api.backtest(accountIndex.value, roundTripCostBps.value, tradesPerDay.value)
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
    result.value = null
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  await loadPools()
  await run()
})
</script>

<template>
  <a-space direction="vertical" fill style="width: 100%">
    <a-alert type="warning" :show-icon="true">
      <template #title>这是情景推演，不是历史回放</template>
      <div style="font-size: 13px; line-height: 1.8">
        上游不提供历史成交明细（交易频道只能取到近端快照），所以跟单摩擦无法从真实成交回放得出，
        只能按你给的<b>每轮往返成本</b>与<b>日均交易次数</b>参数化推演。
        池净值曲线本身是真实的（来自上游净值序列），扣减项是假设。
        真实漏损要靠跟单页的「执行成本测量」长期累积实测。
      </div>
    </a-alert>

    <a-card :bordered="false">
      <template #title>参数</template>

      <a-space wrap :size="16">
        <a-select
          v-model="accountIndex"
          :loading="poolsLoading"
          :style="{ width: '440px' }"
          placeholder="选择池（可按名称或账户索引搜索）"
          allow-search
          :filter-option="filterPoolOption"
        >
          <a-option
            v-for="p in pools"
            :key="p.accountIndex"
            :value="String(p.accountIndex)"
            :label="poolOptionLabel(p)"
          >
            {{ poolOptionLabel(p) }} · {{ formatUsd(p.totalAssetValue) }}
          </a-option>
        </a-select>

        <a-input-number v-model="roundTripCostBps" :min="0" :step="1" :style="{ width: '190px' }">
          <template #prefix>往返成本</template>
          <template #suffix>bps</template>
        </a-input-number>

        <a-input-number v-model="tradesPerDay" :min="0" :step="0.5" :style="{ width: '190px' }">
          <template #prefix>日均交易</template>
          <template #suffix>次/天</template>
        </a-input-number>

        <a-button type="primary" :loading="loading" @click="run">运行</a-button>
      </a-space>

      <a-alert v-if="errorMessage" type="error" :title="errorMessage" style="margin-top: 12px" />

      <div style="margin-top: 10px; font-size: 12px; color: var(--color-text-3)">
        往返成本参考：Lighter 标准档挂单与吃单均为 0 手续费，所以这一项填的是<b>点差 + 滑点</b>，
        不是手续费。实测深盘一个往返约 2.7 bps，薄盘 12~26 bps。
      </div>
    </a-card>

    <template v-if="result">
      <a-card :bordered="false">
        <template #title>
          <a-space>
            <span>{{ result.poolName || '（未命名）' }}</span>
            <a-tag v-if="verdict" size="small" :color="verdict.color">{{ verdict.text }}</a-tag>
          </a-space>
        </template>
        <template #extra>
          <span style="font-size: 12px; color: var(--color-text-3)">
            样本 {{ result.sampleDays }} 天 · 运营费 {{ formatPercent(result.operatorFeePercent) }} ·
            往返成本 {{ formatNumber(result.assumedRoundTripCostBps, 1) }} bps
          </span>
        </template>

        <a-grid :cols="4" :col-gap="12" :row-gap="14">
          <a-grid-item>
            <MetricTile
              label="池净值累计收益"
              :value="formatPercent(result.poolTotalReturnPercent)"
              note="基准"
            />
          </a-grid-item>
          <a-grid-item>
            <MetricTile
              label="跟单累计收益"
              :value="formatPercent(result.copyTotalReturnPercent)"
              :note="`被摩擦吃掉 ${formatNumber(totalDrag, 2)} 个百分点`"
              tone="primary"
            />
          </a-grid-item>
          <a-grid-item>
            <MetricTile
              label="直接入池累计收益"
              :value="formatPercent(result.depositTotalReturnPercent)"
              note="扣运营费分成"
              tone="success"
            />
          </a-grid-item>
          <a-grid-item>
            <MetricTile
              label="跟单超额"
              :value="formatPercent(copyOverDeposit)"
              note="跟单 − 直接入池"
              :tone="copyOverDeposit >= 0 ? 'success' : 'danger'"
            />
          </a-grid-item>
        </a-grid>
      </a-card>

      <a-card :bordered="false">
        <template #title>三方净值曲线</template>
        <template #extra>
          <span style="font-size: 12px; color: var(--color-text-3)">起点归一为 1.0</span>
        </template>

        <LineChart v-if="series.length > 0" :series="series" :baseline="1" :height="340" />
        <a-empty v-else description="没有曲线数据" />
      </a-card>

      <a-card :bordered="false">
        <a-alert type="info" :show-icon="true">
          <template #title>怎么读这三条线</template>
          <div style="font-size: 13px; line-height: 1.9">
            <div>
              <b>跟单净值</b>与<b>直接入池净值</b>的差距就是跟单的全部意义所在。跟单多承担了执行风险与摩擦，
              换来的是流动性（随时可退）与不被运营者分成锁定的自由度；直接入池则被动接受运营者抽成。
            </div>
            <div>
              把「日均交易」调到池子的实际频次，曲线才有参考意义。频次越高，摩擦累积越快——
              这是跟单最容易低估的一项成本。
            </div>
          </div>
        </a-alert>
      </a-card>
    </template>
  </a-space>
</template>
