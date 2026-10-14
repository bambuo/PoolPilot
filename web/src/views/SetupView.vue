<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { Message } from '@arco-design/web-vue'

import { setupApi, type NotificationDelivery } from '@/api/client'
import AuthCard from '@/components/AuthCard.vue'
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
const { reason, skip } = useSetup()

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
    const delivery = await setupApi.configureNotifications({
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

    result.value = delivery

    if (delivery.delivered) {
      Message.success('通道已配好，去注册吧')
    } else {
      // 发失败也把参数存下了（重试是改这一条，不会多出一条通道）。
      // 这里什么都不标：系统仍然算未初始化，留在本页改到通为止
      Message.warning('参数已保存，但这封测试邮件没发出去——按下面的原因改一改')
    }
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '保存失败'
  } finally {
    saving.value = false
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
  <AuthCard
    title="初始化"
    hint="配一条邮箱通道，注册验证码才能寄到邮箱。"
  >
    <!-- 不要用裸 `<template>` 包这一层：作为组件的子节点，它会被渲染成一个真正的
         `<template>` 元素（display:none），字段全藏在里面——DOM 里数得到输入框，屏幕上却什么都没有 -->
    <div class="setup-body">
      <!-- 顶部只在"通道已就绪"时给一行提示（reason 为空即就绪）。
           "发不出去"这件事副标题已经说了，再立一条告警只是把表单往下推——
           而具体是哪种毛病（参数不全 / 口令不对）由保存后的结果条逐字给出。 -->
      <div v-if="reason === null" class="ready-line">
        通知通道已就绪，注册验证码会寄到发件人邮箱。
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

      <div v-if="errorMessage" class="auth-error">{{ errorMessage }}</div>

      <a-space direction="vertical" fill style="width: 100%">
        <a-button type="primary" long :loading="saving" @click="submit">
          {{ saving ? '正在发送测试邮件…' : '保存并发送测试邮件' }}
        </a-button>
        <a-button v-if="result?.delivered" long @click="goToRegister">去注册</a-button>
      </a-space>

      <div class="setup-foot">
        <span>只想先试试？</span>
        <a-link @click="skipSetup">跳过，验证码会写进服务端日志</a-link>
        <div>配好之后这一页不再出现（以后改通道去「通知 → 提供商」）。</div>
      </div>
    </div>
  </AuthCard>
</template>

<style scoped>
/* 这一页比其它三张表单长，把外边距**只在这一页**收紧一点：
   它是首访页，"保存并发送测试邮件"落在折叠线以下会让人以为没有提交入口。
   直接改 AuthCard 会连带动到登录/注册/MFA 三页（那三页本来有余量，没必要为此重拍截图）。
   AuthCard 的根元素是 .auth-page，父组件 scoped 样式能命中它 */
.auth-page {
  padding: 16px;
}

.auth-page :deep(.auth-card) {
  padding: 22px 24px 20px;
}

/* 副标题与第一个字段之间要留一口气：它们挨在一起会读成一句话 */
.setup-body {
  margin-top: 16px;
}

.setup-result {
  margin-bottom: 12px;
}

.auth-error {
  margin-bottom: 12px;
  font-size: 12px;
  line-height: 1.7;
  color: rgb(var(--danger-6));
}

/* 跳过是一条安静的出路：它不该显得像推荐动作。
   底下那句"配好之后不再出现"是这一页的出口说明——放在动作旁边比塞进副标题更合适
   （塞进去副标题会变成两行，而这条卡片的高度刚好卡在一屏里） */
.setup-foot {
  margin-top: 14px;
  font-size: 12px;
  line-height: 1.7;
  color: var(--color-text-3);
}

.ready-line {
  margin-top: 16px;
  font-size: 12px;
  line-height: 1.7;
  color: rgb(var(--success-6));
}
</style>
