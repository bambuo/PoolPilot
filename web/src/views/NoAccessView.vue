<script setup lang="ts">
import { IconLock } from '@arco-design/web-vue/es/icon'

import { useSignOut } from '@/composables/useSignOut'

/// 无权限 / 路径不存在的落点。
///
/// **两种原因共用一页，文案刻意不区分**：如果"这个地址存在但你没权限"与"这个地址压根不存在"
/// 给出两种不同的回答，就等于把"站内有哪些地址"泄露出去了——猜地址的人要的正是这个区分。
/// 所以这里只说"页面不存在，或你的账号没有访问它的权限"。
///
/// 无权限账号登录后也落在这里（首页 /graph 不在他的菜单里，守卫最终把他送到这一页）。
const { signingOut, signOut } = useSignOut()
</script>

<template>
  <div class="no-access">
    <div class="no-access-card">
      <IconLock class="no-access-mark" />
      <h1 class="no-access-title">无法访问</h1>
      <p class="no-access-lead">页面不存在，或你的账号没有访问它的权限。</p>

      <!-- 无权限时唯一的出路是换一个账号：把登出放在这里，而不是让人自己去找顶栏的账号菜单
           （那时导航可能是空的，他未必知道往哪儿点） -->
      <a-button type="primary" :loading="signingOut" @click="signOut">退出登录</a-button>
    </div>
  </div>
</template>

<style scoped>
.no-access {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 60vh;
}

/* 居中一张卡：与登录页的形制呼应，但它是工作台里的一个页面，不是整屏的认证页 */
.no-access-card {
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  max-width: 420px;
  padding: 32px 28px;
  text-align: center;
  background: var(--color-bg-1);
  border: 1px solid var(--color-border-1);
  border-radius: 10px;
}

.no-access-mark {
  font-size: 34px;
  color: var(--color-text-4);
}

.no-access-title {
  margin: 0;
  font-size: 18px;
  font-weight: 600;
  color: var(--color-text-1);
}

.no-access-lead {
  margin: 0 0 6px;
  font-size: 13px;
  line-height: 1.8;
  color: var(--color-text-3);
}
</style>
