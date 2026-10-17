<script setup lang="ts">
import { computed } from 'vue'

import LineChart from '@/components/LineChart.vue'

/// 跟单员详情的权益曲线：公开页与工作台共用画法与基准线口径——
/// 基准线画在初始本金上，高于它才是赚的。
const props = defineProps<{
  points: readonly { equity: number }[]
  initialCapital: number
  height: number
  emptyText: string
}>()

const series = computed(() =>
  props.points.length === 0
    ? []
    : [
        {
          label: '账户权益',
          color: 'rgb(var(--primary-6))',
          values: props.points.map((point) => point.equity),
        },
      ],
)
</script>

<template>
  <LineChart
    v-if="points.length > 1"
    :series="series"
    :baseline="initialCapital"
    :height="height"
  />
  <a-empty v-else :description="emptyText" />
</template>
