import { ref } from 'vue'

import { describePublicError, publicApi, type PublicPageStatus } from '@/api/public'

/// 公开面两页的开关状态。模块级单例（与 useSession / usePublicStream 同一形制）：
/// 两页拿的是同一份 `{ leads, followers }`，一次读取即可。
///
/// 为什么需要它：EventSource 看不到 404 的响应体，"这一页没开放"与"实时流正在连接"
/// 在浏览器里长得一模一样。生产默认两片都关，没有这个读口时匿名访客会永远停在
/// "实时数据连接中…"。
///
/// 读取失败时 `status` 保持 null：页面按"未知"处理（仍然尝试建流），
/// 不因为这一发失败就把功能判死；下一次进入公开页会重试。
const status = ref<PublicPageStatus | null>(null)
const errorMessage = ref('')

let pending: Promise<void> | null = null

async function load(): Promise<void> {
  if (status.value !== null) return
  if (pending !== null) return pending

  pending = (async () => {
    try {
      status.value = await publicApi.status()
      errorMessage.value = ''
    } catch (error) {
      errorMessage.value = describePublicError(error)
    } finally {
      pending = null
    }
  })()

  return pending
}

export function usePublicPageStatus() {
  return { status, errorMessage, load }
}
