<script setup lang="ts">
/// 多空方向标记：绿色「多」、红色「空」。
///
/// 不用 ▲/▼：那组符号紧挨标的与盈亏时，很容易被读成行情涨跌，
/// 而不是仓位方向。单字中文没有这层歧义，也与中文交易界面习惯一致。
withDefaults(
  defineProps<{
    side: 'long' | 'short' | null
    size?: number
  }>(),
  { size: 14 },
)
</script>

<template>
  <a-tooltip v-if="side" :content="side === 'long' ? '多' : '空'">
    <span
      class="direction"
      :class="side === 'long' ? 'direction-long' : 'direction-short'"
      :style="{ fontSize: `${size}px` }"
      role="img"
      :aria-label="side === 'long' ? '多' : '空'"
    >
      {{ side === 'long' ? '多' : '空' }}
    </span>
  </a-tooltip>
  <span v-else class="direction-empty">空仓</span>
</template>

<style scoped>
.direction {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 1em;
  font-weight: 600;
  line-height: 1;
}

.direction-long {
  color: rgb(var(--success-6));
}

.direction-short {
  color: rgb(var(--danger-6));
}

.direction-empty {
  color: var(--color-text-3);
}
</style>
