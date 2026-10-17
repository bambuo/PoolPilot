<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'

import { api, type FollowerSummary, type LeadTrader, type PoolSummary } from '@/api/client'
import { copyModeLabel } from '@/domain/copyMode'
import { formatPercent, formatUsd } from '@/utils/format'

/// 系统关系图（首页）。
///
/// 一屏说清这套系统的三层结构，让第一次打开的人立刻看懂"谁在观察谁、谁在跟谁"：
///
/// ```
///   池（标签云：平台上的可选集合，字号 = 总资产）
///      ╲  │  ╱          连线 = 这个池有带单员在观察
///      带单员（被挑出来的观察对象）
///      ╲  │  ╱          连线 = 有跟单员订阅这个带单员
///      跟单员（真正下场、各自记账的账户）
/// ```
///
/// 三条设计决定：
/// <list type="number">
///   <item>**池层用标签云而不是节点卡片**：池有三百多个，逐个画成节点就是一团无法阅读的乱麻。
///     云如实表达"这是一个很大的可选集合"，而其中**有带单员的那些才被连线拉出来**——
///     一眼就能看出"我挑了哪几个在盯"。</item>
///   <item>**连线的坐标是量出来的，不是算出来的**：标签会随窗口宽度换行，位置无法预先计算，
///     所以节点先渲染，再量它们的实际位置去画曲线（与 LineChart 同一条思路：
///     用实测尺寸，不用固定坐标系）。窗口变化、数据变化、切换筛选都会重画。</item>
///   <item>**每一层都可点，通往各自的配置页**：池 → 池详情，带单员 → 带单员页，
///     跟单员 → 跟单详情。图不只是看的，它是入口。</item>
/// </list>
const pools = ref<PoolSummary[]>([])
const leads = ref<LeadTrader[]>([])
const followers = ref<FollowerSummary[]>([])
const loading = ref(false)
const errorMessage = ref('')

/// 只显示有带单员的池。默认**关**：默认态要如实呈现"池很多、我挑了这几个"。
const onlyLinked = ref(false)

/// 云里最多显示多少个池。
///
/// 为什么不全画：三百多个标签叠出来有一千多像素高，会把下面两层推到屏幕之外——
/// 而这一页存在的意义正是"一屏看懂三层"。所以云只放**资产最大的这一批 + 全部有带单员的池**
/// （后者无论如何都要在，连线得有个起点），剩下的以一行明确的数量说明去处：
/// 少显示几个不等于隐瞒，前提是把"还有多少没显示"写出来。
const CloudLimit = 48

/// 有带单员的池索引集合——描边、连线、筛选都看它。
const leadIndexes = computed(() => new Set(leads.value.map((l) => String(l.accountIndex))))

const hasLead = (index: number | string): boolean => leadIndexes.value.has(String(index))

/// 云里实际渲染的池。
///
/// 排序上有一条刻意打破"字号=总资产"的规则：**有带单员的池排在最前面**。
/// 理由是可读性——它们的连线要从标签下沿出发，如果被埋在第 40 个标签的位置，
/// 线就得横穿整片云去够自己的带单员，一眼看不出"谁连着谁"。
/// 排到最前之后，连线起点集中在头一两行，云里也不再有长距离横穿。
/// 描边本身已经说明它们为什么排前面，字号仍然照实反映总资产。
const shownPools = computed(() => {
  if (onlyLinked.value) {
    return pools.value.filter((p) => hasLead(p.accountIndex))
  }

  const linked = pools.value.filter((p) => hasLead(p.accountIndex))
  const rest = pools.value
    .filter((p) => !hasLead(p.accountIndex))
    .slice(0, Math.max(0, CloudLimit - linked.length))

  return [...linked, ...rest]
})

/// 没显示出来的池数量。界面必须说出来，否则"看不到"会被读成"不存在"。
const hiddenPoolCount = computed(() => pools.value.length - shownPools.value.length)

const orphanLeads = computed(() =>
  leads.value.filter((l) => l.followerCount === 0),
)

const totalFollowerCapital = computed(() =>
  followers.value.reduce((sum, f) => sum + f.initialCapital, 0),
)

// ── 连线：先渲染节点，再量位置画曲线 ────────────────────

interface Wire {
  readonly id: string
  readonly from: string
  readonly to: string
  readonly d: string
}

const rootRef = ref<HTMLElement | null>(null)
const wires = ref<Wire[]>([])

/// 鼠标悬停的节点。突出与它相连的线、压低其余——三百多个标签里要追一根线，靠的就是这个。
const hoverKey = ref<string | null>(null)

const poolKey = (index: number | string): string => `pool:${index}`
const leadKey = (index: number | string): string => `lead:${index}`
const followerKey = (id: number | string): string => `follower:${id}`

function measure(): void {
  const root = rootRef.value
  if (root === null) return

  const origin = root.getBoundingClientRect()
  const boxes = new Map<string, DOMRect>()

  for (const element of root.querySelectorAll<HTMLElement>('[data-node]')) {
    const key = element.dataset.node
    if (key !== undefined) {
      boxes.set(key, element.getBoundingClientRect())
    }
  }

  const next: Wire[] = []

  for (const lead of leads.value) {
    connect(next, poolKey(lead.accountIndex), leadKey(lead.accountIndex), boxes, origin)
  }

  for (const follower of followers.value) {
    connect(next, leadKey(follower.leadAccountIndex), followerKey(follower.id), boxes, origin)
  }

  wires.value = next
}

/// 从起点节点下沿接一条三次贝塞尔到终点节点上沿。
/// 控制点按垂直距离的比例下压，短连线也不会退化成一根直棍。
function connect(
  target: Wire[],
  from: string,
  to: string,
  boxes: Map<string, DOMRect>,
  origin: DOMRect,
): void {
  // 起点没渲染（例如筛选后那个标签被隐藏）就整条不画：一条从容器顶角出发的线
  // 比不画更糟——它会指向一个不存在的关系
  if (!boxes.has(from) || !boxes.has(to)) {
    return
  }

  const source = boxes.get(from)!
  const sink = boxes.get(to)!
  const x1 = source.left + source.width / 2 - origin.left
  const y1 = source.bottom - origin.top
  const x2 = sink.left + sink.width / 2 - origin.left
  const y2 = sink.top - origin.top

  // 两层之间的间距被压到几乎贴合时不画：那种线只剩一个点，看不出方向
  if (y2 - y1 < 2) {
    return
  }

  const bend = Math.max(18, (y2 - y1) * 0.45)

  target.push({
    id: `${from}->${to}`,
    from,
    to,
    d: `M ${x1.toFixed(1)} ${y1.toFixed(1)} `
      + `C ${x1.toFixed(1)} ${(y1 + bend).toFixed(1)}, ${x2.toFixed(1)} ${(y2 - bend).toFixed(1)}, `
      + `${x2.toFixed(1)} ${y2.toFixed(1)}`,
  })
}

const isHot = (wire: Wire): boolean =>
  hoverKey.value !== null && (wire.from === hoverKey.value || wire.to === hoverKey.value)

const isDim = (wire: Wire): boolean => hoverKey.value !== null && !isHot(wire)

// ── 标签云 ─────────────────────────────────────────────

/// 云的字号：按总资产取**对数**映射到 11–30px，并给大标签更重的字重。
///
/// 三个细节决定它像不像"云"：
/// <list type="number">
///   <item>**对数而不是线性**：池的资产跨两个数量级以上，线性映射会让除头部之外的
///     所有标签都挤在最小档，看起来还是一排等大的字。</item>
///   <item>**相对本次显示的集合缩放**（最大的 30px、最小的 11px）：云要传达的信息就是
///     "谁比谁大"，绝对数值由悬停提示与池详情页给。相对缩放才让反差始终看得见。</item>
///   <item>**字重跟着字号走**：只放大字号、字重不变时，大标签会显得发虚；
///     经典标签云是"越大越重"，那才有一坨的密实感。</item>
/// </list>
const tagScale = computed(() => {
  const values = shownPools.value.map((p) => Math.max(1, p.totalAssetValue))

  if (values.length === 0) {
    return () => ({ size: '12px', weight: 400 })
  }

  const low = Math.log(Math.min(...values))
  const high = Math.log(Math.max(...values))
  const span = Math.max(1e-6, high - low)

  return (totalAssetValue: number) => {
    const t = (Math.log(Math.max(1, totalAssetValue)) - low) / span

    return {
      size: `${(11 + t * 19).toFixed(1)}px`,
      weight: t > 0.55 ? 600 : t > 0.25 ? 500 : 400,
    }
  }
})

// ── 数据 ───────────────────────────────────────────────

/// 本次加载里"没读到"的数据源。三份数据来自三个不同的菜单权限点，
/// **只授了「总览」的账号读不到带单员/跟单是正常的降级**，不该让整页变成错误页：
/// 把没读到的那几块说清楚，其余照常画。
const missingSources = ref('')

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = ''
  missingSources.value = ''

  try {
    // 三份数据互不依赖，并行取。池列表最大（三百多个池），不该拖住另外两份。
    // 每一份**单独容错**：403（没有对应权限）或网络故障只影响它自己
    const [poolRows, leadRows, followerRows] = await Promise.all([
      api.listPools('user').catch(() => null),
      api.leadTraders().catch(() => null),
      api.followers().catch(() => null),
    ])

    const missing: string[] = []
    if (poolRows === null) missing.push('池列表')
    if (leadRows === null) missing.push('带单员')
    if (followerRows === null) missing.push('跟单')
    missingSources.value = missing.join('、')

    // 池按总资产降序：标签云里"大小"要有个一致的方向，从左上往右下递减最自然
    pools.value = poolRows === null ? [] : [...poolRows].sort((a, b) => b.totalAssetValue - a.totalAssetValue)
    leads.value = leadRows === null ? [] : [...leadRows].sort((a, b) => a.accountIndex - b.accountIndex)
    followers.value = followerRows === null ? [] : [...followerRows].sort((a, b) => a.id - b.id)
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    loading.value = false
  }

  await nextTick()
  measure()
}

let observer: ResizeObserver | null = null

// 筛选切换与数据变化都会改变节点位置，两者都要重量
watch([shownPools, leads, followers], async () => {
  await nextTick()
  measure()
})

onMounted(async () => {
  await load()

  observer = new ResizeObserver(() => measure())

  if (rootRef.value !== null) {
    observer.observe(rootRef.value)
  }
})

onBeforeUnmount(() => {
  observer?.disconnect()
  observer = null
})

const modeLabel = (mode: string): string => copyModeLabel(mode)
</script>

<template>
  <a-space direction="vertical" fill style="width: 100%">
    <a-card :bordered="false">
      <template #title>
        <a-space>
          <span>系统关系</span>
          <a-tag v-if="!loading" size="small">池 → 带单员 → 跟单员</a-tag>
        </a-space>
      </template>
      <template #extra>
        <a-space>
          <a-checkbox v-model="onlyLinked" :disabled="loading">只显示有带单员的池</a-checkbox>
          <a-button size="small" :loading="loading" @click="load">刷新</a-button>
        </a-space>
      </template>

      <a-alert v-if="errorMessage" type="error" :title="errorMessage" />

      <a-alert
        v-if="missingSources"
        type="warning"
        :title="`以下数据本次未读到：${missingSources}——多数情况是当前账号没有对应菜单的查看权限`"
      />

      <div class="intro">
        <b>池</b>是平台上的全部可选对象；<b>带单员</b>是你挑出来持续观察的池——它盯着池子的持仓与成交，
        产出信号；<b>跟单员</b>订阅带单员的信号，用各自的本金独立记账。
        <span class="intro-hint">
          连线只表示两种关系：<b>池 → 带单员</b>是正在观察，<b>带单员 → 跟单员</b>是正在订阅。
          点任意一层进入对应页面配置。
        </span>
      </div>
    </a-card>

    <a-spin :loading="loading" style="width: 100%">
      <div ref="rootRef" class="graph">
        <!-- 连线铺在节点之上但不可点：半透明描边让交叉处也能读；
             悬停节点时与它相连的线加粗、其余压淡，三百多个标签里靠这个追关系 -->
        <svg class="wires">
          <path
            v-for="wire in wires"
            :key="wire.id"
            :d="wire.d"
            class="wire"
            :class="{ 'wire--hot': isHot(wire), 'wire--dim': isDim(wire) }"
          />
        </svg>

        <section class="layer">
          <header class="layer-head">
            <span class="layer-name">池</span>
            <span class="layer-meta">
              显示 {{ shownPools.length }} 个 · 其中 {{ leads.length }} 个有带单员
              <template v-if="hiddenPoolCount > 0">
                ·另 {{ hiddenPoolCount }} 个未显示
              </template>
            </span>
            <span class="layer-legend">
              字号 = 总资产 · 灰字 = 已冻结 ·
              <span class="tag-link-sample">有带单员</span>（排在最前）
            </span>
            <RouterLink class="layer-action" :to="{ name: 'pools' }">看全部池 →</RouterLink>
          </header>

          <div class="cloud">
            <RouterLink
              v-for="pool in shownPools"
              :key="pool.accountIndex"
              class="tag"
              :class="{ 'tag--frozen': pool.isFrozen, 'tag--linked': hasLead(pool.accountIndex) }"
              :style="{
                fontSize: tagScale(pool.totalAssetValue).size,
                fontWeight: tagScale(pool.totalAssetValue).weight,
              }"
              :data-node="poolKey(pool.accountIndex)"
              :to="{ name: 'pool-detail', params: { accountIndex: String(pool.accountIndex) } }"
              @mouseenter="hoverKey = poolKey(pool.accountIndex)"
              @mouseleave="hoverKey = null"
            >
              {{ pool.name || `池 ${pool.accountIndex}` }}
            </RouterLink>

            <span v-if="shownPools.length === 0" class="empty-line">没有可显示的池</span>
          </div>
        </section>

        <section class="layer">
          <header class="layer-head">
            <span class="layer-name">带单员</span>
            <span class="layer-meta">
              共 {{ leads.length }} 个
              <template v-if="orphanLeads.length > 0"> · {{ orphanLeads.length }} 个无人跟随</template>
            </span>
            <RouterLink class="layer-action" :to="{ name: 'leads' }">创建 / 管理 →</RouterLink>
          </header>

          <div class="row">
            <RouterLink
              v-for="lead in leads"
              :key="lead.accountIndex"
              class="node node--lead"
              :data-node="leadKey(lead.accountIndex)"
              :to="{ name: 'leads' }"
              @mouseenter="hoverKey = leadKey(lead.accountIndex)"
              @mouseleave="hoverKey = null"
            >
              <div class="node-head">
                <span class="node-title">{{ lead.name }}</span>
                <a-tag v-if="!lead.isEnabled" size="small" color="gray">已暂停</a-tag>
                <a-tag v-else-if="lead.isWatching" size="small" color="green">监控中</a-tag>
                <a-tag v-else size="small" color="orange">未连接</a-tag>
              </div>
              <div class="node-meta">
                <span>累计信号 {{ lead.signalCount }}</span>
                <span v-if="lead.followerCount > 0">跟单 {{ lead.followerCount }}</span>
                <span v-else class="node-idle">无人跟随</span>
              </div>
            </RouterLink>

            <RouterLink v-if="leads.length === 0" class="node node--add" :to="{ name: 'leads' }">
              <span style="font-size: 20px; line-height: 1">+</span>
              <span>还没有带单员，去创建</span>
            </RouterLink>
          </div>
        </section>

        <section class="layer">
          <header class="layer-head">
            <span class="layer-name">跟单员</span>
            <span class="layer-meta">
              共 {{ followers.length }} 个
              <template v-if="followers.length > 0">
                · 合计本金 {{ formatUsd(totalFollowerCapital) }}
              </template>
            </span>
            <RouterLink class="layer-action" :to="{ name: 'copy-list' }">跟单 / 管理 →</RouterLink>
          </header>

          <div class="row">
            <RouterLink
              v-for="follower in followers"
              :key="follower.id"
              class="node node--follower"
              :class="{ 'node--muted': !follower.isActive }"
              :data-node="followerKey(follower.id)"
              :to="{ name: 'copy-detail', params: { id: String(follower.id) } }"
              @mouseenter="hoverKey = followerKey(follower.id)"
              @mouseleave="hoverKey = null"
            >
              <div class="node-head">
                <span class="node-title">跟单员 #{{ follower.id }}</span>
                <a-tag size="small" color="arcoblue">{{ modeLabel(follower.mode) }}</a-tag>
                <a-tag size="small" :color="follower.isActive ? 'green' : 'gray'">
                  {{ follower.isActive ? '跟随中' : '已暂停' }}
                </a-tag>
              </div>
              <div class="node-meta">
                <span>本金 {{ formatUsd(follower.initialCapital) }}</span>
                <span
                  v-if="follower.isValuationAvailable"
                  :style="{
                    color:
                      follower.returnPercent >= 0
                        ? 'rgb(var(--success-6))'
                        : 'rgb(var(--danger-6))',
                  }"
                >
                  {{ formatPercent(follower.returnPercent, 3) }}
                </span>
                <span v-else class="node-idle">估值不可得</span>
              </div>
              <div v-if="!follower.leadIsEnabled" class="node-warn">订阅的带单员已暂停</div>
            </RouterLink>

            <RouterLink
              v-if="leads.length > 0 && followers.length === 0"
              class="node node--add"
              :to="{ name: 'copy-list' }"
            >
              <span style="font-size: 20px; line-height: 1">+</span>
              <span>还没有跟单员，去开始跟单</span>
            </RouterLink>
          </div>
        </section>

        <!-- 整页全空时给一条最短的上手路径，而不是三行"暂无" -->
        <div v-if="!loading && leads.length === 0" class="onboarding">
          想让它跑起来：先到「带单管理」页挑一个池开始观察（哪怕没人跟也值得先看起来），
          再用一笔本金订阅它。
          <RouterLink :to="{ name: 'leads' }" class="layer-action">现在去创建带单员 →</RouterLink>
        </div>
      </div>
    </a-spin>
  </a-space>
</template>

<style scoped>
.intro {
  font-size: 12px;
  color: var(--color-text-3);
  line-height: 1.8;
}

.intro-hint {
  display: block;
}

.graph {
  position: relative;
  display: flex;
  flex-direction: column;
  gap: 34px;
  padding: 4px 0 8px;
}

/* 连线层：铺满整张关系图，不参与点击（节点都在它上面且可点） */
.wires {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  pointer-events: none;
  overflow: visible;
}

.wire {
  fill: none;
  stroke: rgb(var(--primary-6));
  stroke-width: 1.5;
  stroke-opacity: 0.35;
  transition: stroke-opacity 0.15s, stroke-width 0.15s;
}

.wire--hot {
  stroke-opacity: 1;
  stroke-width: 2.5;
}

.wire--dim {
  stroke-opacity: 0.08;
}

.layer {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.layer-head {
  display: flex;
  align-items: baseline;
  gap: 10px;
  padding-bottom: 6px;
  border-bottom: 1px solid var(--color-border-1);
}

.layer-name {
  font-size: 14px;
  font-weight: 600;
}

.layer-meta {
  font-size: 12px;
  color: var(--color-text-3);
}

.layer-legend {
  margin-left: auto;
  font-size: 11px;
  color: var(--color-text-4);
}

.layer-action {
  margin-left: auto;
  font-size: 12px;
  color: rgb(var(--primary-6));
  text-decoration: none;
}

.layer-action:hover {
  text-decoration: underline;
}

/* 图例里那个样例标签：与云里"有带单员"的标记同一套外观 */
.tag-link-sample {
  padding: 0 4px;
  color: rgb(var(--primary-6));
  border: 1px solid rgb(var(--primary-6));
  border-radius: 4px;
  background: rgba(var(--primary-6), 0.08);
}

/* ── 池：标签云 ─────────────────────────────────────── */

/* 云：居中成团 + 大小混排。
   左对齐 + 行高一致那是"标签列表"；云要的是每一行居中、大小标签在同一行里垂直居中互挤，
   边缘自然形成参差的轮廓。 */
.cloud {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: center;
  gap: 4px 12px;
  padding: 6px 0;
  line-height: 1.25;
}

.tag {
  padding: 0 6px;
  color: var(--color-text-2);
  text-decoration: none;
  border: 1px solid transparent;
  border-radius: 4px;
  white-space: nowrap;
  transition: color 0.15s, border-color 0.15s, background 0.15s;
}

.tag:hover {
  color: rgb(var(--primary-6));
  border-color: rgb(var(--primary-6));
}

.tag--frozen {
  color: var(--color-text-4);
}

/* 有带单员的池：描一圈边。这一圈是"连线从哪来"的锚点——
   三百多个标签里没有它，就找不到线的另一端 */
.tag--linked {
  color: rgb(var(--primary-6));
  font-weight: 500;
  border-color: rgb(var(--primary-6));
  background: rgba(var(--primary-6), 0.08);
}

.empty-line {
  font-size: 12px;
  color: var(--color-text-3);
}

/* ── 带单员 / 跟单员：节点行 ───────────────────────── */

.row {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 12px;
}

.node {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 210px;
  max-width: 300px;
  padding: 10px 12px;
  color: inherit;
  text-decoration: none;
  background: var(--color-bg-2);
  border: 1px solid var(--color-border-1);
  border-radius: 6px;
  transition: border-color 0.15s, box-shadow 0.15s;
}

.node:hover {
  border-color: rgb(var(--primary-6));
  box-shadow: 0 2px 10px rgb(0 0 0 / 8%);
}

.node--lead {
  border-top: 2px solid rgb(var(--primary-6));
}

.node--muted {
  opacity: 0.65;
}

.node-head {
  display: flex;
  align-items: center;
  gap: 6px;
}

.node-title {
  font-size: 13px;
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.node-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  font-size: 12px;
  color: var(--color-text-3);
}

.node-idle {
  color: var(--color-text-4);
}

.node-warn {
  font-size: 11px;
  color: rgb(var(--warning-6));
}

.node--add {
  align-items: center;
  justify-content: center;
  min-height: 72px;
  color: var(--color-text-3);
  background: transparent;
  border-style: dashed;
  font-size: 12px;
  text-align: center;
}

/* ── 全空时的上手路径 ───────────────────────────────── */

.onboarding {
  padding: 12px 14px;
  font-size: 13px;
  line-height: 1.8;
  color: var(--color-text-2);
  background: rgba(var(--primary-6), 0.06);
  border: 1px dashed rgb(var(--primary-6));
  border-radius: 6px;
}
</style>
