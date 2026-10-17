<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'

import { api, type LeadTrader, type PoolDetail } from '@/api/client'
import LeadDetailContent from '@/components/LeadDetailContent.vue'
import { usePermission } from '@/composables/usePermission'
import type { LeadDetailViewData } from '@/domain/leadDetail'
import { formatDateTime, formatSince } from '@/utils/format'

/// 带单员详情 —— 管理动作、取数与页面外壳。
///
/// 指标、持仓、挂单与收益曲线统一由 `LeadDetailContent` 渲染，与公开带单广场共用
/// 同一套术语和列定义；本页只额外提供配置状态、暂停/恢复/停止观察和完整池详情入口。
///
/// 两个请求分开取：带单员配置（leads view）与池详情（pools view）。
/// 池详情取不到时仍然显示带单员配置与动作，不把整页判死。
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

/// 工作台完整池详情 → 共享展示模型。
///
/// 已实现盈亏由已平仓轮次按市场汇总；挂单把池详情的 `marketIndex` 对到持仓的
/// `marketId`，之后表格的展开区只按市场分组。
const content = computed<LeadDetailViewData | null>(() => {
  if (pool.value === null || lead.value === null) return null

  const gross = pool.value.snapshot.grossExposure
  const realizedByMarket = new Map<number, number>()

  for (const trip of pool.value.roundTrips) {
    if (!trip.isClosed) continue

    realizedByMarket.set(trip.marketId, (realizedByMarket.get(trip.marketId) ?? 0) + trip.grossPnl)
  }

  return {
    totalAssetValue: pool.value.summary.totalAssetValue,
    operatorFeePercent: pool.value.summary.operatorFeePercent,
    operatorSharePercent: pool.value.operatorSharePercent,
    minOperatorSharePercent: pool.value.minOperatorSharePercent,
    isDepositLimited: pool.value.isDepositLimited,
    metrics: pool.value.metrics,
    exposure: {
      grossExposure: pool.value.snapshot.grossExposure,
      netExposure: pool.value.snapshot.netExposure,
      exposureRatioPercent: pool.value.snapshot.exposureRatioPercent,
      liquidationBuffer: pool.value.snapshot.liquidationBuffer,
      activePositionCount: pool.value.snapshot.activePositionCount,
    },
    positions: pool.value.positions.map((position) => ({
      marketId: position.marketId,
      symbol: position.symbol,
      side: position.side,
      avgEntryPrice: position.avgEntryPrice,
      markPrice: position.markPrice,
      liquidationPrice: position.liquidationPrice,
      notional: Math.abs(position.notional),
      weightPercent: gross <= 0 ? 0 : (Math.abs(position.notional) / gross) * 100,
      realizedPnl: realizedByMarket.get(position.marketId) ?? 0,
      unrealizedPnl: position.unrealizedPnl,
      unrealizedPnlPercent: position.unrealizedPnlPercent,
    })),
    orders: pool.value.orders.map((order) => ({
      marketId: order.marketIndex,
      orderIndex: order.orderIndex,
      side: order.side,
      price: order.price,
      remainingSize: order.remainingSize,
      remainingNotional: order.remainingNotional,
      isPartiallyFilled: order.isPartiallyFilled,
    })),
    navCurve: pool.value.navCurve,
    followerCount: lead.value.followerCount,
    signalCount: lead.value.signalCount,
    trackingHint: `本机自 ${formatDateTime(lead.value.createdAt)} 开始跟踪`,
  }
})

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
          <RouterLink :to="{ name: 'leads' }" class="back-link">← 带单管理</RouterLink>
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
            :title="canEdit ? '' : '需要「带单管理」的管理权限（编辑）'"
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
              :title="canDelete ? '' : '需要「带单管理」的管理权限（删除）'"
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

    <a-card v-if="content" :bordered="false">
      <LeadDetailContent :data="content" />
    </a-card>
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
</style>
