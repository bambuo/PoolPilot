<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'

import { api, type LeadTrader, type PoolSummary } from '@/api/client'
import { usePermission } from '@/composables/usePermission'
import { formatAgeSince, formatDateTime, formatSince, formatUsd } from '@/utils/format'
import { filterPoolOption, poolOptionLabel } from '@/utils/poolOptions'

/// 带单员 —— 被观察的池。
///
/// 一屏列出所有带单员，每张卡片只放**决定"这个观察值不值得留"的几个数**：
/// 观察状态（在不在盯）、累计信号与最近信号（盯出了多少动静）、跟单员数（有谁在跟）。
/// 布局与「跟单管理」页一致，信息分层也一样：状态 → 关键数 → 归属 → 操作。
///
/// 关键设计：**带单员与跟单员是两件事**。带单员只要存在就在观察，
/// 有没有人跟它无关；所以"无人跟随"不是故障，卡片只是如实说出来。
///
/// 状态刻意分成三种而不是两种：**已暂停**（我关的）与**未连接**（启用着但收不到推送）
/// 是两件完全不同的事，合成一个"未监控"会让人以为是自己关的。

/// 本页自身的权限键。服务端按 `menu:{code}` 生成，前端只读不写。
///
/// 带单员的写口有两类动作：创建与暂停/恢复都改配置（edit），停止观察会删掉带单员本身（delete）——
/// 与菜单管理页同一套动作语义。
const PAGE_PERMISSION = 'menu:leads'

const { can, hasRole, refresh: refreshPermissions } = usePermission()

const canEdit = computed(() => can(PAGE_PERMISSION, 'edit'))
const canDelete = computed(() => can(PAGE_PERMISSION, 'delete'))

/// 共享带单员的暂停/恢复/停止只许 superadmin 操作；创建新观察不受限制。
const isAdmin = computed(() => hasRole('superadmin'))

const leads = ref<LeadTrader[]>([])
const pools = ref<PoolSummary[]>([])
const loading = ref(false)
const errorMessage = ref('')

/// 池列表取失败的原因。**不能静默吞掉**：吞掉的后果是创建弹窗里空空一个"暂无数据"，
/// 看起来像"这个平台没有池子"，而事实是这次请求失败了。
const poolListError = ref('')

const createOpen = ref(false)
const newPoolIndex = ref('')
const creating = ref(false)

/// 已有带单员的池索引，用于在下拉里标注"已有带单员"而不是"新建"。
const leadIndexes = computed(() => new Set(leads.value.map((l) => String(l.accountIndex))))

const followerCountOf = (index: number | string): number => {
  const key = String(index)
  return leads.value.find((l) => String(l.accountIndex) === key)?.followerCount ?? 0
}

/// 可创建的池：冻结的排除（运营者已收摊，没有盯的价值）。
/// 已有带单员的池**保留在下拉里**——选中它就是把这个带单员重新打开，
/// 比"先在卡片里找到它再点恢复"更顺手。
/// 但这条"重新打开"属于共享开关，只有管理员能做；普通账号只列还没有带单员的池。
const selectablePools = computed(() =>
  pools.value.filter(
    (pool) =>
      !pool.isFrozen
      && (isAdmin.value || !leadIndexes.value.has(String(pool.accountIndex))),
  ),
)

const poolNameOf = (index: number | string): string => {
  const key = String(index)
  return pools.value.find((p) => String(p.accountIndex) === key)?.name ?? `池 ${key}`
}

/// 池子权益：卡片上给一个"这池多大"的量级参照。取不到就不显示，不编数字。
const poolEquityOf = (index: number | string): number | null => {
  const key = String(index)
  return pools.value.find((p) => String(p.accountIndex) === key)?.totalAssetValue ?? null
}

function openCreate(): void {
  newPoolIndex.value = selectablePools.value[0] ? String(selectablePools.value[0].accountIndex) : ''
  createOpen.value = true
}

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = ''

  try {
    leads.value = await api.leadTraders()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    loading.value = false
  }

  await loadPools()
}

async function loadPools(): Promise<void> {
  poolListError.value = ''

  try {
    pools.value = await api.listPools('all')
  } catch (error) {
    // 池名与权益解析失败只影响显示（退回「池 {索引}」），但**创建弹窗会因此变空**——
    // 那一步必须说出来，否则使用者只会看到一个空下拉，无从判断原因
    poolListError.value = error instanceof Error ? error.message : String(error)
  }
}

async function create(): Promise<void> {
  if (!newPoolIndex.value) return

  creating.value = true
  errorMessage.value = ''

  try {
    await api.configureLeadTrader(newPoolIndex.value, true)
    createOpen.value = false
    await load()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    creating.value = false
  }
}

async function setEnabled(lead: LeadTrader, isEnabled: boolean): Promise<void> {
  errorMessage.value = ''

  try {
    await api.configureLeadTrader(lead.accountIndex, isEnabled)
    await load()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  }
}

/// 停止观察 = 删除带单员。有跟单员订阅时后端会拒绝，这里把原因原样显示出来。
async function stop(lead: LeadTrader): Promise<void> {
  errorMessage.value = ''

  try {
    await api.deleteLeadTrader(lead.accountIndex)
    await load()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  }
}

onMounted(async () => {
  // 权限缓存按会话共享：进来先拉一次，按钮状态才与当前会话一致
  try {
    await refreshPermissions()
  } catch (error) {
    console.warn('读取权限失败，本页按钮将按无权限处理', error)
  }

  await load()
})
</script>

<template>
  <a-space direction="vertical" fill style="width: 100%">
    <a-card :bordered="false">
      <template #title>
        <a-space>
          <span>带单管理</span>
          <a-tag v-if="!loading" size="small">{{ leads.length }} 个</a-tag>
        </a-space>
      </template>
      <!-- 右上角只留「刷新」：创建带单员的入口**只有**列表里那张虚线卡片一处
           （与「交易所」「跟单」两页同一口径）。两处入口含义完全一样，多一个只会让人犹豫"点哪个" -->
      <template #extra>
        <a-button size="small" :loading="loading" @click="load">刷新</a-button>
      </template>

      <a-alert v-if="errorMessage" type="error" :title="errorMessage" />

      <div style="font-size: 12px; color: var(--color-text-3); line-height: 1.7">
        一个带单员负责盯着一个池：订阅它的持仓与成交流，把每次仓位变化记成信号。
        <b>它的存在与有没有人跟单无关</b>——只要它在，池子就一直被观察，
        这样「先观察一段时间再决定跟不跟」才有历史可看。
        带单员是<b>共享观察对象</b>：暂停、恢复与停止只有管理员可以操作。
      </div>

      <div v-if="!canEdit && !canDelete" class="readonly-line">
        当前账号对本页只有查看权限——不能创建、暂停或停止带单员。
      </div>
    </a-card>

    <a-spin :loading="loading" style="width: 100%">
      <div v-if="!loading && leads.length === 0" class="empty-line">还没有带单员</div>

      <div v-if="!loading" class="card-grid">
        <!-- 卡片本身不是链接：里面带着暂停/停止这类操作，
             把整张卡做成链接会让"点暂停"顺手跳走一页 -->
        <div v-for="lead in leads" :key="lead.accountIndex" class="lead-card">
          <div class="card-head">
            <RouterLink
              class="card-title"
              :to="{ name: 'lead-detail', params: { accountIndex: String(lead.accountIndex) } }"
            >
              {{ lead.name || poolNameOf(lead.accountIndex) }}
            </RouterLink>

            <a-tag v-if="!lead.isEnabled" size="small" color="gray">已暂停</a-tag>
            <a-tag v-else-if="lead.isWatching" size="small" color="green">监控中</a-tag>
            <a-tooltip v-else content="已启用，但此刻没有收到推送——连接还没建立或已经断开">
              <a-tag size="small" color="orange">未连接</a-tag>
            </a-tooltip>
          </div>

          <div class="card-signals">
            <a-tooltip content="这个池被观察到并落库的仓位变化条数。与有没有人跟单无关——没人跟也会记。">
              <span class="signals-label">累计信号</span>
            </a-tooltip>
            <span class="signals-value" :class="{ 'signals-value-empty': lead.signalCount === 0 }">
              {{ lead.signalCount }}
            </span>
            <span class="signals-unit">条</span>
          </div>

          <div class="card-meta">
            <span v-if="lead.lastSignalAt">最近一条 {{ formatSince(lead.lastSignalAt) }}</span>
            <span v-else>还没有信号</span>
            <span style="color: var(--color-text-4)">观察 {{ formatAgeSince(lead.createdAt) }}</span>
          </div>

          <div class="card-followers">
            <template v-if="lead.followerCount > 0">
              <a-tag size="small" color="arcoblue">{{ lead.activeFollowerCount }} 跟随</a-tag>
              <span>共 {{ lead.followerCount }} 个跟单员</span>
            </template>
            <span v-else class="card-idle">无人跟随（观察照旧）</span>
          </div>

          <div class="card-foot">
            <span class="foot-pool">
              <template v-if="poolEquityOf(lead.accountIndex) !== null">
                <span class="foot-pool-label">池子权益</span>
                <span class="foot-pool-value">{{ formatUsd(poolEquityOf(lead.accountIndex) ?? 0) }}</span>
              </template>
            </span>
            <a-space :size="12">
              <a-link
                v-if="lead.isEnabled"
                :disabled="!canEdit || !isAdmin"
                :title="
                  !canEdit
                    ? '需要「带单管理」的管理权限（编辑）'
                    : !isAdmin
                      ? '共享的带单员只有管理员可以暂停或恢复观察'
                      : ''
                "
                @click="setEnabled(lead, false)"
              >
                暂停
              </a-link>
              <a-link
                v-else
                :disabled="!canEdit || !isAdmin"
                :title="
                  !canEdit
                    ? '需要「带单管理」的管理权限（编辑）'
                    : !isAdmin
                      ? '共享的带单员只有管理员可以暂停或恢复观察'
                      : ''
                "
                @click="setEnabled(lead, true)"
              >
                恢复
              </a-link>
              <a-link
                status="danger"
                :disabled="!canDelete || !isAdmin"
                :title="
                  !canDelete
                    ? '需要「带单管理」的管理权限（删除）'
                    : !isAdmin
                      ? '共享的带单员只有管理员可以停止观察'
                      : ''
                "
                @click="stop(lead)"
              >
                停止
              </a-link>
            </a-space>
          </div>

          <!-- 账户索引是排查用的，不参与日常阅读：缩小置灰，悬停才给出创建时刻 -->
          <a-tooltip :content="`创建于 ${formatDateTime(lead.createdAt)}`">
            <span class="card-stamp">#{{ lead.accountIndex }}</span>
          </a-tooltip>
        </div>

        <button
          type="button"
          class="lead-card add-card"
          :disabled="!canEdit"
          :title="canEdit ? '' : '需要「带单管理」的管理权限（编辑）'"
          @click="openCreate"
        >
          <span style="font-size: 26px; line-height: 1">+</span>
          <span style="margin-top: 6px">创建带单员</span>
        </button>
      </div>
    </a-spin>

    <a-modal v-model:visible="createOpen" title="创建带单员" :ok-loading="creating" @ok="create">
      <a-space direction="vertical" fill :size="14">
        <a-alert v-if="poolListError" type="warning" :show-icon="true">
          池列表这次没取到（{{ poolListError }}），下面的选择框会是空的。
          <a-link @click="loadPools">重试</a-link>
        </a-alert>

        <div>
          <div style="font-size: 13px; margin-bottom: 6px">选择要观察的池</div>
          <a-select
            v-model="newPoolIndex"
            :style="{ width: '100%' }"
            placeholder="选择池（可按名称或账户索引搜索）"
            allow-search
            :filter-option="filterPoolOption"
          >
            <a-option
              v-for="p in selectablePools"
              :key="p.accountIndex"
              :value="String(p.accountIndex)"
              :label="poolOptionLabel(p)"
            >
              {{ poolOptionLabel(p) }} · {{ formatUsd(p.totalAssetValue) }}
              <span v-if="leadIndexes.has(String(p.accountIndex))">
                · 已有带单员（{{ followerCountOf(p.accountIndex) }} 人跟随）
              </span>
            </a-option>
          </a-select>
        </div>

        <a-alert type="info" :show-icon="true">
          创建即开始观察：服务会订阅这个池的持仓与成交频道，把每次仓位变化记成信号，
          <b>即使没有任何跟单员订阅</b>。已有带单员的池再次选中会把它的观察重新打开。
        </a-alert>
      </a-space>
    </a-modal>
  </a-space>
</template>

<style scoped>
/* 只在该页确实有被禁用的按钮时才显示（见模板里的判断） */
.readonly-line {
  margin-top: 10px;
  font-size: 12px;
  color: rgb(var(--warning-6));
}

.card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
  gap: 14px;
}

/* 空态只用一行提示，下面照旧是那张虚线卡片——"怎么添加"的入口在空列表里
   不该变成一个长得不一样的普通按钮 */
.empty-line {
  margin-bottom: 12px;
  font-size: 12px;
  color: var(--color-text-3);
}

.lead-card {
  position: relative;
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 16px 16px 18px;
  background: var(--color-bg-2);
  border: 1px solid var(--color-border-1);
  border-radius: 6px;
  transition: border-color 0.15s, box-shadow 0.15s;
}

.lead-card:hover {
  border-color: rgb(var(--primary-6));
  box-shadow: 0 2px 10px rgb(0 0 0 / 8%);
}

.card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.card-title {
  font-size: 15px;
  font-weight: 600;
  color: inherit;
  text-decoration: none;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.card-title:hover {
  color: rgb(var(--primary-6));
  text-decoration: underline;
}

.card-signals {
  display: flex;
  align-items: baseline;
  gap: 6px;
  padding: 8px 0;
  border-top: 1px solid var(--color-border-1);
  border-bottom: 1px solid var(--color-border-1);
}

.signals-label {
  font-size: 12px;
  color: var(--color-text-3);
  cursor: help;
}

/* 卡上主指标。显式给 color 而不是继承：继承意味着"改父级颜色会顺手改掉它"，
   而这个数是整张卡的视觉锚点，不该跟着别处飘。 */
.signals-value {
  margin-left: auto;
  font-size: 22px;
  font-weight: 500;
  color: var(--color-text-1);

  /* 等宽数字：一排卡片横向扫过去时，位数不同的数字也能对齐（"8 条" 与 "64 条" 的 6 不该把 8 挤歪） */
  font-variant-numeric: tabular-nums;
}

/* 信号数为 0 = "还没攒到"，与"攒了 64 条"不是一回事：
   同一个字号下用最深一档会让空观察者看起来一样有分量。浅一档，与它旁边那句「还没有信号」一致 */
.signals-value-empty {
  color: var(--color-text-3);
}

.signals-unit {
  font-size: 12px;
  color: var(--color-text-3);
}

.card-meta,
.card-followers,
.card-foot {
  font-size: 12px;
  color: var(--color-text-3);
}

.card-meta {
  display: flex;
  gap: 12px;
}

.card-followers {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 24px;
}

.card-idle {
  color: var(--color-text-3);
}

.card-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin-top: 2px;
}

/* 池子权益是这张卡上最有决策分量的数字（"这个池值不值得跟"看的就是它），
   原先整个用 --color-text-4（全卡最浅一档，比旁边"共 N 个跟单员"还浅）——轻重倒置。
   现在按"标签浅、数值深"分开：标签 text-3，数值 text-1 并加粗一档。 */
.foot-pool {
  display: inline-flex;
  align-items: baseline;
  gap: 6px;
}

.foot-pool-label {
  color: var(--color-text-3);
}

.foot-pool-value {
  color: var(--color-text-1);
  font-weight: 500;
  font-variant-numeric: tabular-nums;
}

/* 账户索引是排查用的，不该参与日常阅读 */
.card-stamp {
  position: absolute;
  right: 14px;
  bottom: 4px;
  font-family: monospace;
  font-size: 10px;
  color: var(--color-text-4);
  cursor: help;
}

/* 新建卡：虚线边框，明显不是数据卡 */
.add-card {
  align-items: center;
  justify-content: center;
  min-height: 180px;
  color: var(--color-text-3);
  background: transparent;

  /* 虚线描边比数据卡深一档：它是"这里可以加一条"的唯一提示，
     用与数据卡同一档的浅灰时，在空列表里会淡得只剩一个悬空的加号 */
  border-style: dashed;
  border-color: var(--color-border-3);
  cursor: pointer;
  font-size: 13px;
}

.add-card:hover {
  border-color: rgb(var(--primary-6));
  box-shadow: none;
}

/* 无权限时"创建"卡不该还是可点的样子：native 按钮的 disabled 不换光标，
   悬停也不该再亮起来——否则它会与"这里能加一条"的暗示自相矛盾 */
.add-card:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}

.add-card:disabled:hover {
  border-color: var(--color-border-3);
  box-shadow: none;
}
</style>
