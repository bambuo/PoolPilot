import type { Component } from 'vue'

/// 视图注册表：**code → 视图组件（惰性）**。
///
/// 后端只决定"给当前用户注册哪些 code"（见 useMenu），**哪个 code 落在哪一个 .vue 文件是前端的事**，
/// 全部写死在这张表里。把这两件事分开的要害在于：后端即便改动了一个 code，前端也不会因此去加载
/// 一个它没打算放的模块——表里查不到就只是"不注册"（并在控制台留一句），而不是执行别的什么。
///
/// 原先 router/index.ts 里散落的静态导入统一收在这里：全应用"有哪些页面"只有一个出处，
/// 树上的节点也才能靠 code 找到它的视图。
///
/// 视图文件共 19 个（17 个既有 + 本批新增的用户管理与角色管理）。
/// 公开带单/公开跟单两个浏览页**不在这张表里**：它们不属于按权限注册的集合，
/// 由静态路由直接懒加载（见 router/index.ts）。
export type ViewLoader = () => Promise<Component>

export const viewRegistry: Record<string, ViewLoader> = {
  // ── 公开页：由静态路由直接引用（登录之前就要能用，不能等菜单树）──
  login: () => import('@/views/LoginView.vue'),
  register: () => import('@/views/RegisterView.vue'),
  setup: () => import('@/views/SetupView.vue'),
  mfa: () => import('@/views/MfaView.vue'),

  // ── 业务页：由菜单树决定注册哪些（未授权的 code 根本不注册，chunk 也不会被请求）──
  graph: () => import('@/views/GraphView.vue'),
  pools: () => import('@/views/PoolList.vue'),
  // 详情页是 hidden 节点：可被深链命中，但不进导航
  'pool-detail': () => import('@/views/PoolDetailView.vue'),
  leads: () => import('@/views/LeadListView.vue'),
  'copy-list': () => import('@/views/FollowListView.vue'),
  'copy-detail': () => import('@/views/FollowDetailView.vue'),
  exchanges: () => import('@/views/ExchangeListView.vue'),
  'symbol-mappings': () => import('@/views/SymbolMappingView.vue'),
  feasibility: () => import('@/views/FeasibilityView.vue'),
  backtest: () => import('@/views/BacktestView.vue'),
  notifications: () => import('@/views/NotificationView.vue'),
  'system-menus': () => import('@/views/MenuManageView.vue'),
  'system-users': () => import('@/views/UserManageView.vue'),
  'system-roles': () => import('@/views/RoleManageView.vue'),

  // ── 无权限 / 未匹配的落点：静态路由直接引用 ──
  'no-access': () => import('@/views/NoAccessView.vue'),
}

/// 取一个**代码里写死存在**的视图加载器（静态路由用）。
///
/// 与 `viewRegistry[code]` 的区别在类型与失败方式：表是 `Record<string, …>`，
/// 索引出来带 `undefined`，直接塞进路由表的 `component` 是通不过类型检查的；
/// 而且这里取不到属于程序写错了——宁可当场抛，也不要把一个空组件塞进去换来一片白屏。
export function knownViewLoader(code: string): ViewLoader {
  const loader = viewRegistry[code]

  if (loader === undefined) {
    throw new Error(`viewRegistry 缺少视图：${code}`)
  }

  return loader
}
