import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { onUnmounted, readonly, ref } from 'vue'

import { usePermission } from '@/composables/usePermission'

/// 工作台跟单模块的实时通道（/hubs/followers）。
///
/// 与 `useMonitorHub` 一样是模块级单例：一条连接供列表与详情共用。
/// 两类事件：
///   · followerChanged —— 账本/池/配置变了，页面重读权威数据；
///   · marketPrice —— 只是价格变了，页面按已有持仓本地重算，不打 REST。
///
/// 行情按市场分组推送：页面把当前展示的市场登记进来，后端只发这些市场的帧。
export type FollowerLiveState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected'

export interface FollowerLiveHandler {
  onChanged?: (followerId: number) => void
  onMarketPrice?: (marketId: number, price: number) => void
}

let connection: HubConnection | null = null
let starting: Promise<void> | null = null
let retryTimer: number | null = null

/// 自动重连的重试间隔。SignalR 自己的重试会耗尽，耗尽后到这个模块兜底；
/// 页面还开着却永远停在"连接已断开"不是我们想要的终态。
const RETRY_DELAY_MS = 5_000

const handlers = new Set<FollowerLiveHandler>()
const markets = new Set<number>()

const state = ref<FollowerLiveState>('disconnected')

function scheduleRetry(): void {
  if (retryTimer !== null || handlers.size === 0) return

  retryTimer = window.setTimeout(() => {
    retryTimer = null
    void ensureConnection()
  }, RETRY_DELAY_MS)
}

function cancelRetry(): void {
  if (retryTimer !== null) {
    window.clearTimeout(retryTimer)
    retryTimer = null
  }
}

async function ensureConnection(): Promise<void> {
  if (connection !== null) {
    await (starting ?? Promise.resolve())
    return
  }

  const permission = usePermission()

  if (!permission.loaded.value) {
    try {
      await permission.refresh()
    } catch {
      // 权限读不到是暂时性的（网络/会话刷新）；页面还开着就隔一会儿再试
      state.value = 'disconnected'
      scheduleRetry()

      return
    }
  }

  if (!permission.can('menu:copy-list', 'view')) {
    state.value = 'disconnected'
    return
  }

  const hub = new HubConnectionBuilder()
    .withUrl('/hubs/followers')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()

  hub.on('followerChanged', (followerId: number) => {
    for (const handler of handlers) {
      handler.onChanged?.(followerId)
    }
  })

  hub.on('marketPrice', (marketId: number, price: number) => {
    for (const handler of handlers) {
      handler.onMarketPrice?.(marketId, price)
    }
  })

  hub.onreconnecting(() => {
    state.value = 'reconnecting'
  })

  hub.onreconnected(() => {
    state.value = 'connected'
    void subscribeAllMarkets(hub)
  })

  hub.onclose(() => {
    if (connection === hub) {
      connection = null
      starting = null
    }

    state.value = 'disconnected'
    scheduleRetry()
  })

  connection = hub
  state.value = 'connecting'

  starting = hub
    .start()
    .then(async () => {
      state.value = 'connected'
      await subscribeAllMarkets(hub)
    })
    .catch((error: unknown) => {
      state.value = 'disconnected'
      connection = null
      starting = null

      if (import.meta.env.DEV) {
        console.warn('[followerHub] 连接失败', error)
      }

      scheduleRetry()
    })

  return starting
}

async function subscribeAllMarkets(hub: HubConnection): Promise<void> {
  if (hub.state !== HubConnectionState.Connected || markets.size === 0) return

  try {
    await hub.invoke('SubscribeMarkets', [...markets])
  } catch (error) {
    if (import.meta.env.DEV) {
      console.warn('[followerHub] 订阅行情失败', error)
    }
  }
}

/// 登记页面当前展示的市场。重复调用幂等；断线重连后会自动重新订阅。
export function subscribeFollowerMarkets(marketIds: readonly number[]): void {
  for (const marketId of marketIds) {
    markets.add(marketId)
  }

  if (connection?.state === HubConnectionState.Connected) {
    void subscribeAllMarkets(connection)
  }
}

/// 订阅实时事件；组件卸载时调用返回的清理函数。
export function useFollowerLive(handler: FollowerLiveHandler): {
  state: Readonly<typeof state>
  stop: () => void
} {
  handlers.add(handler)
  void ensureConnection()

  const stop = (): void => {
    handlers.delete(handler)
  }

  onUnmounted(stop)

  return { state: readonly(state), stop }
}

/// 登出时必须调用：这条连接带着会话 Cookie，服务端已经认不出它时不该继续重连。
export async function disconnectFollowerHub(): Promise<void> {
  const current = connection

  cancelRetry()
  connection = null
  starting = null
  markets.clear()
  handlers.clear()
  state.value = 'disconnected'

  if (current !== null) {
    await current.stop().catch(() => undefined)
  }
}
