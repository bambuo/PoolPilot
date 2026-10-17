<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'

import { describePublicError, publicApi, type PublicLeadTrader } from '@/api/public'
import { filterPoolOption } from '@/utils/poolOptions'

/// 公开跟单页的**带单员**选择器（候选来自 G1）。
///
/// 这里列的是带单员，不是池——这不是措辞讲究，而是对象关系：带单员与池 1:1，
/// 它的身份就是池索引，作用是替系统盯着那个池产出信号。跟单员订阅的是**带单员的信号**，
/// 所以没有观察者的池根本不可跟（服务端会回 400，界面上也不该给那个选项）。
/// 想跟别的池，先去公开带单页点「观察这个池」。
///
/// 与池选择器（G4）的取法不同是刻意的：候选上限就是带单员总数（≤100，且都是"正在被看的"），
/// 一次取全再本地过滤即可；服务端分页搜索是给三百多个池准备的漏斗，这里用不上。
/// 下拉每次展开都重取一遍——带单页刚建的观察者要马上能选到，而不是等人刷新整页。
const model = defineModel<string>({ required: true })

const leads = ref<PublicLeadTrader[]>([])
const loading = ref(false)
const loadError = ref('')

async function load(): Promise<void> {
  loading.value = true
  loadError.value = ''

  try {
    leads.value = await publicApi.leadTraders()
  } catch (error) {
    // 不吞掉：空下拉看起来像"没人被观察"，而事实可能是这一次请求坏了
    loadError.value = describePublicError(error)
  } finally {
    loading.value = false
  }
}

onMounted(() => void load())
</script>

<template>
  <div class="lead-select">
    <a-select
      v-model="model"
      :style="{ width: '100%' }"
      placeholder="选择带单员（可按名称或账户索引搜索）"
      allow-search
      :filter-option="filterPoolOption"
      :loading="loading"
      :trigger-props="{ autoFitPopupMinWidth: true }"
      @dropdown-visible-change="(open: boolean) => open && void load()"
    >
      <a-option
        v-for="lead in leads"
        :key="lead.accountIndex"
        :value="String(lead.accountIndex)"
        :label="lead.name"
      >
        {{ lead.name || `带单员 #${lead.accountIndex}` }} ·
        {{ lead.isWatching ? '观察中' : '未在观察' }}
        <span v-if="lead.followerCount > 0">· 已有 {{ lead.followerCount }} 个跟单员</span>
        <span v-else>· 暂无跟单员</span>
      </a-option>
    </a-select>

    <div v-if="loadError" class="select-error">
      {{ loadError }}
      <a-link @click="load">重试</a-link>
    </div>
    <div v-else-if="!loading && leads.length === 0" class="select-empty">还没有带单员。跟单员订阅的是带单员，先到<RouterLink :to="{ name: 'public-leads' }" class="select-link">带单广场</RouterLink>为你想跟的池建立观察者。</div>
  </div>
</template>

<style scoped>
/* 同 PublicPoolSelect：表单内容区不会自动把子元素拉满 */
.lead-select {
  width: 100%;
}

.select-link {
  color: rgb(var(--primary-6));
  text-decoration: none;
}

.select-error {
  margin-top: 6px;
  font-size: 12px;
  line-height: 1.7;
  color: rgb(var(--danger-6));
}

.select-empty {
  margin-top: 6px;
  font-size: 12px;
  color: var(--color-text-3);
}
</style>
