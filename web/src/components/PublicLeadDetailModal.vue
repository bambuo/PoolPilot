<script setup lang="ts">
import { computed } from 'vue'

import type { PublicLeadDetail, PublicLeadTrader } from '@/api/public'
import LeadDetailContent from '@/components/LeadDetailContent.vue'
import { useLeadDetail } from '@/composables/usePublicStream'
import type { LeadDetailViewData } from '@/domain/leadDetail'
import { formatLocalTime, formatSince } from '@/utils/format'

/// 公开带单员详情：带单广场点卡片打开。
///
/// 数据来自 `GET /api/public/stream?lead=<accountIndex>`，只认 SSE 帧，不回退到
/// 单次 REST 取数；首帧到达前用列表卡片的摘要铺标题、状态与仓位变动数。
///
/// 指标、持仓、挂单与收益曲线由 `LeadDetailContent` 渲染，与工作台详情共用同一套
/// 术语和列定义。公开面不提供暂停/停止操作，也不显示完整池详情入口。
const visible = defineModel<boolean>('visible', { required: true })

const props = defineProps<{
  /// null = 未选中；打开时由父层赋值，切换池会重新订阅。
  accountIndex: number | null
  /// 列表里这个带单员的实时摘要：首帧到达前用它铺标题/状态/信号数。
  summary: PublicLeadTrader | null
}>()

const { lead: liveLead, status: streamStatus, errorMessage, lastUpdatedAt } =
  useLeadDetail(() => (visible.value ? props.accountIndex : null))

const lead = computed<PublicLeadDetail | null>(() => liveLead.value)

const displayName = computed(() => {
  if (lead.value !== null) return lead.value.name || `带单员 #${lead.value.accountIndex}`
  if (props.summary !== null) return props.summary.name || `带单员 #${props.summary.accountIndex}`
  return props.accountIndex === null ? '带单员详情' : `带单员 #${props.accountIndex}`
})

const isWatching = computed(() => lead.value?.isWatching ?? props.summary?.isWatching ?? false)
const followerCount = computed(() => lead.value?.followerCount ?? props.summary?.followerCount ?? 0)
const signalCount = computed(() => lead.value?.signalCount ?? props.summary?.signalCount ?? 0)
const lastSignalAt = computed(() => lead.value?.lastSignalAt ?? props.summary?.lastSignalAt ?? null)

/// 公开 DTO 直接映射成共享展示模型；字段形状与工作台适配后的模型一致。
const content = computed<LeadDetailViewData | null>(() => {
  const detail = lead.value

  if (detail === null) return null

  return {
    totalAssetValue: detail.totalAssetValue,
    operatorFeePercent: detail.operatorFeePercent,
    operatorSharePercent: detail.operatorSharePercent,
    minOperatorSharePercent: detail.minOperatorSharePercent,
    isDepositLimited: detail.isDepositLimited,
    metrics: detail.metrics,
    exposure: detail.exposure,
    positions: detail.positions,
    orders: detail.orders,
    navCurve: detail.navCurve,
    followerCount: detail.followerCount,
    signalCount: detail.signalCount,
    trackingHint: '按净值记录的首尾时间跨度计算。',
  }
})

/// 只用一句说明链路状态：断开时说原因，正常时给出最近一帧到达的时刻。
const liveNote = computed(() => {
  if (streamStatus.value === 'reconnecting') return '实时连接已断开，正在重连'
  if (lastUpdatedAt.value === null) return ''

  return `实时更新 · ${formatLocalTime(new Date(lastUpdatedAt.value).toISOString())}`
})

function closeModal(): void {
  visible.value = false
}
</script>

<template>
  <a-modal v-model:visible="visible" width="1040px" :footer="false">
    <template #title>
      <a-space :size="8" wrap>
        <span>{{ displayName }}</span>
        <a-tag size="small" :color="isWatching ? 'green' : 'gray'">
          {{ isWatching ? '观察中' : '未在观察' }}
        </a-tag>
        <a-tooltip
          v-if="lead?.isFrozen"
          content="该带单员已停止接受新资金；跟随不受影响。"
        >
          <a-tag size="small" color="red">已停止入金</a-tag>
        </a-tooltip>
      </a-space>
    </template>

    <div class="detail-body">
      <a-alert v-if="errorMessage" type="error" :title="errorMessage" />

      <a-space v-else direction="vertical" fill :size="16">
        <a-tooltip
          v-if="liveNote"
          content="带单员的持仓或成交变化时自动刷新；收益与持仓来自上游快照。"
        >
          <div class="stream-note">{{ liveNote }}</div>
        </a-tooltip>

        <!-- 首帧之前：标题/状态/跟随者数已经有摘要，正文只留一行，不画假的 0 -->
        <template v-if="lead === null">
          <div class="meta-line">
            带单员 #{{ accountIndex }} · 跟随者 <b>{{ followerCount }}</b> · 仓位变动
            <b>{{ signalCount }}</b> 次
            <template v-if="lastSignalAt"> · 最近变动 {{ formatSince(lastSignalAt) }}</template>
          </div>
          <div class="connecting-line">正在读取这个带单员的业绩与持仓…</div>
        </template>

        <template v-else>
          <div class="meta-line">
            带单员 #{{ lead.accountIndex }} · 跟随者 <b>{{ lead.followerCount }}</b> · 仓位变动
            <b>{{ lead.signalCount }}</b> 次
            <template v-if="lead.lastSignalAt">
              · 最近变动 {{ formatSince(lead.lastSignalAt) }}
            </template>
          </div>

          <LeadDetailContent v-if="content" :data="content" />
        </template>

        <div class="modal-actions">
          <a-button @click="closeModal">关闭</a-button>
        </div>
      </a-space>
    </div>
  </a-modal>
</template>

<style scoped>
.meta-line {
  font-size: 12px;
  color: var(--color-text-3);
  line-height: 1.7;
}

.stream-note {
  font-size: 12px;
  color: var(--color-text-3);
}

.connecting-line {
  padding: 24px 0;
  font-size: 12px;
  color: var(--color-text-3);
}

.modal-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
