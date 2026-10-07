<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'

/// 轻量折线图。
///
/// 手写 SVG 而不引入图表库：这里只需要"若干条序列叠在一起看形状"，
/// 而图表库会带来远大于需求的体积与配置面。需要交互式图表时再换。
///
/// 两个踩过的坑，都记在这里以免重犯：
/// <list type="number">
///   <item>viewBox 固定尺寸 + width:100% 会按**宽高比**缩放，于是 height 参数
///     失去意义（要 280px 实际渲染出 430px），字号也随之放大。</item>
///   <item>纵轴刻度是右对齐的，留给它的宽度必须按**最长标签的实际宽度**算。
///     固定 52 会让 8 位数字的刻度溢出画布左侧，被裁掉开头的 0。</item>
/// </list>
/// 因此这里实测容器宽度，让 1 个 viewBox 单位 = 1 个 CSS 像素，刻度宽度按位数推算。
interface Series {
  label: string
  color: string
  values: number[]
}

const props = defineProps<{
  series: Series[]
  height?: number
  /** 是否以 1.0 为基准画水平参考线（净值曲线用）。 */
  baseline?: number
}>()

const plotHeight = computed(() => props.height ?? 260)

const topPad = 12
const bottomPad = 22

/// 刻度字号。等宽数字约 0.6em 宽，再加 8px 与轴线的间隙。
const AXIS_FONT_SIZE = 10

const host = ref<HTMLElement | null>(null)
const hostWidth = ref(900)

let observer: ResizeObserver | null = null

onMounted(() => {
  if (!host.value) return

  observer = new ResizeObserver((entries) => {
    const width = entries[0]?.contentRect.width ?? 0

    if (width > 0) {
      hostWidth.value = width
    }
  })

  observer.observe(host.value)
  hostWidth.value = host.value.clientWidth || hostWidth.value
})

onBeforeUnmount(() => observer?.disconnect())

const bounds = computed(() => {
  const all = props.series.flatMap((s) => s.values)

  if (all.length === 0) {
    return { min: 0, max: 1 }
  }

  const min = Math.min(...all)
  const max = Math.max(...all)
  const pad = (max - min) * 0.08 || 0.02

  return { min: min - pad, max: max + pad }
})

/// 纵轴刻度的小数位按数值跨度动态决定。
///
/// 固定 3 位会出事：份额净值的量级在 0.0016 附近，跨度只有万分之几，
/// 固定 3 位会让所有刻度都显示成 "0.001"——轴上读不出任何差异。
const axisDigits = computed(() => {
  const { min, max } = bounds.value
  const span = Math.abs(max - min)

  if (span === 0) return 3

  // 跨度每小一个数量级就多留一位；上限 8 位，避免出现长串
  return Math.min(8, Math.max(0, 3 - Math.floor(Math.log10(span))))
})

const gridLines = computed(() => {
  const { min, max } = bounds.value
  const steps = 4
  const usable = Math.max(1, plotHeight.value - topPad - bottomPad)

  return Array.from({ length: steps + 1 }, (_, i) => {
    const value = min + ((max - min) * i) / steps

    return {
      label: value.toFixed(axisDigits.value),
      y: topPad + (usable * (steps - i)) / steps,
    }
  })
})

const axisWidth = computed(() => {
  const longest = gridLines.value.reduce((max, line) => Math.max(max, line.label.length), 0)
  return Math.ceil(longest * AXIS_FONT_SIZE * 0.62) + 8
})

const padLeft = computed(() => axisWidth.value + 4)

function xOf(index: number, count: number): number {
  if (count <= 1) return padLeft.value

  const usable = Math.max(1, hostWidth.value - padLeft.value - 12)
  return padLeft.value + (usable * index) / (count - 1)
}

function yOf(value: number): number {
  const usable = Math.max(1, plotHeight.value - topPad - bottomPad)
  const { min, max } = bounds.value

  if (max === min) return topPad + usable / 2

  return topPad + usable * (1 - (value - min) / (max - min))
}

function pathOf(values: number[]): string {
  if (values.length === 0) return ''

  return values
    .map((v, i) => `${i === 0 ? 'M' : 'L'} ${xOf(i, values.length).toFixed(1)} ${yOf(v).toFixed(1)}`)
    .join(' ')
}

const baselineY = computed(() => (props.baseline === undefined ? null : yOf(props.baseline)))
</script>

<template>
  <div ref="host" style="width: 100%">
    <div style="display: flex; gap: 16px; margin-bottom: 6px; flex-wrap: wrap">
      <span
        v-for="s in series"
        :key="s.label"
        style="display: flex; align-items: center; gap: 6px; font-size: 12px"
      >
        <span :style="{ background: s.color, width: '10px', height: '3px', display: 'inline-block' }" />
        {{ s.label }}
      </span>
    </div>

    <svg
      :width="hostWidth"
      :height="plotHeight"
      :viewBox="`0 0 ${hostWidth} ${plotHeight}`"
      style="display: block"
    >
      <line
        v-for="line in gridLines"
        :key="line.label + line.y"
        :x1="padLeft"
        :y1="line.y"
        :x2="hostWidth - 12"
        :y2="line.y"
        stroke="var(--color-border-1)"
        stroke-width="1"
      />
      <text
        v-for="line in gridLines"
        :key="`t-${line.label}-${line.y}`"
        :x="padLeft - 6"
        :y="line.y + 4"
        text-anchor="end"
        :font-size="AXIS_FONT_SIZE"
        fill="var(--color-text-3)"
      >
        {{ line.label }}
      </text>

      <line
        v-if="baselineY !== null"
        :x1="padLeft"
        :y1="baselineY"
        :x2="hostWidth - 12"
        :y2="baselineY"
        stroke="var(--color-text-4)"
        stroke-width="1"
        stroke-dasharray="4 4"
      />

      <path
        v-for="s in series"
        :key="s.label"
        :d="pathOf(s.values)"
        fill="none"
        :stroke="s.color"
        stroke-width="1.6"
      />
    </svg>
  </div>
</template>
