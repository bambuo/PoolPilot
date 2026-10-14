import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { onUnmounted, ref, type Ref } from 'vue'

/// 监控 hub 的连接。
///
/// 这个项目里**只有这一处**建立 SignalR 连接：连接是全局资源（一条长连接、一份自动重连策略），
/// 每个页面各建一条的话，切几次页面就会堆出一串连接，而且它们互相不知道对方存在。
/// 所以做成模块级的单例：第一个用到的人建立它，之后的调用者只是挂上自己的回调。
///
/// 自动重连用 SignalR 自带的策略（0s、2s、10s、30s，之后每 30s 一次）。
/// 重连期间界面必须能区分"没有新数据"与"连接断了"——所以把状态也暴露出去，
/// 否则一条断了的连接看起来和"账户一直没变化"一模一样。
export type MonitorHubState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected'

interface LiveAccountHandler {
  followerId: number
  handler: (account: unknown) => void
  /** 链路健康（true/false）；后端判不出来时不调用。 */
  onHealth?: (healthy: boolean) => void
  /** 每秒的未实现盈亏与标记价：都按系统标准标的给。后端用交易所的标记价当场算的。 */
  onPnl?: (
    byPair: Record<string, number>,
    total: number,
    marksByPair: Record<string, number>,
  ) => void
}

let connection: HubConnection | null = null
let starting: Promise<void> | null = null

const state = ref<MonitorHubState>('disconnected')
const accountHandlers = new Set<LiveAccountHandler>()

function ensureConnection(): Promise<void> {
  if (connection !== null) {
    return starting ?? Promise.resolve()
  }

  // 走同源相对路径：开发期由 Vite 代理到后端（vite.config.ts 里 /hubs 已开 ws），
  // 生产期由 API 自己托管前端。写死后端地址会让部署时多一处要改的地方
  const hub = new HubConnectionBuilder()
    .withUrl('/hubs/monitor')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()

  // 事件名与后端 MonitorHub 的 SendAsync 一致；带 followerId，页面只认自己那一个
  hub.on('liveAccount', (followerId: number, account: unknown) => {
    for (const entry of accountHandlers) {
      if (entry.followerId === followerId) {
        entry.handler(account)
      }
    }
  })

  // 每秒的未实现盈亏：币安不推价格驱动的仓位更新，所以盈亏由后端拿标记价当场算
  hub.on(
    'livePnl',
    (
      followerId: number,
      byPair: Record<string, number>,
      total: number,
      marksByPair: Record<string, number>,
    ) => {
      for (const entry of accountHandlers) {
        if (entry.followerId === followerId) {
          entry.onPnl?.(byPair, total, marksByPair)
        }
      }
    },
  )

  // 后端用金丝雀判定的"这条链路推不推得出数据"：不转达给界面的话，
  // 一条收不到任何数据的通道会一直显示"实时"
  hub.on('accountStreamHealth', (followerId: number, healthy: boolean) => {
    for (const entry of accountHandlers) {
      if (entry.followerId === followerId) {
        entry.onHealth?.(healthy)
      }
    }
  })

  hub.onreconnecting(() => {
    state.value = 'reconnecting'
  })

  hub.onreconnected(() => {
    state.value = 'connected'
  })

  hub.onclose(() => {
    state.value = 'disconnected'
  })

  connection = hub
  state.value = 'connecting'

  starting = hub
    .start()
    .then(() => {
      state.value = 'connected'
    })
    .catch((error: unknown) => {
      // 连不上不该让页面报错：它只是"没有实时更新"，页面上的数据仍然有效。
      // 状态如实置为 disconnected，界面据此显示"实时通道未连接"
      state.value = 'disconnected'
      connection = null
      starting = null

      if (import.meta.env.DEV) {
        console.warn('[monitorHub] 连接失败', error)
      }
    })

  return starting
}

/// 断开监控连接。**退出登录时必须调用**。
///
/// 这条连接带着会话 Cookie 建起来，登出之后服务端已经认不出它了：
/// 不主动断开，浏览器会带着一条已经没有身份的连接反复自动重连，
/// 服务端那边则是一串 401——看上去像后端出了问题。
export async function disconnectMonitorHub(): Promise<void> {
  const current = connection

  connection = null
  starting = null

  if (current !== null) {
    await current.stop()
  }

  state.value = 'disconnected'
}

/// 订阅某个实盘跟单员的账户快照。组件卸载时自动退订。
export function useLiveAccount(
  followerId: Ref<string>,
  onChange: (account: unknown) => void,
  onHealth?: (healthy: boolean) => void,
  onPnl?: (
    byPair: Record<string, number>,
    total: number,
    marksByPair: Record<string, number>,
  ) => void,
): { state: Ref<MonitorHubState> } {
  const entry: LiveAccountHandler = {
    followerId: Number(followerId.value),
    handler: onChange,
    ...(onHealth === undefined ? {} : { onHealth }),
    ...(onPnl === undefined ? {} : { onPnl }),
  }

  accountHandlers.add(entry)
  void ensureConnection()

  onUnmounted(() => {
    accountHandlers.delete(entry)
  })

  return { state }
}

/// 断开（目前只有退出页面时才用得到；留着是为了让"连接归谁管"这件事在代码里是明确的）。
export async function stopMonitorHub(): Promise<void> {
  const hub = connection

  connection = null
  starting = null
  accountHandlers.clear()
  state.value = 'disconnected'

  if (hub && hub.state !== HubConnectionState.Disconnected) {
    await hub.stop()
  }
}
