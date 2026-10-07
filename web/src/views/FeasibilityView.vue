<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'

import { api, type PoolSummary, type SizeFeasibility } from '@/api/client'
import { filterPoolOption, poolOptionLabel } from '@/utils/poolOptions'
import { formatNumber, formatPercent, formatUsd } from '@/utils/format'

/// 规模门槛计算器。
///
/// 这一页回答的是跟单最实际的问题：**这点本金跟得动吗**。
///
/// 关键机制：跟单要求按池的敞口比例等比缩放仓位，缩放后得到「理想跟单名义价值」。
/// 若它低于市场的最小下单额，你有两个选择——放弃，或者硬凑到最小单，
/// 后者会让你的单笔风险相对池子被放大若干倍。放大倍数就是这一页的核心输出。
const route = useRoute()

const accountIndex = ref(String(route.query.pool ?? '281474976552443'))
const capital = ref(200)
const results = ref<SizeFeasibility[]>([])
const poolName = ref('')
const loading = ref(false)
const errorMessage = ref('')

/// 把候选池填进下拉框，避免手敲二十位账户索引。
const pools = ref<PoolSummary[]>([])
const poolsLoading = ref(false)

const blocking = computed(() => results.value.filter((r) => !r.canPlaceMinimumOrder))

/// 达到最小下单额所需本金的最大值——也就是"要跟满这个池的所有市场"的门槛。
const requiredCapital = computed(() => {
  if (results.value.length === 0) return 0
  return Math.max(...results.value.map((r) => r.requiredCapitalForMinOrder))
})

/// 当前本金下最宽松的市场需要多少本金（决定"至少能跟一个市场"的门槛）。
const minimumViableCapital = computed(() => {
  if (results.value.length === 0) return 0
  return Math.min(...results.value.map((r) => r.requiredCapitalForMinOrder))
})

async function loadPools(): Promise<void> {
  poolsLoading.value = true

  try {
    pools.value = await api.listPools('user')
  } catch {
    // 下拉框是便利功能，失败不该阻塞主流程
  } finally {
    poolsLoading.value = false
  }
}

async function evaluate(): Promise<void> {
  loading.value = true
  errorMessage.value = ''

  try {
    results.value = await api.feasibility(accountIndex.value, capital.value)
    poolName.value = pools.value.find((p) => String(p.accountIndex) === accountIndex.value)?.name ?? ''
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
    results.value = []
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  await loadPools()
  await evaluate()
})
</script>

<template>
  <a-space direction="vertical" fill style="width: 100%">
    <a-card :bordered="false">
      <template #title>规模门槛</template>

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

        <a-input-number v-model="capital" :min="1" :step="100" :style="{ width: '150px' }">
          <template #prefix>本金</template>
          <template #suffix>USDC</template>
        </a-input-number>

        <a-button type="primary" :loading="loading" @click="evaluate">计算</a-button>
      </a-space>

      <a-alert v-if="errorMessage" type="error" :title="errorMessage" style="margin-top: 12px" />
    </a-card>

    <template v-if="results.length > 0">
      <a-card :bordered="false">
        <a-alert :type="blocking.length === 0 ? 'success' : 'warning'" :show-icon="true">
          <template #title>
            {{
              blocking.length === 0
                ? `本金 ${formatUsd(capital)} 足以跟满全部 ${results.length} 个市场`
                : `本金 ${formatUsd(capital)} 跟不动 ${blocking.length}/${results.length} 个市场`
            }}
          </template>
          <div style="font-size: 13px; line-height: 1.9">
            <div>
              <b>跟满全部市场</b>需要本金
              <b>{{ formatUsd(requiredCapital) }}</b>（取各市场门槛的最大值）。
            </div>
            <div>
              <b>至少跟一个市场</b>需要本金
              <b>{{ formatUsd(minimumViableCapital) }}</b>（取各市场门槛的最小值）。
            </div>
            <div v-if="poolName" style="color: var(--color-text-3)">
              参考池：{{ poolName }} · {{ accountIndex }}
            </div>
          </div>
        </a-alert>
      </a-card>

      <a-card :bordered="false">
        <template #title>逐市场门槛</template>
        <template #extra>
          <span style="font-size: 12px; color: var(--color-text-3)">
            放大倍数 &gt; 1 表示硬凑最小单会让你的单笔风险高于池子
          </span>
        </template>

        <a-table
          :data="results"
          row-key="marketId"
          size="small"
          :bordered="false"
          :pagination="false"
        >
          <template #columns>
            <a-table-column title="市场" :width="110">
              <template #cell="{ record }">
                {{ record.symbol }}
                <span style="color: var(--color-text-3); font-size: 11px">#{{ record.marketId }}</span>
              </template>
            </a-table-column>

            <a-table-column title="池敞口占比" :width="120" align="right">
              <template #cell="{ record }">
                {{ formatPercent(record.poolExposureRatioPercent) }}
              </template>
            </a-table-column>

            <a-table-column title="理想跟单名义价值" :width="165" align="right">
              <template #cell="{ record }">{{ formatUsd(record.idealFollowNotional) }}</template>
            </a-table-column>

            <a-table-column title="市场最小下单额" :width="145" align="right">
              <template #cell="{ record }">{{ formatUsd(record.minOrderNotional) }}</template>
            </a-table-column>

            <a-table-column title="能否下单" :width="110" align="center">
              <template #cell="{ record }">
                <a-tag size="small" :color="record.canPlaceMinimumOrder ? 'green' : 'red'">
                  {{ record.canPlaceMinimumOrder ? '可以' : '不够' }}
                </a-tag>
              </template>
            </a-table-column>

            <a-table-column title="所需本金" :width="135" align="right">
              <template #cell="{ record }">
                <b>{{ formatUsd(record.requiredCapitalForMinOrder) }}</b>
              </template>
            </a-table-column>

            <a-table-column title="敞口放大倍数" :width="140" align="right">
              <template #cell="{ record }">
                <span
                  :style="{
                    color:
                      record.amplificationFactor > 3
                        ? 'rgb(var(--danger-6))'
                        : record.amplificationFactor > 1
                          ? 'rgb(var(--warning-6))'
                          : 'rgb(var(--success-6))',
                  }"
                >
                  {{ formatNumber(record.amplificationFactor, 2) }}x
                </span>
              </template>
            </a-table-column>
          </template>

          <template #empty>
            <a-empty description="该池当前没有需要跟的持仓" />
          </template>
        </a-table>
      </a-card>

      <a-card :bordered="false">
        <a-alert type="info" :show-icon="true">
          <template #title>这个门槛是怎么算出来的</template>
          <div style="font-size: 13px; line-height: 1.9">
            <div>
              1. 理想跟单名义价值 = 你的本金 × 该市场的池敞口占比。敞口占比 = 池在该市场的名义价值 / 池的总资产。
            </div>
            <div>
              2. 市场最小下单额 = max（最小下单金额, 最小下单数量 × 当前价）。两者取大，因为交易所两个约束同时生效。
            </div>
            <div>
              3. 所需本金 = 最小下单额 / 敞口占比。这就是「想等比跟上，本金至少得有多少」。
            </div>
            <div>
              4. 放大倍数 = 最小下单额 / 理想跟单名义价值。它衡量的是：本金不够时硬凑最小单，
              你的仓位相对池子被放大了多少倍，单笔风险同比例放大。
            </div>
          </div>
        </a-alert>
      </a-card>
    </template>
  </a-space>
</template>
