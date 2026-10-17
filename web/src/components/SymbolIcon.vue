<script setup lang="ts">
import { computed } from 'vue'

import {
  monochromeSymbols,
  pngSymbolCodes,
  symbolCodes,
} from '@/assets/symbolIcons'
import anthropicLogo from '@/assets/symbols/simple/anthropic.svg'
import spacexLogo from '@/assets/symbols/simple/spacex.svg'

/// 标的图标。
///
/// 命名跟 Binance 的 symbol 口径：可交易对象是 symbol，资产是 baseAsset/quoteAsset。
/// nvstly/icons 全库的 PNG 已用 VTracer 描成 SVG，放在 `public/symbol-icons`；
/// 只有 6 个描摹失败的保留 PNG。这里按 symbol 代码拼 URL，图片不进 JS 包。
/// 白/浅色图形（AAPL、WLD、HYPE 等）用遮罩跟随主题文字色，避免透明背景上消失；
/// 其余没有公开图标的 symbol 用透明底 + 品牌色字母描边兜底。
const props = withDefaults(defineProps<{ symbol: string; size?: number }>(), {
  size: 18,
})

const SYMBOL_CODES = new Set(symbolCodes)
const PNG_SYMBOL_CODES = new Set(pngSymbolCodes)
const MONOCHROME_LOGOS = new Set(monochromeSymbols)
const LOGO_BASE = `${import.meta.env.BASE_URL}symbol-icons/`

/// nvstly 缺少的三只：用 Simple Icons 的单色 SVG 遮罩。
const MASK_LOGOS: Record<string, string> = {
  ANTH: anthropicLogo,
  ANTHROPIC: anthropicLogo,
  SPACEX: spacexLogo,
  SPCX: spacexLogo,
}

const FALLBACK_COLORS: Record<string, string> = {
  CRWV: '#1E293B',
  GRASS: '#166534',
  KAITO: '#B91C1C',
  LIT: '#0E7490',
  SNDK: '#0F172A',
  XPL: '#111827',
}

/// 透明底上的描边与字母。色相分散，避免相邻标的看起来像同一类。
const DEFAULT_COLORS = [
  '#2563EB',
  '#7C3AED',
  '#DB2777',
  '#EA580C',
  '#059669',
  '#0891B2',
  '#DC2626',
  '#4F46E5',
  '#0D9488',
  '#9333EA',
]

const normalized = computed(() => props.symbol.trim().toUpperCase())

const symbolLogo = computed(() => {
  const symbol = normalized.value
  const file = SYMBOL_CODES.has(symbol)
    ? `${symbol}.svg`
    : PNG_SYMBOL_CODES.has(symbol)
      ? `${symbol}.png`
      : undefined

  return file === undefined ? undefined : `${LOGO_BASE}${encodeURIComponent(file)}`
})

/// 单色图形走遮罩；其余可直接显示的图形走 img。
const maskSource = computed(() => {
  if (symbolLogo.value !== undefined && MONOCHROME_LOGOS.has(normalized.value)) {
    return symbolLogo.value
  }

  return MASK_LOGOS[normalized.value]
})

const imageLogo = computed(() =>
  symbolLogo.value !== undefined && !MONOCHROME_LOGOS.has(normalized.value)
    ? symbolLogo.value
    : undefined,
)

const hasLogo = computed(() => imageLogo.value !== undefined || maskSource.value !== undefined)

/// 去掉 1000PEPE 这类倍数前缀，让两个字母落在真正的名字上。
const label = computed(() => {
  const base = normalized.value.replace(/^\d+/, '') || normalized.value

  return (base.slice(0, 2) || '?').padEnd(2, ' ').trim()
})

const fallbackColor = computed(() => {
  const symbol = normalized.value

  return FALLBACK_COLORS[symbol] ?? DEFAULT_COLORS[hash(symbol) % DEFAULT_COLORS.length]
})

/// 单色 SVG 用遮罩；直接绑 style 对象，避免模板里对 ref
/// 做字符串插值时拿不到实际 URL。
const logoMask = computed(() => {
  const url = maskSource.value

  if (url === undefined) return undefined

  return {
    maskImage: `url("${url}")`,
    WebkitMaskImage: `url("${url}")`,
  }
})

function hash(value: string): number {
  let result = 0

  for (const character of value) {
    result = (result * 31 + character.charCodeAt(0)) | 0
  }

  return Math.abs(result)
}
</script>

<template>
  <span
    class="symbol-icon"
    :class="{
      'symbol-icon-fallback': !hasLogo,
      'symbol-icon-mask': hasLogo && maskSource !== undefined,
    }"
    :style="hasLogo ? { '--symbol-size': `${size}px` } : { color: fallbackColor, '--symbol-size': `${size}px` }"
    aria-hidden="true"
  >
    <img v-if="imageLogo" class="symbol-image" :src="imageLogo" alt="" />
    <span v-else-if="maskSource" class="symbol-mask" :style="logoMask" />
    <template v-else>{{ label }}</template>
  </span>
</template>

<style scoped>
.symbol-icon {
  display: inline-flex;
  flex: 0 0 auto;
  align-items: center;
  justify-content: center;
  width: var(--symbol-size, 18px);
  height: var(--symbol-size, 18px);
  border-radius: 50%;
  font-size: calc(var(--symbol-size, 18px) * 0.5);
  font-weight: 700;
  line-height: 1;
  letter-spacing: -0.2px;
}

/* 兜底不画底色，只留一圈与字母同色的描边。 */
.symbol-icon-fallback {
  box-shadow: inset 0 0 0 1px currentColor;
}

.symbol-icon-mask {
  color: var(--color-text-1);
}

.symbol-image {
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.symbol-mask {
  width: 78%;
  height: 78%;
  background: currentColor;
  -webkit-mask-position: center;
  mask-position: center;
  -webkit-mask-size: contain;
  mask-size: contain;
  -webkit-mask-repeat: no-repeat;
  mask-repeat: no-repeat;
}
</style>
