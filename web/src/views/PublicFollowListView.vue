<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'

import { describePublicError, publicApi, type PublicFollower, type PublicFollowerSummary } from '@/api/public'
import PublicFollowCard from '@/components/PublicFollowCard.vue'
import PublicFollowCreateModal from '@/components/PublicFollowCreateModal.vue'
import PublicFollowDetailModal from '@/components/PublicFollowDetailModal.vue'
import PublicPageHeader from '@/components/PublicPageHeader.vue'
import { usePublicPageStatus } from '@/composables/usePublicPageStatus'
import { usePublicStream, usePushHint } from '@/composables/usePublicStream'
import { useSetup } from '@/composables/useSetup'
import { Message } from '@arco-design/web-vue'

/// 公开跟单页 —— 留言板式浏览 + 匿名开一条纸面跟单，匿名可达。
///
/// 每条卡片是一张**纸面跟单**的公开摘要（G2）：跟着哪个带单员、现在值多少、跟得紧不紧。
/// 点"详情"按 id 订阅实时详情（detail 帧，DTO 与 G3 逐键一致，流水只给最近 50 条）。
/// 这里没有实盘概念——公开契约根本不携带交易所字段，界面上也就无处可显示。
///
/// 删除的身份模型必须理解到位：`isOwnedByCaller` 是服务端按**当前浏览器**（Cookie）
/// 判定"这是不是你创建的"。换浏览器、清 Cookie、隐私模式之后这条就删不掉了，
/// 只能由管理员处理——确认框与创建成功的提示里都要把这句话明说，不留到事后。
///
/// 实时性：列表只来自 SSE（usePublicStream）的 snapshot 帧，**没有任何轮询**——
/// 断线由 EventSource 原生重连，页面保持上一帧并显示"正在重连"。
/// 不接 SignalR——hub 要登录且有 menu:graph 才准入，匿名访客进不去。
///
/// 归属（"这条是不是我建的"）不能从共享的 snapshot 帧里拿到：那一帧是给所有订阅者的，
/// 装不下按 Cookie 裁剪的 isOwnedByCaller。页面打开时用 `ownedFollowerIds` **取一次**，
/// 建/删后本地增删；卡片与详情的数据仍然只来自 SSE。

/// 口径 11：系统未初始化时公开页只渲染"系统尚未就绪"，不发任何业务请求。
/// needsSetup 已由守卫在放行前问过一次，这里同步读。
const needsSetup = useSetup().needsSetup.value === true

const pageStatus = usePublicPageStatus()

/// 开关明确为 false 时才显示"未启用"；null（还没读到/读取失败）按"未知"处理。
const pageDisabled = computed(() => pageStatus.status.value?.followers === false)

const {
  followers: streamFollowers,
  status: streamStatus,
  connect: connectStream,
  close: closeStream,
} = usePublicStream()
const { pending: pushPending, expectPush } = usePushHint()

/// 当前浏览器拥有的行。SSE 帧上的 isOwnedByCaller 恒为 false（共享帧不能按人裁剪），
/// 这里把一次性读口拿到的 Id 叠加到卡片上。
const ownedIds = ref<Set<number>>(new Set())
const ownershipError = ref('')

/// null = 首帧还没到：显示连接占位，而不是"还没有人开过跟单"的空状态。
const followers = computed<PublicFollowerSummary[]>(() =>
  (streamFollowers.value ?? []).map((follower) =>
    follower.isOwnedByCaller || ownedIds.value.has(follower.id)
      ? { ...follower, isOwnedByCaller: true }
      : follower,
  ),
)
const isStreamReady = computed(() => streamFollowers.value !== null)

async function loadOwnership(): Promise<void> {
  try {
    ownedIds.value = new Set(await publicApi.ownedFollowerIds())
    ownershipError.value = ''
  } catch (error) {
    // 取不到归属只影响"删除入口"，不影响浏览；如实说一句，不把整页打成错误。
    ownershipError.value = describePublicError(error)
  }
}

/// 顶部说明随链路状态变：reconnecting 时追加一句，不弹 toast、不打断阅读。
/// `connecting` 单独一句——首帧还没到时写"已断开"是在说假话（它还没连上过）。
const streamNote = computed(() => {
  const base = '实时推送：卡片与详情自动更新'

  // 首帧之前不说"已断开"：EventSource 第一次连不上也会报 error，但那时它从没连通过，
  // 说"断开"既吓人又不准确（这句与下面的"正在连接实时数据…"同时出现过）
  if (!isStreamReady.value) return '实时数据连接中…'
  if (streamStatus.value === 'reconnecting') return `${base}；实时连接已断开，正在重连`

  return base
})

const errorMessage = ref('')

/// 新建成功的提示：只在**本次停留**里显示（关掉或下次创建前重置），内容就是
/// "可删除性绑定当前浏览器"这一条——它是这里唯一不能被事后想起来的代价。
const createdNotice = ref(false)

const createOpen = ref(false)

/// 详情弹窗：id 由卡片给出，弹窗自己订阅 detail 流（不拿列表里那份摘要凑——
/// 摘要没有流水、曲线，也没有"此刻还可不可得估值"的最新答案）。
const detailVisible = ref(false)
const detailId = ref<number | null>(null)

/// 详情弹窗的归属：详情帧也来自共享流，因此同样以本地 ownedIds 为准。
const detailOwned = computed(() => detailId.value !== null && ownedIds.value.has(detailId.value))

/// 点开详情时先用列表里这一行的实时摘要把数值铺上（详情首帧可能要等一次装配），
/// 首帧到达后由详情帧整体替换。按 id 从当前列表取，摘要本身也跟着列表流更新。
const detailSummary = computed<PublicFollowerSummary | null>(() =>
  detailId.value === null ? null : (followers.value.find((f) => f.id === detailId.value) ?? null),
)

/// 删除目标：卡片与详情两个入口共用同一份确认框。详情入口给的是详情对象——
/// 它的字段结构上兼容摘要（positions 元素只是多了几列），所以类型能接住。
const deleteTarget = ref<PublicFollowerSummary | null>(null)
const deleting = ref(false)

function openDetail(id: number): void {
  detailId.value = id
  detailVisible.value = true
}

function onCreated(id: number): void {
  createdNotice.value = true

  // 新行的归属由创建响应确定，不必等下一次归属读取
  ownedIds.value = new Set(ownedIds.value).add(id)

  // 写成功后服务端立即推一帧，页面不再 load()。帧可能比 POST 响应先到：
  // 列表里已有它就不提示；否则起 3 秒提示，期间收到任何一帧会自动收起。
  if (!followers.value.some((follower) => follower.id === id)) {
    expectPush()
  }
}

async function confirmDelete(): Promise<void> {
  const target = deleteTarget.value
  if (target === null) return

  deleting.value = true
  errorMessage.value = ''

  try {
    await publicApi.deleteFollower(target.id)

    const remaining = new Set(ownedIds.value)
    remaining.delete(target.id)
    ownedIds.value = remaining

    deleteTarget.value = null
    // 那条"已创建、只有当前浏览器能删"的提示跟着删除一起收掉：
    // 行都删了还挂着这句话，只会让人以为页面上还有一条自己的跟单。
    createdNotice.value = false

    // 删的正是在详情里看的那条：弹窗引用的账本已经不存在了，跟着关
    if (detailId.value === target.id) {
      detailVisible.value = false
    }

    Message.success('已删除')

    // 同上：帧可能已经把这张卡撤掉了；还在就提示"等待实时刷新"，不主动 load。
    if (followers.value.some((follower) => follower.id === target.id)) {
      expectPush()
    }
  } catch (error) {
    // 403（身份对不上，比如另一处把 Cookie 清了）/ 404 的 {"error"} 原样显示
    errorMessage.value = describePublicError(error)
    deleteTarget.value = null
  } finally {
    deleting.value = false
  }
}

if (!needsSetup) {
  onMounted(() => {
    // 先问一次开关：明确关掉时不建流、也不取归属，直接显示"未启用"。
    // 读取失败按"未知"处理，仍然建流；列表与详情本身只等 SSE 帧，
    // 归属另取一次（不是数据、不是轮询）。
    void pageStatus.load().then(() => {
      if (pageDisabled.value) return

      connectStream()
      void loadOwnership()
    })
  })

  onUnmounted(closeStream)
}
</script>

<template>
  <div class="public-page">
    <PublicPageHeader />

    <main class="public-body">
      <a-space direction="vertical" fill style="width: 100%">
        <!-- 未就绪：一句话说明，不发请求也不给"重试"——修它不是公开访客的事，是管理员的 -->
        <a-card v-if="needsSetup" :bordered="false">
          <a-empty description="系统尚未就绪，公开页暂时不提供数据。" />
        </a-card>

        <!-- 开关明确关掉：说明白"这一页没开"，而不是让 EventSource 永远显示连接中 -->
        <a-card v-else-if="pageDisabled" :bordered="false">
          <a-empty description="跟单广场未启用。" />
        </a-card>

        <template v-else>
          <a-card :bordered="false">
            <template #title>
              <a-space>
                <span>跟单广场</span>
                <a-tag v-if="isStreamReady" size="small">{{ followers.length }} 条</a-tag>
              </a-space>
            </template>
            <template #extra>
              <span class="stale-note">{{ streamNote }}</span>
            </template>

            <a-alert v-if="errorMessage" type="error" :title="errorMessage" />

            <!-- 归属读取失败只影响删除入口；浏览与实时数据不受影响，所以用 warning 而不是 error -->
            <a-alert v-if="ownershipError" type="warning" :title="`删除入口暂时不可用：${ownershipError}`" />

            <a-alert v-if="createdNotice" type="info" :show-icon="true" closable @close="createdNotice = false">
              这条跟单已创建。它只在<b>当前这个浏览器</b>里可以删除——换浏览器、清 Cookie
              或使用隐私模式之后，这条就删不掉了，只能由管理员处理。
            </a-alert>

            <div v-if="pushPending" class="push-note">已提交，等待实时刷新</div>

            <div class="intro"><b>纸面跟单</b>的留言板：每条卡片是一个模拟账户，按真实盘口记账、不碰真实资金。匿名也能开一条；自己创建的会标「你创建的」并带删除入口，点卡片看持仓、曲线与流水。</div>
          </a-card>

          <!-- 连接状态只在卡片区右上角那一处（streamNote）说；正文不再重复第二句——
               两处同时说"连接中/已断开"，既啰嗦又会出现互相矛盾的措辞 -->
          <!-- 空列表不再写"还没有人开过跟单"：0 条与虚线新增卡已经把这件事说完了 -->

          <!-- 网格与「开一条」**不依赖首帧**：它们是页面骨架与入口，
               挂在"数据到达"上的结果是流一断整页就只剩一句占位、连新增都点不到 -->
          <div class="card-grid">
            <PublicFollowCard
              v-for="follower in followers"
              :key="follower.id"
              :follower="follower"
              @detail="openDetail(follower.id)"
              @remove="deleteTarget = follower"
            />

            <button type="button" class="add-card" @click="createOpen = true">
              <span style="font-size: 26px; line-height: 1">+</span>
              <span style="margin-top: 6px">开一条纸面跟单</span>
            </button>
          </div>
        </template>
      </a-space>
    </main>

    <PublicFollowCreateModal v-model:visible="createOpen" @created="onCreated" />

    <PublicFollowDetailModal
      v-model:visible="detailVisible"
      :follower-id="detailId"
      :summary="detailSummary"
      :is-owned="detailOwned"
      @request-delete="(f: PublicFollower) => (deleteTarget = f)"
    />

    <!-- 删除确认。除了"删什么"，必须把身份代价说明白：删的机会只属于当前这个浏览器，
         错过之后这条会一直留在公开列表里，直到管理员处理 -->
    <a-modal :visible="deleteTarget !== null" title="删除这条跟单？" :footer="false" @cancel="deleteTarget = null">
      <a-space direction="vertical" fill :size="12">
        <div class="confirm-line">
          将删除{{ deleteTarget?.label ? `「${deleteTarget.label}」` : `跟单 #${deleteTarget?.id}` }}
          的纸面账、全部流水与权益曲线，<b>无法恢复</b>。
        </div>
        <div class="confirm-warning">
          注意：删除权限跟着<b>当前这个浏览器</b>走。换浏览器、清 Cookie 或使用隐私模式之后，
          这条就删不掉了——它会一直留在公开列表里，直到管理员处理。
        </div>
      </a-space>
      <div class="confirm-actions">
        <a-button @click="deleteTarget = null">取消</a-button>
        <a-button type="primary" status="danger" :loading="deleting" @click="confirmDelete">删除</a-button>
      </div>
    </a-modal>
  </div>
</template>

<style scoped>
.public-page {
  display: flex;
  flex-direction: column;
  min-height: 100vh;
}

.public-body {
  flex: 1 1 auto;
  padding: 16px 20px;
  overflow: auto;
}

.intro {
  margin-top: 4px;
  font-size: 12px;
  color: var(--color-text-3);
  line-height: 1.7;
}

.stale-note {
  font-size: 12px;
  color: var(--color-text-3);
}

.push-note {
  margin-top: 8px;
  font-size: 12px;
  color: var(--color-text-3);
}

.card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
  gap: 14px;
}

/* 新建卡：虚线边框，明显不是数据卡（与工作台「开始跟单」卡同一形制） */
.add-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  min-height: 180px;
  color: var(--color-text-3);
  background: transparent;
  border: 1px dashed var(--color-border-3);
  border-radius: 6px;
  cursor: pointer;
  font-size: 13px;
}

.add-card:hover {
  border-color: rgb(var(--primary-6));
}

.confirm-line {
  font-size: 13px;
  line-height: 1.8;
}

.confirm-warning {
  font-size: 12px;
  color: rgb(var(--warning-6));
  line-height: 1.8;
}

.confirm-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 14px;
}
</style>
