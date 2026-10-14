import { createRouter, createWebHistory } from 'vue-router'

import { useSession } from '@/composables/useSession'

import PoolList from '@/views/PoolList.vue'

/// 路由表。惰性加载非首屏页面。
///
/// 首页是**系统关系图**（`/graph`）：它一屏说清"池 → 带单员 → 跟单员"的关系，
/// 第一次打开的人从那里进入，比直接落在三百多个池的表格上更容易看懂这套系统。
/// 池列表仍然是最常用的页面，排在导航第二项。
export const router = createRouter({
  history: createWebHistory(),
  routes: [
    // ── 公开页（不需要登录）────────────────────────────────
    // 这三页共用同一个"没有顶栏"的外壳，见 App.vue；`meta.public` 是唯一的判据，
    // 路由守卫也读它——两处各写一份清单，早晚会有一处漏掉新页面。
    {
      path: '/login',
      name: 'login',
      component: () => import('@/views/LoginView.vue'),
      meta: { public: true },
    },
    {
      path: '/register',
      name: 'register',
      component: () => import('@/views/RegisterView.vue'),
      meta: { public: true },
    },
    {
      // 第二步：已绑定输六位码、未绑定扫码 + 输码，两种状态同一页
      path: '/mfa',
      name: 'mfa',
      component: () => import('@/views/MfaView.vue'),
      meta: { public: true },
    },
    { path: '/', redirect: '/graph' },
    {
      path: '/graph',
      name: 'graph',
      component: () => import('@/views/GraphView.vue'),
    },
    { path: '/pools', name: 'pools', component: PoolList },
    // 带单员：被观察的池。排在跟单之前——先有观察，才有可订阅的信号。
    {
      path: '/leads',
      name: 'leads',
      component: () => import('@/views/LeadListView.vue'),
    },
    {
      path: '/copy',
      name: 'copy-list',
      component: () => import('@/views/FollowListView.vue'),
    },
    {
      // 参数是**跟单员自己的 Id**（自增），不是池索引：同一个池可以挂多个跟单员。
      path: '/copy/:id',
      name: 'copy-detail',
      component: () => import('@/views/FollowDetailView.vue'),
      props: true,
    },
    // 旧路径保留为重定向而非直接删除：这一节原先叫「纸面跟单台账」，
    // 已经打开的页面和书签不该因为改名而失效。
    { path: '/ledger', redirect: '/copy' },
    { path: '/ledger/:accountIndex', redirect: (to) => `/copy/${String(to.params.accountIndex)}` },
    // 交易所：各平台的连接配置与凭据。它是"这套系统能接哪些平台"的登记处，
    // 与池/带单员那些数据对象不同，所以排在数据类页面之后。
    {
      path: '/exchanges',
      name: 'exchanges',
      component: () => import('@/views/ExchangeListView.vue'),
    },
    // 标的映射：跨交易所的"源标的 → 目标合约"对照表。跟单员收到信号时查的就是它，
    // 所以它紧跟在交易所配置后面。
    {
      path: '/symbol-mappings',
      name: 'symbol-mappings',
      component: () => import('@/views/SymbolMappingView.vue'),
    },
    {
      path: '/feasibility',
      name: 'feasibility',
      component: () => import('@/views/FeasibilityView.vue'),
    },
    {
      path: '/backtest',
      name: 'backtest',
      component: () => import('@/views/BacktestView.vue'),
    },
    // 通知：通道（提供商）/ 样板 / 投递记录。三块在同一页的三个标签下——
    // 它们描述同一件事的三个面（谁能发、发什么、发了什么），拆成三个路由只多三处入口。
    {
      path: '/notifications',
      name: 'notifications',
      component: () => import('@/views/NotificationView.vue'),
    },
    {
      path: '/pools/:accountIndex',
      name: 'pool-detail',
      component: () => import('@/views/PoolDetailView.vue'),
      props: true,
    },
    // 「实时监控台」已撤：池详情页打开时现查即可，跟单本身就是后台实时监控，那一页没有存身之处。
    // 保留重定向而不是让它变成 404 白屏——书签与已打开的页面不该因为撤项而失效。
    { path: '/monitor', redirect: '/copy' },
    // 兜底：未知路径一律回首页（关系图）。没有这一条时 vue-router 匹配不到就什么都不渲染，
    // 使用者看到的是"导航在、内容空白"的白屏，比跳错页更难判断。
    { path: '/:pathMatch(.*)*', redirect: '/graph' },
  ],
})

/// 全局守卫：**默认要登录**，只有 `meta.public` 的页面例外。
///
/// 默认拒而不是默认许：新增页面时忘记标记，结果是"进不去"（一次点击就能发现），
/// 而不是"没登录也能看"（往往没人发现）。
router.beforeEach(async (to) => {
  const { user, loaded, load, redirectAfterLogin } = useSession()

  // 会话只问一次：它是服务端说了算的状态，但没必要每次跳转都打一遍 /me。
  // 之后由 401 统一处理负责纠正（见 main.ts 的 bindUnauthorizedHandler）。
  if (!loaded.value) {
    await load()
  }

  const isPublic = to.meta.public === true

  if (!isPublic && user.value === null) {
    // 记下想去的地方，登录成功后回跳——但**只接受站内路径**，
    // 免得一个构造出来的 url 查询参数把刚登录的人带去别处
    redirectAfterLogin.value = to.fullPath.startsWith('/') && !to.fullPath.startsWith('//') ? to.fullPath : null

    return { name: 'login' }
  }

  if (isPublic && user.value !== null && to.name !== 'mfa') {
    // 已登录的人不该停在登录/注册页；但 MFA 第二步例外——
    // 那时会话刚签发、而挑战还没用完（绑定同理）
    return { name: 'graph' }
  }

  return true
})
