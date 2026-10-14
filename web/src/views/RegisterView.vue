<script setup lang="ts">
import { computed, onUnmounted, ref } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { Message } from '@arco-design/web-vue'

import { authApi } from '@/api/client'
import AuthCard from '@/components/AuthCard.vue'

/// 注册：邮箱 → 验证码 → 两次密码。
///
/// 验证码这一步**必须自己按一次"发送"**（而不是提交时顺带发）：
/// 使用者要先去收信、再把码填回来，这个顺序只能靠一个独立的动作来分开。
/// 发送成功后进入 60 秒倒计时——那不是装饰，服务端确实会拒绝这段时间内的重发，
/// 界面先说出来，免得人反复点、反复被拒。
const router = useRouter()

const email = ref('')
const code = ref('')
const password = ref('')
const confirmPassword = ref('')

const sending = ref(false)
const submitting = ref(false)
const errorMessage = ref('')
const cooldown = ref(0)

let timer: number | null = null

const canSendCode = computed(() => !sending.value && cooldown.value === 0 && email.value.trim() !== '')

function startCooldown(seconds: number): void {
  cooldown.value = seconds

  if (timer !== null) {
    window.clearInterval(timer)
  }

  timer = window.setInterval(() => {
    cooldown.value -= 1

    if (cooldown.value <= 0 && timer !== null) {
      window.clearInterval(timer)
      timer = null
    }
  }, 1000)
}

onUnmounted(() => {
  if (timer !== null) {
    window.clearInterval(timer)
  }
})

async function sendCode(): Promise<void> {
  errorMessage.value = ''

  if (email.value.trim() === '') {
    errorMessage.value = '先填邮箱——验证码要发到那里'
    return
  }

  sending.value = true

  try {
    const delivery = await authApi.sendRegistrationCode(email.value.trim())

    // 发出去的三种情形都要说清：真发了 / 只进了服务端日志 / 失败了。
    // 只报"已发送"会把后两种也盖成成功，而人接下来只会干等一封不会来的邮件
    if (delivery.delivered) {
      Message.success(delivery.message)
    } else if (delivery.logFallback) {
      // 降级提示要停久一点：它不是"做完了"，而是"这样收不到邮件，去配通道"
      Message.warning({ content: delivery.message, duration: 8000 })
    } else {
      errorMessage.value = delivery.message
    }

    startCooldown(60)
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '发送验证码失败'
  } finally {
    sending.value = false
  }
}

async function submit(): Promise<void> {
  errorMessage.value = ''

  if (password.value !== confirmPassword.value) {
    errorMessage.value = '两次输入的密码不一致'
    return
  }

  submitting.value = true

  try {
    await authApi.register({
      email: email.value.trim(),
      code: code.value.trim(),
      password: password.value,
      confirmPassword: confirmPassword.value,
    })

    // 注册不签发会话（MFA 是强制的，注册完还要走一次登录把令牌绑上），
    // 所以这里把人送回登录页，并说清"下一步是绑令牌"
    Message.success('注册成功，去登录并绑定验证器')
    await router.push({ name: 'login' })
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '注册失败'
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <AuthCard
    title="注册"
    hint="验证码会发到你填的邮箱。注册完成后第一次登录，系统会让你绑定一个验证器（TOTP），之后每次登录都要输它给出的六位码。"
  >
    <a-form
      :model="{ email, code, password, confirmPassword }"
      layout="vertical"
      class="auth-form"
      @submit.prevent="submit"
    >
      <a-form-item label="邮箱" required>
        <a-input v-model="email" placeholder="用来登录，也用来收验证码" autocomplete="username" />
      </a-form-item>

      <a-form-item label="验证码" required>
        <div class="code-row">
          <a-input v-model="code" placeholder="六位数字" maxlength="6" />
          <a-button :loading="sending" :disabled="!canSendCode" @click="sendCode">
            {{ cooldown > 0 ? `${cooldown}s 后重发` : '发送验证码' }}
          </a-button>
        </div>
      </a-form-item>

      <a-form-item label="密码" required>
        <a-input-password v-model="password" placeholder="至少 8 位" autocomplete="new-password" />
      </a-form-item>

      <a-form-item label="确认密码" required>
        <a-input-password
          v-model="confirmPassword"
          placeholder="再输一遍"
          autocomplete="new-password"
          @keyup.enter="submit"
        />
      </a-form-item>

      <div v-if="errorMessage" class="auth-error">{{ errorMessage }}</div>

      <a-button type="primary" long :loading="submitting" @click="submit">注册</a-button>
    </a-form>

    <template #footer>
      <div class="auth-foot">
        已经有账号了？<RouterLink :to="{ name: 'login' }">去登录</RouterLink>
      </div>
    </template>
  </AuthCard>
</template>

<style scoped>
.auth-form {
  margin-top: 18px;
}

/* 验证码与"发送"同一行：它们是同一个动作的两半 */
.code-row {
  display: flex;

  /* `flex: 1` 不能省：Arco 的表单项内容区本身是个 flex 容器，这个 div 作为它的
     flex 子项会**按内容收缩**（实测 290px，而上面几个整宽字段是 380px）——
     于是这一行与其它字段的右边缘对不齐 */
  flex: 1;
  gap: 8px;
}

.code-row .arco-input-wrapper {
  flex: 1;
  min-width: 0;
}

/* 按钮宽度固定：倒计时文案比"发送验证码"长，宽度随文案变的话整行每秒抖一下 */
.code-row .arco-btn {
  flex: 0 0 auto;
  min-width: 124px;
}

.auth-error {
  margin-bottom: 12px;
  font-size: 12px;
  line-height: 1.7;
  color: rgb(var(--danger-6));
}

.auth-foot {
  margin-top: 16px;
  font-size: 13px;
  color: var(--color-text-3);
}
</style>
