<script setup lang="ts">
/// 登录 / 注册 / MFA 三页共用的外框。
///
/// 单独成一个组件而不是每页各画一遍：这三页的差别只在中间那张表单，
/// 而"整屏居中 + 一张卡片 + 品牌在顶"如果各写一份，改一次要改三处；
/// 更要紧的是**它们看起来必须一致**——登录与注册在视觉上不一样，
/// 会让人怀疑自己是不是走错了地方。
defineProps<{
  /// 卡片标题，如"登录"。
  title: string
  /// 标题下的一句话说明（可选）。
  hint?: string
}>()
</script>

<template>
  <div class="auth-page">
    <div class="auth-card">
      <!-- 品牌与顶栏同一份标志：登录页是这套系统给人看的第一屏 -->
      <div class="auth-brand">
        <img class="auth-mark" src="/logo.svg" alt="" aria-hidden="true" />
        <span>PoolPilot</span>
      </div>

      <h1 class="auth-title">{{ title }}</h1>
      <p v-if="hint" class="auth-hint">{{ hint }}</p>

      <slot />

      <slot name="footer" />
    </div>
  </div>
</template>

<style scoped>
.auth-page {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 100vh;
  padding: 24px;
  background: var(--color-fill-1);
}

.auth-card {
  width: 100%;
  max-width: 380px;
  padding: 28px 28px 24px;
  background: var(--color-bg-1);
  border: 1px solid var(--color-border-1);
  border-radius: 10px;
}

.auth-brand {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  color: var(--color-text-1);
  font-size: 16px;
  font-weight: 600;
}

.auth-mark {
  display: block;
  width: 22px;
  height: 22px;
}

.auth-title {
  margin: 18px 0 0;
  font-size: 20px;
  font-weight: 600;
  color: var(--color-text-1);
}

.auth-hint {
  margin: 6px 0 0;
  font-size: 13px;
  line-height: 1.7;
  color: var(--color-text-3);
}
</style>
