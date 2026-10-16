<script setup lang="ts">
import { Message } from '@arco-design/web-vue'
import { IconLaunch } from '@arco-design/web-vue/es/icon'
import { computed, onMounted, ref, watch } from 'vue'

import { api, type ExchangeConfigRow, type FollowerSummary } from '@/api/client'
import { usePermission } from '@/composables/usePermission'
import {
  EXCHANGE_PRESET_GROUPS,
  codeLabel,
  kindBadge,
  presetOf,
  type ExchangeCode,
  type ExchangeKind,
  type ExchangePreset,
} from '@/domain/exchanges'
import { formatDateTime } from '@/utils/format'

/// 交易所 —— 各平台的连接配置与凭据。
///
/// 卡片布局与「带单员」「跟单」一致：一屏看清"我配了哪些平台、配没配凭据、是主网还是测试网"。
///
/// 两条界面上必须说清的事（都属于"不说清就会被误解"的那一类）：
/// <list type="number">
///   <item>**凭据按原样保存（明文）**。做过一版主密钥 + AES-GCM，但密钥与密文躺在同一个库里，
///     它谁也防不住，只换来"没设密钥就不让存凭据"这道坎——"看起来加密了"比明文更误导，
///     所以整套撤掉了，界面上直说。**因此这里只该放没有提现权限、最好也没有交易权限的密钥。**</item>
///   <item>**读取只回尾号**：界面没有"查看凭据"入口。那是减少屏幕上的暴露面，与存储方式无关。</item>
/// </list>

/// 本页自身的权限键。服务端按 `menu:{code}` 生成，前端只读不写。
///
/// 配置的写口有三处：新建/编辑（含启用/停用，都改配置）归 edit，删除归 delete——
/// 与菜单管理页同一套动作语义。
const PAGE_PERMISSION = 'menu:exchanges'

const { can, refresh: refreshPermissions } = usePermission()

const canEdit = computed(() => can(PAGE_PERMISSION, 'edit'))
const canDelete = computed(() => can(PAGE_PERMISSION, 'delete'))

const exchanges = ref<ExchangeConfigRow[]>([])

/// 实盘跟单员：用来回答"这条配置现在有没有人在用"。
///
/// 单列一个 ref 而不是和交易所一起 Promise.all：**取不到它不该让整页崩掉**——
/// 这页的主角是配置本身。取不到时界面写"这次没取到"，而不是"没有人在用"（那是两件事）。
const liveFollowers = ref<FollowerSummary[]>([])
const followersFailed = ref(false)

const loading = ref(false)
const errorMessage = ref('')
const saving = ref(false)


// ── 新建 / 编辑弹窗 ────────────────────────────────────

const formOpen = ref(false)
const editingId = ref<number | null>(null)

/// 弹窗的步骤：1 = 选平台（卡片），2 = 填配置。
///
/// 分成两步的理由是"先认平台、再填细节"：平台决定了地址、形态、凭据字段要几个，
/// 把它和名称、费率、凭据挤在一屏里，等于让使用者在还不知道要填什么的情况下先看一堆字段。
/// 编辑既有配置时**直接进第 2 步**——平台是已经定下来的事实，不需要再选一次。
const step = ref<1 | 2>(1)

/// 上一次**应用过**的预设。
///
/// 不能从 `form.code` 反推它是谁：a-select 的 v-model 在 `@change` 之前就已经更新，
/// 回调里读到的"当前"其实已经是"新"的那个了，于是"字段里还是不是上一个预设的默认值"
/// 永远判错——表现为换平台之后接口地址没跟着换（实测踩到过：切到 OKX 仍留着币安的地址）。
const lastPresetCode = ref<ExchangeCode>('binance')

/// 表单的形状。写成具名接口而不是内联字面量：初值来自 <see cref="freshForm"/>，
/// 编辑时整体替换，两处必须长得一样。
interface ExchangeForm {
  code: string
  kind: ExchangeKind
  displayName: string
  isTestnet: boolean
  restBaseUrl: string
  wsUrl: string
  makerFeePercent: number
  takerFeePercent: number
  notes: string
  apiKey: string
  apiSecret: string
  passphrase: string
}

/// 一张全新的表单：平台预设的地址与费率，空的名称、备注与凭据。
///
/// 每次「新建」都从它重来一遍，而不是在上一次的表单上改几个字段——
/// 上一次新建中途关掉时留在表单里的密钥会跟着下一条配置一起被保存，
/// 而界面上看不出那把 key 是旧的、更看不出它是为别的平台填的。
function freshForm(code: ExchangeCode): ExchangeForm {
  const preset = presetOf(code)

  return {
    code,
    kind: preset.kind,
    displayName: '',
    isTestnet: false,
    restBaseUrl: preset.restBaseUrl,
    wsUrl: preset.wsUrl,
    makerFeePercent: preset.makerFeePercent,
    takerFeePercent: preset.takerFeePercent,
    notes: '',
    apiKey: '',
    apiSecret: '',
    passphrase: '',
  }
}

const form = ref<ExchangeForm>(freshForm('binance'))

/// 凭据字段是否要显示。已有凭据时默认折叠——避免"打开编辑框看到两个空字段，
/// 以为凭据丢了"，也避免误提交空值把凭据清掉。
const credentialsTouched = ref(false)

const preset = computed(() => presetOf(form.value.code))

const isEditing = computed(() => editingId.value !== null)

const editingRow = computed(() =>
  editingId.value === null ? null : exchanges.value.find((e) => e.id === editingId.value) ?? null,
)

/// 名称去空白后是否为空。后端的必填判定用的就是 trim 之后的值，这里保持同一口径——
/// 否则"名称为空格"会被前端放行、到后端才被拒。
const hasName = computed(() => form.value.displayName.trim() !== '')

/// 凭据的两个字段是否只填了一个。
///
/// 后端只接受成对的 key 与 secret（半份凭据调不通任何接口，卡片上却会显示"已配置"，
/// 那种"看起来成功"的失败最难查）。与其等一次失败的往返，不如在按钮还按不下去的时候就说明白。
/// **passphrase 不在此列**：它是第三项，OKX 之外的平台不需要，OKX 缺它有平台自己的报错。
///
/// 编辑态下 `credentialsTouched` 为 false（没动凭据）时一律不算——那是"保持原样"，
/// 不是"只填了一个"。
const credentialPairIncomplete = computed(() => {
  if (!credentialsTouched.value) return false

  const hasKey = form.value.apiKey.trim() !== ''
  const hasSecret = form.value.apiSecret.trim() !== ''

  return hasKey !== hasSecret
})

/// 保存按钮按不下去的原因（空串 = 可以保存）。
///
/// 禁用的按钮必须说明为什么禁用——只灰掉而不给理由，使用者只能靠猜，
/// 或者把每个字段试一遍才知道差什么。
const saveBlockedReason = computed(() => {
  if (!hasName.value) return '填上名称才能保存——同一个平台可以配多条，名字是区分它们的唯一依据。'
  if (credentialPairIncomplete.value) return 'API 密钥与密钥要成对填：只填一个调不通任何接口。'

  return ''
})

/// 已经配过这家的平台：第 1 步卡片右上角的「已配置」标记。
///
/// 它不是禁用条件——同一个平台配多条是常态（两个币安账号），标记只回答"我是不是已经有了"。
const configuredCodes = computed(() => new Set(exchanges.value.map((e) => e.code)))

/// 这一家要交出哪几样凭据，用在头卡的副行。措辞跟着预设走：
/// Lighter 的第二个字段叫"签名私钥"，写成通用的 API Secret 就是一句错话。
function credentialSummaryOf(exchangePreset: ExchangePreset): string {
  return exchangePreset.needsPassphrase
    ? `API 密钥、${exchangePreset.secretLabel} 与 Passphrase`
    : `API 密钥 与 ${exchangePreset.secretLabel}`
}

/// 步骤条可点：**只允许从第 2 步退回第 1 步**（Arco 的 Steps 是 1-based）。
/// 给"想换一家"多一条路——它比先找到「返回」按钮更顺手。
function onStepChange(current: number): void {
  if (current === 1 && !isEditing.value) step.value = 1
}

function openCreate(): void {
  editingId.value = null
  credentialsTouched.value = true

  // 从第 1 步开始：平台还没选，先把 form 摆在一个自洽的初值上
  lastPresetCode.value = 'binance'
  form.value = freshForm('binance')
  step.value = 1
  formOpen.value = true
}

/// 第 1 步选中一个平台：套用它的预设（地址、形态、费率）并进入第 2 步。
///
/// 换了平台就**清空凭据**：为上一家填的 key 在这一家没有任何意义，留着它只会在保存时
/// 被当成这一家的凭据存下去——那正是"半份错误凭据"的来源，而且界面上看不出来。
/// 返回再点同一张卡不清（不该因为一次误退而让刚填的 key 消失）。
function choosePlatform(code: ExchangeCode): void {
  if (code !== form.value.code) {
    form.value.apiKey = ''
    form.value.apiSecret = ''
    form.value.passphrase = ''
  }

  applyPreset(code)
  step.value = 2
}

function openEdit(row: ExchangeConfigRow): void {
  editingId.value = row.id
  credentialsTouched.value = false

  // 平台已定，直接进第 2 步
  step.value = 2

  lastPresetCode.value = row.code as ExchangeCode
  form.value = {
    code: row.code,
    kind: row.kind as ExchangeKind,
    displayName: row.displayName,
    isTestnet: row.isTestnet,

    // 地址刻意留空：读取接口不返回它（由平台预设决定、不面向界面），
    // 提交时也不会带上这两个键——后端据此保持已存的值不变
    restBaseUrl: '',
    wsUrl: '',
    makerFeePercent: row.makerFeePercent,
    takerFeePercent: row.takerFeePercent,
    notes: row.notes ?? '',
    apiKey: '',
    apiSecret: '',
    passphrase: '',
  }

  formOpen.value = true
}

/// 平台一换就把预设套上去。
///
/// 用 watch 而不是 `@change`：实测 Arco 的 `a-select` 并没有把 `change` 事件送进那个处理器
/// （表现是平台换了、接口地址还留着上一家的）。watch 盯的是数据本身，不依赖组件的事件约定。
/// 打开表单时预置的 code 与 `lastPresetCode` 相同，因此会被下面的守卫跳过——
/// 否则"编辑一条已有配置"会在打开的一瞬间被套上平台默认地址，把存好的地址悄悄改掉。
watch(
  () => form.value.code,
  (code) => {
    if (code === lastPresetCode.value) return

    applyPreset(code as ExchangeCode)
  },
)

/// 换预设会把地址与费率**替换成该平台的默认值**，但只在字段为空或仍是上一个预设的值时才动它——
/// 使用者手改过的内容不该被一次下拉切换静默抹掉。
function applyPreset(code: ExchangeCode): void {
  const previous = presetOf(lastPresetCode.value)
  const next = presetOf(code)

  lastPresetCode.value = code
  form.value.code = code
  form.value.kind = next.kind

  const keep = (current: string, was: string, fallback: string): string =>
    current.trim() === '' || current === was ? fallback : current

  form.value.restBaseUrl = keep(form.value.restBaseUrl, previous.restBaseUrl, next.restBaseUrl)
  form.value.wsUrl = keep(form.value.wsUrl, previous.wsUrl, next.wsUrl)

  const untouchedFees =
    form.value.makerFeePercent === previous.makerFeePercent
    && form.value.takerFeePercent === previous.takerFeePercent

  if (untouchedFees) {
    form.value.makerFeePercent = next.makerFeePercent
    form.value.takerFeePercent = next.takerFeePercent
  }
}

async function submit(): Promise<void> {
  saving.value = true

  try {
    const payload = {
      code: form.value.code,
      kind: form.value.kind,
      displayName: form.value.displayName.trim(),
      isTestnet: form.value.isTestnet,

      // 地址只在**新建**时随预设提交；编辑时不带这两个键，
      // 后端对"不传"的语义是保持原值（读取接口不返回它，前端也就无从回传）
      ...(editingId.value === null
        ? { restBaseUrl: form.value.restBaseUrl.trim(), wsUrl: form.value.wsUrl.trim() }
        : {}),
      makerFeePercent: form.value.makerFeePercent,
      takerFeePercent: form.value.takerFeePercent,
      notes: form.value.notes.trim(),
      // 凭据只在"动过"时才发出去：没动就不带这三个键，
      // 后端据此保持原样（三态语义），也就不会误清已存的凭据
      ...(credentialsTouched.value
        ? {
            apiKey: form.value.apiKey,
            apiSecret: form.value.apiSecret,
            passphrase: preset.value.needsPassphrase ? form.value.passphrase : '',
          }
        : {}),
    }

    if (editingId.value === null) {
      await api.createExchange(payload)
    } else {
      await api.updateExchange(editingId.value, { ...payload, isEnabled: editingRow.value?.isEnabled ?? true })
    }

    formOpen.value = false

    // 写操作的结果用 Toast 报。**错误尤其要走这条路**：弹窗是盖在页面上的一层，
    // 写在页面顶部的错误条在它开着的时候根本看不见——失败会表现为"点了没反应"。
    Message.success(editingId.value === null ? '配置已创建' : '配置已保存')
    await load()
  } catch (error) {
    Message.error(error instanceof Error ? error.message : String(error))
  } finally {
    saving.value = false
  }
}

async function setEnabled(row: ExchangeConfigRow, isEnabled: boolean): Promise<void> {
  try {
    // 不带地址：地址不面向界面，更新时"不传"即保持原值
    await api.updateExchange(row.id, {
      displayName: row.displayName,
      isTestnet: row.isTestnet,
      makerFeePercent: row.makerFeePercent,
      takerFeePercent: row.takerFeePercent,
      notes: row.notes ?? '',
      isEnabled,
    })

    Message.success(isEnabled ? '已启用' : '已停用')
    await load()
  } catch (error) {
    Message.error(error instanceof Error ? error.message : String(error))
  }
}

// 删除用独立的开关 + 目标两条状态：v-model 需要的是可赋值的表达式，
// 绑成 `deleteTarget !== null` 编译不过（那是判断，不是赋值目标）
const deleteOpen = ref(false)
const deleteTarget = ref<ExchangeConfigRow | null>(null)
const deleting = ref(false)

function requestDelete(row: ExchangeConfigRow): void {
  deleteTarget.value = row
  deleteOpen.value = true
}

async function confirmDelete(): Promise<void> {
  if (deleteTarget.value === null) return

  deleting.value = true

  try {
    await api.deleteExchange(deleteTarget.value.id)

    Message.success(`已删除「${deleteTarget.value.displayName}」`)
    deleteOpen.value = false
    deleteTarget.value = null
    await load()
  } catch (error) {
    Message.error(error instanceof Error ? error.message : String(error))
  } finally {
    deleting.value = false
  }
}

/// 新增态的「返回」回第 1 步、编辑态是「取消」直接关。按钮上的字与动作都由这里的一处判断决定，
/// 免得字写「返回」而动作是关窗这类对不上的情况散在两个地方。
function backOrCancel(): void {
  if (isEditing.value) formOpen.value = false
  else step.value = 1
}

/// 挂在某条配置上的实盘跟单员。
///
/// **按配置 Id 关联，不按显示名**：名字是使用者起的、后端不保证唯一，
/// 按名字匹配会把关系对错——而错的后果是"停用一条正在被实盘使用的配置"看起来毫无影响。
function liveUsersOf(configId: number): FollowerSummary[] {
  return liveFollowers.value.filter((f) => f.exchangeConfigId === configId)
}

/// 费率去尾零：0.0200% 与 0.02% 是同一个数，卡面上后者更省眼睛（仍然保留 4 位小数的精度）。
function feeText(percent: number): string {
  return `${Number(percent.toFixed(4))}%`
}

/// 平台标识图。认不出的标识返回 null，由调用方决定显示成什么样——
/// 这里不塞一个占位图：假的图标比没有图标更容易让人认错平台。
function iconOf(code: string): string | null {
  return presetOf(code).icon
}

function credentialsSummary(row: ExchangeConfigRow): string {
  if (!row.hasCredentials) return '未配置'

  // 明说"明文"：卡片是使用者每天看的地方，凭据怎么存的必须在那里就能看到，
  // 而不是藏在文档或某次对话里
  return `已配置（明文）· 尾号 ${row.apiKeyHint ?? '****'}${row.hasPassphrase ? ' · 含 passphrase' : ''}`
}

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = ''

  try {
    exchanges.value = await api.exchanges()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    loading.value = false
  }

  // 使用情况另取一次，失败只影响这一行（见 liveFollowers 的说明）
  try {
    liveFollowers.value = (await api.followers()).filter((f) => f.mode === 'live')
    followersFailed.value = false
  } catch {
    liveFollowers.value = []
    followersFailed.value = true
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
          <span>交易所</span>
          <a-tag v-if="!loading" size="small">{{ exchanges.length }} 个</a-tag>
        </a-space>
      </template>
      <!-- 右上角只留「刷新」：新建的入口**只有**列表里那张虚线卡片一处。
           两处入口的含义完全一样，多一个只会让人犹豫"点哪个"，还让空列表里的入口
           与它旁边那个长得不一样的普通按钮互相打架 -->
      <template #extra>
        <a-button size="small" :loading="loading" @click="load">刷新</a-button>
      </template>

      <a-alert v-if="errorMessage" type="error" :title="errorMessage" />

      <div class="hint-line">
        这里登记各交易所的连接信息与凭据。实盘跟单员用这里的凭据下单——
        <b>执行侧目前只有币安 U 本位永续</b>，OKX 与 Gate 只有合约目录（供标的映射用）。
        界面上的费率只是预填的常见标准档，请按你的实际档位改。
        凭据<b>以明文保存在本机数据库</b>（未加密，也不假装加密），只放权限最小、能随时吊销的密钥。
      </div>

      <div v-if="!canEdit && !canDelete" class="readonly-line" style="margin-top: 10px">
        当前账号对本页只有查看权限——不能新建、编辑、启停或删除交易所配置。
      </div>
    </a-card>

    <a-spin :loading="loading" style="width: 100%">
      <div v-if="!loading && exchanges.length === 0" class="empty-line">还没有配置任何交易所</div>

      <div v-if="!loading" class="card-grid">
        <div v-for="row in exchanges" :key="row.id" class="exchange-card">
          <div class="card-head">
            <img
              v-if="iconOf(row.code)"
              class="platform-icon"
              :src="iconOf(row.code) ?? ''"
              :alt="codeLabel(row.code)"
              :title="codeLabel(row.code)"
            />
            <span class="card-title">{{ row.displayName }}</span>
            <a-space :size="6">
              <a-tag size="small" color="arcoblue">{{ codeLabel(row.code) }}</a-tag>

              <!-- 形态与新建弹窗同款胶囊（CEX / DEX）：同一个概念在列表与弹窗里必须长一样 -->
              <span class="kind-capsule" :class="`kind-capsule-${row.kind}`">
                {{ kindBadge(row.kind) }}
              </span>

              <a-tag v-if="row.isTestnet" size="small" color="orange">测试网</a-tag>
            </a-space>
          </div>

          <div class="card-state">
            <a-tag size="small" :color="row.isEnabled ? 'green' : 'gray'">
              {{ row.isEnabled ? '已启用' : '已停用' }}
            </a-tag>
            <span :class="{ 'cred-missing': !row.hasCredentials }">
              {{ credentialsSummary(row) }}
            </span>
          </div>

          <!-- 使用情况：这张卡要回答的核心问题之一——
               项目的基本事实是"配好凭据不会自己开始交易"，真正让它动钱的是挂上它的实盘跟单员。
               没有这一行，"停用一条正在被使用的配置"看不出任何后果 -->
          <div class="card-usage">
            <template v-if="liveUsersOf(row.id).length > 0">
              <a-tag size="small" color="arcoblue">{{ liveUsersOf(row.id).length }} 个实盘跟单</a-tag>
              <a-tooltip
                :content="liveUsersOf(row.id).map((f) => `跟单员 #${f.id} · ${f.leadName}`).join('\n')"
              >
                <span class="usage-names">{{ liveUsersOf(row.id).map((f) => f.leadName).join('、') }}</span>
              </a-tooltip>
            </template>
            <span v-else-if="followersFailed" class="usage-none">实盘使用情况这次没取到</span>
            <span v-else class="usage-none">暂无实盘跟单员在用</span>
          </div>

          <div class="card-meta">
            <span>挂单 {{ feeText(row.makerFeePercent) }}</span>
            <span>吃单 {{ feeText(row.takerFeePercent) }}</span>
          </div>

          <div v-if="row.notes" class="card-notes">{{ row.notes }}</div>

          <div class="card-foot">
            <a-space :size="12">
              <a-link
                :disabled="!canEdit"
                :title="canEdit ? '' : '需要「交易所」的管理权限（编辑）'"
                @click="openEdit(row)"
              >
                编辑
              </a-link>
              <a-link
                v-if="row.isEnabled"
                :disabled="!canEdit"
                :title="canEdit ? '' : '需要「交易所」的管理权限（编辑）'"
                @click="setEnabled(row, false)"
              >
                停用
              </a-link>
              <a-link
                v-else
                :disabled="!canEdit"
                :title="canEdit ? '' : '需要「交易所」的管理权限（编辑）'"
                @click="setEnabled(row, true)"
              >
                启用
              </a-link>
              <a-link
                status="danger"
                :disabled="!canDelete"
                :title="canDelete ? '' : '需要「交易所」的管理权限（删除）'"
                @click="requestDelete(row)"
              >
                删除
              </a-link>
            </a-space>
            <a-tooltip :content="`创建于 ${formatDateTime(row.createdAt)}`">
              <span class="card-stamp">#{{ row.id }}</span>
            </a-tooltip>
          </div>
        </div>

        <button
          type="button"
          class="exchange-card add-card"
          :disabled="!canEdit"
          :title="canEdit ? '' : '需要「交易所」的管理权限（编辑）'"
          @click="openCreate"
        >
          <span style="font-size: 26px; line-height: 1">+</span>
          <span style="margin-top: 6px">新建配置</span>
        </button>
      </div>
    </a-spin>

    <!-- 新建 / 编辑：新建分两步，编辑直接进第 2 步。
         遮罩点击不关闭：这是表单，一次误点不该把刚填的东西丢掉（Esc 仍可关）。 -->
    <a-modal
      v-model:visible="formOpen"
      :title="isEditing ? '编辑交易所配置' : '新建交易所配置'"
      :width="520"
      :footer="false"
      :mask-closable="false"
      unmount-on-close
    >
      <!-- 步骤条居中收窄，并可点回退（LuckyCat 的形制：给"想换个平台"多一条路）。
           `changeable` 不能省：Arco 只在它为真时才发 change 事件，少了它这个回退是死的 -->
      <a-steps v-if="!isEditing" :current="step" changeable class="ex-steps" @change="onStepChange">
        <a-step>选择平台</a-step>
        <a-step>填写配置</a-step>
      </a-steps>

      <!-- 第 1 步：平台卡。形制照 LuckyCat——**图标居中在上、名称在下**，一行三张。
           卡面只放"认得出哪家"所需的图标与名称：类别由分组标题说，卡片上不再挂胶囊
           （同一件事在两处说，读了也没多一个信息）；简介与费率也不放，两者都不是"选哪家"的依据 -->
      <div v-if="step === 1">
        <div v-for="group in EXCHANGE_PRESET_GROUPS" :key="group.kind" class="platform-group">
          <div class="platform-group-title" :class="`platform-group-${group.kind}`">
            {{ group.title }}
          </div>

          <div class="platform-grid">
            <button
              v-for="p in group.presets"
              :key="p.code"
              type="button"
              class="platform-card"
              @click="choosePlatform(p.code)"
            >
              <span v-if="configuredCodes.has(p.code)" class="platform-configured">已配置</span>

              <span class="platform-icon-lg">
                <img v-if="p.icon" :src="p.icon" :alt="p.label" />
                <span v-else class="platform-letter">{{ p.label.slice(0, 1) }}</span>
              </span>

              <span class="platform-name">{{ p.label }}</span>
            </button>
          </div>
        </div>
      </div>

      <template v-else>
        <!-- 头卡：说清"在配谁"与"这家要交出什么"。副行是**凭据需求**，措辞跟着预设走——
             Lighter 的第二个字段叫签名私钥，不是 API Secret，写成通用词等于说错话 -->
        <div class="ex-head">
          <span class="ex-head-icon">
            <img v-if="preset.icon" :src="preset.icon" :alt="preset.label" />
            <span v-else class="platform-letter">{{ preset.label.slice(0, 1) }}</span>
          </span>

          <div class="ex-head-text">
            <div class="ex-head-name">
              <span>{{ preset.label }}</span>
              <span class="kind-capsule" :class="`kind-capsule-${preset.kind}`">
                {{ kindBadge(preset.kind) }}
              </span>
            </div>
            <div class="ex-head-sub">需要 {{ credentialSummaryOf(preset) }}</div>
          </div>

          <a-link v-if="!isEditing" class="reselect" @click="backOrCancel">换一个平台</a-link>
        </div>

        <a-form :model="form" layout="vertical">
          <a-form-item label="名称" required>
            <a-input
              v-model="form.displayName"
              placeholder="同一个平台配多条时靠它区分，例如「币安主账号」"
            />
          </a-form-item>

          <!-- 凭据：三态与折叠都保留（这是本项目的语义，不跟 LuckyCat 的"留空保持原值"混），
               只是字段改成竖排表单的样子 -->
          <div class="credentials">
            <div class="cred-head">
              <span class="cred-title">凭据</span>
              <span v-if="editingRow" class="field-hint">当前：{{ credentialsSummary(editingRow) }}</span>
              <a-link v-if="editingRow && !credentialsTouched" @click="credentialsTouched = true">
                更换 / 清除
              </a-link>
            </div>

            <div class="cred-hint-box">
              <span aria-hidden="true">ℹ️</span>
              <span>{{ preset.credentialHint }}</span>
            </div>

            <!-- 邀请注册入口：取 key 指引的下一个问题往往就是"还没有账号"。
                 只在新建流程出现（编辑既有配置的人已经有账号了）；新标签页打开，
                 不打断填表；样式收敛为一条次级链接——不向已经配好的人吆喝 -->
            <a-link
              v-if="!isEditing && preset.inviteUrl"
              class="invite-link"
              :href="preset.inviteUrl"
              target="_blank"
              rel="noopener noreferrer"
              :title="`通过邀请链接注册 ${preset.label}`"
            >
              还没有账号？通过邀请注册
              <IconLaunch class="invite-link-icon" />
            </a-link>

            <template v-if="!credentialsTouched">
              <div class="field-hint">
                不动这里就保持原样。要清除已存的凭据，点「更换 / 清除」后把字段留空保存即可。
              </div>
            </template>

            <template v-else>
              <a-form-item label="API 密钥">
                <a-input v-model="form.apiKey" placeholder="在平台的 API 管理页创建后粘贴" allow-clear />
              </a-form-item>

              <a-form-item :label="preset.secretLabel">
                <a-input-password
                  v-model="form.apiSecret"
                  placeholder="创建时一并给出，之后只显示一次"
                  allow-clear
                />
              </a-form-item>

              <a-form-item v-if="preset.needsPassphrase" label="Passphrase">
                <a-input-password v-model="form.passphrase" placeholder="创建 key 时自己设的口令" allow-clear />
              </a-form-item>

              <div v-if="credentialPairIncomplete" class="field-error">
                只填了一个：API 密钥与密钥必须成对，半份凭据调不通任何接口。
              </div>

              <div class="field-hint">
                凭据<b>按原样保存在本机数据库里（明文，未加密）</b>——请只放没有提现权限、
                最好也没有交易权限的密钥。界面保存后只回显尾号，不提供查看入口。
                <template v-if="isEditing">两个字段都留空 = 清除已存的凭据。</template>
              </div>
            </template>
          </div>

          <!-- 费率与备注收进可折叠区：两项都已按常见标准档预填，多数人不必动；
               摊开摆着会把"填名称与凭据"这件正事挤到折叠线以下 -->
          <a-collapse :bordered="false" class="optional-block">
            <a-collapse-item key="optional">
              <template #header>
                <span class="optional-title">费率与备注</span>
                <span class="optional-hint">
                  {{ feeText(form.makerFeePercent) }} / {{ feeText(form.takerFeePercent) }}（已预填常见标准档）
                </span>
              </template>

              <div class="fee-row">
                <a-form-item label="挂单费率 %">
                  <a-input-number v-model="form.makerFeePercent" :min="0" :step="0.01" :precision="4" />
                </a-form-item>
                <a-form-item label="吃单费率 %">
                  <a-input-number v-model="form.takerFeePercent" :min="0" :step="0.01" :precision="4" />
                </a-form-item>
              </div>

              <a-form-item label="备注">
                <a-textarea v-model="form.notes" :auto-size="{ minRows: 2 }" placeholder="可留空" />
              </a-form-item>
            </a-collapse-item>
          </a-collapse>
        </a-form>
      </template>

      <!-- 页脚左右分置（LuckyCat 的形制）：左边写清"为什么保存按不下去"，右边是动作。
           第 1 步没有页脚——点卡片即前进，回退靠步骤条或右上角的 × -->
      <div v-if="step === 2" class="ex-foot">
        <div class="save-blocked-line">
          <span v-if="saveBlockedReason && !saving" class="save-blocked">{{ saveBlockedReason }}</span>
        </div>

        <a-space>
          <a-button @click="backOrCancel">{{ isEditing ? '取消' : '返回' }}</a-button>
          <a-button
            type="primary"
            :disabled="saveBlockedReason !== ''"
            :loading="saving"
            @click="submit"
          >
            {{ saving ? '保存中…' : '保存配置' }}
          </a-button>
        </a-space>
      </div>
    </a-modal>

    <!-- 删除确认 -->
    <a-modal
      v-model:visible="deleteOpen"
      title="删除这条交易所配置？"
      :ok-loading="deleting"
      ok-text="删除"
      cancel-text="取消"
      @ok="confirmDelete"
    >
      <div style="font-size: 13px; line-height: 1.8">
        将删除「{{ deleteTarget?.displayName }}」及其保存的凭据，<b>无法恢复</b>。
      </div>
    </a-modal>
  </a-space>
</template>

<style scoped>
.hint-line {
  font-size: 12px;
  color: var(--color-text-3);
  line-height: 1.8;
}

/* 只在该页确实有被禁用的按钮时才显示（见模板里的判断） */
.readonly-line {
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

.exchange-card {
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

.exchange-card:hover {
  border-color: rgb(var(--primary-6));
  box-shadow: 0 2px 10px rgb(0 0 0 / 8%);
}

.card-head {
  display: flex;
  align-items: center;
  gap: 8px;
}

.card-head .card-title {
  margin-right: auto;
}

.card-title {
  font-size: 15px;
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.card-state {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  color: var(--color-text-3);
  padding: 8px 0;
  border-top: 1px solid var(--color-border-1);
  border-bottom: 1px solid var(--color-border-1);
}

/* 凭据缺失要显眼：它决定这条配置现在能不能真的用 */
.cred-missing {
  color: rgb(var(--warning-6));
}

.card-meta,
.card-foot {
  display: flex;
  gap: 12px;
  font-size: 12px;
  color: var(--color-text-3);
}

/* 使用情况一行：标签 + 用它的那些人（名字多了省略号收尾，完整名单在悬停里） */
.card-usage {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 22px;
  font-size: 12px;
  color: var(--color-text-3);
}

.usage-names {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  cursor: help;
}

.usage-none {
  color: var(--color-text-3);
}


.card-notes {
  font-size: 12px;
  color: var(--color-text-3);
  line-height: 1.6;
}

.card-foot {
  align-items: center;
  justify-content: space-between;
  margin-top: 2px;
}

.card-stamp {
  font-family: monospace;
  font-size: 10px;
  color: var(--color-text-4);
  cursor: help;
}

.field-error {
  font-size: 12px;
  color: rgb(var(--danger-6));
  line-height: 1.7;
}


.field-hint {
  font-size: 12px;
  color: var(--color-text-3);
  line-height: 1.7;
  margin-top: 6px;
}

.credentials {
  padding: 10px 12px;
  border: 1px dashed var(--color-border-1);
  border-radius: 6px;
}

.cred-head {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 8px;
}

.cred-head .field-hint {
  margin: 0;
}

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

/* 无权限时"新建配置"卡不该还是可点的样子：native 按钮的 disabled 不换光标，
   悬停也不该再亮起来——否则它会与"这里可以加一条"的暗示自相矛盾 */
.add-card:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}

.add-card:disabled:hover {
  border-color: var(--color-border-3);
  box-shadow: none;
}

/* ── 弹窗：步骤条 ──────────────────────────────────── */

/* 居中收窄：它是这张弹窗的"路线图"，不是需要占满宽度的控件 */
.ex-steps {
  max-width: 320px;
  margin: 4px auto 20px;
}

/* ── 弹窗第 1 步：平台卡（形制照 LuckyCat：图标在上、名称在下、居中）── */

.platform-group + .platform-group {
  margin-top: 16px;
}

/* 组标题只是"这几家是一类"的提示，做成小字而不是大标题：
   它不需要被读，只需要被扫到 */
.platform-group-title {
  margin-bottom: 8px;
  font-size: 12px;
  font-weight: 600;
}

.platform-group-cex {
  color: rgb(var(--arcoblue-6));
}

.platform-group-dex {
  color: rgb(var(--purple-6));
}

/* 三列固定：一家占一行也还是卡片大小，不会被拉成一整条 */
.platform-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
}

.platform-card {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  padding: 10px 8px;
  font: inherit;
  background: var(--color-bg-1);
  border: 1px solid var(--color-border-2);
  border-radius: 10px;
  cursor: pointer;
  transition: border-color 0.15s, background-color 0.15s;
}

.platform-card:hover {
  border-color: rgb(var(--primary-6));
  background: var(--color-fill-1);
}

/* 已配置标记：安静的绿字，不抢"选哪家"这件事 */
.platform-configured {
  position: absolute;
  top: 6px;
  right: 8px;
  font-size: 11px;
  color: rgb(var(--success-6));
}

/* 图标给足位置：认平台靠它，名称只是确认 */
.platform-icon-lg {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
}

.platform-icon-lg img {
  width: 30px;
  height: 30px;
  border-radius: 6px;
  object-fit: contain;
}

/* 没有图标的兜底：首字母，而不是一个假图标 */
.platform-letter {
  font-size: 17px;
  font-weight: 700;
  color: var(--color-text-2);
}

.platform-name {
  font-size: 13px;
  font-weight: 500;
  color: var(--color-text-1);
}

/* 形态胶囊。缩写（CEX / DEX）而不是中文全称：它要跟平台名挤在一行里，
   而这两个缩写在本领域是通用写法。配色沿用设计稿的语义：CEX 金底、DEX 紫底 */
.kind-capsule {
  padding: 1px 8px;
  font-size: 10px;
  font-weight: 600;
  border-radius: 100px;
  white-space: nowrap;
}

.kind-capsule-cex {
  color: rgb(var(--arcoblue-6));
  background: rgb(var(--gold-2));
}

.kind-capsule-dex {
  color: rgb(var(--purple-6));
  background: rgb(var(--purple-1));
}

/* ── 弹窗第 2 步：平台头卡 ─────────────────────────── */

.ex-head {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px;
  margin-bottom: 16px;
  background: var(--color-fill-1);
  border: 1px solid var(--color-border-2);
  border-radius: 10px;
}

.ex-head-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
}

.ex-head-icon img {
  width: 32px;
  height: 32px;
  border-radius: 6px;
  object-fit: contain;
}

.ex-head-text {
  flex: 1;
  min-width: 0;
}

.ex-head-name {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 14px;
  font-weight: 600;
  color: var(--color-text-1);
}

/* 副行写的是"这家要交出什么凭据"，不是又一处平台名 */
.ex-head-sub {
  font-size: 12px;
  color: var(--color-text-3);
}

.reselect {
  flex: 0 0 auto;
  font-size: 12px;
}

/* ── 弹窗第 2 步：表单区 ───────────────────────────── */

.fee-row {
  display: flex;
  gap: 16px;
}

.fee-row .arco-form-item {
  flex: 1;
}

/* 可折叠的次要区（费率与备注）：与凭据区同一种虚线分组框——一眼看出这两块是"分类"，
   而不是又一组必填项。折起来时标题行右侧带出当前费率，不展开也知道里面是什么 */
.optional-block {
  border: 1px dashed var(--color-border-2);
  border-radius: 8px;
}

.optional-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--color-text-1);
}

.optional-hint {
  margin-left: 10px;
  font-size: 12px;
  color: var(--color-text-3);
}

/* 凭据整块：三态与折叠的语义都在这只盒子里 */
.credentials {
  padding: 12px 12px 4px;
  margin-bottom: 20px;
  border: 1px dashed var(--color-border-2);
  border-radius: 8px;
}

.cred-head {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 24px;
}

.cred-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--color-text-1);
}

.cred-head .field-hint {
  margin: 0;
}

.cred-head .arco-link {
  margin-left: auto;
  font-size: 12px;
}

/* 平台自己的取 key 指引。金底沿用"要留意"的语义 */
.cred-hint-box {
  display: flex;
  gap: 8px;
  padding: 8px 10px;
  margin-bottom: 10px;
  font-size: 12px;
  line-height: 1.7;
  color: var(--color-text-2);
  background: rgb(var(--gold-1));
  border: 1px solid rgb(var(--gold-3));
  border-radius: 6px;
}

/* 邀请注册入口：紧贴取 key 指引的下一条。小、灰、可不理——
   它只对"还没有账号"的人有用，有账号的人扫过去不构成打扰 */
.invite-link {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  margin: -4px 0 10px;
  font-size: 12px;
}

.invite-link-icon {
  font-size: 12px;
}

/* 凭据字段的竖排表单项：把默认的 20px 下边距收一点，盒子里的节奏才不散 */
.credentials .arco-form-item {
  margin-bottom: 12px;
}

/* ── 弹窗页脚：左右分置 ────────────────────────────── */

.ex-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-top: 8px;
}

.save-blocked-line {
  min-width: 0;
  font-size: 12px;
  line-height: 1.7;
}

.save-blocked {
  color: rgb(var(--warning-6));
}
</style>
