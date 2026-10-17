import { onUnmounted, ref, shallowRef, watch, type Ref } from 'vue'

import type { PublicFollower, PublicFollowerSummary, PublicLeadDetail, PublicLeadTrader } from '@/api/public'

/// 公开两页的实时通道。
///
/// 模块级单例（与 useSession / usePermission 同一形制）：两个公开页拿的本来就是同一份
/// 全量快照，共用一条连接既省浏览器对同源 EventSource 的连接额度，也避免两页在切换
/// 瞬间看到两个时间点的数据。
///
/// **零轮询**：本模块里没有任何定时轮询，也没有自己起的重连定时器。断线由
/// EventSource 原生重连；404（公开开关关掉）与长时间连不上都只把 status 停在
/// reconnecting。数据只可能来自推送帧，不再退回 G1/G2 接口轮询。
///
/// 与页面的分工写死在这里：本模块只负责"把最新快照交出去"与"如实报告链路状态"，
/// 不碰页面错误、不弹提示；页面据 status 显示一句说明即可。
export type PublicStreamStatus = 'connecting' | 'live' | 'reconnecting'

/// 契约（与后端逐字一致）：GET /api/public/stream，事件名 snapshot，
/// data 是 {leads, followers, takenAt} 的全量快照（不做增量）。
const STREAM_URL = '/api/public/stream'
const SNAPSHOT_EVENT = 'snapshot'

/// 详情流：GET /api/public/stream?detail=<id>，事件名 detail，
/// data 是 G3 的 PublicFollower（逐键不变）。
const DETAIL_EVENT = 'detail'
const DETAIL_GONE_EVENT = 'gone'

/// 带单员详情流：GET /api/public/stream?lead=<accountIndex>，事件名 lead，
/// data 是从池详情精选出来的 PublicLeadDetail。终止帧与详情流同为 gone（带 {id}）。
const LEAD_EVENT = 'lead'

/// 写成功后等这么久还没看到新帧，才提示"已提交，等待实时刷新"。
/// 只是提示文案的延时，到点也不发任何请求。
const PUSH_HINT_DELAY_MS = 3_000

interface PublicStreamSnapshot {
  leads: PublicLeadTrader[]
  followers: PublicFollowerSummary[]
  takenAt: string
}

/// null = 还没收到过快照。页面据此区分"还没实时数据"与"实时数据确实是空的"：
/// 前者显示连接占位，后者才显示空状态。
const leads: Ref<PublicLeadTrader[] | null> = ref(null)
const followers: Ref<PublicFollowerSummary[] | null> = ref(null)
const takenAt = ref<string | null>(null)
const status = ref<PublicStreamStatus>('connecting')

/// 已接受帧的序号。只给"等推送"提示用：不依赖 takenAt 是否变化，
/// 只要真的来了一帧新的全量快照就算数。
const frameSeq = ref(0)

let source: EventSource | null = null

function warnDev(message: string, error: unknown): void {
  // 只在开发构建里出声：一帧坏数据不该污染生产页面的错误区。
  if (import.meta.env.DEV) {
    console.warn(`[publicStream] ${message}`, error)
  }
}

/// 解析并校验快照帧。形状不对就当作没收到——保留上一份数据比把 undefined
/// 写进列表更安全（模板里一次 .length 就会炸）。
function parseSnapshot(raw: string): PublicStreamSnapshot | null {
  try {
    const value: unknown = JSON.parse(raw)

    if (typeof value !== 'object' || value === null) return null

    const snapshot = value as Partial<PublicStreamSnapshot>

    if (
      !Array.isArray(snapshot.leads) ||
      !Array.isArray(snapshot.followers) ||
      typeof snapshot.takenAt !== 'string'
    ) {
      return null
    }

    return { leads: snapshot.leads, followers: snapshot.followers, takenAt: snapshot.takenAt }
  } catch (error) {
    warnDev('快照解析失败', error)
    return null
  }
}

function handleSnapshot(event: MessageEvent<string>): void {
  const snapshot = parseSnapshot(event.data)

  // 一帧坏数据不改变状态：下一帧到达时照常整体覆盖。
  if (snapshot === null) return

  // 全量快照：整体替换而不是合并——增量语义一旦两边理解不一致，就会留下幽灵卡片。
  leads.value = snapshot.leads
  followers.value = snapshot.followers
  takenAt.value = snapshot.takenAt
  frameSeq.value += 1

  // 收到帧 = 连接确实可用；这是唯一能把 connecting/reconnecting 拉回 live 的信号。
  // 不用 onopen：连上却收不到数据时，状态必须是"还没接上"而不是"实时"。
  status.value = 'live'
}

/// 建立列表连接。重复调用是安全的：整个应用生命周期只保留一条流。
///
/// **needsSetup 时不要调用**（页面判断）：系统还没初始化，公开接口不存在，
/// 建连只会换来一串注定失败的请求。
function connect(): void {
  if (source !== null) return

  status.value = 'connecting'

  const stream = new EventSource(STREAM_URL)
  source = stream

  stream.addEventListener(SNAPSHOT_EVENT, (event) => {
    // 已 close 或已换成新连接时，旧连接排队中的事件一律丢弃。
    if (source !== stream) return

    handleSnapshot(event as MessageEvent<string>)
  })

  stream.onerror = () => {
    if (source !== stream) return

    // 404（关闭开关）与网络中断在这里不可区分：EventSource 不暴露状态码。
    // 对访客来说两者都是"实时数据没接上"；浏览器自己会重连（404 时永久关闭），
    // 我们只改状态——不起定时器、不轮询，数据保持上一帧。
    status.value = 'reconnecting'
  }
}

/// 断开列表连接。页面卸载时调用。
///
/// 快照数据刻意**保留**：在两个公开页之间切换时，新页面能先显示上一帧，
/// 而不是白屏等新连接的首帧；下一次 connect 会先把 status 置回 connecting。
function close(): void {
  if (source !== null) {
    source.close()
    source = null
  }
}

export function usePublicStream() {
  return { leads, followers, status, takenAt, connect, close }
}

/// 写成功后的"等推送"提示。
///
/// 服务端在写成功后立即推一帧，页面不再调用 load()；但推送与 POST 响应是两条连接，
/// 帧有可能先到。调用方应先用当前列表确认新状态还没出现，再调用 expectPush：
/// 已经出现就不必提示，此后 3 秒内收到任何一帧也会立刻取消提示。
/// 这里只挂一个一次性 setTimeout，不发起任何请求。
export function usePushHint(): { pending: Ref<boolean>; expectPush: () => void } {
  const pending = ref(false)
  let timer: number | null = null

  function clear(): void {
    if (timer !== null) {
      window.clearTimeout(timer)
      timer = null
    }

    pending.value = false
  }

  // 任何一帧新快照都意味着"等待"结束；也包括别的写操作触发的帧。
  watch(frameSeq, clear)

  function expectPush(): void {
    clear()

    timer = window.setTimeout(() => {
      timer = null
      pending.value = true
    }, PUSH_HINT_DELAY_MS)
  }

  onUnmounted(clear)

  return { pending, expectPush }
}

/// 终止帧只有 `{ id }`：行被删除或不再是公开纸面行。读不出 id 时仍按"已不存在"处理。
function parseGoneId(raw: string): number | null {
  try {
    const value: unknown = JSON.parse(raw)

    if (typeof value === 'object' && value !== null && 'id' in value) {
      const id = (value as { id?: unknown }).id

      if (typeof id === 'number' && Number.isSafeInteger(id)) return id
    }
  } catch (error) {
    warnDev('终止帧解析失败', error)
  }

  return null
}

/// 解析详情帧。两个公开详情（跟单 / 带单员）的 DTO 都由服务端定型，
/// 这里只做最小校验：是对象就能渲染。
function parseDetail<T>(raw: string): T | null {
  try {
    const value: unknown = JSON.parse(raw)

    return typeof value === 'object' && value !== null ? (value as T) : null
  } catch (error) {
    warnDev('详情帧解析失败', error)
    return null
  }
}

/// 从非 2xx 响应里读出服务端的 {"error"} 原文；读不到就用状态码兜底。
async function readErrorText(response: Response, fallback: string): Promise<string> {
  try {
    const body: unknown = await response.json()

    if (typeof body === 'object' && body !== null && 'error' in body) {
      const error = (body as { error?: unknown }).error

      if (typeof error === 'string' && error !== '') return error
    }
  } catch {
    // 非 JSON 错误体：走下面的状态码文案。
  }

  return `${fallback}（HTTP ${response.status}）`
}

/// 公开详情的 SSE 订阅规格：两个详情流只差 URL、事件名与两句文案，
/// 连接/重连/终止/一次性错误探测的骨架完全一样。
interface StreamDetailSpec {
  streamUrl: (id: number) => string
  eventName: string
  goneMessage: (id: number | null) => string
  probeFallback: string
}

const followerDetailSpec: StreamDetailSpec = {
  streamUrl: (id) => `${STREAM_URL}?detail=${encodeURIComponent(id)}`,
  eventName: DETAIL_EVENT,
  goneMessage: (id) =>
    id === null ? '这条跟单已不存在或已不可见。' : `跟单 #${id} 已不存在或已不可见。`,
  probeFallback: '详情暂时不可用',
}

const leadDetailSpec: StreamDetailSpec = {
  streamUrl: (id) => `${STREAM_URL}?lead=${encodeURIComponent(id)}`,
  eventName: LEAD_EVENT,
  goneMessage: (id) =>
    id === null ? '这个带单员已不存在或已不可见。' : `带单员 #${id} 已不存在或已不可见。`,
  probeFallback: '带单员详情暂时不可用',
}

/// 详情订阅的通用实现。id 由调用方用 getter 给出：null = 弹窗关闭，立即断开。
///
/// 换 id 时先断旧连接再建新连接；只认详情帧与 gone 终止帧，不调用单次详情接口。
/// 首帧未到时 value 为 null。行被删除/转私有后服务端发 gone，这里主动收线并显示
/// "已不存在"，不再拿旧数据展示。
function useStreamDetail<T>(id: () => number | null, spec: StreamDetailSpec) {
  const value = shallowRef<T | null>(null)
  const detailStatus = ref<PublicStreamStatus>('connecting')
  const errorMessage = ref('')

  /// 最近一帧到达的本地时刻。详情里的数字会随帧更新，界面上给一个时间戳，
  /// 让"实时"是可验证的，而不是一句标签。
  const lastUpdatedAt = ref<number | null>(null)

  let source: EventSource | null = null

  /// EventSource 看不到状态码与响应体。详情流返回 404（不存在 / 非公开 / 开关关掉）
  /// 时浏览器会永久关闭连接，只留下一个 CLOSED 的 onerror。为了把服务端 {"error"}
  /// 原文显示出来，这里对同一 URL 补一次**一次性** fetch：读到错误体就结束；
  /// 若连接其实能建（瞬时故障），立刻取消响应体，让 EventSource 自己重连。
  /// 它不是回退到单次详情接口取数据，也不重试。
  async function probeTerminalError(stream: EventSource, targetId: number): Promise<void> {
    try {
      const response = await fetch(spec.streamUrl(targetId), {
        headers: { accept: 'text/event-stream' },
        // 探测要的是"此刻"的响应；命中缓存会把已失效的 200 当成成功。
        cache: 'no-store',
      })

      // 弹窗已关或已换 id：探测结果作废。
      if (source !== stream) {
        void response.body?.cancel()
        return
      }

      if (response.ok) {
        // 连接本身可用，这次 error 只是瞬时抖动；交给原生重连，不挂错误。
        void response.body?.cancel()
        return
      }

      const text = await readErrorText(response, spec.probeFallback)

      if (source === stream) errorMessage.value = text
    } catch {
      // 探测也失败就保持"正在重连"，不编造错误文案。
    }
  }

  function close(): void {
    if (source !== null) {
      source.close()
      source = null
    }
  }

  function reset(): void {
    value.value = null
    errorMessage.value = ''
    lastUpdatedAt.value = null
    detailStatus.value = 'connecting'
  }

  function open(targetId: number): void {
    close()
    reset()

    const stream = new EventSource(spec.streamUrl(targetId))
    source = stream

    stream.addEventListener(spec.eventName, (event) => {
      // 已 close 或已换 id 时，旧连接排队中的事件一律丢弃。
      if (source !== stream) return

      const detail = parseDetail<T>((event as MessageEvent<string>).data)

      if (detail === null) return

      value.value = detail
      errorMessage.value = ''
      lastUpdatedAt.value = Date.now()
      detailStatus.value = 'live'
    })

    // 行被删除/转私有：服务端发 gone，客户端主动收线，不再拿旧数据继续展示。
    stream.addEventListener(DETAIL_GONE_EVENT, (event) => {
      if (source !== stream) return

      const goneId = parseGoneId((event as MessageEvent<string>).data)

      value.value = null
      lastUpdatedAt.value = null
      errorMessage.value = spec.goneMessage(goneId)
      close()
    })

    stream.onerror = () => {
      if (source !== stream) return

      detailStatus.value = 'reconnecting'

      // CLOSED = 浏览器不会再重连（404/503 这类明确拒绝）。只有这时才值得
      // 补一次 fetch 读服务端错误原文；普通断网是 CONNECTING，交给原生重连。
      if (stream.readyState === EventSource.CLOSED) {
        void probeTerminalError(stream, targetId)
      }
    }
  }

  watch(
    id,
    (next) => {
      if (next === null) {
        close()
        reset()
        return
      }

      open(next)
    },
    { immediate: true },
  )

  onUnmounted(close)

  return { value, status: detailStatus, errorMessage, lastUpdatedAt }
}

export function useFollowerDetail(id: () => number | null) {
  const detail = useStreamDetail<PublicFollower>(id, followerDetailSpec)

  return {
    follower: detail.value,
    status: detail.status,
    errorMessage: detail.errorMessage,
    lastUpdatedAt: detail.lastUpdatedAt,
  }
}

/// 带单员（池）详情的实时订阅。数据来自 `?lead=` 流，与列表快照同一条 SSE 契约。
export function useLeadDetail(id: () => number | null) {
  const detail = useStreamDetail<PublicLeadDetail>(id, leadDetailSpec)

  return {
    lead: detail.value,
    status: detail.status,
    errorMessage: detail.errorMessage,
    lastUpdatedAt: detail.lastUpdatedAt,
  }
}
