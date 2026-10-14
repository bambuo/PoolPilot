import { ref, type Ref } from 'vue'

import { ApiError, authApi, setUnauthorizedHandler, type Session } from '@/api/client'

/// 当前会话。
///
/// 模块级单例（与 `useTheme`、`useMonitorHub` 同一形制）：登录态是整个页面的全局状态，
/// 每处 `useSession()` 都拿到同一份，而不是各存一份、各自去猜对方有没有更新。
///
/// **前端不持有任何令牌**：会话在 HttpOnly Cookie 里，脚本读不到也写不了。
/// 这里存的只是"当前是谁"这份渲染用的信息——它过期了没关系，下一次请求会 401 把它纠正回来。
const user: Ref<Session | null> = ref(null)

/// 是否已经问过服务端一次。路由守卫靠它避免每次跳转都打一次 `/me`。
const loaded = ref(false)

/// 未登录时去哪儿。守卫在跳转前记录，登录成功后按它回跳（只接受站内路径）。
const redirectAfterLogin = ref<string | null>(null)

async function load(): Promise<void> {
  try {
    user.value = await authApi.me()
  } catch (error) {
    // 401 是"还没登录"的正常答复，不是故障；其余错误也不该让页面卡住——
    // 真正的失败会在随后的业务请求上以同一条路径暴露出来
    if (!(error instanceof ApiError) || error.status !== 401) {
      console.warn('读取会话失败', error)
    }

    user.value = null
  } finally {
    loaded.value = true
  }
}

/// 会话没了：清掉本地这份，并把下一次跳转指向登录页。
function clear(): void {
  user.value = null
}

function set(session: Session): void {
  user.value = session
  loaded.value = true
}

/// 把"任何请求收到 401"接到会话清理上。在 main.ts 里调用一次。
///
/// 之所以要这一条：会话过期是**服务端说了算**的（Cookie 到期、账号被删），
/// 前端不可能自己算准。任何一次请求收到 401 都说明"那份缓存的身份已经不成立了"，
/// 此时统一清掉，界面自然回到登录页，而不是留着一个看着像登录着的壳子继续报错。
export function bindUnauthorizedHandler(onUnauthorized: () => void): void {
  setUnauthorizedHandler(() => {
    clear()
    onUnauthorized()
  })
}

export function useSession() {
  return { user, loaded, redirectAfterLogin, load, set, clear }
}
