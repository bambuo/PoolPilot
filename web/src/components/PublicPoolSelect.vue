<script setup lang="ts">
import { computed, onUnmounted, ref } from 'vue'

import { describePublicError, publicApi, type PublicLeadTrader, type PublicPoolOption } from '@/api/public'
import { formatUsd } from '@/utils/format'

/// 公开带单页的池选择器（G4）：「观察这个池」用它——那一步的语义正是"给这个池建一个带单员"。
/// 跟单弹窗**不**用它：那里的候选是带单员（见 PublicLeadSelect），不是全量池。
///
/// 搜索走**服务端**而不是本地过滤：G4 按 q + limit 分页返回（默认 20 条、上限 50），
/// 池子远不止这些——全量拉到浏览器再筛是把漏斗倒过来用。所以关掉本地过滤
/// （`:filter-option="false"`），服务端按池名子串或精确账户索引查。
///
/// 输入防抖 300ms：每敲一个字符发一次请求会先撞上限流（429），那句话使用者
/// 无从下手，而这里本可以避免。请求带序号防交错——两次搜索的响应乱序回来时，
/// 只有**最后一次**的才许覆盖选项，否则下拉里显示的会是上一个关键词的结果。
const model = defineModel<string>({ required: true })

/// 页面已经从 SSE 拿到的带单员列表：候选框据此标出"已有带单员（N 人跟随）"，
/// 与工作台的创建弹窗保持同一套信息。
const props = defineProps<{ leads?: PublicLeadTrader[] }>()

const options = ref<PublicPoolOption[]>([])
const loading = ref(false)
const loadError = ref('')
const lastQuery = ref('')

const leadIndexes = computed(() => new Set((props.leads ?? []).map((lead) => lead.accountIndex)))

function followerCountOf(accountIndex: number): number {
  return (props.leads ?? []).find((lead) => lead.accountIndex === accountIndex)?.followerCount ?? 0
}

let searchSeq = 0
let debounceTimer: number | null = null

async function fetchOptions(q: string): Promise<void> {
  const seq = ++searchSeq

  lastQuery.value = q
  loading.value = true
  loadError.value = ''

  try {
    const list = await publicApi.poolOptions(q)

    if (seq === searchSeq) {
      options.value = list
    }
  } catch (error) {
    if (seq === searchSeq) {
      loadError.value = describePublicError(error)
    }
  } finally {
    if (seq === searchSeq) {
      loading.value = false
    }
  }
}

function onSearch(inputValue: string): void {
  if (debounceTimer !== null) {
    window.clearTimeout(debounceTimer)
  }

  debounceTimer = window.setTimeout(() => {
    debounceTimer = null
    void fetchOptions(inputValue.trim())
  }, 300)
}

// 组件创建即取第一页：点开下拉不该先是一片空白再等人输入
void fetchOptions('')

onUnmounted(() => {
  if (debounceTimer !== null) {
    window.clearTimeout(debounceTimer)
  }
})
</script>

<template>
  <div class="pool-select">
    <a-select
      v-model="model"
      :style="{ width: '100%' }"
      placeholder="选择池（可按名称或账户索引搜索）"
      allow-search
      :filter-option="false"
      :loading="loading"
      @search="onSearch"
    >
      <a-option
        v-for="pool in options"
        :key="pool.accountIndex"
        :value="String(pool.accountIndex)"
        :label="pool.name || `池 ${pool.accountIndex}`"
      >
        {{ pool.name || `（未命名）` }} · #{{ pool.accountIndex }} ·
        {{ formatUsd(pool.totalAssetValue) }}
        <span v-if="leadIndexes.has(pool.accountIndex)" class="lead-annotation">
          · 已有带单员（{{ followerCountOf(pool.accountIndex) }} 人跟随）
        </span>
      </a-option>
    </a-select>

    <!-- 这一行只在**这次请求失败**时出现。不吞掉它：空下拉看起来像"没有池子"，
         而事实可能是这次搜索坏了——两者对使用者的下一步完全不同 -->
    <div v-if="loadError" class="select-error">
      {{ loadError }}
      <a-link @click="fetchOptions(lastQuery)">重试</a-link>
    </div>
    <div v-else-if="!loading && options.length === 0" class="select-empty">没有匹配的池</div>
  </div>
</template>

<style scoped>
/* 选择器在表单内容区里默认按内容收缩，必须自己撑满，否则弹窗里只有半截宽 */
.pool-select {
  width: 100%;
}

.select-error {
  margin-top: 6px;
  font-size: 12px;
  line-height: 1.7;
  color: rgb(var(--danger-6));
}

.lead-annotation {
  color: var(--color-text-3);
  font-size: 12px;
}

.select-empty {
  margin-top: 6px;
  font-size: 12px;
  color: var(--color-text-3);
}
</style>
