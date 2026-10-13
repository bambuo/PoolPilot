<script setup lang="ts">
import { computed, type Component } from 'vue'
import { RouterLink, RouterView, useRoute } from 'vue-router'

import { useTheme } from '@/composables/useTheme'

// 图标按需导入（与 HeaderHint 里的用法一致：不全局注册，用到哪几个进哪几个）
import {
  IconDashboard,
  IconFilter,
  IconMoon,
  IconSun,
  IconHistory,
  IconList,
  IconRelation,
  IconScan,
  IconStorage,
  IconUserGroup,
} from '@arco-design/web-vue/es/icon'

/// 工作台外壳。导航项按「总览 → 观察 → 跟单 → 分析 → 回测」的使用顺序排列：
/// 首页是关系图（一屏说清三层结构），之后是池列表与带单员（被观察的池），
/// 才是跟单（下场的账户），最后是门槛与回测。
///
/// 不要用 `a-layout-header` 的默认外观：Arco 给它的 CSS 只有 `flex: 0 0 auto`，
/// **既没有高度也没有背景**（它的示例都自己传 height）。不设高度会让它塌成内容高度、
/// 背景透明，导航就变成一条看不见的横线——比没有导航更糟。
/// 所以高度、布局与描边都显式写出；而**颜色一律取令牌**（文本色、底色、悬停底色），
/// 顶栏因此跟着主题一起变，而不是像第一版那样写死一段深灰。
interface NavItem {
  readonly to: string
  readonly label: string
  /// 图标。**每个都要与那一页在做的事对得上**，不是装饰：
  /// 菜单是唯一一屏能看到全部功能的地方，图标在这里的作用是"想起那一页是干什么的"。
  /// 选取理由逐条写在下面，改图标时先读一遍——把"筛选"的图标挂到"回测"上，
  /// 比没有图标更糟：它会给出一个错的提示。
  readonly icon: Component
  /// 该项之下是否还有子页面。池详情 `/pools/:accountIndex` 仍属于「池列表」。
  readonly hasChildren?: boolean
}

const navItems: readonly NavItem[] = [
  // 那一页是一屏系统关系图（池 → 带单员 → 跟单员），仪表盘是最贴切的"总览"
  { to: '/graph', label: '总览', icon: IconDashboard },
  // 表格式的池目录
  { to: '/pools', label: '池列表', icon: IconList, hasChildren: true },
  // "一个带单员负责盯着一个池：订阅它的持仓与成交流"——扫描/盯盘
  { to: '/leads', label: '带单员', icon: IconScan },
  // 跟单员是各自独立记账的账户，一个带单员下可挂多个
  { to: '/copy', label: '跟单', icon: IconUserGroup, hasChildren: true },
  // 与新建/编辑交易所弹窗的标题图标同款（那一栏存的是配置与凭据）
  { to: '/exchanges', label: '交易所', icon: IconStorage },
  // 映射＝"源标的 → 目标合约"的对应关系
  { to: '/symbol-mappings', label: '标的映射', icon: IconRelation },
  // 门槛＝这本金够不够跟，是该被筛出来的
  { to: '/feasibility', label: '规模门槛', icon: IconFilter },
  // 回测＝拿历史数据跑一遍
  { to: '/backtest', label: '回测面板', icon: IconHistory },
]

const route = useRoute()

// 主题：开关在顶栏最右。整页跟着 Arco 的暗色属性翻转，配色令牌的说明见 useTheme
const { theme, toggle: toggleTheme } = useTheme()

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
      <!-- 标志与文字同一个链接般的整体：图 + 名。标记本身纯装饰（名字就在旁边），
           所以 alt 留空并 aria-hidden——屏幕阅读器读两遍"PoolPilot"没有意义 -->
      <span class="brand">
        <img class="brand-mark" src="/logo.svg" alt="" aria-hidden="true" />
        <span>PoolPilot</span>
      </span>

      <nav class="nav">
        <RouterLink
          v-for="item in navItems"
          :key="item.to"
          :to="item.to"
          class="nav-link"
          :class="{ 'nav-link-active': isActive(item) }"
        >
          <component :is="item.icon" class="nav-icon" />
          <span>{{ item.label }}</span>
        </RouterLink>
      </nav>

      <!-- 主题开关。按钮上只有图标、没有文字，名字必须由 aria-label 给出——
           读屏读的是"切换到深色主题"，而不是一个没有名字的按钮 -->
      <button
        type="button"
        class="theme-toggle"
        :aria-label="theme === 'dark' ? '切换到浅色主题' : '切换到深色主题'"
        :title="theme === 'dark' ? '切换到浅色主题' : '切换到深色主题'"
        @click="toggleTheme"
      >
        <component :is="theme === 'dark' ? IconSun : IconMoon" />
      </button>
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

  /* 底色取令牌，跟着主题走（暗色下是 #232324，与原来那段写死的深灰几乎一致）。
     描边是必须的：浅色主题下顶栏与内容区都是浅色，没有这条线两者会连成一片 */
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
}

/* 标志用 public/logo.svg：与浏览器标签页图标是同一份文件，一处改动两边跟着变 */
.brand-mark {
  display: block;
  width: 22px;
  height: 22px;
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
  gap: 6px;
  height: 100%;
  padding: 0 14px;
  color: var(--color-text-3);
  font-size: 14px;
  text-decoration: none;
  /* border-box 是必需的：下边框算进 100% 高度，否则链接会比导航条高出 2px */
  box-sizing: border-box;
  border-bottom: 2px solid transparent;
  transition: color 0.15s, border-color 0.15s;
}

/* 图标比文字小一档并与文字同色：它是提示，不是主角——比文字还显眼的图标会把菜单读成一排图案 */
.nav-icon {
  font-size: 15px;
  color: inherit;
}

.nav-link:hover {
  color: var(--color-text-1);
}

/* 当前路由高亮。由 isActive() 显式判定后加类，见 script 里的说明。 */
.nav-link-active {
  color: var(--color-text-1);
  border-bottom-color: rgb(var(--primary-6));
}

/* 主题开关：顶栏是手写样式（见文件顶部说明），按钮也一样手写——
   用 Arco 的按钮组件会让它的字色跟着主题走，而顶栏**在两个主题下都是深色**。
   margin-left: auto 把它推到最右，导航保持原来的靠左排布 */
.theme-toggle {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 30px;
  height: 30px;
  margin-left: auto;
  padding: 0;
  font-size: 16px;
  color: var(--color-text-3);
  background: transparent;
  border: 0;
  border-radius: 6px;
  cursor: pointer;
  transition: color 0.15s, background 0.15s;
}

.theme-toggle:hover {
  color: var(--color-text-1);
  background: var(--color-fill-2);
}

.content {
  padding: 16px 20px;
  overflow: auto;
  background: var(--color-fill-1);
}

/* 页面底板与基础文字色。
   Arco 只在 body 上定义**令牌**，从不设 body 自己的颜色与背景——实测：
   文字色继承的是浏览器默认黑；背景则是**浏览器的白画布**（html/body 都是透明的）。
   浅色主题下这两件事碰巧都成立，切到暗色就全露出来：
     · 深字落在深底上（关系图的池名几乎看不见）；
     · `.content` 的 --color-fill-1 在暗色里是"4% 白"的半透明层（fill 令牌本就设计成
       叠在页面底色上），叠在白画布上整页仍是白的，加载遮罩还被衬成一片浅灰。
   所以底板必须由应用自己给一个**不透明**的令牌：bg-1（浅 #fff / 暗 #17171a）。
   这是全应用唯一一条要穿透到 body 的规则，用 :global 明写在这里 */
:global(body) {
  color: var(--color-text-1);
  background: var(--color-bg-1);
}
</style>
