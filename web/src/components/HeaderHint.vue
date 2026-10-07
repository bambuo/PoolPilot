<script setup lang="ts">
import { IconQuestionCircle } from '@arco-design/web-vue/es/icon'

/// 表头的「列名 + ?」组合：鼠标悬停 ? 显示该列的含义。
///
/// 问号用 Arco 官方的 IconQuestionCircle 图标（跟手造 CSS 圆圈说再见）。
/// 做成组件而不是六份 tooltip 拷贝：提示文案是术语表，集中在一处才好维护；
/// 且所有列的问号样式必须一致，散写必然漂移。
defineProps<{
  label: string
  hint: string
}>()
</script>

<template>
  <span class="header-hint">
    <span>{{ label }}</span>
    <a-tooltip :content="hint" position="tl">
      <!-- tabindex=0 让键盘使用者也能聚焦并触发悬浮提示；图标颜色随 hover 走 currentColor -->
      <span class="hint-mark" tabindex="0" role="note" :aria-label="hint">
        <IconQuestionCircle />
      </span>
    </a-tooltip>
  </span>
</template>

<style scoped>
.header-hint {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.hint-mark {
  display: inline-flex;
  align-items: center;
  color: var(--color-text-3);
  cursor: help;
  font-size: 14px;
  transition: color 0.15s;
}

.hint-mark:hover {
  color: rgb(var(--primary-6));
}
</style>
