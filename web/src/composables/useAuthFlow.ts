import { ref, type Ref } from 'vue'

import { authApi, type LoginChallenge, type Session } from '@/api/client'
import { useSession } from '@/composables/useSession'

/// 登录流程走到一半时的状态。
///
/// 登录第一步（邮箱 + 口令）与第二步（六位码 / 扫码绑定）是两个页面，中间靠这个模块级单例衔接：
/// 挑战 Id 与待绑定的密钥**只放在内存里**，不写 localStorage——密钥写进浏览器存储，
/// 等于把它复制到所有能执行脚本的地方。代价是刷新页面要从头登录一次，这个代价可以接受。
const challenge: Ref<LoginChallenge | null> = ref(null)

/// 邮箱留着只是为了在第二步页面上显示"正在为谁验证"，不参与任何判断。
const email = ref('')

async function start(emailValue: string, password: string): Promise<LoginChallenge> {
  const started = await authApi.login({ email: emailValue, password })

  challenge.value = started
  email.value = emailValue

  return started
}

function clear(): void {
  challenge.value = null
  email.value = ''
}

export function useAuthFlow() {
  const { set } = useSession()

  /// 完成第二步：已绑定走 verify、未绑定走 bind。成功即写入会话。
  async function complete(code: string): Promise<Session> {
    const current = challenge.value

    if (current === null) {
      throw new Error('登录流程已失效，请重新输入邮箱与密码')
    }

    const session =
      current.status === 'mfaBindingRequired'
        ? await authApi.bindMfa({ challengeId: current.challengeId, code })
        : await authApi.verifyMfa({ challengeId: current.challengeId, code })

    set(session)
    clear()

    return session
  }

  return { challenge, email, start, complete, clear }
}
