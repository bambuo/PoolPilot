import { createRouter, createWebHistory } from 'vue-router'

import PoolList from '@/views/PoolList.vue'

/// 路由表。惰性加载非首屏页面。
///
/// 首页是**系统关系图**（`/graph`）：它一屏说清"池 → 带单员 → 跟单员"的关系，
/// 第一次打开的人从那里进入，比直接落在三百多个池的表格上更容易看懂这套系统。
/// 池列表仍然是最常用的页面，排在导航第二项。
export const router = createRouter({
  history: createWebHistory(),
  routes: [
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
