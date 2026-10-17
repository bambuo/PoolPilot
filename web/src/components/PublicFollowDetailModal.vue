<script setup lang="ts">
import { computed } from 'vue'

import type { PublicFollower, PublicFollowerPosition, PublicFollowerSummary } from '@/api/public'
import LineChart from '@/components/LineChart.vue'
import MetricTile from '@/components/MetricTile.vue'
import { useFollowerDetail } from '@/composables/usePublicStream'
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

/// 权益曲线：基准线画在初始本金上——高于它才是赚的（与工作台同口径）。
const equitySeries = computed(() => {
  const f = follower.value
  if (f === null || f.curve.length === 0) return []

  return [
    {
      label: '账户权益',
      color: 'rgb(var(--primary-6))',
      values: f.curve.map((point) => point.equity),
    },
  ]
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

/// 该持仓的未实现盈亏是否不可得：取不到价且有仓位时是不可得，不是 0。
function unrealizedUnavailable(position: PublicFollowerPosition): boolean {
  return !position.hasMark && position.size !== 0
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
          <a-grid :cols="6" :col-gap="12" :row-gap="14">
            <a-grid-item>
              <MetricTile
                label="账户权益"
                :value="formatUsd(follower.equity, 4)"
                :note="`本金 ${formatUsd(follower.initialCapital)}`"
                :tone="follower.equity >= follower.initialCapital ? 'success' : 'danger'"
              />
            </a-grid-item>
            <a-grid-item>
              <MetricTile
                label="收益率"
                :value="formatPercent(follower.returnPercent, 4)"
                :note="formatUsd(follower.equity - follower.initialCapital, 4)"
                :tone="follower.returnPercent >= 0 ? 'success' : 'danger'"
              />
            </a-grid-item>
            <a-grid-item>
              <MetricTile
                label="未实现盈亏"
                :value="formatUsd(follower.unrealizedPnl, 4)"
                :note="
                  follower.marksUnavailable.length > 0
                    ? '含取不到价的标的'
                    : '按盘口中间价盯市'
                "
                :tone="follower.marksUnavailable.length > 0 ? 'warning' : 'default'"
              />
            </a-grid-item>
            <a-grid-item>
              <MetricTile
                label="持仓名义价值"
                :value="formatUsd(follower.grossNotional)"
                note="总敞口"
              />
            </a-grid-item>
            <a-grid-item>
              <MetricTile
                label="已实现盈亏"
                :value="formatUsd(follower.realizedPnl, 4)"
                note="平仓部分结转"
              />
            </a-grid-item>
            <a-grid-item>
              <MetricTile
                label="成交 / 跳过"
                :value="`${follower.fillCount} / ${follower.skipCount}`"
                :note="follower.skipCount > 0 ? '跳过 = 带单员动了没跟上' : '全部跟上'"
                :tone="follower.skipCount > 0 ? 'warning' : 'success'"
              />
            </a-grid-item>
          </a-grid>

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
            <div class="section-title">持仓（{{ follower.positions.length }}）</div>
            <a-table
              :data="follower.positions"
              row-key="marketId"
              size="small"
              :bordered="false"
              :scroll="{ x: 980 }"
              :pagination="false"
            >
              <template #columns>
                <a-table-column title="标的" data-index="symbol" :width="130" />
                <a-table-column title="方向" :width="70" align="center">
                  <template #cell="{ record }">
                    <a-tag v-if="record.side" size="small" :color="record.side === 'long' ? 'green' : 'red'">
                      {{ record.side === 'long' ? '多' : '空' }}
                    </a-tag>
                    <span v-else class="dim">空仓</span>
                  </template>
                </a-table-column>
                <a-table-column title="数量" :width="110" align="right">
                  <template #cell="{ record }">{{ formatNumber(record.size, 6) }}</template>
                </a-table-column>
                <a-table-column title="开仓均价" :width="140" align="right">
                  <template #cell="{ record }">{{ formatNumber(record.entryVwap, 6) }}</template>
                </a-table-column>
                <a-table-column title="标记价" :width="140" align="right">
                  <template #cell="{ record }">
                    <span v-if="record.hasMark">{{ formatNumber(record.markPrice, 6) }}</span>
                    <span v-else class="dim">取价失败</span>
                  </template>
                </a-table-column>
                <a-table-column title="名义价值" :width="110" align="right">
                  <template #cell="{ record }">{{ formatUsd(record.notional) }}</template>
                </a-table-column>
                <a-table-column title="未实现盈亏" :width="160" align="right">
                  <template #cell="{ record }">
                    <span v-if="unrealizedUnavailable(record)" class="dim">不可得</span>
                    <span
                      v-else
                      :style="{
                        color:
                          record.unrealizedPnl >= 0 ? 'rgb(var(--success-6))' : 'rgb(var(--danger-6))',
                      }"
                    >
                      {{ formatUsd(record.unrealizedPnl, 4) }}
                    </span>
                  </template>
                </a-table-column>
                <a-table-column title="已实现盈亏" :width="120" align="right">
                  <template #cell="{ record }">{{ formatUsd(record.realizedPnl, 4) }}</template>
                </a-table-column>
              </template>
              <template #empty>
                <a-empty description="我方与池子当前都是空仓" />
              </template>
            </a-table>
          </div>

          <div>
            <div class="section-title">
              流水（最近 {{ follower.fills.length }} 条）
              <span class="section-note">更早的记录不在公开面上提供</span>
            </div>
            <a-table
              :data="follower.fills"
              row-key="id"
              size="small"
              :bordered="false"
              :scroll="{ x: 1000 }"
              :pagination="{ pageSize: 10, size: 'small' }"
            >
              <template #columns>
                <a-table-column title="时间" :width="160">
                  <template #cell="{ record }">{{ formatDateTime(record.observedAt) }}</template>
                </a-table-column>
                <a-table-column title="标的" data-index="symbol" :width="100" />
                <a-table-column title="动作" :width="80" align="center">
                  <template #cell="{ record }">
                    <a-tag v-if="record.isFilled && record.side" size="small" :color="record.side === 'buy' ? 'green' : 'red'">
                      {{ record.side === 'buy' ? '买入' : '卖出' }}
                    </a-tag>
                    <span v-else class="dim">—</span>
                  </template>
                </a-table-column>
                <a-table-column title="数量" :width="100" align="right">
                  <template #cell="{ record }">
                    {{ record.isFilled ? formatNumber(record.size, 6) : '—' }}
                  </template>
                </a-table-column>
                <a-table-column title="价格" :width="105" align="right">
                  <template #cell="{ record }">{{ formatNumber(record.price, 6) }}</template>
                </a-table-column>
                <a-table-column title="名义价值" :width="95" align="right">
                  <template #cell="{ record }">{{ formatUsd(record.notional) }}</template>
                </a-table-column>
                <a-table-column title="已实现盈亏" :width="105" align="right">
                  <template #cell="{ record }">{{ formatUsd(record.realizedPnl, 4) }}</template>
                </a-table-column>
                <a-table-column title="结果" :width="85" align="center">
                  <template #cell="{ record }">
                    <a-tag size="small" :color="record.isFilled ? 'green' : 'orange'">
                      {{ record.isFilled ? '已成交' : '未跟上' }}
                    </a-tag>
                  </template>
                </a-table-column>
                <a-table-column title="说明" :width="130">
                  <template #cell="{ record }">
                    <span
                      v-if="!record.isFilled && record.skipReason"
                      class="note-cell"
                      :title="skipReasonLabel(record.skipReason)"
                    >
                      {{ skipReasonLabel(record.skipReason) }}
                    </span>
                    <span v-else class="dim">—</span>
                  </template>
                </a-table-column>
              </template>
              <template #empty>
                <a-empty description="还没有任何流水" />
              </template>
            </a-table>
          </div>

          <div>
            <div class="section-title">
              权益曲线
              <span class="section-note">虚线为初始本金</span>
            </div>
            <LineChart
              v-if="follower.curve.length > 1"
              :series="equitySeries"
              :baseline="follower.initialCapital"
              :height="240"
            />
            <a-empty v-else :description="follower.curve.length === 0 ? '还没有权益点' : '目前只有 1 个权益点，画不出趋势'" />
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

.dim {
  color: var(--color-text-3);
}

/* 表格内容一律不折行：列宽已经按最长的日期/数量/金额排过，折行只会把行撑高 */
:deep(.arco-table-td-content),
:deep(.arco-table-th-title) {
  white-space: nowrap;
}

/* 说明列可能容纳比当前三种更长的跳过原因：越界用省略号，悬停看全文 */
.note-cell {
  display: block;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.modal-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
