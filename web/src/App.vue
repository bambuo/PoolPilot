<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink, RouterView, useRoute } from 'vue-router'

/// 工作台外壳。导航项按「总览 → 观察 → 跟单 → 分析 → 回测」的使用顺序排列：
/// 首页是关系图（一屏说清三层结构），之后是池列表与带单员（被观察的池），
/// 才是跟单（下场的账户），最后是门槛与回测。
///
/// 不要用 `a-layout-header` 的默认外观：Arco 给它的 CSS 只有 `flex: 0 0 auto`，
/// **既没有高度也没有背景**（它的示例都自己传 height）。不设高度会让它塌成内容高度、
/// 背景透明，配上浅色文字就变成一条看不见的导航——比没有导航更糟。
/// 所以这里的高度与配色全部显式写出，不依赖框架默认值。
interface NavItem {
  readonly to: string
  readonly label: string
  /// 该项之下是否还有子页面。池详情 `/pools/:accountIndex` 仍属于「池列表」。
  readonly hasChildren?: boolean
}

const navItems: readonly NavItem[] = [
  { to: '/graph', label: '总览' },
  { to: '/pools', label: '池列表', hasChildren: true },
  { to: '/leads', label: '带单员' },
  { to: '/copy', label: '跟单', hasChildren: true },
  { to: '/exchanges', label: '交易所' },
  { to: '/symbol-mappings', label: '标的映射' },
  { to: '/feasibility', label: '规模门槛' },
  { to: '/backtest', label: '回测面板' },
]

const route = useRoute()

/// 高亮由路径显式判定，不用 router 的 `router-link-active`。
///
/// 理由：`router-link-active` 的包含式匹配取决于**路由表的嵌套结构**，而池列表与池详情
/// 在本项目里是平级的兄弟路由，靠它匹配不到。显式判断既可靠，又把规则写在能读到的地方。
const activePath = computed(() => route.path)

function isActive(item: NavItem): boolean {
  return item.hasChildren
    ? activePath.value === item.to || activePath.value.startsWith(`${item.to}/`)
    : activePath.value === item.to
}
</script>

<template>
  <a-layout class="shell">
    <header class="topbar">
      <span class="brand">PoolPilot</span>

      <nav class="nav">
        <RouterLink
          v-for="item in navItems"
          :key="item.to"
          :to="item.to"
          class="nav-link"
          :class="{ 'nav-link-active': isActive(item) }"
        >
          {{ item.label }}
        </RouterLink>
      </nav>
    </header>

    <a-layout-content class="content">
      <RouterView />
    </a-layout-content>
  </a-layout>
</template>

<style scoped>
.shell {
  height: 100vh;
}

.topbar {
  flex: 0 0 auto;
  display: flex;
  align-items: center;
  gap: 32px;
  height: 56px;
  padding: 0 24px;
  background: rgb(29, 33, 41);
}

.brand {
  color: #fff;
  font-size: 17px;
  font-weight: 600;
  letter-spacing: 0.2px;
}

.nav {
  display: flex;
  align-items: center;
  gap: 4px;
  height: 100%;
}

.nav-link {
  display: flex;
  align-items: center;
  height: 100%;
  padding: 0 14px;
  color: rgba(255, 255, 255, 0.7);
  font-size: 14px;
  text-decoration: none;
  /* border-box 是必需的：下边框算进 100% 高度，否则链接会比导航条高出 2px */
  box-sizing: border-box;
  border-bottom: 2px solid transparent;
  transition: color 0.15s, border-color 0.15s;
}

.nav-link:hover {
  color: #fff;
}

/* 当前路由高亮。由 isActive() 显式判定后加类，见 script 里的说明。 */
.nav-link-active {
  color: #fff;
  border-bottom-color: rgb(var(--primary-6));
}

.content {
  padding: 16px 20px;
  overflow: auto;
  background: var(--color-fill-1);
}
</style>
