<script setup lang="ts">
import { computed } from 'vue'

import type { PublicLeadDetail, PublicLeadTrader } from '@/api/public'
import LineChart from '@/components/LineChart.vue'
import MetricTile from '@/components/MetricTile.vue'
import { useLeadDetail } from '@/composables/usePublicStream'
import {
  formatDateTime,
  formatLocalTime,
  formatNumber,
  formatPercent,
  formatSince,
  formatUsd,
} from '@/utils/format'

/// 公开带单员详情：带单广场点卡片打开。
///
/// 数据来自 `GET /api/public/stream?lead=<accountIndex>`。文案刻意采用跟单产品里
/// **带单员画像**的标准说法（收益率、最大回撤、AUM、跟随者、带单天数、收益分成），
/// 而不是池子运营口径（份额、净值、运营者份额、敞口）。同一个数换一个名字，
/// 陌生访客不必先学会 Lighter 的池机制才能读懂这张卡。
///
/// 与跟单详情同一条纪律：只认 SSE 帧，不回退到单次 REST 取数；首帧到达前用列表卡片
/// 的摘要把标题与状态铺上，正文只留一行"读取中"，不画假的 0。
const visible = defineModel<boolean>('visible', { required: true })

const props = defineProps<{
  /// null = 未选中；打开时由父层赋值，切换池会重新订阅。
  accountIndex: number | null
  /// 列表里这个带单员的实时摘要：首帧到达前用它铺标题/状态/信号数。
  summary: PublicLeadTrader | null
}>()

const { lead: liveLead, status: streamStatus, errorMessage, lastUpdatedAt } =
  useLeadDetail(() => (visible.value ? props.accountIndex : null))

/// 首帧前的展示数据：摘要有的字段先用，池的净值/敞口等首帧。
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

/// 只用一句说明链路状态：断开时说原因，正常时给出最近一帧到达的时刻。
const liveNote = computed(() => {
  if (streamStatus.value === 'reconnecting') return '实时连接已断开，正在重连'
  if (lastUpdatedAt.value === null) return ''

  return `实时更新 · ${formatLocalTime(new Date(lastUpdatedAt.value).toISOString())}`
})

/// 收益曲线：把份额净值换算成**区间收益率**（以窗口首点为 0%）。
///
/// 跟单产品的标准画法是收益率曲线而不是净值曲线——访客要看的是"跟着他能赚多少"，
/// 不是"一份份额值多少钱"。公开接口只回最近 180 个净值点，所以这里明确是
/// **区间**收益：窗口首点归一为 0，末点就是这段时间的累计收益率。
const navSeries = computed(() => {
  const detail = lead.value
  if (detail === null || detail.navCurve.length < 2) return []

  const first = detail.navCurve[0]
  if (first === undefined || first.sharePrice <= 0) return []

  return [
    {
      label: '收益率（%）',
      color: 'rgb(var(--primary-6))',
      values: detail.navCurve.map((point) => (point.sharePrice / first.sharePrice - 1) * 100),
    },
  ]
})

/// 曲线窗口的累计收益率，用于标题右侧那句话（与图上末点一致）。
const windowReturnPercent = computed(() => {
  const points = lead.value?.navCurve ?? []
  const first = points[0]
  const last = points[points.length - 1]

  if (points.length < 2 || first === undefined || last === undefined || first.sharePrice <= 0) {
    return null
  }

  return (last.sharePrice / first.sharePrice - 1) * 100
})

/// 仓位比例的语气。全仓保证金下它本身不是爆仓判据，真正的判据是强平缓冲——
/// 这里只做量级提示，不用"危险"这类断言措辞。
/// 返回值必须是 Arco Tag 的预设色名，不是 Alert 的语义名。
const exposureTone = computed(() => {
  const ratio = lead.value?.exposure.exposureRatioPercent ?? 0

  if (ratio > 400) return 'red'
  if (ratio > 200) return 'orange'
  return 'green'
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

          <a-alert
            v-if="!lead.metrics.isStatisticallyMeaningful"
            type="warning"
            :show-icon="true"
          >
            该带单员的业绩记录只有 {{ lead.metrics.spanDays }} 天，收益与风险指标仅供参考。
          </a-alert>

          <div>
            <div class="section-title">
              带单表现
              <span class="section-note">收益率与风险指标按净值记录计算</span>
            </div>
            <a-grid :cols="6" :col-gap="12" :row-gap="14">
              <a-grid-item>
                <MetricTile
                  label="总收益率"
                  :value="formatPercent(lead.metrics.realTotalReturnPercent)"
                  note="按净值计算"
                />
              </a-grid-item>
              <a-grid-item>
                <MetricTile
                  label="年化收益率"
                  :value="formatPercent(lead.metrics.realAnnualizedReturnPercent)"
                  note="按业绩区间折算"
                  tone="primary"
                />
              </a-grid-item>
              <a-grid-item>
                <MetricTile
                  label="最大回撤"
                  :value="formatPercent(lead.metrics.realMaxDrawdownPercent)"
                  note="区间内最深"
                  tone="danger"
                />
              </a-grid-item>
              <a-grid-item>
                <MetricTile
                  label="夏普比率"
                  :value="formatNumber(lead.metrics.realSharpe, 2)"
                  :note="`波动率 ${formatPercent(lead.metrics.periodVolatilityPercent)}`"
                />
              </a-grid-item>
              <a-grid-item>
                <MetricTile
                  label="资产管理规模（AUM）"
                  :value="formatUsd(lead.totalAssetValue)"
                  note="带单员管理的资金"
                />
              </a-grid-item>
              <a-grid-item>
                <MetricTile label="跟随者" :value="`${lead.followerCount} 人`" />
              </a-grid-item>
            </a-grid>
          </div>

          <div>
            <div class="section-title">带单概况</div>
            <a-descriptions :column="4" size="small">
              <a-descriptions-item label="带单天数">
                <a-tooltip content="按净值记录的首尾时间跨度计算。">
                  <span>{{ lead.metrics.spanDays }} 天</span>
                </a-tooltip>
              </a-descriptions-item>
              <a-descriptions-item label="收益分成">
                <a-tooltip content="带单员从跟随者收益中提取的比例。">
                  <span>{{ formatPercent(lead.operatorFeePercent) }}</span>
                </a-tooltip>
              </a-descriptions-item>
              <a-descriptions-item label="带单员跟投">
                <a-tooltip
                  :content="
                    lead.isDepositLimited
                      ? `带单员自有资金占资产管理规模的 ${formatPercent(lead.operatorSharePercent)}；已到平台下限 ${formatPercent(lead.minOperatorSharePercent)}，不再接受新入金（跟随不受影响）。`
                      : `带单员自有资金占资产管理规模的 ${formatPercent(lead.operatorSharePercent)}；平台要求不低于 ${formatPercent(lead.minOperatorSharePercent)}。`
                  "
                >
                  <a-tag size="small" color="arcoblue">
                    {{ formatPercent(lead.operatorSharePercent) }}
                  </a-tag>
                </a-tooltip>
              </a-descriptions-item>
              <a-descriptions-item label="仓位变动">
                <a-tooltip content="系统记录的该带单员仓位变化次数。">
                  <span>{{ lead.signalCount }} 次</span>
                </a-tooltip>
              </a-descriptions-item>
            </a-descriptions>
          </div>

          <div>
            <div class="section-title">
              当前风险
              <span class="section-note">全仓保证金 · 强平缓冲越厚越安全</span>
            </div>
            <a-descriptions :column="4" size="small">
              <a-descriptions-item label="总持仓">
                {{ formatUsd(lead.exposure.grossExposure) }}
              </a-descriptions-item>
              <a-descriptions-item label="净持仓">
                {{ formatUsd(lead.exposure.netExposure) }}
              </a-descriptions-item>
              <a-descriptions-item label="仓位比例">
                <a-tooltip content="总持仓 ÷ 资产管理规模（AUM）。">
                  <a-tag size="small" :color="exposureTone">
                    {{ formatPercent(lead.exposure.exposureRatioPercent) }}
                  </a-tag>
                </a-tooltip>
              </a-descriptions-item>
              <a-descriptions-item label="强平缓冲">
                <a-tooltip content="抵押品减去维持保证金要求；数值越厚，离强平越远。">
                  <span>{{ formatUsd(lead.exposure.liquidationBuffer) }}</span>
                </a-tooltip>
              </a-descriptions-item>
              <a-descriptions-item label="持仓数量">
                {{ lead.exposure.activePositionCount }} 个
              </a-descriptions-item>
            </a-descriptions>
          </div>

          <div>
            <div class="section-title">
              当前持仓（{{ lead.exposure.activePositionCount }}）
              <span class="section-note">
                {{
                  lead.exposure.activePositionCount > lead.positions.length
                    ? `仅列前 ${lead.positions.length} 个，按持仓价值排序`
                    : '按持仓价值排序'
                }}
              </span>
            </div>
            <a-table
              :data="lead.positions"
              row-key="marketId"
              size="small"
              :bordered="false"
              :pagination="false"
            >
              <template #columns>
                <a-table-column title="币种" data-index="symbol" :width="140" />
                <a-table-column title="方向" :width="80" align="center">
                  <template #cell="{ record }">
                    <a-tag size="small" :color="record.side === 'long' ? 'green' : 'red'">
                      {{ record.side === 'long' ? '多' : '空' }}
                    </a-tag>
                  </template>
                </a-table-column>
                <a-table-column title="持仓价值" :width="140" align="right">
                  <template #cell="{ record }">{{ formatUsd(record.notional) }}</template>
                </a-table-column>
                <a-table-column title="持仓占比">
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
              <template #empty>
                <a-empty description="当前空仓" />
              </template>
            </a-table>
          </div>

          <div>
            <div class="section-title">
              收益曲线
              <span class="section-note">
                最近 {{ lead.navCurve.length }} 个点 · 截至
                {{ formatDateTime(lead.navCurve.at(-1)?.at ?? null) }} · 区间累计
                {{ windowReturnPercent === null ? '—' : formatPercent(windowReturnPercent) }}
              </span>
            </div>
            <LineChart v-if="navSeries.length > 0" :series="navSeries" :baseline="0" :height="240" />
            <a-empty
              v-else
              :description="
                lead.navCurve.length === 0 ? '没有业绩数据' : '目前只有 1 个净值点，画不出曲线'
              "
            />
          </div>
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

/* 占比条：长度与数字是同一个量的两种表达，条只用来横扫比较，数字仍以文字为准 */
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

.modal-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
