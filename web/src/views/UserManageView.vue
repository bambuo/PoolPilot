<script setup lang="ts">
import { Message } from '@arco-design/web-vue'
import { computed, onMounted, ref } from 'vue'

import { adminUserApi, roleApi, type RoleSummary, type UserSummary } from '@/api/client'
import { usePermission } from '@/composables/usePermission'
import { formatLocalDateTime } from '@/utils/format'

/// 系统 → 用户管理。账号清单与角色分配。
///
/// 这一页只做一件事：把角色分配给账号（注册是开放的，账号由人自己注册出来）。
/// **不做删账号 / 改密码 / 重置 MFA**——那三件事需要"证明你是你"的额外通道
/// （备用邮箱、恢复码、管理员介入），没有通道时怎么做都是洞，文档里刻意不做
/// （docs/AUTH.md 第七节），界面也就不给入口。
///
/// 与其他管理页一致的口径：按钮按动作禁用；保存后刷新本页（列表里的角色标签跟着变）。

/// 本页自身的权限键。服务端按 `menu:{code}` 生成，前端只读不写。
const PAGE_PERMISSION = 'menu:system-users'

const users = ref<UserSummary[]>([])
const loading = ref(false)
const errorMessage = ref('')

const { can, refresh: refreshPermissions } = usePermission()

/// 本页的写动作只有"分配角色"一个，归 edit 管。**没有 delete**：不删账号是刻意的（见文件头），
/// 所以这里不读 delete——多留一个永远为真的判断，只会让后来的人以为漏了什么。
const canEdit = computed(() => can(PAGE_PERMISSION, 'edit'))

// ── 列表 ────────────────────────────────────────────────

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = ''

  try {
    users.value = await adminUserApi.list()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    loading.value = false
  }
}

// ── 分配角色 ──────────────────────────────────────────────

const rolesOpen = ref(false)
const rolesTarget = ref<UserSummary | null>(null)
const allRoles = ref<RoleSummary[]>([])
const assignedRoles = ref<string[]>([])
const rolesLoading = ref(false)
const rolesSaving = ref(false)
const rolesError = ref('')

/// 打开分配对话框。角色清单来自 /api/roles（登录即可读；菜单管理页用的也是它），
/// **勾选初值直接取行上已有的 roles**——列表已经把它们带回来了，不用另打一次请求。
async function openRoles(user: UserSummary): Promise<void> {
  rolesTarget.value = user
  rolesError.value = ''
  rolesLoading.value = true

  try {
    if (allRoles.value.length === 0) {
      allRoles.value = await roleApi.list()
    }

    assignedRoles.value = user.roles.map((role) => role.code)
    rolesOpen.value = true
  } catch (error) {
    Message.error(error instanceof Error ? error.message : String(error))
  } finally {
    rolesLoading.value = false
  }
}

async function saveRoles(): Promise<void> {
  if (rolesTarget.value === null) return

  rolesSaving.value = true
  rolesError.value = ''

  try {
    // 整体替换：勾了什么就是什么，一个都不勾 = 这个账号看不到任何页面（后端照做）
    await adminUserApi.setRoles(rolesTarget.value.id, assignedRoles.value)
    rolesOpen.value = false
    Message.success('角色已保存')
    await load()
  } catch (error) {
    rolesError.value = error instanceof Error ? error.message : String(error)
  } finally {
    rolesSaving.value = false
  }
}

onMounted(async () => {
  // 权限缓存按会话共享：拉一次，让本页的按钮状态与当前会话一致
  try {
    await refreshPermissions()
  } catch (error) {
    console.warn('读取权限失败，本页按钮将按无权限处理', error)
  }

  await load()
})
</script>

<template>
  <a-space direction="vertical" fill style="width: 100%">
    <a-card :bordered="false">
      <template #title>
        <a-space>
          <span>用户管理</span>
          <a-tag v-if="!loading" size="small">{{ users.length }} 个账号</a-tag>
        </a-space>
      </template>

      <template #extra>
        <a-button size="small" :loading="loading" @click="load">刷新</a-button>
      </template>

      <div class="hint-line">
        注册是<b>开放的</b>——任何人都可以给自己注册账号；但新注册的账号<b>默认没有任何角色</b>，
        在下面给它分配角色之前，它看不到任何页面。角色本身（有哪些、各拿哪些菜单）
        在「系统 → 角色管理」里维护。
      </div>

      <a-alert v-if="errorMessage" type="error" :title="errorMessage" style="margin-bottom: 12px" />

      <div v-if="!canEdit" class="readonly-line">当前账号对本页只有查看权限（不能分配角色）。</div>

      <a-table :data="users" :loading="loading" row-key="id" :bordered="false" size="small">
        <template #columns>
          <a-table-column title="邮箱" :width="280">
            <template #cell="{ record }">
              <span class="cell-strong">{{ (record as UserSummary).email }}</span>
            </template>
          </a-table-column>

          <a-table-column title="角色">
            <template #cell="{ record }">
              <!-- 无角色不是"没配全"，而是这个账号当下**一个页面都进不去**：
                   说成一句醒目的话，比一排空标签更能说明问题 -->
              <a-tag v-if="(record as UserSummary).roles.length === 0" size="small" color="orangered">
                无角色 · 看不到任何菜单
              </a-tag>
              <a-space v-else :size="4" wrap>
                <a-tag v-for="role in (record as UserSummary).roles" :key="role.code" size="small">
                  {{ role.title }}
                </a-tag>
              </a-space>
            </template>
          </a-table-column>

          <a-table-column title="创建时间" :width="170">
            <template #cell="{ record }">
              <!-- 库里的 createdAt 是 ISO UTC 字符串；账号的创建时间要按本地时区读 -->
              {{ formatLocalDateTime((record as UserSummary).createdAt) }}
            </template>
          </a-table-column>

          <a-table-column title="操作" :width="120" align="center">
            <template #cell="{ record }">
              <a-link
                :disabled="!canEdit"
                :title="canEdit ? '' : '没有编辑权限'"
                @click="openRoles(record as UserSummary)"
              >
                分配角色
              </a-link>
            </template>
          </a-table-column>
        </template>

        <template #empty>
          <a-empty description="还没有任何账号——注册入口是开放的，第一个注册的账号会成为超级管理员" />
        </template>
      </a-table>
    </a-card>

    <!-- ── 分配角色 ─────────────────────────────────────── -->
    <a-modal
      v-model:visible="rolesOpen"
      :title="`分配角色 · ${rolesTarget?.email ?? ''}`"
      :width="480"
      :footer="false"
      :mask-closable="false"
      unmount-on-close
    >
      <a-spin :loading="rolesLoading" style="width: 100%">
        <div class="hint-line">
          勾上的角色立即生效（对方不需要重新登录）：多个角色取权限<b>并集</b>，一个都不勾 =
          它看不到任何页面。superadmin 等于全部权限，给出前先想一遍。
        </div>

        <a-checkbox-group v-model="assignedRoles" class="role-list">
          <a-checkbox v-for="role in allRoles" :key="role.code" :value="role.code">
            {{ role.title }}
            <span class="mono muted">{{ role.code }}</span>
            <a-tag v-if="role.isSystem" size="small" color="gray">系统</a-tag>
          </a-checkbox>
        </a-checkbox-group>

        <div v-if="allRoles.length === 0" class="muted">
          还没有任何角色——先去「系统 → 角色管理」建一个。
        </div>

        <a-alert v-if="rolesError" type="error" :title="rolesError" style="margin-top: 12px" />

        <div class="modal-foot">
          <a-space>
            <a-button @click="rolesOpen = false">取消</a-button>
            <a-button type="primary" :loading="rolesSaving" @click="saveRoles">保存</a-button>
          </a-space>
        </div>
      </a-spin>
    </a-modal>
  </a-space>
</template>

<style scoped>
.hint-line {
  margin-bottom: 12px;
  font-size: 12px;
  line-height: 1.8;
  color: var(--color-text-3);
}

.readonly-line {
  margin-bottom: 12px;
  font-size: 12px;
  color: rgb(var(--warning-6));
}

.cell-strong {
  font-weight: 600;
  color: var(--color-text-1);
}

.mono {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
}

.muted {
  color: var(--color-text-3);
}

.modal-foot {
  display: flex;
  justify-content: flex-end;
  margin-top: 8px;
}

.role-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
</style>
