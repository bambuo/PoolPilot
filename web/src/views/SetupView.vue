<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { Message } from '@arco-design/web-vue'
import { IconEmail } from '@arco-design/web-vue/es/icon'

import { setupApi, type NotificationDelivery } from '@/api/client'
import SmtpFields from '@/components/SmtpFields.vue'
import { useSetup } from '@/composables/useSetup'

/// 首次初始化：**这套系统还发不出注册验证码**，先把通知通道配好。
///
/// 为什么需要这一页：注册要验证码 → 验证码要一条通道 → 而通道的配置在登录之后。
/// 库里还没有任何账号时，这三件事绕成一个闭合的环。这一页就是那个开口：
/// 它在未初始化时可匿名访问，配完即失效（再进来会被守卫送回登录页）。
///
/// 界面上只做一件事——把通道配出来。测试邮件在保存时**同一次**发出，
/// 因为"口令对不对"只有真发一次才知道，而那正好是初始化唯一想确认的事。
const router = useRouter()
const { reason, skip, load } = useSetup()

/// 通道参数是否已配齐（reason 为空即配齐）。
///
/// 注意它说的是"**参数配齐了**"，不是"验证过了"——所以标签写"已配置"而不是"已就绪"：
/// 口令对不对只有发一封测试邮件才知道，那是另一个按钮的事。
const notifyConfigured = computed(() => reason.value === null)

/// 展开的区。默认把「通知配置」摊开——这一页第一眼就该看到要填什么，
/// 让人先去点一下标题才展开，是把"唯一要做的事"藏起来
const expandedSections = ref<string[]>(['notify'])

const form = ref({
  displayName: '通知邮箱',
  host: '',
  port: 587,
  useStartTls: true,
  userName: '',
  password: '',
  fromAddress: '',
  fromName: 'PoolPilot',
  isEnabled: true,
})

const saving = ref(false)
const testing = ref(false)
const errorMessage = ref('')
const result = ref<NotificationDelivery | null>(null)

async function submit(): Promise<void> {
  errorMessage.value = ''
  result.value = null

  if (form.value.host.trim() === '' || form.value.fromAddress.trim() === '') {
    errorMessage.value = 'SMTP 主机与发件人地址都要填——它们是"能不能发出去"的前提'
    return
  }

  saving.value = true

  try {
    await setupApi.configureNotifications({
      kind: 'email',
      displayName: form.value.displayName.trim() === '' ? '通知邮箱' : form.value.displayName.trim(),
      isEnabled: true,
      host: form.value.host.trim(),
      port: form.value.port,
      useStartTls: form.value.useStartTls,
      userName: form.value.userName.trim() === '' ? null : form.value.userName.trim(),
      password: form.value.password === '' ? null : form.value.password,
      fromAddress: form.value.fromAddress.trim(),
      fromName: form.value.fromName.trim() === '' ? 'PoolPilot' : form.value.fromName.trim(),
    })

    // 重新问一次状态：标签要翻成"已配置"、"去注册"与测试按钮也要跟着变。
    // 本地直接改一份状态会和服务端说两份话，而这个标签的意义就在于它说的是服务端那边的事实
    await load()
    Message.success('已保存。接着发一封测试邮件，确认这条路真的通')
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '保存失败'
  } finally {
    saving.value = false
  }
}

/// 发测试邮件。它发的是**已保存**的参数——注册时用的也是那一份，
/// 所以界面在这里提示"改完字段记得先保存"。
async function sendTest(): Promise<void> {
  errorMessage.value = ''
  result.value = null
  testing.value = true

  try {
    result.value = await setupApi.testNotification()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '发送测试邮件失败'
  } finally {
    testing.value = false
  }
}

async function goToRegister(): Promise<void> {
  await router.push({ name: 'register' })
}

async function skipSetup(): Promise<void> {
  skip()
  await router.push({ name: 'register' })
}
</script>

<template>
  <!-- 初始化是**一整页**，不是一张居中的小卡片：它是系统起来之后的第一屏，
       要装的也不止一件事（今天只有通知配置）。所以给它自己的顶栏与内容区，
       形制与登录之后那些页面一致——人不会觉得"我进错了什么弹窗"。
       匿名可见，所以顶栏只有品牌：导航项此刻一个都点不进去。 -->
  <div class="setup-page">
    <header class="setup-topbar">
      <span class="brand">
        <img class="brand-mark" src="/logo.svg" alt="" aria-hidden="true" />
        <span>PoolPilot</span>
      </span>
      <span class="topbar-note">首次初始化</span>
    </header>

    <main class="setup-main">
      <div class="setup-panel">
        <!-- 页头左右分置：左边说清这一页要做什么，右边是**做完了才出现**的下一步。
             它是个常规尺寸的按钮，不占整行——"去注册"是紧接着的下一步，
             不是这一页的主任务（主任务是上面那些初始化项） -->
        <div class="setup-header">
          <div>
            <h1 class="setup-title">初始化</h1>
            <p class="setup-lead">
              这套系统还没有能发出注册验证码的通道。下面每一项都是"用起来之前必须先解决的事"，
              做完一项它就会标成已就绪。
            </p>
          </div>

          <a-button v-if="notifyConfigured" type="primary" @click="goToRegister">去注册</a-button>
        </div>

        <!-- ── 初始化项按"可折叠区"划分，一项一个区 ──────────────────
             目前只有「通知配置」一项。后续的初始化项照这个形制往下加即可：
               · 区标题 + 右上角状态标签（`#header` / `#extra` 两个插槽，箭头与分隔线由 Arco 自己画）
               · 区首一行说明"这一区解决什么"
               · 该区**自己的**保存动作（各区的参数与后果都不一样，共用一个页面级按钮
                 就说不清"我按下的是哪一件事"）
             用折叠区而不是分步向导：初始化项彼此独立、也没有先后依赖，
             一屏能看到全部要做的事，比强迫人按顺序走更合适。 -->
        <a-collapse v-model:active-key="expandedSections" class="setup-sections">
          <a-collapse-item key="notify">
            <template #header>
              <span class="section-title">
                <IconEmail class="section-icon" />
                通知配置
              </span>
            </template>

            <template #extra>
              <!-- 标签说的是"参数配齐了没有"，不是"验证过了"：
                   口令对不对只有测试邮件能回答，那是下面第二个按钮的事 -->
              <a-tag size="small" :color="notifyConfigured ? 'green' : 'orange'">
                {{ notifyConfigured ? '已配置' : '未配置' }}
              </a-tag>
            </template>

            <div class="section-note">
              配一条邮箱通道，注册验证码才能寄到邮箱；没配之前，验证码只会写进服务端日志。
            </div>

            <!-- 字段组与「通知 → 提供商」共用同一份（见 SmtpFields 的说明） -->
            <SmtpFields
              v-model="form"
              password-placeholder="邮箱服务商给的授权码"
              :password-hint="null"
              :show-enabled="false"
              :show-display-name="false"
              :show-from-name="false"
            />

            <div v-if="result" class="setup-result">
              <a-alert :type="result.delivered ? 'success' : 'warning'">
                <template #title>{{ result.message }}</template>
                <div v-if="result.error && !result.message.includes(result.error)">{{ result.error }}</div>
              </a-alert>
            </div>

            <div v-if="errorMessage" class="setup-error">{{ errorMessage }}</div>

            <!-- 两个动作拆开：保存只是写库，测试会真的往外发信、而且测的是**已保存**那一份。
                 合成一个按钮时，人无法"只保存先不测"，也说不清刚按下的是哪一件事 -->
            <a-space>
              <a-button type="primary" :loading="saving" @click="submit">保存</a-button>
              <a-button :loading="testing" :disabled="!notifyConfigured" @click="sendTest">
                {{ testing ? '正在发送…' : '发送测试邮件' }}
              </a-button>
            </a-space>

            <div v-if="!notifyConfigured" class="button-hint">先保存，才能发测试邮件。</div>
            <div v-else class="button-hint">测试邮件用的是「已保存」的参数——改完字段记得先保存。</div>
          </a-collapse-item>
        </a-collapse>

        <div class="setup-foot">
          <span>只想先试试？</span>
          <a-link @click="skipSetup">跳过，验证码写进服务端日志</a-link>
          <span class="foot-sep">·</span>
          <span>配好之后这一页不再出现（以后改通道去「通知 → 提供商」）</span>
        </div>

      </div>
    </main>
  </div>
</template>

<style scoped>
/* 页面底板。与 App.vue 里登录之后的外壳同一套令牌：
   顶栏 bg-2 + 下描边，内容区 fill-1，内容块 bg-1。
   公开页没有 App 外壳，所以这一层要自己给全 */
.setup-page {
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  min-height: 100vh;
  background: var(--color-fill-1);
}

.setup-topbar {
  flex: 0 0 auto;
  display: flex;
  align-items: center;
  gap: 12px;
  height: 56px;
  padding: 0 20px;
  background: var(--color-bg-2);
  border-bottom: 1px solid var(--color-border-1);
}

.brand {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  color: var(--color-text-1);
  font-size: 17px;
  font-weight: 600;
  letter-spacing: 0.2px;
}

.brand-mark {
  display: block;
  width: 22px;
  height: 22px;
}

.topbar-note {
  font-size: 12px;
  color: var(--color-text-3);
}

.setup-main {
  flex: 1 1 auto;
  padding: 20px;
}

/* 内容块宽度给足：这一页要装"若干个可折叠区"，挤成 460 的窄条就又是一个弹窗。
   上限 860 是读长文本的舒适宽度，也让两个区并排时还有余地 */
.setup-panel {
  /* 又是同一个坑：Arco 没有全局 border-box，不声明的话 max-width 只约束内容盒，
     外框还要再加上左右各 24px（写 860 渲染成 910）。这一版已经踩到第三次了 */
  box-sizing: border-box;
  max-width: 860px;
  margin: 0 auto;
  padding: 20px 24px 24px;
  background: var(--color-bg-1);
  border: 1px solid var(--color-border-1);
  border-radius: 8px;
}

/* 页头：说明在左、下一步在右。按钮顶对齐标题那一行，而不是跟段落垂直居中——
   它属于"这一页的结论"，与标题同一层 */
.setup-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}

.setup-title {
  margin: 0;
  font-size: 20px;
  font-weight: 600;
  color: var(--color-text-1);
}

.setup-lead {
  margin: 6px 0 18px;
  font-size: 13px;
  line-height: 1.8;
  color: var(--color-text-3);
}

/* 折叠区用 Arco 的原生形制：外层一个描边圆角容器、项间一条分隔线、
   箭头在标题左侧。**不做覆盖**——那套样式是组件的一部分，
   改掉之后它会立刻不像这个组件库里的东西（这一版先踩过一遍） */
.setup-sections {
  margin-bottom: 16px;
}

/* 标题与图标同色，图标只作提示（与顶栏、通知页的分寸一致） */
.section-title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-weight: 600;
}

.section-icon {
  font-size: 15px;
  color: inherit;
}

.section-note {
  margin-bottom: 12px;
  font-size: 12px;
  line-height: 1.7;
  color: var(--color-text-3);
}

/* 按钮下面的一句说明：讲清"这两个按钮的区别"与"测试测的是哪一份" */
.button-hint {
  margin-top: 8px;
  font-size: 12px;
  line-height: 1.7;
  color: var(--color-text-3);
}

.setup-result {
  margin-bottom: 12px;
}

.setup-error {
  margin-bottom: 12px;
  font-size: 12px;
  line-height: 1.7;
  color: rgb(var(--danger-6));
}

/* 跳过是一条安静的出路：它不该显得像推荐动作 */
.setup-foot {
  font-size: 12px;
  line-height: 1.9;
  color: var(--color-text-3);
}

.foot-sep {
  margin: 0 6px;
}
</style>
