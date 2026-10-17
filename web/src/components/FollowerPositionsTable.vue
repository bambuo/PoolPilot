<script setup lang="ts">
import DirectionMark from '@/components/DirectionMark.vue'
import HeaderHint from '@/components/HeaderHint.vue'
import SymbolName from '@/components/SymbolName.vue'
import type { FollowerPositionRow, PnlTone } from '@/domain/followerDetail'

/// 跟单员详情的持仓表：公开页与工作台共用共享列。
/// 实盘专属的杠杆/强平价/平仓操作只在 `showLiveColumns` 时出现；
/// 平仓按钮由工作台通过 `live-action` 插槽提供（权限判断与动作都在页面侧）。
withDefaults(
  defineProps<{
    rows: FollowerPositionRow[]
    emptyText: string
    showRealized?: boolean
    showLiveColumns?: boolean
  }>(),
  {
    showRealized: true,
    showLiveColumns: false,
  },
)

const numericStyle = {
  whiteSpace: 'nowrap',
  fontVariantNumeric: 'tabular-nums',
}

function toneColor(tone: PnlTone): string {
  if (tone === 'profit') return 'rgb(var(--success-6))'
  if (tone === 'loss') return 'rgb(var(--danger-6))'

  return 'var(--color-text-3)'
}
</script>

<template>
  <a-table
    :data="rows"
    row-key="key"
    size="small"
    :bordered="false"
    :pagination="false"
    :scroll="{ x: showLiveColumns ? 1240 : 1208 }"
  >
    <template #columns>
      <a-table-column title="标的" :width="130" fixed="left">
        <template #cell="{ record }">
          <a-tooltip v-if="record.symbolHint" :content="record.symbolHint">
            <SymbolName :symbol="record.symbol" />
          </a-tooltip>
          <SymbolName v-else :symbol="record.symbol" />
        </template>
      </a-table-column>

      <a-table-column title="方向" :width="70" align="center" fixed="left">
        <template #cell="{ record }">
          <DirectionMark :side="record.side" />
        </template>
      </a-table-column>

      <a-table-column
        title="数量"
        :width="120"
        align="right"
        :header-cell-style="numericStyle"
        :body-cell-style="numericStyle"
      >
        <template #cell="{ record }">{{ record.sizeText }}</template>
      </a-table-column>

      <a-table-column
        title="开仓价格"
        :width="120"
        align="right"
        :header-cell-style="numericStyle"
        :body-cell-style="numericStyle"
      >
        <template #cell="{ record }">{{ record.entryText }}</template>
      </a-table-column>

      <a-table-column
        title="标记价格"
        :width="120"
        align="right"
        :header-cell-style="numericStyle"
        :body-cell-style="numericStyle"
      >
        <template #cell="{ record }">
          <span v-if="record.markText !== null">{{ record.markText }}</span>
          <a-tooltip v-else :content="record.markHint ?? '取不到标记价，该标的的未实现盈亏不可得'">
            <span class="dim">取价失败</span>
          </a-tooltip>
        </template>
      </a-table-column>

      <a-table-column
        title="清算价格"
        :width="120"
        align="right"
        :header-cell-style="numericStyle"
        :body-cell-style="numericStyle"
      >
        <template #title>
          <HeaderHint
            label="清算价格"
            hint="账户级全仓清算价：只让这一笔价格变动，权益跌到维持保证金要求时的价格。没有正数解时显示 —。"
          />
        </template>
        <template #cell="{ record }">{{ record.liquidationText ?? '—' }}</template>
      </a-table-column>

      <a-table-column
        title="持仓价值"
        :width="110"
        align="right"
        :header-cell-style="numericStyle"
        :body-cell-style="numericStyle"
      >
        <template #cell="{ record }">{{ record.notionalText }}</template>
      </a-table-column>

      <a-table-column
        v-if="showLiveColumns"
        title="杠杆"
        :width="70"
        align="right"
        :header-cell-style="numericStyle"
        :body-cell-style="numericStyle"
      >
        <template #cell="{ record }">{{ record.leverageText ?? '—' }}</template>
      </a-table-column>

      <a-table-column
        v-if="showRealized"
        title="已实现盈亏"
        :width="128"
        align="right"
        :header-cell-style="numericStyle"
        :body-cell-style="numericStyle"
      >
        <template #cell="{ record }">{{ record.realizedText ?? '—' }}</template>
      </a-table-column>

      <a-table-column
        title="未实现盈亏"
        :width="160"
        align="right"
        fixed="right"
        :header-cell-style="numericStyle"
        :body-cell-style="numericStyle"
      >
        <template #cell="{ record }">
          <span :style="{ color: toneColor(record.unrealizedTone) }">
            {{ record.unrealizedText }}
          </span>
        </template>
      </a-table-column>

      <a-table-column
        title="投资回报率"
        :width="130"
        align="right"
        fixed="right"
        :header-cell-style="numericStyle"
        :body-cell-style="numericStyle"
      >
        <template #title>
          <HeaderHint
            label="投资回报率"
            hint="未实现盈亏 ÷ 保证金（保证金 = 持仓价值 × 源池初始保证金率）；保证金不可得时显示 —。"
          />
        </template>
        <template #cell="{ record }">
          <span :style="{ color: toneColor(record.returnTone ?? 'neutral') }">
            {{ record.returnText ?? '—' }}
          </span>
        </template>
      </a-table-column>

      <template v-if="showLiveColumns">
        <a-table-column title="操作" :width="90" align="center" fixed="right">
          <template #cell="{ record }">
            <slot name="live-action" :row="record">
              <span class="dim">—</span>
            </slot>
          </template>
        </a-table-column>
      </template>
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

/* 表格内容一律不折行：列宽已经按最长的日期/数量/金额排过，折行只会把行撑高 */
:deep(.arco-table-td-content),
:deep(.arco-table-th-title) {
  white-space: nowrap;
}

/* 右对齐列默认左右各 16px 内边距，120px 的列只剩 88px 给内容：
   六位小数的价格（如 26,756.477612）比它宽，会从内容盒右缘溢出去，
   右边缘因此对不齐。收到 8px 后内容盒有 104px，表头与数值也共用同一条右边界。 */
:deep(.arco-table-cell-align-right) {
  padding-left: 8px;
  padding-right: 8px;
}
</style>
