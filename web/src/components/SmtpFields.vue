<script setup lang="ts">
/// SMTP 通道的字段组。**通知页的弹窗与首次初始化页共用这一份**：
/// 两处各写一遍的话，加一个字段（比如以后的抄送）就得记得改两处，
/// 而初始化那一处正是最容易忘的——它只在新机器上走一次。
///
/// 它只负责"字段长什么样"，口令的三态（保持 / 清除 / 换新）由调用方决定：
/// 通知页那边要区分"没动它"和"清空"，初始化页那边口令永远是必填的第一次。
defineProps<{
  /// 口令框的占位文案（两处的语义不同：一处"留空保持原值"，一处"服务商给的授权码"）。
  passwordPlaceholder: string
  /// 已存口令的尾号；没有就传 null（界面据此决定要不要显示那一句提示）。
  passwordHint: string | null
  /// 是否显示"启用"开关。初始化时不需要——那一步配出来的一条必然是启用的。
  showEnabled?: boolean
  /// 是否显示"显示名"。初始化页不显示：那是随时能改的装饰字段，
  /// 而首屏要填的东西越少越好（它默认会给一个"通知邮箱"）。
  showDisplayName?: boolean
  /// 是否显示"发件人显示名"。同上——不发它就用默认的 PoolPilot。
  showFromName?: boolean
}>()

const form = defineModel<{
  displayName: string
  host: string
  port: number
  useStartTls: boolean
  userName: string
  password: string
  fromAddress: string
  fromName: string
  isEnabled: boolean
}>({ required: true })
</script>

<template>
  <a-form :model="form" layout="vertical">
    <a-form-item v-if="showDisplayName !== false" label="显示名" required>
      <a-input v-model="form.displayName" placeholder="如「主邮箱」「通知专用」" />
    </a-form-item>

    <a-form-item label="SMTP 主机" required>
      <a-input v-model="form.host" placeholder="如 smtp.example.com" />
    </a-form-item>

    <div class="two-col">
      <a-form-item label="端口">
        <a-input-number v-model="form.port" :min="1" :max="65535" />
      </a-form-item>

      <a-form-item label="STARTTLS">
        <a-switch v-model="form.useStartTls" />
      </a-form-item>
    </div>

    <a-form-item label="登录名">
      <a-input v-model="form.userName" placeholder="通常是完整邮箱地址" />
    </a-form-item>

    <a-form-item label="口令（授权码）">
      <a-input-password v-model="form.password" :placeholder="passwordPlaceholder" allow-clear />
      <template #extra>
        <span class="field-hint">
          按原样保存在本机数据库里（明文，未加密），界面只回显尾号。
          <template v-if="passwordHint">当前尾号 {{ passwordHint }}。</template>
        </span>
      </template>
    </a-form-item>

    <a-form-item label="发件人地址" required>
      <a-input v-model="form.fromAddress" placeholder="验证码邮件的发出地址" />
    </a-form-item>

    <a-form-item v-if="showFromName !== false" label="发件人显示名">
      <a-input v-model="form.fromName" />
    </a-form-item>

    <a-form-item v-if="showEnabled !== false" label="启用">
      <a-switch v-model="form.isEnabled" />
      <template #extra>
        <span class="field-hint">停用的通道不会被样板选到（选了也发不出去）。</span>
      </template>
    </a-form-item>
  </a-form>
</template>

<style scoped>
/* 端口与 STARTTLS 同一行：它们在语义上都是"怎么连上这台服务器" */
.two-col {
  display: flex;
  gap: 16px;
}

.two-col .arco-form-item {
  flex: 1;
}

.field-hint {
  font-size: 12px;
  line-height: 1.7;
  color: var(--color-text-3);
}
</style>
