import { ref, type Ref } from 'vue'

import { menuApi, type MenuNodeDto } from '@/api/client'
import { router } from '@/router'
import { viewRegistry } from '@/router/viewRegistry'

/// 权限菜单树与动态路由注册。**App.vue 的导航与菜单管理页共用这一份数据**，
/// 谁改了菜单（增删改）都让这里刷新一次，两处同时跟着变，不会出现"页面里改了、导航没变"。
///
/// 为什么是模块级单例（与 useSession / useTheme 同一形制）：树是全局唯一的一份，
/// 每处 `useMenu()` 各存一份就会互相打架——一处 refresh 了，另一处还留着旧的。
const tree: Ref<MenuNodeDto[]> = ref([])

/// 动态路由是否已经注册过。守卫据此决定"要不要先注册再放行"。
const registered = ref(false)

/// 已注册的动态路由名。**removeRoute 只能按 name 操作**，所以必须自己记着注册过哪些；
/// 每次重新注册前先按这份名单清干净，免得改名/删除后留下指向旧路径的僵尸路由。
let registeredNames: string[] = []

/// 把树里的可进入节点挂成路由。
///
/// 只有 `menu` / `hidden` 且带 path 的节点才注册；`group` 是导航分组，不是页面。
/// 调用方（refresh）会先 clearRoutes() 清掉上一轮——改名/删除过的节点因此不会留下僵尸路由。
function applyRoutes(nodes: readonly MenuNodeDto[]): void {
  const names: string[] = []
  const seen = new Set<string>()

  const walk = (list: readonly MenuNodeDto[]): void => {
    for (const node of list) {
      const path = node.path

      // 只有 menu / hidden 且带 path 的节点是可进入的页面；group 是导航分组，不是页面
      if ((node.kind === 'menu' || node.kind === 'hidden') && path !== null && !seen.has(node.code)) {
        const component = viewRegistry[node.code]

        if (component === undefined) {
          // 表里没有这个 code 的视图：跳过而不是猜。注册一个没有组件的路由只会白屏，
          // 留一句日志比"点了没反应"好查。
          console.warn(`菜单「${node.code}」在 viewRegistry 中没有对应视图，已跳过注册`)
        } else {
          // code 是路由 name，必须唯一；重复的节点只认第一个（后端已保证唯一，这里是防线）
          seen.add(node.code)
          if (router.hasRoute(node.code)) {
            router.removeRoute(node.code)
          }
          router.addRoute({ path, name: node.code, component })
          names.push(node.code)
        }
      }

      if (node.children.length > 0) {
        walk(node.children)
      }
    }
  }

  walk(nodes)
  registeredNames = names
}

/// 拉一次菜单树并据此重注册路由。守卫（首次放行前）与菜单管理页（增删改后）都调它。
async function refresh(): Promise<void> {
  try {
    const nodes = await menuApi.tree()

    clearRoutes()
    tree.value = nodes
    applyRoutes(nodes)
    registered.value = true
  } catch (error) {
    // 读不到就当作"没有任何菜单"：注册标记仍要置上，否则守卫每次导航都会重试，
    // 把一次网络故障放大成一场请求风暴。真正的 401 由统一出口负责登出。
    console.warn('读取权限菜单失败', error)

    clearRoutes()
    registered.value = true
  }
}

/// 清掉全部动态路由并复位注册标记。登出时调用；下次登录由守卫重新注册。
function clearRoutes(): void {
  for (const name of registeredNames) {
    if (router.hasRoute(name)) {
      router.removeRoute(name)
    }
  }

  registeredNames = []
  tree.value = []
  registered.value = false
}

export function useMenu() {
  return { tree, registered, refresh, clearRoutes }
}
