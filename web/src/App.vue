<script setup lang="ts">
import { computed, type Component } from 'vue'
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router'

import type { MenuNodeDto } from '@/api/client'
import { resolveIcon } from '@/components/iconRegistry'
import { useMenu } from '@/composables/useMenu'
import { useSession } from '@/composables/useSession'
import { useSignOut } from '@/composables/useSignOut'
import { useTheme } from '@/composables/useTheme'

// 顶栏自用的图标（与导航项不同，导航项的图标由后端数据经 iconRegistry 解析）
import { IconDown, IconMoon, IconSun, IconUser } from '@arco-design/web-vue/es/icon'

/// 工作台外壳。导航**由数据驱动**：来自 `/api/menus/tree`（见 useMenu），
/// 顺序、标题、图标、层级都由后端那份菜单决定，前端不再写死一张 navItems。
///
/// 为什么不写死：本批引入权限之后，"谁能看到哪些页"是数据；写死的导航会在人没有权限时
/// 依然点得进去（然后被守卫弹回来），那种"看得到、进不去"比看不到更让人困惑。
///
/// 不要用 `a-layout-header` 的默认外观：Arco 给它的 CSS 只有 `flex: 0 0 auto`，
/// **既没有高度也没有背景**（它的示例都自己传 height）。不设高度会让它塌成内容高度、
/// 背景透明，导航就变成一条看不见的横线——比没有导航更糟。
/// 所以高度、布局与描边都显式写出；而**颜色一律取令牌**（文本色、底色、悬停底色），
/// 顶栏因此跟着主题一起变。
///
/// 分组用一个**单一形状**的条目表达（而不是"分组/链接"两种形状的联合）：
/// 模板里读同一个字段、不必靠 v-if 收窄类型，写起来更直白。
interface NavEntry {
  readonly kind: 'group' | 'link'
  /// v-for 的 key（节点 code，唯一且稳定）。
  readonly key: string
  /// 分组标题，或链接文字。
  readonly label: string
  /// 链接目标的路径。分组条目为空串（它不可点击，模板里也不读它）。
  readonly to: string
  readonly icon: Component
  /// 分组的子项（缩进显示）；链接条目为空数组。
  readonly links: readonly NavLink[]
}

interface NavLink {
  readonly to: string
  readonly label: string
  readonly icon: Component
}

const route = useRoute()
const router = useRouter()
const { user } = useSession()
const { tree } = useMenu()
const { signingOut, signOut } = useSignOut()

/// 公开页（登录 / 注册 / 初始化 / MFA）不套工作台外壳：那时还没有"当前用户"，
/// 一排导航点进去只会被守卫弹回登录页。判据与路由守卫共用同一处 meta。
const isPublicPage = computed(() => route.meta.public === true)

/// 从一段子树里收集**可进入且进导航**的菜单（`menu` 且 `isVisible` 且有 path）。
///
/// `hidden` 不进导航（它本就是"能进但不露面"的页面，如各种详情页）；
/// `isVisible === false` 的同样不显示——但**它的路由仍然注册**（由 useMenu 负责），
/// 所以它只是不露面，不是进不去。
function collectLinks(nodes: readonly MenuNodeDto[]): NavLink[] {
  const links: NavLink[] = []

  for (const node of nodes) {
    if (node.kind === 'menu' && node.isVisible && node.path !== null) {
      links.push({ to: node.path, label: node.title, icon: resolveIcon(node.icon) })
    }

    if (node.children.length > 0) {
      links.push(...collectLinks(node.children))
    }
  }

  return links
}

/// 把菜单树摊成顶栏要渲染的条目序列（保持后端给的顺序）。
///
/// 一级 `group` 变成一个分组条目（其下缩进显示可见的菜单子项；一个可见子项都没有的分组
/// 连标签也不显示——空标题没有意义）；一级 `menu` 变成一条普通链接；其余（`hidden`、
/// 不可见）不进导航。
const navEntries = computed<NavEntry[]>(() => {
  const entries: NavEntry[] = []

  for (const node of tree.value) {
    if (node.kind === 'group') {
      const links = collectLinks(node.children)

      if (links.length > 0) {
        entries.push({
          kind: 'group',
          key: node.code,
          label: node.title,
          to: '',
          icon: resolveIcon(node.icon),
          links,
        })
      }
    } else if (node.kind === 'menu' && node.isVisible && node.path !== null) {
      entries.push({
        kind: 'link',
        key: node.code,
        label: node.title,
        to: node.path,
        icon: resolveIcon(node.icon),
        links: [],
      })
    }
  }

  return entries
})

/// 高亮由路径显式判定，不用 router 的 `router-link-active`。
///
/// 理由：`router-link-active` 的包含式匹配取决于**路由表的嵌套结构**，而池列表与池详情
/// 在本项目里是平级的兄弟路由，靠它匹配不到。显式判断既可靠，又把规则写在能读到的地方。
/// 规则用"路径前缀"而不是"是否列在某个子菜单里"：详情页（`/pools/123`）属于 `/pools`
/// 这件事，只有路径本身说得清——菜单树里并没有"谁是谁的子页"这条数据。
const activePath = computed(() => route.path)

function isActivePath(path: string): boolean {
  return activePath.value === path || activePath.value.startsWith(`${path}/`)
}

/// 分组自身的高亮：它的**任一子项**是当前页时，分组标题也点亮——
/// 否则进了「菜单管理」后，「系统」看上去像没被选中，读的人会以为导航丢了当前页。
function isGroupActive(entry: NavEntry): boolean {
  return entry.links.some((link) => isActivePath(link.to))
}

/// 下拉子项的行内跳转。用显式函数而不是 `@click="router.push(...)"`：
/// push 返回 Promise，直接写在模板里会留下一个没人接的返回值。
function navigateTo(path: string): void {
  void router.push(path)
}

// 主题：开关在顶栏最右。整页跟着 Arco 的暗色属性翻转，配色令牌的说明见 useTheme
const { theme, toggle: toggleTheme } = useTheme()
</script>

<template>
  <!-- 公开页（登录 / 注册 / 初始化 / MFA）：只有一份居中的卡片，没有顶栏与导航 -->
  <RouterView v-if="isPublicPage" />

  <a-layout v-else class="shell">
    <header class="topbar">
      <!-- 标志与文字同一个链接般的整体：图 + 名。标记本身纯装饰（名字就在旁边），
           所以 alt 留空并 aria-hidden——屏幕阅读器读两遍"PoolPilot"没有意义 -->
      <span class="brand">
        <img class="brand-mark" src="/logo.svg" alt="" aria-hidden="true" />
        <span>PoolPilot</span>
      </span>

      <nav class="nav">
        <template v-for="entry in navEntries" :key="entry.key">
          <!-- 一级分组（如「系统」）：它不可直接进入，点开/悬停是下拉子菜单。
               触发器复用一级链接的样式，因此与旁边的普通项同一行、同一套高亮规则。 -->
          <a-dropdown v-if="entry.kind === 'group'" :trigger="['hover', 'click']" position="bl">
            <span
              class="nav-link nav-link-trigger"
              :class="{ 'nav-link-active': isGroupActive(entry) }"
            >
              <component :is="entry.icon" class="nav-icon" />
              <span>{{ entry.label }}</span>
              <IconDown class="nav-caret" />
            </span>

            <template #content>
              <a-doption
                v-for="link in entry.links"
                :key="link.to"
                @click="navigateTo(link.to)"
              >
                <span class="nav-dropdown-item">
                  <component :is="link.icon" class="nav-icon" />
                  <span>{{ link.label }}</span>
                </span>
              </a-doption>
            </template>
          </a-dropdown>

          <RouterLink
            v-else
            :to="entry.to"
            class="nav-link"
            :class="{ 'nav-link-active': isActivePath(entry.to) }"
          >
            <component :is="entry.icon" class="nav-icon" />
            <span>{{ entry.label }}</span>
          </RouterLink>
        </template>

        <!-- 一个可点菜单都没有（无任何权限）：给一句明确的说明，而不是留下一条空导航。
             空白会让人以为导航坏了，而这里的事实是"这个账号确实没有可进的页面"。 -->
        <span v-if="navEntries.length === 0" class="nav-empty">无可用菜单</span>
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

      <!-- 账号：只放一个图标，**不显示邮箱**——顶栏是导航，不是一个"我是谁"的展示位；
           邮箱进 title（悬停可见）而不是占一行宽度。菜单里的项以后会长（个人信息、切换账号…），
           所以从现在起就按"点开是一张菜单"来组织 -->
      <a-dropdown v-if="user" trigger="click" class="user-menu">
        <!-- title/aria-label 挂在 button 上而不是外层下拉：Arco 的 dropdown 不把
             `title` 透到 DOM，挂外层等于没有提示（这一步实测踩过） -->
        <button
          type="button"
          class="user-button"
          :title="`当前登录：${user.email}`"
          :aria-label="`账号（当前登录：${user.email}）`"
        >
          <IconUser class="user-icon" />
        </button>

        <template #content>
          <a-doption :disabled="signingOut" @click="signOut">退出登录</a-doption>
        </template>
      </a-dropdown>
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

  /* 加了「通知」与账号菜单之后，1280px 宽的窗口里导航会被挤到换行——
     换行后的顶栏是一团两行文字，比缺一项还难看。间距收到 20px，
     并让下面的导航项与账号按钮各自「不再折行、放不下就截断」 */
  gap: 20px;
  height: 56px;
  padding: 0 20px;

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

/* 无权限时的说明：与导航文字同一档但更淡，静静说明"这里没有可点的东西" */
.nav-empty {
  font-size: 13px;
  color: var(--color-text-4);
}

/* 分组（如「系统」）：点开/悬停是下拉子菜单，触发器本身不可直接进入。
   它复用 .nav-link 的外观，因此与旁边的普通项同高、同色、用同一套高亮规则；
   这里只补两件它没有的事：鼠标指针，以及一个"可展开"的指示三角。 */
.nav-link-trigger {
  cursor: pointer;
}

.nav-caret {
  font-size: 12px;
  color: var(--color-text-4);
}

/* 下拉里的子项：图标 + 文字，图标仍走 iconRegistry 的那一套解析 */
.nav-dropdown-item {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.nav-link {
  display: flex;
  align-items: center;
  gap: 6px;
  height: 100%;
  padding: 0 10px;
  /* 标签不许折行：「总览」被挤成「总 / 览」两行是这一版顶栏踩过的坑 */
  white-space: nowrap;
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

/* 当前路由高亮。由 isActivePath() 显式判定后加类，见 script 里的说明。 */
.nav-link-active {
  color: var(--color-text-1);
  border-bottom-color: rgb(var(--primary-6));
}

/* 账号按钮与主题开关同一形制：一个图标、无边框、悬停一层浅底。
   顺序是主题在前、账号在后——账号是这条右侧控件带的最后一个 */
.user-button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 30px;
  height: 30px;
  padding: 0;
  font: inherit;
  color: var(--color-text-3);
  background: transparent;
  border: 0;
  border-radius: 6px;
  cursor: pointer;
  transition: color 0.15s, background 0.15s;
}

.user-button:hover {
  color: var(--color-text-1);
  background: var(--color-fill-2);
}

.user-icon {
  font-size: 16px;
}

/* 主题开关：顶栏是手写样式（见文件顶部说明），按钮也一样手写——
   用 Arco 的按钮组件会让它的字色跟着主题走，而顶栏**在两个主题下都是深色** */
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
