import { createRouter, createWebHistory } from 'vue-router'

import { usePublicPageStatus } from '@/composables/usePublicPageStatus'
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

/// 匿名访客的进站落点：**公开带单页**。
///
/// 有了对外可读的那两页之后，"/" 再一律把人送去登录页就是错的：陌生人输入站点地址
/// （或从别处点进来）看到的应该是一屏内容，而不是一堵要求注册 + MFA 的墙。
/// 选带单页而不是跟单页：跟单页没人开过时是空的，第一屏什么都不说；带单页一进来就能
/// 回答"这台机器在看哪些池"。要换只改这一个常量。
export const PUBLIC_HOME_PATH = '/public/leads'

/// 只有登录流程本身的那几页，才"不允许已登录的人停留"。
///
/// 判据以前是 meta.public——那是"这四页都没登录什么事"还成立时的写法。
/// 公开带单/公开跟单同样是 meta.public（要走无导航的裸外壳），却是登录用户
/// 也该能看的页面：继续沿用 meta.public 会造成"登录后一点公开页就被弹回总览"。
/// 改成显式集合：新增公开页默认**不**被弹走，新增登录流程页要记得加进来。
/// `mfa` 不在这里：那一刻会话刚签发、挑战还没用完（绑定同理），要把人留在本页走完。
const AUTH_ONLY_ROUTE_NAMES: string[] = ['login', 'register', 'setup']

/// 认证流程页。服务端关闭登录/注册时，这几页不应该再给匿名访客看到表单——
/// 守卫读到 `/api/public/status` 的 `accountsEnabled === false` 就把人送回公开首页。
/// 后端仍然会 404，守卫只是让界面不展示一条注定失败的流程。
const ACCOUNTS_ROUTE_NAMES: string[] = ['login', 'register', 'mfa', 'setup']

/// 路由表。**只保留与权限无关的那些**：公开页、旧地址重定向、无权限页与兜底。
///
/// 业务页面**不在这里**——它们由登录后的菜单树动态 `addRoute`（见 useMenu）。
/// 未授权的路由根本不注册，于是它的 chunk 也永远不会被请求：这是防 URL 枚举的关键，
/// 拿一个猜出来的地址来试，只会得到与"这个地址不存在"完全一样的回答。
export const router = createRouter({
  history: createWebHistory(),
  routes: [
    // ── 公开页（不需要登录）────────────────────────────────
    // 这些页共用同一个"没有工作台外壳"的裸 RouterView（见 App.vue），`meta.public` 是
    // 外壳的判据；但**守卫不把已登录用户一律踢开**——公开带单/公开跟单正是登录用户
    // 也该看的页（"登录了不许停"只适用于下面 AUTH_ONLY_ROUTE_NAMES 那几个登录流程页）。
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
    // 公开带单 / 公开跟单：匿名可达，登录用户也照样能看（这就是它们不在
    // AUTH_ONLY_ROUTE_NAMES 里的原因）。**静态注册 + 直接懒加载**：它们不属于
    // 按权限动态注册的那一组，不经 viewRegistry、也不让 useMenu 参与。
    // `meta.showWhenNeedsSetup`：系统未初始化时守卫**不**把匿名访客送去 setup 页，
    // 页面自己渲染"系统尚未就绪"（口径 11）——跳走等于把公开页藏进初始化之后。
    {
      path: '/public/leads',
      name: 'public-leads',
      component: () => import('@/views/PublicLeadListView.vue'),
      meta: { public: true, showWhenNeedsSetup: true },
    },
    {
      path: '/public/follows',
      name: 'public-follows',
      component: () => import('@/views/PublicFollowListView.vue'),
      meta: { public: true, showWhenNeedsSetup: true },
    },
    // 无权限 / 路径不存在的统一落点。**不是公开页**：它显示在工作台外壳里，
    // 未登录的人先被送去登录页，而不是从这一页反推出"站内有哪些地址"。
    { path: '/no-access', name: 'no-access', component: knownViewLoader('no-access') },

    // 进站根路径。**不能用 `redirect`**（和下面的兜底同一个理由）：redirect 在守卫之前就被
    // 解析掉，那一刻还没问过 /me，"该去工作台还是该去公开页"根本无从判断——一律送去
    // /graph 的结果是匿名访客在站点地址上看到一堵登录墙。改成挂个占位组件、由守卫在
    // 知道"是谁"之后改写。
    { path: '/', name: 'entry', component: knownViewLoader('no-access') },

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

  // 服务端关闭登录/注册时，认证流程页不再对匿名访客展示表单；直接回落公开首页。
  // 已有会话的用户仍按原逻辑走（登录页会把他送回工作台）。
  if (user.value === null && typeof to.name === 'string' && ACCOUNTS_ROUTE_NAMES.includes(to.name)) {
    const pageStatus = usePublicPageStatus()
    await pageStatus.load()

    if (pageStatus.status.value?.accountsEnabled === false) {
      return { path: PUBLIC_HOME_PATH, replace: true }
    }
  }

  const isPublic = to.meta.public === true

  // 未初始化：一切入口先落在初始化页。已经登录的人不拦（他早就过了注册那一关，
  // 通道可以稍后在「通知 → 提供商」里配）；点了"跳过"的人也不拦（那是一条明确的选择）。
  // `showWhenNeedsSetup` 的公开页同样不拦：它们由页面自己渲染"系统尚未就绪"（口径 11），
  // 把人跳去 setup 等于对匿名访客隐藏了"这里有一个公开页"这一事实。
  if (
    setup.needsSetup.value === true &&
    !setup.skipped.value &&
    user.value === null &&
    to.name !== 'setup' &&
    to.meta.showWhenNeedsSetup !== true
  ) {
    return { name: 'setup' }
  }

  // 进站根路径：此刻已经问过"是谁"，才谈得上选落点。
  // 已登录 → 工作台首页；匿名 → 公开带单页（不是登录墙，见 PUBLIC_HOME_PATH）。
  if (to.name === 'entry') {
    return { path: user.value === null ? PUBLIC_HOME_PATH : HOME_PATH, replace: true }
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

  if (user.value !== null && typeof to.name === 'string' && AUTH_ONLY_ROUTE_NAMES.includes(to.name)) {
    // 已登录的人不该停在登录/注册页（初始化页同理，那是登录之前的一步）。
    // 判据是上面的显式集合而不是 meta.public——公开带单/公开跟单是登录用户也该能看的。
    return { path: HOME_PATH }
  }

  // 走到这里 = 已登录且路由已注册。仍未命中任何已注册路由（含刷新深链、无权限路径）的，
  // 统一落无权限页——**不区分 403 与 404**，页面文案不泄露那条路径到底存不存在。
  if (to.name === 'not-found') {
    return { name: 'no-access', replace: true }
  }

  return true
})
