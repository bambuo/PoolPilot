<script setup lang="ts">
import { ref } from 'vue'
import { RouterLink, useRouter } from 'vue-router'

import AuthCard from '@/components/AuthCard.vue'
import { useAuthFlow } from '@/composables/useAuthFlow'
import { useSession } from '@/composables/useSession'

/// 登录第一步：邮箱 + 口令。
///
/// 它**不会直接把人送进工作台**：口令正确之后服务端只回一个挑战，
/// 下一步永远是 MFA（已绑定输码、未绑定扫码）。所以这里的"成功"是"跳到 /mfa"，
/// 而不是"进首页"——把这一步的终点写在类型里，界面就不会误以为登录已经完成。
const router = useRouter()
const { redirectAfterLogin } = useSession()
const { start } = useAuthFlow()

const email = ref('')
const password = ref('')
const errorMessage = ref('')
const submitting = ref(false)

async function submit(): Promise<void> {
  errorMessage.value = ''

  if (email.value.trim() === '' || password.value === '') {
    errorMessage.value = '邮箱与密码都要填'
    return
  }

  submitting.value = true

  try {
    await start(email.value.trim(), password.value)

    // 想去哪儿由进来时的守卫记着，MFA 那一步用完即回
    const target = redirectAfterLogin.value
    redirectAfterLogin.value = null

    await router.push({ name: 'mfa', query: target === null ? {} : { redirect: target } })
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '登录失败'
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <AuthCard title="登录" hint="这套工作台要求双重验证：口令之后还要一枚验证器里的六位码。">
    <a-form :model="{ email, password }" layout="vertical" class="auth-form" @submit.prevent="submit">
      <a-form-item label="邮箱" required>
        <a-input v-model="email" placeholder="注册时用的邮箱" autocomplete="username" />
      </a-form-item>

      <a-form-item label="密码" required>
        <a-input-password
          v-model="password"
          placeholder="登录密码"
          autocomplete="current-password"
          @keyup.enter="submit"
        />
      </a-form-item>

      <div v-if="errorMessage" class="auth-error">{{ errorMessage }}</div>

      <a-button type="primary" long :loading="submitting" @click="submit">登录</a-button>
    </a-form>

    <template #footer>
      <div class="auth-foot">
        还没有账号？<RouterLink :to="{ name: 'register' }">注册</RouterLink>
      </div>
    </template>
  </AuthCard>
</template>

<style scoped>
.auth-form {
  margin-top: 18px;
}

/* 错误写在按钮紧上方：紧挨着"再试一次"那个动作，比放在页面顶部更容易被看到 */
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
