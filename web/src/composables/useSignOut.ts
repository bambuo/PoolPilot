import { ref } from 'vue'
import { Message } from '@arco-design/web-vue'
import { useRouter } from 'vue-router'

import { authApi } from '@/api/client'
import { disconnectFollowerHub } from '@/composables/useFollowerLive'
import { disconnectMonitorHub } from '@/composables/useMonitorHub'
import { useMenu } from '@/composables/useMenu'
import { usePermission } from '@/composables/usePermission'
import { useSession } from '@/composables/useSession'

/// 退出登录。集中一处：它要按固定顺序做完一串收尾（断连接、清会话、撤动态路由、清权限缓存、
/// 回登录页），顺序或遗漏任一步都会留下尾巴——写两遍必然有一遍漏掉。
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
      // 留着它们会在服务端变成一串没人认领的 401
      await disconnectMonitorHub()
      await disconnectFollowerHub()
      clearSession()

      // 动态路由与权限缓存都跟着这次会话走。不清掉的话，换个账号登录会先看到
      // 上一份菜单/权限闪一下、再被守卫纠正——更糟的是那份菜单可能属于别人。
      clearRoutes()
      clearPermissions()

      await router.push({ name: 'login' })
    } catch (error) {
      Message.error(error instanceof Error ? error.message : '退出登录失败')
    } finally {
      signingOut.value = false
    }
  }

  return { signingOut, signOut }
}
