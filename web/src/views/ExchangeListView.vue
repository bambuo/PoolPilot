<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'

import { api, type ExchangeConfigRow } from '@/api/client'
import {
  EXCHANGE_PRESETS,
  codeLabel,
  kindLabel,
  presetOf,
  type ExchangeCode,
  type ExchangeKind,
} from '@/domain/exchanges'
import { formatDateTime, formatNumber } from '@/utils/format'

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
const exchanges = ref<ExchangeConfigRow[]>([])
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

const form = ref({
  code: 'binance',
  kind: 'cex' as ExchangeKind,
  displayName: '',
  isTestnet: false,
  restBaseUrl: '',
  wsUrl: '',
  makerFeePercent: 0.02,
  takerFeePercent: 0.05,
  notes: '',
  apiKey: '',
  apiSecret: '',
  passphrase: '',
})

/// 凭据字段是否要显示。已有凭据时默认折叠——避免"打开编辑框看到两个空字段，
/// 以为凭据丢了"，也避免误提交空值把凭据清掉。
const credentialsTouched = ref(false)

const preset = computed(() => presetOf(form.value.code))

const isEditing = computed(() => editingId.value !== null)

const editingRow = computed(() =>
  editingId.value === null ? null : exchanges.value.find((e) => e.id === editingId.value) ?? null,
)

function openCreate(): void {
  editingId.value = null
  credentialsTouched.value = true

  // 从第 1 步开始：平台还没选，先把 form 摆在一个自洽的初值上
  lastPresetCode.value = 'binance'
  applyPreset('binance')
  form.value.displayName = ''
  form.value.notes = ''
  step.value = 1
  formOpen.value = true
}

/// 第 1 步选中一个平台：套用它的预设（地址、形态、费率）并进入第 2 步。
function choosePlatform(code: ExchangeCode): void {
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
  errorMessage.value = ''

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
    await load()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    saving.value = false
  }
}

async function setEnabled(row: ExchangeConfigRow, isEnabled: boolean): Promise<void> {
  errorMessage.value = ''

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

    await load()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
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
  errorMessage.value = ''

  try {
    await api.deleteExchange(deleteTarget.value.id)
    deleteOpen.value = false
    deleteTarget.value = null
    await load()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    deleting.value = false
  }
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
}

onMounted(load)
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
      <template #extra>
        <a-space>
          <a-button size="small" :loading="loading" @click="load">刷新</a-button>
          <a-button type="primary" size="small" @click="openCreate">新建配置</a-button>
        </a-space>
      </template>

      <a-alert v-if="errorMessage" type="error" :title="errorMessage" />

      <div class="hint-line">
        这里登记各交易所的连接信息与凭据。实盘跟单员用这里的凭据下单——
        <b>执行侧目前只有币安 U 本位永续</b>，OKX 与 Gate 只有合约目录（供标的映射用）。
        界面上的费率只是预填的常见标准档，请按你的实际档位改。
        凭据<b>以明文保存在本机数据库</b>（未加密，也不假装加密），只放权限最小、能随时吊销的密钥。
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
            <a-space :size="4">
              <a-tag size="small" color="arcoblue">{{ codeLabel(row.code) }}</a-tag>
              <a-tag size="small" :color="row.kind === 'dex' ? 'purple' : 'gray'">
                {{ kindLabel(row.kind) }}
              </a-tag>
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

          <div class="card-meta">
            <span>挂单 {{ formatNumber(row.makerFeePercent, 4) }}%</span>
            <span>吃单 {{ formatNumber(row.takerFeePercent, 4) }}%</span>
          </div>

          <div v-if="row.notes" class="card-notes">{{ row.notes }}</div>

          <div class="card-foot">
            <a-space :size="12">
              <a-link @click="openEdit(row)">编辑</a-link>
              <a-link v-if="row.isEnabled" @click="setEnabled(row, false)">停用</a-link>
              <a-link v-else @click="setEnabled(row, true)">启用</a-link>
              <a-link status="danger" @click="requestDelete(row)">删除</a-link>
            </a-space>
            <a-tooltip :content="`创建于 ${formatDateTime(row.createdAt)}`">
              <span class="card-stamp">#{{ row.id }}</span>
            </a-tooltip>
          </div>
        </div>

        <button type="button" class="exchange-card add-card" @click="openCreate">
          <span style="font-size: 26px; line-height: 1">+</span>
          <span style="margin-top: 6px">新建配置</span>
        </button>
      </div>
    </a-spin>

    <!-- 新建 / 编辑：新建分两步，编辑直接进第 2 步 -->
    <a-modal
      v-model:visible="formOpen"
      :title="isEditing ? '编辑交易所配置' : '新建交易所配置'"
      :ok-loading="saving"
      :width="560"
      :footer="false"
    >
      <a-steps v-if="!isEditing" :current="step" style="margin-bottom: 18px">
        <a-step>选择平台</a-step>
        <a-step>填写配置</a-step>
      </a-steps>

      <!-- 第 1 步：平台以卡片列出。点卡片即选中并进入下一步 -->
      <div v-if="step === 1" class="platform-grid">
        <button
          v-for="p in EXCHANGE_PRESETS"
          :key="p.code"
          type="button"
          class="platform-card"
          @click="choosePlatform(p.code)"
        >
          <div class="platform-head">
            <img v-if="p.icon" class="platform-icon" :src="p.icon" :alt="p.label" />
            <span class="platform-name">{{ p.label }}</span>
            <a-tag size="small" :color="p.kind === 'dex' ? 'purple' : 'gray'">
              {{ kindLabel(p.kind) }}
            </a-tag>
          </div>
          <div class="platform-blurb">{{ p.blurb }}</div>
          <div class="platform-fees">
            挂单 {{ formatNumber(p.makerFeePercent, 4) }}% · 吃单 {{ formatNumber(p.takerFeePercent, 4) }}%
          </div>
        </button>
      </div>

      <a-space v-else direction="vertical" fill :size="14">
        <!-- 平台已选定：显示出来，并在新建时给一条回到第 1 步的路 -->
        <div class="chosen-platform">
          <span class="field-label" style="margin: 0">平台</span>
          <img v-if="preset.icon" class="platform-icon" :src="preset.icon" :alt="preset.label" />
          <span class="platform-name">{{ preset.label }}</span>
          <a-tag size="small" :color="preset.kind === 'dex' ? 'purple' : 'gray'">
            {{ kindLabel(preset.kind) }}
          </a-tag>
          <a-link v-if="!isEditing" class="reselect" @click="step = 1">换一个平台</a-link>
        </div>

        <div>
          <div class="field-label">名称</div>
          <a-input v-model="form.displayName" placeholder="同一个平台配多条时靠它区分，例如「币安主账号」" />
        </div>


        <a-space :size="12" wrap>
          <div>
            <div class="field-label">挂单费率 %</div>
            <a-input-number v-model="form.makerFeePercent" :min="0" :step="0.01" :precision="4" />
          </div>
          <div>
            <div class="field-label">吃单费率 %</div>
            <a-input-number v-model="form.takerFeePercent" :min="0" :step="0.01" :precision="4" />
          </div>
        </a-space>

        <!-- 凭据 -->
        <div class="credentials">
          <div class="cred-head">
            <span class="field-label" style="margin: 0">凭据</span>
            <template v-if="editingRow">
              <span class="field-hint" style="margin: 0">
                当前：{{ credentialsSummary(editingRow) }}
              </span>
              <a-link v-if="!credentialsTouched" @click="credentialsTouched = true">
                更换 / 清除
              </a-link>
            </template>
          </div>

          <template v-if="!credentialsTouched">
            <div class="field-hint">
              不动这里就保持原样。要清除已存的凭据，点「更换 / 清除」后把字段留空保存即可。
            </div>
          </template>

          <template v-else>
            <a-space direction="vertical" fill :size="10" style="width: 100%">
              <a-input v-model="form.apiKey" placeholder="API Key" allow-clear />
              <a-input-password v-model="form.apiSecret" :placeholder="preset.secretLabel" allow-clear />
              <a-input-password
                v-if="preset.needsPassphrase"
                v-model="form.passphrase"
                placeholder="Passphrase"
                allow-clear
              />
              <div class="field-hint">
                凭据<b>按原样保存在本机数据库里（明文，未加密）</b>——请只放没有提现权限、
                最好也没有交易权限的密钥。界面保存后只回显尾号，不提供查看入口。
                <template v-if="isEditing">两个字段都留空 = 清除已存的凭据。</template>
              </div>
            </a-space>
          </template>
        </div>

        <div>
          <div class="field-label">备注</div>
          <a-textarea v-model="form.notes" :auto-size="{ minRows: 2 }" placeholder="可留空" />
        </div>

      </a-space>

      <!-- 底部的按钮放在两步之外。第 1 步没有按钮（点卡片即前进，右上角的 × 可退出），
           第 2 步才有上一步与确定 -->
      <div v-if="step === 2" class="modal-actions">
        <a-space>
          <a-button v-if="!isEditing" @click="step = 1">上一步</a-button>
          <a-button type="primary" :loading="saving" @click="submit">确定</a-button>
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

.mono {
  font-family: monospace;
  font-size: 11px;
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

.field-label {
  font-size: 13px;
  margin-bottom: 6px;
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

/* ── 第 1 步：平台卡片 ──────────────────────────────── */

.platform-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
}

.platform-card {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 14px;
  text-align: left;
  background: var(--color-bg-2);
  border: 1px solid var(--color-border-1);
  border-radius: 6px;
  cursor: pointer;
  transition: border-color 0.15s, box-shadow 0.15s;
}

.platform-card:hover {
  border-color: rgb(var(--primary-6));
  box-shadow: 0 2px 10px rgb(0 0 0 / 8%);
}

.platform-head {
  display: flex;
  align-items: center;
  gap: 8px;
}

/* 品牌图标统一 20×20 的圆角方块：各家给的形状与底色都不一样（有的深底、有的透明），
   统一成同一尺寸与圆角，一排卡片看起来才是一条线上的东西 */
.platform-icon {
  flex: 0 0 auto;
  width: 20px;
  height: 20px;
  border-radius: 4px;
  object-fit: contain;
}

.platform-head a-tag,
.platform-head .arco-tag {
  margin-left: auto;
}

.platform-name {
  font-size: 14px;
  font-weight: 600;
  color: var(--color-text-1);
}

.platform-blurb {
  font-size: 12px;
  color: var(--color-text-3);
  line-height: 1.6;
}

.platform-fees {
  font-size: 11px;
  color: var(--color-text-4);
}

/* 第 2 步里的"已选平台"行：平台不该再是个下拉，它是已定的事实 */
.chosen-platform {
  display: flex;
  align-items: center;
  gap: 8px;
  padding-bottom: 10px;
  border-bottom: 1px solid var(--color-border-1);
}

.reselect {
  margin-left: auto;
  font-size: 12px;
}

.modal-actions {
  display: flex;
  justify-content: flex-end;
  padding-top: 4px;
}
</style>
