<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { Message } from '@arco-design/web-vue'

import {
  notificationApi,
  type NotificationDelivery,
  type NotificationLogEntry,
  type NotificationProvider,
  type NotificationProviderPayload,
  type NotificationTemplate,
} from '@/api/client'

/// 通知：通道（提供商）/ 样板 / 投递记录。
///
/// 三块用标签页而不是三个路由：它们描述同一件事的三个面——**谁能发、发什么、发了什么**，
/// 使用者通常在配置完通道之后顺手就要看记录，来回跳页只会打断这条线。
///
/// 界面上刻意把"能不能发出去"这件事摆在明处：通道是否可用、样板有没有指向通道、
/// 上一次投递成没成。配错了却没有任何提示，是这类功能最费时间的一种坏法。
const tab = ref<'providers' | 'templates' | 'logs'>('providers')

const providers = ref<NotificationProvider[]>([])
const templates = ref<NotificationTemplate[]>([])
const logs = ref<NotificationLogEntry[]>([])
const loading = ref(false)
const errorMessage = ref('')

// ── 通道 ────────────────────────────────────────────────

const providerOpen = ref(false)
const editingProvider = ref<NotificationProvider | null>(null)
const savingProvider = ref(false)
const testingProvider = ref(false)
const testResult = ref<NotificationDelivery | null>(null)
const clearingPassword = ref(false)

const providerForm = ref<{
  displayName: string
  isEnabled: boolean
  host: string
  port: number
  useStartTls: boolean
  userName: string
  password: string
  fromAddress: string
  fromName: string
}>(emptyProviderForm())

function emptyProviderForm() {
  return {
    displayName: '',
    isEnabled: true,
    host: '',
    port: 587,
    useStartTls: true,
    userName: '',
    password: '',
    fromAddress: '',
    fromName: 'PoolPilot',
  }
}

function openProvider(provider: NotificationProvider | null): void {
  editingProvider.value = provider
  testResult.value = null
  clearingPassword.value = false

  providerForm.value =
    provider === null
      ? emptyProviderForm()
      : {
          displayName: provider.displayName,
          isEnabled: provider.isEnabled,
          host: provider.host ?? '',
          port: provider.port,
          useStartTls: provider.useStartTls,
          userName: provider.userName ?? '',
          // 留空 = 保持原值（三态里的"没动它"）。界面**不回显**已存的口令，
          // 只给尾号提示——它和交易所凭据是同一档东西
          password: '',
          fromAddress: provider.fromAddress ?? '',
          fromName: provider.fromName,
        }

  providerOpen.value = true
}

async function saveProvider(): Promise<void> {
  savingProvider.value = true
  errorMessage.value = ''

  const form = providerForm.value

  const payload: NotificationProviderPayload = {
    kind: 'email',
    displayName: form.displayName.trim(),
    isEnabled: form.isEnabled,
    host: form.host.trim() === '' ? null : form.host.trim(),
    port: form.port,
    useStartTls: form.useStartTls,
    userName: form.userName.trim() === '' ? null : form.userName.trim(),
    // 三态：没填 = 不传（保持原值）；填了就换新；点过「清除口令」= 传空串显式清除
    ...(clearingPassword.value
      ? { password: '' }
      : form.password === ''
        ? {}
        : { password: form.password }),
    fromAddress: form.fromAddress.trim() === '' ? null : form.fromAddress.trim(),
    fromName: form.fromName.trim() === '' ? 'PoolPilot' : form.fromName.trim(),
  }

  try {
    if (editingProvider.value === null) {
      await notificationApi.createProvider(payload)
      Message.success('通道已创建')
    } else {
      await notificationApi.updateProvider(editingProvider.value.id, payload)
      Message.success('通道已保存')
    }

    providerOpen.value = false
    await loadAll()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '保存失败'
  } finally {
    savingProvider.value = false
  }
}

/// 清空口令是一个**保存时才生效**的意图，不即时打一次接口：
/// 弹窗里还有别的字段可能一起改，分成两次写会让"我到底保存了什么"变得说不清。
/// 它对应凭据三态里的"显式清除"（传空串）。
function markPasswordForClearing(): void {
  providerForm.value.password = ''
  clearingPassword.value = true
}

async function testProvider(provider: NotificationProvider): Promise<void> {
  testingProvider.value = true

  try {
    testResult.value = await notificationApi.testProvider(provider.id)
    await loadAll()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '测试发送失败'
  } finally {
    testingProvider.value = false
  }
}

async function deleteProvider(provider: NotificationProvider): Promise<void> {
  errorMessage.value = ''

  try {
    await notificationApi.deleteProvider(provider.id)
    Message.success('通道已删除')
    await loadAll()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '删除失败'
  }
}

// ── 样板 ────────────────────────────────────────────────

/// 草稿里的 providerId 用 0 表示"不指定"：这一版 Arco 的 a-select 不接受 null 作值，
/// 而 0 不可能是通道 Id（自增从 1 起）。与后端的 null 只在这两处边界上互转。
const templateDraft = ref<Record<string, { subject: string; body: string; providerId: number }>>({})
const savingTemplate = ref<string | null>(null)

function draftOf(template: NotificationTemplate) {
  templateDraft.value[template.purpose] ??= {
    subject: template.subject,
    body: template.body,
    providerId: template.providerId ?? 0,
  }

  return templateDraft.value[template.purpose]!
}

function restoreDefault(template: NotificationTemplate): void {
  templateDraft.value[template.purpose] = {
    subject: template.defaultSubject,
    body: template.defaultBody,
    providerId: draftOf(template).providerId,
  }
}

async function saveTemplate(template: NotificationTemplate): Promise<void> {
  const draft = draftOf(template)
  savingTemplate.value = template.purpose
  errorMessage.value = ''

  try {
    const saved = await notificationApi.updateTemplate(template.purpose, {
      subject: draft.subject,
      body: draft.body,
      providerId: draft.providerId === 0 ? null : draft.providerId,
    })

    templateDraft.value[template.purpose] = {
      subject: saved.subject,
      body: saved.body,
      providerId: saved.providerId ?? 0,
    }

    Message.success('样板已保存')
    await loadAll()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '保存失败'
  } finally {
    savingTemplate.value = null
  }
}

// ── 记录 ────────────────────────────────────────────────

const logsLoaded = ref(false)

/// 时间按 `YYYY-MM-DD HH:mm:ss` 显示，不用 toLocaleString：
/// 后者的长短与分隔方式随环境变（实测在 170px 的列里会被折成两行，
/// 读起来像"11:3 4:48"）。固定宽度也便于一眼比较先后。
function formatTime(value: string): string {
  const at = new Date(value)

  if (Number.isNaN(at.getTime())) {
    return value
  }

  const pad = (n: number) => String(n).padStart(2, "0")

  return (
    `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())} ` +
    `${pad(at.getHours())}:${pad(at.getMinutes())}:${pad(at.getSeconds())}`
  )
}

function resultOf(entry: NotificationLogEntry): { text: string; color: string } {
  if (entry.delivered) {
    return { text: '已发出', color: 'green' }
  }

  return entry.logFallback
    ? { text: '仅写日志', color: 'orange' }
    : { text: '失败', color: 'red' }
}

async function loadAll(): Promise<void> {
  loading.value = true
  errorMessage.value = ''

  try {
    providers.value = await notificationApi.providers()
    templates.value = await notificationApi.templates()
    logs.value = await notificationApi.logs()
    logsLoaded.value = true

    // 库里的值覆盖草稿：刚保存完要以服务端为准，而不是留着界面上那份
    for (const template of templates.value) {
      templateDraft.value[template.purpose] = {
        subject: template.subject,
        body: template.body,
        providerId: template.providerId ?? 0,
      }
    }
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '读取通知配置失败'
  } finally {
    loading.value = false
  }
}

onMounted(loadAll)
</script>

<template>
  <a-space direction="vertical" fill style="width: 100%">
    <a-card :bordered="false">
      <template #title>
        <a-space>
          <span>通知</span>
          <a-tag v-if="!loading" size="small">{{ providers.length }} 条通道</a-tag>
        </a-space>
      </template>

      <template #extra>
        <a-button size="small" :loading="loading" @click="loadAll">刷新</a-button>
      </template>

      <!-- 表前说明，与池列表 / 带单员页同一种形制（卡片内、浅色小字） -->
      <div class="page-note">
        <div>
          系统往外汇出的消息都在这里：<b>提供商</b>是通道（现在只有邮箱 / SMTP），
          <b>样板</b>是每类通知要说什么、由哪条通道发，<b>记录</b>是每一次投递的实际内容与结果。
        </div>
        <div>
          通道没配好时不会假装成功——注册验证码会降级写进服务端日志，并在界面上说明，
          投递记录里也标成"仅写日志"。
        </div>
      </div>

      <a-alert v-if="errorMessage" type="error" :title="errorMessage" style="margin-bottom: 12px" />

      <a-tabs v-model:active-key="tab">
        <!-- ── 提供商 ────────────────────────────────── -->
        <a-tab-pane key="providers" title="提供商">
          <div class="provider-grid">
            <button
              v-for="provider in providers"
              :key="provider.id"
              type="button"
              class="provider-card"
              @click="openProvider(provider)"
            >
              <div class="provider-head">
                <span class="provider-name">{{ provider.displayName }}</span>
                <span class="kind-capsule">{{ provider.kind }}</span>
                <a-tag v-if="!provider.isEnabled" size="small" color="gray">已停用</a-tag>
              </div>

              <div class="provider-row">
                <span class="label">服务器</span>
                <span class="value">
                  {{ provider.host ?? '未填' }}<template v-if="provider.host">:{{ provider.port }}</template>
                </span>
              </div>

              <div class="provider-row">
                <span class="label">发件人</span>
                <span class="value">{{ provider.fromAddress ?? '未填' }}</span>
              </div>

              <div class="provider-row">
                <span class="label">口令</span>
                <span class="value">
                  {{ provider.passwordHint === null ? '未填' : `尾号 ${provider.passwordHint}` }}
                </span>
              </div>

              <div class="provider-foot">
                <span :class="provider.isUsable ? 'usable' : 'not-usable'">
                  {{ provider.isUsable ? '可用' : '还不能发（缺参数或已停用）' }}
                </span>

                <a-space @click.stop>
                  <a-link @click="testProvider(provider)">测试发送</a-link>
                  <a-link status="danger" @click="deleteProvider(provider)">删除</a-link>
                </a-space>
              </div>
            </button>

            <button type="button" class="add-card" @click="openProvider(null)">
              <span class="add-plus">+</span>
              <span>新建通道</span>
            </button>
          </div>

          <a-alert v-if="testResult" :type="testResult.delivered ? 'success' : 'warning'" class="test-result">
            <template #title>{{ testResult.message }}</template>
            <!-- 失败时 message 里已经带了 SMTP 的原话，不再重复一遍 -->
            <div v-if="testResult.error && !testResult.message.includes(testResult.error)">
              {{ testResult.error }}
            </div>
          </a-alert>
        </a-tab-pane>

        <!-- ── 样板 ──────────────────────────────────── -->
        <a-tab-pane key="templates" title="样板">
          <div v-for="template in templates" :key="template.purpose" class="template-card">
            <div class="template-head">
              <span class="template-name">{{ template.displayName }}</span>
              <a-tag size="small" :color="template.isCustomized ? 'arcoblue' : 'gray'">
                {{ template.isCustomized ? '已自定义' : '默认样板' }}
              </a-tag>
              <a-link class="template-reset" @click="restoreDefault(template)">恢复默认</a-link>
            </div>

            <a-form :model="draftOf(template)" layout="vertical">
              <a-form-item label="由哪条通道发">
                <!-- 没选 = 0。这一版 Arco 的 a-select 不接受 null 作值（与标的映射页同一条结论），
                     而 0 不可能是通道 Id（自增从 1 起），所以它天然是"没有"的哨兵 -->
                <a-select v-model="draftOf(template).providerId" :placeholder="'还没选（选了才能真的发出去）'">
                  <a-option :value="0">不指定</a-option>
                  <a-option v-for="provider in providers" :key="provider.id" :value="provider.id">
                    {{ provider.displayName }}{{ provider.isUsable ? '' : '（不可用）' }}
                  </a-option>
                </a-select>
              </a-form-item>

              <a-form-item label="主题">
                <a-input v-model="draftOf(template).subject" />
              </a-form-item>

              <a-form-item label="正文">
                <a-textarea v-model="draftOf(template).body" :auto-size="{ minRows: 6 }" />
                <template #extra>
                  <span class="field-hint">
                    可用占位符：<code>{code}</code> 验证码、<code>{email}</code> 收件邮箱、
                    <code>{minutes}</code> 有效分钟数。
                    <template v-if="template.requiredPlaceholders.length > 0">
                      其中 <b>{{ template.requiredPlaceholders.join('、') }}</b> 必须保留——
                      去掉它收信人就看不到那串码，而系统这边照样报"发送成功"。
                    </template>
                  </span>
                </template>
              </a-form-item>

              <a-space>
                <a-button
                  type="primary"
                  :loading="savingTemplate === template.purpose"
                  @click="saveTemplate(template)"
                >
                  保存样板
                </a-button>
              </a-space>
            </a-form>
          </div>
        </a-tab-pane>

        <!-- ── 记录 ──────────────────────────────────── -->
        <a-tab-pane key="logs" title="记录">
          <div v-if="logsLoaded && logs.length === 0" class="empty-line">还没有发出过任何通知。</div>

          <a-table
            v-else
            :data="logs"
            :loading="loading"
            row-key="id"
            :pagination="{ pageSize: 20, showTotal: true, size: 'small' }"
            :bordered="false"
            size="small"
            :expandable="{ width: 40 }"
            :scroll="{ x: 1120 }"
          >
            <!-- 不写 scroll.x 的话 Arco 会把各列按容器宽度**压缩**：时间列被挤到
                 放不下 `2026-10-14 11:52:54` 就折成两行（池列表那边同理，也显式给了宽度） -->
            <template #columns>
              <a-table-column title="时间" :width="210">
                <template #cell="{ record }">
                  <span class="mono">{{ formatTime(record.sentAt) }}</span>
                </template>
              </a-table-column>

              <a-table-column title="类型" :width="120">
                <template #cell="{ record }">{{ record.purposeName }}</template>
              </a-table-column>

              <a-table-column title="通道" :width="200">
                <template #cell="{ record }">
                  <span class="mono">{{ record.providerName }}</span>
                </template>
              </a-table-column>

              <a-table-column title="收件人" :width="220">
                <template #cell="{ record }">
                  <span class="mono">{{ record.recipient }}</span>
                </template>
              </a-table-column>

              <a-table-column title="结果" :width="140">
                <template #cell="{ record }">
                  <a-tag size="small" :color="resultOf(record).color">{{ resultOf(record).text }}</a-tag>
                </template>
              </a-table-column>

              <a-table-column title="失败原因" ellipsis tooltip>
                <template #cell="{ record }">
                  <span class="error-text">{{ record.error ?? '' }}</span>
                </template>
              </a-table-column>
            </template>

            <!-- 展开行放实际内容：记录表要能回答"当时到底发了什么"，
                 而正文是整段文本，塞进列表会把表撑散 -->
            <template #expand-row="{ record }">
              <div class="log-detail">
                <div class="log-subject">主题：{{ record.subject }}</div>
                <pre class="log-body">{{ record.body }}</pre>
              </div>
            </template>
          </a-table>
        </a-tab-pane>
      </a-tabs>
    </a-card>

    <!-- 新建 / 编辑通道 -->
    <a-modal
      v-model:visible="providerOpen"
      :title="editingProvider === null ? '新建通知通道' : '编辑通知通道'"
      :width="520"
      :footer="false"
      :mask-closable="false"
      unmount-on-close
    >
      <div class="ex-head">
        <span class="kind-capsule">email</span>
        <div>
          <div class="ex-head-name">邮箱通知（SMTP）</div>
          <div class="ex-head-sub">用 587 端口的 STARTTLS；465 那种隐式 SSL 连不上</div>
        </div>
      </div>

      <a-form :model="providerForm" layout="vertical">
        <a-form-item label="显示名" required>
          <a-input v-model="providerForm.displayName" placeholder="如「主邮箱」「通知专用」" />
        </a-form-item>

        <a-form-item label="SMTP 主机" required>
          <a-input v-model="providerForm.host" placeholder="如 smtp.example.com" />
        </a-form-item>

        <div class="two-col">
          <a-form-item label="端口">
            <a-input-number v-model="providerForm.port" :min="1" :max="65535" />
          </a-form-item>

          <a-form-item label="STARTTLS">
            <a-switch v-model="providerForm.useStartTls" />
          </a-form-item>
        </div>

        <a-form-item label="登录名">
          <a-input v-model="providerForm.userName" placeholder="通常是完整邮箱地址" />
        </a-form-item>

        <a-form-item label="口令（授权码）">
          <a-input-password
            v-model="providerForm.password"
            :placeholder="editingProvider === null ? '邮箱服务商给的授权码' : '留空保持原值'"
            allow-clear
          />
          <template #extra>
            <span class="field-hint">
              按原样保存在本机数据库里（明文，未加密），界面只回显尾号。
              <template v-if="editingProvider?.passwordHint">
                当前尾号 {{ editingProvider.passwordHint }}。
              </template>
            </span>
          </template>
        </a-form-item>

        <a-form-item label="发件人地址" required>
          <a-input v-model="providerForm.fromAddress" placeholder="验证码邮件的发出地址" />
        </a-form-item>

        <a-form-item label="发件人显示名">
          <a-input v-model="providerForm.fromName" />
        </a-form-item>

        <a-form-item label="启用">
          <a-switch v-model="providerForm.isEnabled" />
          <template #extra>
            <span class="field-hint">停用的通道不会被样板选到（选了也发不出去）。</span>
          </template>
        </a-form-item>

        <div v-if="testResult" class="test-result-inline">
          <a-alert :type="testResult.delivered ? 'success' : 'warning'">
            <template #title>{{ testResult.message }}</template>
            <div v-if="testResult.error && !testResult.message.includes(testResult.error)">
              {{ testResult.error }}
            </div>
          </a-alert>
        </div>
      </a-form>

      <div class="ex-foot">
        <a-link v-if="editingProvider !== null" @click="markPasswordForClearing">
          {{ clearingPassword ? '保存后清空口令' : '清除口令' }}
        </a-link>
        <a-space>
          <a-button v-if="editingProvider !== null" :loading="testingProvider" @click="testProvider(editingProvider)">
            发送测试邮件
          </a-button>
          <a-button @click="providerOpen = false">取消</a-button>
          <a-button type="primary" :loading="savingProvider" @click="saveProvider">保存</a-button>
        </a-space>
      </div>
    </a-modal>
  </a-space>
</template>

<style scoped>
.page-note {
  margin-bottom: 12px;
  font-size: 12px;
  line-height: 1.7;
  color: var(--color-text-3);
}

/* ── 通道卡片（与交易所页同一套形制：卡片是入口、虚线卡是新建）── */
.provider-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
  gap: 14px;
  padding-top: 4px;
}

.provider-card {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 14px 16px 12px;
  font: inherit;
  text-align: left;
  background: var(--color-bg-2);
  border: 1px solid var(--color-border-1);
  border-radius: 6px;
  cursor: pointer;
  transition: border-color 0.15s, box-shadow 0.15s;
}

.provider-card:hover {
  border-color: rgb(var(--primary-6));
  box-shadow: 0 2px 10px rgb(0 0 0 / 8%);
}

.provider-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
}

.provider-name {
  font-size: 15px;
  font-weight: 600;
  color: var(--color-text-1);
}

.kind-capsule {
  padding: 1px 8px;
  font-size: 10px;
  font-weight: 600;
  color: rgb(var(--arcoblue-6));
  background: rgb(var(--arcoblue-1));
  border-radius: 100px;
}

.provider-row {
  display: flex;
  gap: 8px;
  font-size: 12px;
}

.provider-row .label {
  flex: 0 0 48px;
  color: var(--color-text-3);
}

.provider-row .value {
  overflow: hidden;
  color: var(--color-text-1);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.provider-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-top: 6px;
  padding-top: 8px;
  font-size: 12px;
  border-top: 1px solid var(--color-border-1);
}

.usable {
  color: rgb(var(--success-6));
}

.not-usable {
  color: rgb(var(--warning-6));
}

.add-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  min-height: 150px;
  gap: 6px;
  font: inherit;
  font-size: 13px;
  color: var(--color-text-3);
  background: transparent;
  border: 1px dashed var(--color-border-2);
  border-radius: 6px;
  cursor: pointer;
  transition: color 0.15s, border-color 0.15s;
}

.add-card:hover {
  color: rgb(var(--primary-6));
  border-color: rgb(var(--primary-6));
}

.add-plus {
  font-size: 20px;
  line-height: 1;
}

.test-result {
  margin-top: 14px;
}

.test-result-inline {
  margin-bottom: 12px;
}

/* ── 样板 ──────────────────────────────────────────── */
.template-card {
  padding: 16px;
  margin-bottom: 14px;
  background: var(--color-bg-2);
  border: 1px solid var(--color-border-1);
  border-radius: 6px;
}

.template-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 12px;
}

.template-name {
  font-size: 15px;
  font-weight: 600;
  color: var(--color-text-1);
}

.template-reset {
  margin-left: auto;
  font-size: 12px;
}

.field-hint {
  font-size: 12px;
  line-height: 1.7;
  color: var(--color-text-3);
}

.field-hint code {
  padding: 0 4px;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  background: var(--color-fill-2);
  border-radius: 3px;
}

.empty-line {
  padding: 20px 0;
  font-size: 13px;
  color: var(--color-text-3);
  text-align: center;
}

/* ── 记录 ──────────────────────────────────────────── */
/* 时间、邮箱、通道名都是**标识符**：被折断（Arco 的单元格默认 word-break: break-all）
   会让 `11:52:54` 读成 `11:52:5 / 4`。宁可截断也不折行——截断还能靠展开行看全 */
.mono {
  display: block;
  overflow: hidden;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.error-text {
  color: rgb(var(--danger-6));
}

.log-detail {
  padding: 10px 14px;
  background: var(--color-fill-1);
  border-radius: 6px;
}

.log-subject {
  margin-bottom: 6px;
  font-size: 12px;
  color: var(--color-text-2);
}

.log-body {
  margin: 0;
  font-family: inherit;
  font-size: 12px;
  line-height: 1.8;
  color: var(--color-text-1);
  white-space: pre-wrap;
}

/* ── 弹窗 ──────────────────────────────────────────── */
.ex-head {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 12px;
  margin-bottom: 16px;
  background: var(--color-fill-1);
  border: 1px solid var(--color-border-2);
  border-radius: 10px;
}

.ex-head-name {
  font-size: 14px;
  font-weight: 600;
  color: var(--color-text-1);
}

.ex-head-sub {
  font-size: 12px;
  color: var(--color-text-3);
}

.two-col {
  display: flex;
  gap: 16px;
}

.two-col .arco-form-item {
  flex: 1;
}

.ex-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-top: 4px;
}
</style>
