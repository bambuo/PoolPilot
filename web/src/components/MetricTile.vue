<script setup lang="ts">
/// 指标磁贴：标签 + 大号数值 + 注释。
///
/// 不用 Arco 的 Statistic，因为它的 value 只接受 number | Date，而本项目的数值都已在
/// utils/format 里格式化过（K/M 缩写、bps、正负号语气）。要复用它的排版就得把原始数字
/// 再传一遍并在组件内二次格式化，格式化规则就有两份了。磁贴自己写十几行，规则只有一份。
withDefaults(
  defineProps<{
    label: string
    value: string
    note?: string
    tone?: 'default' | 'primary' | 'success' | 'danger' | 'warning'
  }>(),
  { tone: 'default' },
)

const toneColor: Record<string, string> = {
  default: 'var(--color-text-1)',
  primary: 'rgb(var(--primary-6))',
  success: 'rgb(var(--success-6))',
  danger: 'rgb(var(--danger-6))',
  warning: 'rgb(var(--warning-6))',
}
</script>

<template>
  <div>
    <div style="font-size: 13px; color: var(--color-text-3); margin-bottom: 4px">{{ label }}</div>
    <div :style="{ fontSize: '20px', fontWeight: 500, color: toneColor[tone], lineHeight: '1.3' }">
      {{ value }}
    </div>
    <div v-if="note" style="font-size: 11px; color: var(--color-text-4); margin-top: 2px">
      {{ note }}
    </div>
  </div>
</template>
