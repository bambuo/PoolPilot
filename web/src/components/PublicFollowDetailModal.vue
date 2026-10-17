<script setup lang="ts">
import { computed } from 'vue'

import type { PublicFollower, PublicFollowerSummary } from '@/api/public'
import FollowerEquityChart from '@/components/FollowerEquityChart.vue'
import FollowerFillsTable from '@/components/FollowerFillsTable.vue'
import FollowerOverview from '@/components/FollowerOverview.vue'
import FollowerPositionsTable from '@/components/FollowerPositionsTable.vue'
import { useFollowerDetail } from '@/composables/usePublicStream'
import type {
  FollowerFillRow,
  FollowerMetric,
  FollowerPositionRow,
} from '@/domain/followerDetail'
import { formatDateTime, formatLocalDateTime, formatLocalTime, formatNumber, formatPercent, formatUsd } from '@/utils/format'

/// 公开跟单详情（G3 的实时版）：卡片点开后订阅 /api/public/stream?detail=<id>，
/// 只认 detail 帧与 gone 终止帧。首帧未到时显示占位，**不**回退到 G3 的单次详情接口——
/// 列表与详情必须来自同一条实时契约，否则"实时"标签会在一个接口上撒谎。
///
/// 与列表的两条纪律同源：`isValuationAvailable === false` 时说"估值不可得"而不是
/// 把数字当 0 画；删除入口只在归属为真时出现。详情帧来自所有订阅者共享的 SSE，
/// 帧里的 `isOwnedByCaller` 恒为 false，所以归属由父层用一次性读口的结果通过
/// `isOwned` 传进来——不能拿共享帧自己判断。
const visible = defineModel<boolean>('visible', { required: true })

const props = defineProps<{
  /// null = 未选中任何一条；打开时由父层赋值，切换 id 会重新订阅。
  followerId: number | null
  /// 列表里这一行的实时摘要。首帧到达前先用它铺数值，避免弹窗闪一下"正在建立实时视图"。
  summary: PublicFollowerSummary | null
  /// 当前浏览器是否拥有这一行。详情帧来自共享 SSE，装不下按 Cookie 裁剪的归属，
  /// 所以由父层用一次性归属读口的结果传进来。
  isOwned: boolean
}>()

const emit = defineEmits<{
  /// 详情上的删除只负责"把这个对象交给父层去确认"——确认框与真正的 W3 全站只有一份，
  /// 卡片和详情两个入口共用，代价说明不会出现两个版本。
  requestDelete: [follower: PublicFollower]
}>()

/// 弹窗关闭时 getter 给 null，composable 随即 close()；不必在模板里再手动断流。
/// 这同时覆盖"详情开着时换了一条卡片"：id 变化会先断旧连接再建新连接。
const { follower: liveFollower, status: streamStatus, errorMessage, lastUpdatedAt } =
  useFollowerDetail(() => (visible.value ? props.followerId : null))

/// 列表摘要投影成"详情形状"的种子：数值与卡片同源（同一份 SSE 快照），
/// 但明细（持仓/流水/曲线）还没有，等首帧到达再显示。
function seedFromSummary(summary: PublicFollowerSummary): PublicFollower {
  return {
    ...summary,
    mode: 'paper',
    fees: 0,
    marksUnavailable: [],
    positions: [],
    fills: [],
    curve: [],
  }
}

/// 首帧到了就整体换成详情帧；没到之前先用列表摘要，弹窗不再出现加载占位。
const follower = computed<PublicFollower | null>(() =>
  liveFollower.value ?? (props.summary === null ? null : seedFromSummary(props.summary)),
)

/// 明细区只在真正的详情帧到达后显示；摘要有数值，但没有持仓/流水/曲线。
const detailReady = computed(() => liveFollower.value !== null)

/// 共享详情帧上的 isOwnedByCaller 恒为 false；真正可删与否以父层传进来的归属为准。
const ownedByCaller = computed(() => follower.value?.isOwnedByCaller === true || props.isOwned)

/// 只用一句说明链路状态：断开时说服因，正常时给出最近一帧到达的时刻。
/// 两句话同时出现会自相矛盾，这是列表页已经踩过的坑。
const liveNote = computed(() => {
  if (streamStatus.value === 'reconnecting') return '实时连接已断开，正在重连'
  if (lastUpdatedAt.value === null) return ''

  return `实时更新 · ${formatLocalTime(new Date(lastUpdatedAt.value).toISOString())}`
})

/// 总览：六格与工作台同槽位；公开面只有纸面，所以标签固定为纸面口径。
const metrics = computed<FollowerMetric[]>(() => {
  const f = follower.value
  if (f === null) return []

  return [
    {
      label: '账户权益',
      value: formatUsd(f.equity, 4),
      note: `本金 ${formatUsd(f.initialCapital)}`,
      tone: f.equity >= f.initialCapital ? 'success' : 'danger',
    },
    {
      label: '收益率',
      value: formatPercent(f.returnPercent, 4),
      note: formatUsd(f.equity - f.initialCapital, 4),
      tone: f.returnPercent >= 0 ? 'success' : 'danger',
    },
    {
      label: '未实现盈亏',
      value: formatUsd(f.unrealizedPnl, 4),
      note: f.marksUnavailable.length > 0 ? '含取不到价的标的' : '按盘口中间价盯市',
      tone: f.marksUnavailable.length > 0 ? 'warning' : 'default',
    },
    { label: '持仓名义价值', value: formatUsd(f.grossNotional), note: '总敞口' },
    { label: '已实现盈亏', value: formatUsd(f.realizedPnl, 4), note: '平仓部分结转' },
    {
      label: '成交 / 跳过',
      value: `${f.fillCount} / ${f.skipCount}`,
      note: f.skipCount > 0 ? '跳过 = 带单员动了没跟上' : '全部跟上',
      tone: f.skipCount > 0 ? 'warning' : 'success',
    },
  ]
})

const positionRows = computed<FollowerPositionRow[]>(() => {
  const f = follower.value
  if (f === null) return []

  return f.positions
    .filter((position) => position.size !== 0)
    .map((position) => {
      const unavailable = !position.hasMark && position.size !== 0

      return {
        key: String(position.marketId),
        symbol: position.symbol,
        side: position.side,
        sizeText: formatNumber(position.size, 6),
        entryText: formatNumber(position.entryVwap, 6),
        markText: position.hasMark ? formatNumber(position.markPrice, 6) : null,
        liquidationText:
          position.liquidationPrice === null
            ? null
            : formatNumber(position.liquidationPrice, 6),
        notionalText: formatUsd(position.notional),
        unrealizedText: unavailable ? '不可得' : formatUsd(position.unrealizedPnl, 4),
        unrealizedTone: unavailable ? 'neutral' : position.unrealizedPnl >= 0 ? 'profit' : 'loss',
        returnText:
          position.unrealizedPnlPercent === null
            ? null
            : formatPercent(position.unrealizedPnlPercent, 2),
        returnTone:
          position.unrealizedPnlPercent === null
            ? 'neutral'
            : position.unrealizedPnlPercent >= 0
              ? 'profit'
              : 'loss',
        realizedText: formatUsd(position.realizedPnl, 4),
      }
    })
})

const fillRows = computed<FollowerFillRow[]>(() => {
  const f = follower.value
  if (f === null) return []

  return f.fills.map((fill) => {
    const reduceMargin =
      fill.isFilled
      && fill.isReducing
      && fill.initialMarginPercent !== null
      && fill.initialMarginPercent > 0
        ? Math.abs(fill.notional) * fill.initialMarginPercent / 100
        : 0
    const reduceReturn = reduceMargin > 0 ? (fill.realizedPnl / reduceMargin) * 100 : null

    return {
      key: String(fill.id),
      atText: formatDateTime(fill.observedAt),
      symbol: fill.symbol,
      action: fill.isFilled && fill.side ? { side: fill.side } : null,
      quantityText: fill.isFilled ? formatNumber(fill.size, 6) : '—',
      priceText: formatNumber(fill.price, 6),
      notionalText: formatUsd(fill.notional),
      result: {
        label: fill.isFilled ? '已成交' : '未跟上',
        color: fill.isFilled ? 'green' : 'orange',
      },
      realizedText: formatUsd(fill.realizedPnl, 4),
      fillReturnText: reduceReturn === null ? undefined : formatPercent(reduceReturn, 2),
      fillReturnTone:
        reduceReturn === null ? undefined : reduceReturn >= 0 ? 'profit' : 'loss',
      note: !fill.isFilled && fill.skipReason ? skipReasonLabel(fill.skipReason) : undefined,
    }
  })
})

const equityEmptyText = computed(() => {
  const f = follower.value
  if (f === null) return '还没有权益点'

  return f.curve.length === 0 ? '还没有权益点' : '目前只有 1 个权益点，画不出趋势'
})

/// 跳过原因的中文标签。公开契约把 skipReason 定为自由字符串，已知的三种翻成中文，
/// 其余原样显示——不为一个"以后可能有新原因"的字段编故事。
function skipReasonLabel(reason: string): string {
  const map: Record<string, string> = {
    below_minimum_size: '低于最小数量',
    below_minimum_notional: '低于最小金额',
    no_liquidity: '盘口无深度',
  }

  return map[reason] ?? reason
}

function closeModal(): void {
  visible.value = false
}
</script>

<template>
  <a-modal v-model:visible="visible" width="1040px" :footer="false">
    <template #title>
      <a-space :size="8" wrap>
        <span>{{ follower ? follower.label || `纸面跟单 #${follower.id}` : `纸面跟单 #${followerId ?? ''}` }}</span>
        <a-tag v-if="ownedByCaller" size="small" color="arcoblue">你创建的</a-tag>
        <a-tag v-if="follower && !follower.leadIsEnabled" size="small" color="orange">上游已暂停</a-tag>
        <a-tag v-if="follower" size="small" :color="follower.isActive ? 'green' : 'gray'">
          {{ follower.isActive ? '运行中' : '已停止' }}
        </a-tag>
      </a-space>
    </template>

    <div class="detail-body">
      <a-alert v-if="errorMessage" type="error" :title="errorMessage" />

      <!-- 首帧未到：只占一行，不转圈、不回退到 G3 取数 -->
      <div v-else-if="follower === null" class="connecting-line">正在建立实时视图…</div>

      <a-space v-else direction="vertical" fill :size="16">
        <div v-if="liveNote" class="stream-note">{{ liveNote }}</div>

        <div class="meta-line">
          订阅 {{ follower.leadName }}（池 #{{ follower.leadAccountIndex }}） · 本金
          <b>{{ formatUsd(follower.initialCapital) }}</b> · 比例
          <b>{{ formatPercent(follower.scale * 100, 4) }}</b>（池子权益 {{ formatUsd(follower.poolEquity) }}） ·
          创建于 {{ formatLocalDateTime(follower.createdAt) }}
        </div>

        <template v-if="follower.isValuationAvailable">
          <FollowerOverview :metrics="metrics" />

          <!-- 明细区等真正的详情首帧：摘要只有数值，没有持仓/流水/曲线 -->
          <template v-if="detailReady">
            <!-- 取不到盯市价的市场：未实现盈亏不可得。这句话必须在，缺几个就报几个 -->
          <a-alert v-if="follower.marksUnavailable.length > 0" type="warning" :show-icon="true">
            有 {{ follower.marksUnavailable.length }} 个市场取不到盯市价，它们的未实现盈亏不可得（不是 0）。
          </a-alert>

          <!-- 与工作台详情同一条：累计手续费不为 0 才说 -->
          <a-alert
            v-if="follower.fees > 0"
            type="info"
            :show-icon="true"
            :title="`累计手续费 ${formatUsd(follower.fees, 4)}`"
          />

          <div>
            <div class="section-title">持仓（{{ positionRows.length }}）</div>
            <FollowerPositionsTable
              :rows="positionRows"
              empty-text="当前没有持仓"
            />
          </div>

          <div>
            <div class="section-title">
              流水（最近 {{ follower.fills.length }} 条）
              <span class="section-note">更早的记录不在公开面上提供</span>
            </div>
            <FollowerFillsTable
              :rows="fillRows"
              price-mode="single"
              :pagination="{ pageSize: 10, size: 'small' }"
              empty-text="还没有任何流水"
            />
          </div>

          <div>
            <div class="section-title">
              权益曲线
              <span class="section-note">虚线为初始本金</span>
            </div>
            <FollowerEquityChart
              :points="follower.curve"
              :initial-capital="follower.initialCapital"
              :height="240"
              :empty-text="equityEmptyText"
            />
          </div>
          </template>
        </template>

        <a-alert v-else type="warning" :show-icon="true" title="估值不可得">
          暂取不到池子数据，下面的数字此刻没有意义——不按 0 显示，也不画曲线。
        </a-alert>

        <div class="modal-actions">
          <a-button @click="closeModal">关闭</a-button>
          <a-button
            v-if="ownedByCaller"
            type="primary"
            status="danger"
            @click="emit('requestDelete', follower)"
          >
            删除这条跟单
          </a-button>
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

.section-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-1);
  margin-bottom: 8px;
}

.section-note {
  margin-left: 8px;
  font-size: 12px;
  font-weight: 400;
  color: var(--color-text-3);
}

.modal-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
