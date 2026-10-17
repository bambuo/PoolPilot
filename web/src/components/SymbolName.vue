<script setup lang="ts">
import SymbolIcon from '@/components/SymbolIcon.vue'

/// 「图标 + 标的代码」的统一写法。
///
/// 表格与卡片都从这里取，避免每个页面各写一遍间距、等宽字体与省略规则。
withDefaults(
  defineProps<{
    symbol: string
    mono?: boolean
    compact?: boolean
  }>(),
  {
    mono: true,
    compact: false,
  },
)
</script>

<template>
  <span class="symbol-name">
    <SymbolIcon :symbol="symbol" :size="compact ? 14 : 18" />
    <span class="symbol-name-text" :class="{ mono }">{{ symbol }}</span>
  </span>
</template>

<style scoped>
.symbol-name {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  max-width: 100%;
}

.symbol-name-text {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mono {
  font-family: var(--font-mono, ui-monospace, SFMono-Regular, Menlo, monospace);
}
</style>
