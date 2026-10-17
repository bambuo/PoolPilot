<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'

import { describePublicError, publicApi, type PublicLeadTrader } from '@/api/public'
import PublicLeadCard from '@/components/PublicLeadCard.vue'
import PublicLeadDetailModal from '@/components/PublicLeadDetailModal.vue'
import PublicPageHeader from '@/components/PublicPageHeader.vue'
import PublicPoolSelect from '@/components/PublicPoolSelect.vue'
import { usePublicPageStatus } from '@/composables/usePublicPageStatus'
import { usePublicStream, usePushHint } from '@/composables/usePublicStream'
import { useSetup } from '@/composables/useSetup'

/// 公开带单页 —— 匿名可浏览、匿名可新增。
///
/// 带单员 = "有人把这个池子挂在观察之下"。这一页回答两件事：
/// 现在有哪些池正在被观察（G1 的卡片），以及我能不能也为一个池创建观察者（G4 选池 + W1）。
///
/// 与工作台「带单管理」页的分工：这里是**公开面**——只读展示 + 新增，
/// 界面上没有任何暂停/删除入口（那是管理员在工作台上的事，匿名侧连按钮都不该见）。
///
/// 实时性：列表只来自 SSE（usePublicStream）的 snapshot 帧，**没有任何轮询**——
/// 断线由 EventSource 原生重连，页面保持上一帧并显示"正在重连"。
/// 不接 SignalR——hub 要登录且有 menu:graph 才准入，匿名访客进不去。

/// 口径 11：系统未初始化时公开页只显示"系统尚未就绪"，一个业务请求都不发
/// （守卫不会把我们跳去 setup——见 router/index.ts 的 showWhenNeedsSetup）。
/// needsSetup 由守卫在放行前问过一次，这里同步读即可，不再自己 load。
const needsSetup = useSetup().needsSetup.value === true

const pageStatus = usePublicPageStatus()

/// 开关明确为 false 时才显示"未启用"；null（还没读到/读取失败）按"未知"处理，
/// 仍然尝试建流——不因为一次状态读取失败就把功能判死。
const pageDisabled = computed(() => pageStatus.status.value?.leads === false)

const {
  leads: streamLeads,
  status: streamStatus,
  connect: connectStream,
  close: closeStream,
} = usePublicStream()
const { pending: pushPending, expectPush } = usePushHint()

/// null = 首帧还没到：显示连接占位，而不是"还没有任何带单员"的空状态。
const leads = computed<PublicLeadTrader[]>(() => streamLeads.value ?? [])
const isStreamReady = computed(() => streamLeads.value !== null)

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

/// 新增弹窗。选择值用字符串（池索引拼进请求时再 Number 化）：
/// 与工作台同一写法，避开 Arco select 清空给 undefined 的可空绑定坑（本页不开 allow-clear）。
const createOpen = ref(false)
const newPoolIndex = ref('')
const creating = ref(false)

/// 详情弹窗：卡片给出账户索引，弹窗自己订阅 `?lead=` 流。
/// 摘要只用来在首帧前铺标题/状态/仓位变动数——收益、回撤与前几大持仓都在首帧里。
const detailVisible = ref(false)
const detailIndex = ref<number | null>(null)

const detailSummary = computed<PublicLeadTrader | null>(() =>
  detailIndex.value === null
    ? null
    : (leads.value.find((lead) => lead.accountIndex === detailIndex.value) ?? null),
)

function openDetail(accountIndex: number): void {
  detailIndex.value = accountIndex
  detailVisible.value = true
}

function openCreate(): void {
  newPoolIndex.value = ''
  errorMessage.value = ''
  createOpen.value = true
}

async function create(): Promise<void> {
  if (newPoolIndex.value === '') {
    errorMessage.value = '请先选择一个池。'
    return
  }

  creating.value = true
  errorMessage.value = ''

  try {
    const created = await publicApi.observeLeadTrader(Number(newPoolIndex.value))

    createOpen.value = false

    // 写成功后服务端立即推一帧，页面不再 load()。帧可能比 POST 响应先到：
    // 列表里已有它就不提示；否则起 3 秒提示，期间收到任何一帧会自动收起。
    if (!leads.value.some((lead) => lead.accountIndex === created.accountIndex)) {
      expectPush()
    }
  } catch (error) {
    errorMessage.value = describePublicError(error)
  } finally {
    creating.value = false
  }
}

if (!needsSetup) {
  onMounted(() => {
    // 先问一次开关：明确关掉时不建流（否则 EventSource 404 会让页面永远停在"连接中"）。
    // 状态读取失败按"未知"处理，仍然建流；列表数据本身只等 SSE 首帧。
    void pageStatus.load().then(() => {
      if (!pageDisabled.value) connectStream()
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
          <a-empty description="带单广场未启用。" />
        </a-card>

        <template v-else>
          <a-card :bordered="false">
            <template #title>
              <a-space>
                <span>带单广场</span>
                <a-tag v-if="isStreamReady" size="small">{{ leads.length }} 个带单员</a-tag>
              </a-space>
            </template>
            <template #extra>
              <span class="stale-note">{{ streamNote }}</span>
            </template>

            <a-alert v-if="errorMessage" type="error" :title="errorMessage" />

            <div v-if="pushPending" class="push-note">已提交，等待实时刷新</div>

            <div class="intro">每个<b>带单员</b>跟踪一个池，把每次仓位变化记录下来供跟随者模拟成交。你可以匿名浏览，也可以为某个池<b>新增带单员</b>；暂停或删除由管理员在工作台操作。</div>
          </a-card>

          <!-- 连接状态只在卡片区右上角那一处（streamNote）说；正文不再重复第二句——
               两处同时说"连接中/已断开"，既啰嗦又会出现互相矛盾的措辞 -->
          <!-- 空列表不再写"还没有任何带单员"：0 个与虚线新增卡已经把这件事说完了 -->

          <!-- 网格与「新增」**不依赖首帧**：它们是页面骨架与入口，
               挂在"数据到达"上的结果是流一断整页就只剩一句占位、连新增都点不到 -->
          <div class="card-grid">
            <PublicLeadCard
              v-for="lead in leads"
              :key="lead.accountIndex"
              :lead="lead"
              @detail="openDetail(lead.accountIndex)"
            />

            <button type="button" class="add-card" @click="openCreate">
              <span style="font-size: 26px; line-height: 1">+</span>
              <span style="margin-top: 6px">新增带单员</span>
            </button>
          </div>
        </template>
      </a-space>
    </main>

    <PublicLeadDetailModal
      v-model:visible="detailVisible"
      :account-index="detailIndex"
      :summary="detailSummary"
    />

    <a-modal v-model:visible="createOpen" title="新增带单员">
      <a-space direction="vertical" fill :size="14">
        <a-alert v-if="errorMessage" type="error" :title="errorMessage" />

        <div>
          <div class="field-label">选择要观察的池</div>
          <PublicPoolSelect v-model="newPoolIndex" :leads="leads" />
        </div>

        <a-alert type="info" :show-icon="true">
          创建即开始观察：服务会订阅这个池的持仓与成交频道，把每次仓位变化记录下来，即使还没有任何跟随者。已有带单员的池再次选中只返回现状，不会打开或关闭它的开关。
        </a-alert>
      </a-space>

      <template #footer>
        <div class="modal-actions">
          <a-button @click="createOpen = false">取消</a-button>
          <a-button type="primary" :loading="creating" @click="create">开始观察</a-button>
        </div>
      </template>
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

.field-label {
  font-size: 13px;
  margin-bottom: 6px;
}

.modal-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 2px;
}

.card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
  gap: 14px;
}

/* 新建卡：虚线边框，明显不是数据卡（与工作台「开始跟单」卡同一形制）。
   它是本页自己渲染的 button，拿不到 PublicLeadCard 的 scoped 样式，边框自己给全 */
.add-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  min-height: 140px;
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
</style>
