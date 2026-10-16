import { createRouter, createWebHistory } from 'vue-router'

import { useSession } from '@/composables/useSession'
import { useSetup } from '@/composables/useSetup'
import { useMenu } from '@/composables/useMenu'
import { knownViewLoader } from '@/router/viewRegistry'

/// 登录之后首页落哪儿。
///
/// 首页是**系统关系图**（`/graph`）：它一屏说清"池 → 带单员 → 跟单员"的关系，
/// 第一次打开的人从那里进入，比直接落在三百多个池的表格上更容易看懂这套系统。
/// 池列表仍然是最常用的页面，排在导航第二项。
///
/// 注意这是个**路径**而不是路由 name：`graph` 这条路由由菜单树动态注册，
/// 退出登录时会连同其它动态路由一起撤掉；那一刻按 name 跳转会直接报"没有这条路由"。
export const HOME_PATH = '/graph'

/// 路由表。**只保留与权限无关的那些**：公开页、旧地址重定向、无权限页与兜底。
///
/// 业务页面**不在这里**——它们由登录后的菜单树动态 `addRoute`（见 useMenu）。
/// 未授权的路由根本不注册，于是它的 chunk 也永远不会被请求：这是防 URL 枚举的关键，
/// 拿一个猜出来的地址来试，只会得到与"这个地址不存在"完全一样的回答。
export const router = createRouter({
  history: createWebHistory(),
  routes: [
    // ── 公开页（不需要登录）────────────────────────────────
    // 这四页共用同一个"没有顶栏"的外壳，见 App.vue；`meta.public` 是唯一的判据，
    // 路由守卫也读它——两处各写一份清单，早晚会有一处漏掉新页面。
    { path: '/login', name: 'login', component: knownViewLoader('login'), meta: { public: true } },
    {
      path: '/register',
      name: 'register',
      component: knownViewLoader('register'),
      meta: { public: true },
    },
    {
      // 首次初始化：配通知通道。**只在系统还没初始化时可进**——
      // 配好之后守卫会把人送回登录页（改通道去「通知 → 提供商」）
      path: '/setup',
      name: 'setup',
      component: knownViewLoader('setup'),
      meta: { public: true },
    },
    {
      // 第二步：已绑定输六位码、未绑定扫码 + 输码，两种状态同一页
      path: '/mfa',
      name: 'mfa',
      component: knownViewLoader('mfa'),
      meta: { public: true },
    },
    // 无权限 / 路径不存在的统一落点。**不是公开页**：它显示在工作台外壳里，
    // 未登录的人先被送去登录页，而不是从这一页反推出"站内有哪些地址"。
    { path: '/no-access', name: 'no-access', component: knownViewLoader('no-access') },

    { path: '/', redirect: HOME_PATH },

    // 兜底：**未匹配到任何已注册路由**（含刷新深链、无权限路径）统一落无权限页。
    //
    // 这里**不能用 `redirect: '/no-access'`**：redirect 会在守卫之前就被解析掉，
    // 于是刷新一个尚未注册的深链（例如 /pools/123）会先被改写成本页，守卫再没机会
    // 注册那条动态路由，合法用户也会被挡在门外。改成"挂同一个页面 + 由守卫改写"后，
    // 守卫始终能拿到原始路径：注册完重放它，命中就进，仍不命中才落到 /no-access。
    // 挂组件而不是空着：万一将来有一条路径绕过守卫，显示的也仍是"无权限"，而不是白屏。
    { path: '/:pathMatch(.*)*', name: 'not-found', component: knownViewLoader('no-access') },
  ],
})

/// 全局守卫：**默认要登录**，只有 `meta.public` 的页面例外。
///
/// 默认拒而不是默认许：新增页面时忘记标记，结果是"进不去"（一次点击就能发现），
/// 而不是"没登录也能看"（往往没人发现）。
router.beforeEach(async (to) => {
  const { user, loaded, load, redirectAfterLogin } = useSession()
  const setup = useSetup()
  const menu = useMenu()

  // 会话只问一次：它是服务端说了算的状态，但没必要每次跳转都打一遍 /me。
  // 之后由 401 统一处理负责纠正（见 main.ts 的 bindUnauthorizedHandler）。
  if (!loaded.value) {
    await load()
  }

  // 初始化状态也问一次（匿名接口）。**它优先于登录判断**：
  // 未初始化意味着注册验证码根本发不出去，先把人引到初始化页，注册才有意义。
  if (setup.needsSetup.value === null) {
    await setup.load()
  }

  const isPublic = to.meta.public === true

  // 未初始化：一切入口先落在初始化页。已经登录的人不拦（他早就过了注册那一关，
  // 通道可以稍后在「通知 → 提供商」里配）；点了"跳过"的人也不拦（那是一条明确的选择）。
  if (setup.needsSetup.value === true && !setup.skipped.value && user.value === null && to.name !== 'setup') {
    return { name: 'setup' }
  }

  // 已初始化：初始化页不该还能进——它不是第二个配置入口
  if (setup.needsSetup.value === false && to.name === 'setup' && !setup.skipped.value) {
    return user.value === null ? { name: 'login' } : { path: HOME_PATH }
  }

  if (!isPublic && user.value === null) {
    // 记下想去的地方，登录成功后回跳——但**只接受站内路径**，
    // 免得一个构造出来的 url 查询参数把刚登录的人带去别处
    redirectAfterLogin.value = to.fullPath.startsWith('/') && !to.fullPath.startsWith('//') ? to.fullPath : null

    return { name: 'login' }
  }

  // 已登录：**放行之前先确保动态路由已注册**。
  // 未注册时任何业务路径都匹配不到（会落到兜底），放行等于把合法页面当成不存在；
  // 这是本批引入"按权限注册路由"之后必须补上的一步。
  if (user.value !== null && !menu.registered.value) {
    await menu.refresh()

    // 刚注册完，重放一次当前导航：目标路径可能正是在这一步才存在的路由。
    // 只带 path/query/hash 而**不带 name**——此刻 name 还是兜底的 `not-found`，
    // 带上它就会按名字重新解析回兜底，重放等于白做。`replace` 免得在历史里多留一条。
    return { path: to.path, query: to.query, hash: to.hash, replace: true }
  }

  if (isPublic && user.value !== null && to.name !== 'mfa') {
    // 已登录的人不该停在登录/注册页；但 MFA 第二步例外——
    // 那时会话刚签发、而挑战还没用完（绑定同理）
    return { path: HOME_PATH }
  }

  // 走到这里 = 已登录且路由已注册。仍未命中任何已注册路由（含刷新深链、无权限路径）的，
  // 统一落无权限页——**不区分 403 与 404**，页面文案不泄露那条路径到底存不存在。
  if (to.name === 'not-found') {
    return { name: 'no-access', replace: true }
  }

  return true
})
