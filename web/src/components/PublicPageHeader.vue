<script setup lang="ts">
import { RouterLink, useRoute } from 'vue-router'

import { HOME_PATH } from '@/router'
import { useSession } from '@/composables/useSession'

/// 公开页的极简头部。**一个组件、两个公开页各引一次**，不改路由结构。
///
/// 为什么不用工作台顶栏：那一排导航由权限菜单树决定（useMenu），
/// 匿名访客既没有它、也不该看见它——点进去只会被守卫送去登录页。
/// 页面只给公开两页的页签和"登录/注册 或 回工作台"这一个出口。
///
/// 登录/注册入口由 `VITE_SHOW_AUTH_LINKS` 控制：未设置时默认显示，
/// 显式设为 `false`（或 `0`）时隐藏。Vite 的环境变量在构建时内联，
/// 所以改完要重新构建；开发期则重启 Vite。
///
/// 页签高亮按 name 精确判定而不是路径前缀：公开两页是平级兄弟路由，
/// 包含式匹配（router-link-active）在这里没有额外信息，不如写死两个名字。
const { user } = useSession()
const route = useRoute()

const authLinksSetting = (import.meta.env.VITE_SHOW_AUTH_LINKS ?? '').trim().toLowerCase()
const showAuthLinks = authLinksSetting !== 'false' && authLinksSetting !== '0'

const tabs = [
  { name: 'public-leads', label: '带单广场' },
  { name: 'public-follows', label: '跟单广场' },
] as const
</script>

<template>
  <header class="public-bar">
    <RouterLink :to="{ name: 'public-leads' }" class="brand">
      <!-- 与工作台同一份标志；alt 留空并 aria-hidden——名字就在旁边，读屏读两遍没有意义 -->
      <img class="brand-mark" src="/logo.svg" alt="" aria-hidden="true" />
      <span>PoolPilot</span>
    </RouterLink>

    <nav class="tabs">
      <RouterLink
        v-for="tab in tabs"
        :key="tab.name"
        :to="{ name: tab.name }"
        class="tab-link"
        :class="{ 'tab-link-active': route.name === tab.name }"
      >
        {{ tab.label }}
      </RouterLink>
    </nav>

    <div class="spacer" />

    <div v-if="user === null && showAuthLinks" class="auth-links">
      <RouterLink :to="{ name: 'login' }">登录</RouterLink>
      <span class="dot">·</span>
      <RouterLink :to="{ name: 'register' }">注册</RouterLink>
    </div>
    <!-- 已登录的人给回工作台的入口。按 HOME_PATH（路径而非 name）：
         业务路由由菜单树动态注册，从公开页进来时可能还没注册，按 name 跳会报"没有这条路由"。
         匿名且入口被环境变量隐藏时，这里两个分支都不渲染；页签与品牌仍保留 -->
    <RouterLink v-else-if="user !== null" :to="HOME_PATH" class="auth-links">回工作台</RouterLink>
  </header>
</template>

<style scoped>
.public-bar {
  display: flex;
  align-items: center;
  gap: 20px;
  height: 56px;
  padding: 0 20px;

  /* 高度、布局与描边显式写出（工作台顶栏同一道理：Arco 不给 header 高度与背景）；
     颜色一律取令牌，跟着主题一起变 */
  background: var(--color-bg-2);
  border-bottom: 1px solid var(--color-border-1);
}

.brand {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  color: var(--color-text-1);
  font-size: 17px;
  font-weight: 600;
  letter-spacing: 0.2px;
  text-decoration: none;
}

.brand-mark {
  display: block;
  width: 22px;
  height: 22px;
}

.tabs {
  display: flex;
  align-items: center;
  gap: 4px;
  height: 100%;
}

.tab-link {
  display: flex;
  align-items: center;
  height: 100%;
  padding: 0 10px;
  white-space: nowrap;
  color: var(--color-text-3);
  font-size: 14px;
  text-decoration: none;
  box-sizing: border-box;
  border-bottom: 2px solid transparent;
  transition: color 0.15s, border-color 0.15s;
}

.tab-link:hover {
  color: var(--color-text-1);
}

.tab-link-active {
  color: var(--color-text-1);
  border-bottom-color: rgb(var(--primary-6));
}

.spacer {
  flex: 1 1 auto;
}

.auth-links {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  color: var(--color-text-3);
  text-decoration: none;
}

.auth-links a {
  color: rgb(var(--primary-6));
  text-decoration: none;
}

.dot {
  color: var(--color-text-4);
}
</style>
