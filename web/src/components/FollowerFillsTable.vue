<script setup lang="ts">
import HeaderHint from '@/components/HeaderHint.vue'
import SymbolName from '@/components/SymbolName.vue'
import type { FollowerFillRow, PnlTone } from '@/domain/followerDetail'

/// 跟单员详情的流水表：公开页与工作台共用。
///
/// 两种价格形态由 `priceMode` 决定：公开页只有一列「价格」，
/// 工作台分成「开仓价 / 平仓价」。纸面专属的已实现盈亏按需出现。
withDefaults(
  defineProps<{
    rows: FollowerFillRow[]
    emptyText: string
    priceMode: 'single' | 'open-close'
    showRealized?: boolean
    pagination?: { pageSize: number; size?: 'mini' | 'small' | 'medium' | 'large' } | false
  }>(),
  {
    showRealized: true,
    pagination: () => ({ pageSize: 20, size: 'small' }),
  },
)

/// 除最后一列「说明」外，其余列都按内容定宽；说明吸收剩余宽度。
/// 不写死动作/结果这类短列的宽度，否则它们会空出一大截。
const contentStyle = {
  width: '1%',
  whiteSpace: 'nowrap',
}

const numericStyle = {
  width: '1%',
  whiteSpace: 'nowrap',
  fontVariantNumeric: 'tabular-nums',
}

function toneColor(tone: PnlTone | undefined): string {
  if (tone === 'profit') return 'rgb(var(--success-6))'
  if (tone === 'loss') return 'rgb(var(--danger-6))'

  return 'var(--color-text-1)'
}
</script>

<template>
  <a-table
    :data="rows"
    row-key="key"
    size="small"
    :bordered="false"
    :pagination="pagination"
  >
    <template #columns>
      <a-table-column
        title="时间"
        :header-cell-style="contentStyle"
        :body-cell-style="contentStyle"
      >
        <template #cell="{ record }">{{ record.atText }}</template>
      </a-table-column>

      <a-table-column
        title="标的"
        :header-cell-style="contentStyle"
        :body-cell-style="contentStyle"
      >
        <template #cell="{ record }">
          <a-tooltip v-if="record.symbolHint" :content="record.symbolHint">
            <SymbolName :symbol="record.symbol" />
          </a-tooltip>
          <SymbolName v-else :symbol="record.symbol" />
          <span v-if="record.symbolNote" class="dim">（{{ record.symbolNote }}）</span>
        </template>
      </a-table-column>

      <a-table-column
        title="动作"
        :header-cell-style="contentStyle"
        :body-cell-style="contentStyle"
      >
        <template #cell="{ record }">
          <template v-if="record.action === null">
            <span class="dim">—</span>
          </template>
          <a-tag
            v-else-if="record.action.side"
            size="small"
            :color="record.action.side === 'buy' ? 'green' : 'red'"
          >
            {{ record.action.text ?? (record.action.side === 'buy' ? '买入' : '卖出') }}
          </a-tag>
          <template v-else>
            {{ record.action.text }}
            <span v-if="record.action.subtext" class="dim">{{ record.action.subtext }}</span>
            <a-tag
              v-for="tag in record.action.tags ?? []"
              :key="tag.label"
              size="small"
              :color="tag.color"
            >
              {{ tag.label }}
            </a-tag>
          </template>
        </template>
      </a-table-column>

      <a-table-column
        title="数量"
        align="right"
        :header-cell-style="numericStyle"
        :body-cell-style="numericStyle"
      >
        <template #cell="{ record }">{{ record.quantityText }}</template>
      </a-table-column>

      <a-table-column
        v-if="priceMode === 'single'"
        title="价格"
        align="right"
        :header-cell-style="numericStyle"
        :body-cell-style="numericStyle"
      >
        <template #cell="{ record }">{{ record.priceText ?? '—' }}</template>
      </a-table-column>

      <template v-else>
        <a-table-column
          align="right"
          :header-cell-style="numericStyle"
          :body-cell-style="numericStyle"
        >
          <template #title>
            <HeaderHint
              label="开仓价"
              hint="这笔仓位是多少钱开的：开仓行=本单成交均价；平仓行=被平仓位的开仓均价。旧数据用下单时的参考价近似，误差不到一个基点"
            />
          </template>
          <template #cell="{ record }">{{ record.entryPriceText ?? '—' }}</template>
        </a-table-column>

        <a-table-column
          align="right"
          :header-cell-style="numericStyle"
          :body-cell-style="numericStyle"
        >
          <template #title>
            <HeaderHint
              label="平仓价"
              hint="这笔仓位是多少钱平的（本单的成交均价）。只有平仓行有——开仓行与未发出的行是空的"
            />
          </template>
          <template #cell="{ record }">{{ record.closePriceText ?? '—' }}</template>
        </a-table-column>
      </template>

      <a-table-column
        title="名义价值"
        align="right"
        :header-cell-style="numericStyle"
        :body-cell-style="numericStyle"
      >
        <template #cell="{ record }">{{ record.notionalText }}</template>
      </a-table-column>

      <a-table-column
        title="结果"
        align="center"
        :header-cell-style="contentStyle"
        :body-cell-style="contentStyle"
      >
        <template #cell="{ record }">
          <a-tag size="small" :color="record.result.color">{{ record.result.label }}</a-tag>
        </template>
      </a-table-column>

      <a-table-column
        v-if="showRealized"
        title="已实现盈亏"
        align="right"
        :header-cell-style="numericStyle"
        :body-cell-style="numericStyle"
      >
        <template #cell="{ record }">
          <span :style="{ color: toneColor(record.realizedTone) }">
            {{ record.realizedText ?? '—' }}
          </span>
        </template>
      </a-table-column>

      <a-table-column
        v-if="showRealized"
        title="投资回报率"
        align="right"
        :header-cell-style="numericStyle"
        :body-cell-style="numericStyle"
      >
        <template #title>
          <HeaderHint
            label="投资回报率"
            hint="该笔平仓结转的已实现盈亏 ÷ 该笔占用的保证金（平仓名义价值 × 建仓保证金率）。开仓/加仓行显示 —。"
          />
        </template>
        <template #cell="{ record }">
          <span :style="{ color: toneColor(record.fillReturnTone) }">
            {{ record.fillReturnText ?? '—' }}
          </span>
        </template>
      </a-table-column>

      <a-table-column title="说明">
        <template #cell="{ record }">
          <a-tooltip v-if="record.note" :content="record.note">
            <span class="reason-text">{{ record.note }}</span>
          </a-tooltip>
          <span v-else class="dim">—</span>
        </template>
      </a-table-column>
    </template>

    <template #empty>
      <a-empty :description="emptyText" />
    </template>
  </a-table>
</template>

<style scoped>
.dim {
  color: var(--color-text-3);
}

.reason-text {
  display: block;
  max-width: 220px;
  overflow: hidden;
  font-size: 12px;
  color: var(--color-text-3);
  text-overflow: ellipsis;
  white-space: nowrap;
  cursor: help;
}

/* 表格内容一律不折行；说明列自己的省略号规则优先。 */
:deep(.arco-table-td-content),
:deep(.arco-table-th-title) {
  white-space: nowrap;
}
</style>
