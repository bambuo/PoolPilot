import { ref, type Ref } from 'vue'

import { setupApi } from '@/api/client'

/// 首次初始化的状态。
///
/// 与 `useSession` 分开：会话是"我是谁"，初始化是"这套系统配好了没有"——
/// 前者要登录，后者恰恰发生在登录之前（那时还没有任何账号）。
///
/// 模块级单例（与 useTheme / useSession / useMonitorHub 同一形制）：
/// 路由守卫要用它决定"进站先给初始化页还是登录页"，每个页面各存一份就会互相打架。
const needsSetup: Ref<boolean | null> = ref(null)

const reason: Ref<string | null> = ref(null)

/// 使用者主动选择"先用日志里的验证码，稍后再配"。
///
/// 只在本次会话里有效（不落存储）：刷新之后仍然回到初始化页。
/// 这样既给了出路，又不会让"系统还没配好"这件事被永久地藏起来。
const skipped = ref(false)

/// 问一次服务端。匿名接口，未登录也能调。
async function load(): Promise<void> {
  try {
    const status = await setupApi.status()

    needsSetup.value = status.needsSetup
    reason.value = status.reason
  } catch (error) {
    // 问不到就当作"不需要初始化"：登录页那边会以 401 的真实结果说话，
    // 而"因为一个读接口失败就把人锁在初始化页"是更糟的失败方式
    console.warn('读取初始化状态失败', error)
    needsSetup.value = false
    reason.value = null
  }
}

function skip(): void {
  skipped.value = true
}

export function useSetup() {
  return { needsSetup, reason, skipped, load, skip }
}
