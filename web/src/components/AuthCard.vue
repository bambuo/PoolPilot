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
  /* **必须显式声明 border-box**：Arco 没有全局 border-box（实测这里算出来是 content-box），
     于是 `min-height: 100vh` 再加上下各 24px 内边距 = 748px，
     在 720 高的窗口里凭空多出一条滚动条——内容离底部明明还远。
     表现为"登录页右侧总有个滚轮"，而它指向的是一个根本不存在的内容溢出 */
  box-sizing: border-box;
  display: flex;
  min-height: 100vh;
  padding: 24px;
  background: var(--color-fill-1);
}

.auth-card {
  /* 同 .auth-page 的理由：Arco 没有全局 border-box，
     不声明的话 `max-width` 只约束内容盒，实际外框还要再加上左右各 28px 的内边距
     （实测写 460 渲染出来是 506）——这类"宽度差 46px"的偏差最难看出原因 */
  box-sizing: border-box;
  width: 100%;
  max-width: 380px;
  padding: 28px 28px 24px;
  background: var(--color-bg-1);
  border: 1px solid var(--color-border-1);
  border-radius: 10px;

  /* 居中用 margin: auto，而不是父级 align-items/justify-content：
     卡片比视口高时（例如窄窗口下的 MFA 页），flex 居中会把顶部裁掉且滚不上去，
     那种"页面上半截永远看不到"的故障比不居中难查得多 */
  margin: auto;
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
