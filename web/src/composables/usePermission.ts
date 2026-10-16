import { ref } from 'vue'

import { permissionApi } from '@/api/client'

/// 当前会话的权限矩阵。供页面做**按钮级**控制（路由级由动态注册负责，见 useMenu）。
///
/// 模块级单例（与 useSession / useMenu 同一形制）：权限是"当前这个人能做什么"，
/// 整个页面只有一份；每处 `usePermission()` 各拉一次既浪费，也会出现两处判断不一致。
///
/// 缓存到**会话结束**为止：退出登录时 clear()，重新登录后第一次用到的页面 refresh()。
const grants = ref<Map<string, Set<string>>>(new Map())

/// 快照是否已经拉过。**"没拉过"与"拉过但没有权限"是两回事**：
/// 前者该先 refresh 一次再判断（否则默认拒绝会把合法的连接/按钮也挡掉），
/// 后者才是真的没有权限。
const loaded = ref(false)

async function refresh(): Promise<void> {
  const snapshot = await permissionApi.current()

  const map = new Map<string, Set<string>>()
  for (const grant of snapshot.permissions) {
    map.set(grant.permissionKey, new Set(grant.actions))
  }

  grants.value = map
  loaded.value = true
}

/// 是否允许某个动作。
///
/// **默认拒绝**：没拉过权限、或键上没有这个动作，都返回 false——
/// 漏判的结果是"少一个按钮"，比"多一个不该有的按钮"安全得多。
function can(permissionKey: string, action: string): boolean {
  return grants.value.get(permissionKey)?.has(action) ?? false
}

function clear(): void {
  grants.value = new Map()
  loaded.value = false
}

export function usePermission() {
  return { can, refresh, clear, loaded }
}
