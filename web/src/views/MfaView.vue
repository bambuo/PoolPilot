<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import qrcode from 'qrcode-generator'

import { Message } from '@arco-design/web-vue'
import { IconCopy } from '@arco-design/web-vue/es/icon'

import AuthCard from '@/components/AuthCard.vue'
import { useAuthFlow } from '@/composables/useAuthFlow'
import { useSession } from '@/composables/useSession'
import { HOME_PATH } from '@/router'

/// 登录第二步：MFA。
///
/// 两种状态共用一个页面，因为**它们对使用者的差别只有"要不要先扫个码"**：
/// · 已绑定：直接输验证器里的六位码。
/// · 未绑定：先把密钥扫进验证器（或手动输入密钥），再用它给出的码完成绑定 + 登录。
/// 分成两个路由会让"我该进哪一页"变成一个要额外判断、还容易判断错的问题。
const route = useRoute()
const router = useRouter()
const { redirectAfterLogin } = useSession()
const { challenge, email, complete, clear } = useAuthFlow()

const code = ref('')
const errorMessage = ref('')
const submitting = ref(false)

const isBinding = computed(() => challenge.value?.status === 'mfaBindingRequired')

/// 二维码**在前端画**：服务端只给 otpauth:// 那串 URI，
/// 于是密钥不必经过第三方二维码服务，也不必在服务端引一个画图库。
///
/// 用 qrcode-generator（约 10KB、零传递依赖）而不是自己写：
/// QR 要算 Reed-Solomon 纠错，那不是"能手写"的量级。它生成 SVG，
/// 与仓库里手绘折线图的取向一致——矢量、清晰、可随主题取色。
const qrSvg = computed(() => {
  const uri = challenge.value?.otpauthUri

  if (uri === undefined || uri === null) {
    return null
  }

  // 0 = 自动选最小的版本；纠错等级 M 足以覆盖手机屏幕的反光与划痕
  const qr = qrcode(0, 'M')
  qr.addData(uri)
  qr.make()

  return qr.createSvgTag({ cellSize: 4, margin: 0, scalable: true })
})

async function submit(): Promise<void> {
  errorMessage.value = ''

  const trimmed = code.value.trim()

  if (!/^\d{6}$/.test(trimmed)) {
    errorMessage.value = '验证器给出的是六位数字'
    return
  }

  submitting.value = true

  try {
    await complete(trimmed)

    // 回跳目标优先取查询参数（守卫带来的），其次取进来时记下的那一个。
    // 兜底用**路径**而不是路由 name：首页那条路由由菜单树动态注册，
    // 此刻（刚登录、路由还没注册）按 name 跳转只会报"没有这条路由"。
    const target =
      typeof route.query.redirect === 'string' && route.query.redirect.startsWith('/')
        ? route.query.redirect
        : redirectAfterLogin.value

    redirectAfterLogin.value = null

    await router.push(target !== null && !target.startsWith('/mfa') ? target : HOME_PATH)
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '验证失败'
    code.value = ''
  } finally {
    submitting.value = false
  }
}

/// 复制密钥。
///
/// 手动输入的路径上，密钥要从这块屏幕抄到另一块屏幕——长按拖选容易漏字符，
/// 而漏一个字符的表现是"验证器能扫进来、但码永远不对"，那种错要查很久。
async function copySecret(): Promise<void> {
  const secret = challenge.value?.secret

  if (secret === undefined || secret === null) {
    return
  }

  try {
    await navigator.clipboard.writeText(secret)
    Message.success('密钥已复制')
  } catch {
    // 剪贴板可能被浏览器拒绝（无用户手势、非安全上下文）。密钥就在屏幕上，
    // 退回"手动选中"即可——把一次降级做成一条报错，反而更让人以为出了问题
    Message.warning('复制失败，请手动选中密钥')
  }
}

async function backToLogin(): Promise<void> {
  clear()
  await router.push({ name: 'login' })
}
</script>

<template>
  <AuthCard
    :title="isBinding ? '绑定验证器' : '输入验证码'"
    :hint="
      isBinding
        ? `用验证器 App（Google Authenticator、1Password 等）扫描下面的二维码，然后输入它给出的六位码。绑定完成后这个账号就只能靠这台验证器登录了。`
        : `打开验证器 App，把 ${email} 这一项当前显示的六位码填进来。`
    "
  >
    <!-- 挑战只存在内存里：刷新或直接进来时它已经没了，那只能回登录页从头走 -->
    <div v-if="challenge === null" class="auth-empty">
      <p>这次登录流程已经失效（可能是刷新过页面）。</p>
      <a-button type="primary" long @click="backToLogin">回到登录</a-button>
    </div>

    <!-- 命名插槽必须是组件的直接子节点：套在 v-else 的 template 里编译器会直接报错 -->
    <div v-else>
      <div v-if="isBinding && qrSvg" class="qr-block">
        <!-- eslint-disable-next-line vue/no-v-html -- 内容是本地生成的 SVG 字符串，不含外部输入 -->
        <span class="qr-svg" v-html="qrSvg" />

        <!-- 扫不了就手输。密钥单独给一行并允许选中复制——
             手动输入的路径必须在，屏幕碎了、相机坏了都得能进来 -->
        <div class="qr-fallback">
          <span class="qr-fallback-label">扫不了？手动输入密钥</span>
          <div class="qr-secret-row">
            <code class="qr-secret">{{ challenge.secret }}</code>

            <!-- 复制是个**次要动作**：不画边框，悬停时才浮出一层浅底。
                 用 Arco 的文本按钮而不是自己写一个带边框的按钮——
                 边框会把它抬成"页面上的一个操作"，而它只是密钥旁边的一个小动作 -->
            <a-button
              type="text"
              size="mini"
              class="copy-button"
              title="复制密钥"
              aria-label="复制密钥"
              @click="copySecret"
            >
              <template #icon><IconCopy /></template>
            </a-button>
          </div>
        </div>
      </div>

      <a-form :model="{ code }" layout="vertical" class="auth-form" @submit.prevent="submit">
        <a-form-item label="六位验证码" required>
          <a-input
            v-model="code"
            placeholder="000000"
            maxlength="6"
            class="code-input"
            autocomplete="one-time-code"
            @keyup.enter="submit"
          />
        </a-form-item>

        <div v-if="errorMessage" class="auth-error">{{ errorMessage }}</div>

        <a-button type="primary" long :loading="submitting" @click="submit">
          {{ isBinding ? '绑定并登录' : '验证并登录' }}
        </a-button>
      </a-form>
    </div>

    <template #footer>
      <div class="auth-foot">
        <a-link @click="backToLogin">换一个账号登录</a-link>
      </div>
    </template>
  </AuthCard>
</template>

<style scoped>
.auth-form {
  margin-top: 18px;
}

/* 六位码等宽、放大、居中：它是从另一块屏幕抄过来的六个数，
   等宽字体让"抄对了没有"一眼可辨 */
.code-input :deep(input) {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 20px;
  letter-spacing: 8px;
  text-align: center;
}

.qr-block {
  margin-top: 18px;
  text-align: center;
}

.qr-svg {
  display: inline-block;
  padding: 10px;
  background: #fff;
  border: 1px solid var(--color-border-1);
  border-radius: 8px;
}

.qr-svg :deep(svg) {
  display: block;
  width: 168px;
  height: 168px;
}

/* 二维码永远是黑白的（白底是它的可扫性要求），所以不给它跟随主题换色 */
.qr-fallback {
  margin-top: 12px;
  font-size: 12px;
  line-height: 1.8;
  color: var(--color-text-3);
}

.qr-fallback-label {
  display: block;
}

.qr-secret-row {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  margin-top: 2px;
}

/* 与顶栏的主题开关、账号按钮同一档：无边框，悬停给一层浅底 */
.copy-button {
  color: var(--color-text-3);
}

.copy-button:hover {
  color: rgb(var(--primary-6));
}

.qr-secret {
  display: inline-block;
  padding: 2px 6px;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 12px;
  color: var(--color-text-1);
  background: var(--color-fill-2);
  border-radius: 4px;
  word-break: break-all;
  user-select: all;
}

.auth-empty {
  margin-top: 18px;
  font-size: 13px;
  line-height: 1.8;
  color: var(--color-text-3);
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
