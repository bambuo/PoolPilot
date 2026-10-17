<script setup lang="ts">
import MetricTile from '@/components/MetricTile.vue'
import type { FollowerMetric } from '@/domain/followerDetail'

/// 跟单员详情顶部六格总览：公开页与工作台共用同一组槽位。
/// 标签与数值由页面各自映射（实盘的"钱包余额/可用保证金"与纸面的
/// "账户权益/收益率"占同一位置，但含义不同），组件只负责排版。
defineProps<{ metrics: FollowerMetric[] }>()

function tileProps(metric: FollowerMetric) {
  return {
    label: metric.label,
    value: metric.value,
    ...(metric.note === undefined ? {} : { note: metric.note }),
    ...(metric.tone === undefined ? {} : { tone: metric.tone }),
  }
}
</script>

<template>
  <a-grid :cols="6" :col-gap="12" :row-gap="14">
    <a-grid-item v-for="metric in metrics" :key="metric.label">
      <MetricTile v-bind="tileProps(metric)" />
    </a-grid-item>
  </a-grid>
</template>
