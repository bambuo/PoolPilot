import { ref } from 'vue'
import { Message } from '@arco-design/web-vue'
import { useRouter } from 'vue-router'

import { authApi } from '@/api/client'
import { disconnectFollowerHub } from '@/composables/useFollowerLive'
import { disconnectMonitorHub } from '@/composables/useMonitorHub'
import { useMenu } from '@/composables/useMenu'
import { usePermission } from '@/composables/usePermission'
import { useSession } from '@/composables/useSession'
import { PUBLIC_HOME_PATH } from '@/router'

/// 退出登录。集中一处：它要按固定顺序做完一串收尾（断连接、清会话、撤动态路由、清权限缓存、
/// 回匿名落点），顺序或遗漏任一步都会留下尾巴——写两遍必然有一遍漏掉。
///
/// 顶栏的账号菜单与「无权限」页上的按钮共用它，两处行为一致。
export function useSignOut() {
  const router = useRouter()
  const { clear: clearSession } = useSession()
  const { clearRoutes } = useMenu()
  const { clear: clearPermissions } = usePermission()

  const signingOut = ref(false)

  async function signOut(): Promise<void> {
    signingOut.value = true

    try {
      await authApi.logout()

      // 先断开实时连接再清会话：这些连接带着刚才的 Cookie，
      // 留着它们会在服务端变成一串没人认领的 401。
      // **清理失败不阻断退出**：断连只是收尾，不能让它把"已经登出却没有跳转"变成终态。
      await Promise.allSettled([
        disconnectMonitorHub(),
        disconnectFollowerHub(),
      ])
      clearSession()
      clearPermissions()

      // 退出后的落点是**匿名访客的进站落点**，固定为公开带单页。
      // 不在这里查公开页开关：那份状态是模块级缓存，切换开关或后端重启后可能已经过期，
      // 会让"退出"又被送回登录页。两页都关时由公开页自己显示"未启用"和登录入口。
      //
      // 顺序有讲究：先离开工作台，再撤销动态路由。反过来会在"当前页面的路由已经被拔掉、
      // 新页面还没渲染"之间留下一个坏掉的 RouterView——表现正是 URL 换了、页面还停在原处。
      await router.replace(PUBLIC_HOME_PATH)
      clearRoutes()
    } catch (error) {
      Message.error(error instanceof Error ? error.message : '退出登录失败')
    } finally {
      signingOut.value = false
    }
  }

  return { signingOut, signOut }
}
