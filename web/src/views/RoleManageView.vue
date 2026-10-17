<script setup lang="ts">
import type { TreeNodeData } from '@arco-design/web-vue'
import { Message } from '@arco-design/web-vue'
import { computed, onMounted, ref } from 'vue'

import {
  adminMenuApi,
  adminRoleApi,
  roleApi,
  type AdminRoleSummary,
  type MenuNodeDto,
} from '@/api/client'
import { useMenu } from '@/composables/useMenu'
import { usePermission } from '@/composables/usePermission'

/// 系统 → 角色管理。角色的新建 / 改名 / 删除与菜单授权。
///
/// 与「菜单管理」是同一份数据的两个视角：那边从菜单出发"这个页面给哪些角色"，
/// 这里从角色出发"这个角色拿到哪些页面"。两边都是整体替换，写完服务端都会重建策略，
/// 生效不需要重新登录。
///
/// 三件与其他管理页一致的口径：
///   · **保存后要刷新导航**：改的若是自己持有的角色，顶栏的可见菜单跟着变（共用 useMenu 的那一份）。
///   · **删除的拒绝原因要留在弹窗里**：系统角色与还有成员时后端回 400，关掉弹窗就看不见原因了。
///   · **按钮按动作禁用**：本页的权限键是 `menu:system-roles`，edit / delete 各自控制对应按钮。

/// superadmin 恒有全部权限（策略由投影常驻补齐，见 docs/PERMISSIONS.md 第四节）：
/// 它的菜单矩阵不可配置——界面上禁用那个入口，而不是等人提交一次注定被拒的替换。
const SUPERADMIN_CODE = 'superadmin'

/// 本页自身的权限键。服务端按 `menu:{code}` 生成，前端只读不写。
const PAGE_PERMISSION = 'menu:system-roles'

const roles = ref<AdminRoleSummary[]>([])
const loading = ref(false)
const errorMessage = ref('')

const { can, refresh: refreshPermissions } = usePermission()
const { refresh: refreshNav } = useMenu()

const canEdit = computed(() => can(PAGE_PERMISSION, 'edit'))
const canDelete = computed(() => can(PAGE_PERMISSION, 'delete'))

/// 除系统角色以外的角色数。为零时页面上要留一句话——"一个自定义角色都没有"与
/// "数据没读出来"看起来是一样的，必须说清是前者。
const customRoleCount = computed(() => roles.value.filter((role) => !role.isSystem).length)

/// 当前注册默认角色。全库最多一个；没有指定时，新注册账号仍是"无角色"。
const registrationDefault = computed(
  () => roles.value.find((role) => role.isRegistrationDefault) ?? null,
)

/// 正在切换默认标记的角色 code（防重复点击，不改整页 loading）。
const defaultSaving = ref<string | null>(null)

// ── 列表 ────────────────────────────────────────────────

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = ''

  try {
    roles.value = await adminRoleApi.list()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    loading.value = false
  }
}

/// 写操作成功后统一的收尾：重拉角色列表**并刷新导航**。
/// 角色的菜单授权改了、而自己正好是它的成员时，顶栏的可见菜单跟着变；
/// 只刷本页的表格，会出现"页面里改了、导航还是旧的"。
async function afterMutate(message: string): Promise<void> {
  Message.success(message)
  await Promise.all([load(), refreshNav()])
}

// ── 注册默认 ──────────────────────────────────────────────

/// 系统角色不可设为默认（尤其 superadmin：那等于让每个注册者直接拿到全部权限）。
/// 与"没有编辑权限"是两种原因，提示语分开说。
function defaultEntryReason(role: AdminRoleSummary): string {
  if (role.isSystem) return '系统角色不能设为注册默认'
  if (!canEdit.value) return '没有编辑权限'

  return ''
}

function defaultEntryDisabled(role: AdminRoleSummary): boolean {
  return !canEdit.value || role.isSystem || defaultSaving.value !== null
}

async function toggleRegistrationDefault(role: AdminRoleSummary): Promise<void> {
  if (defaultEntryDisabled(role)) return

  defaultSaving.value = role.code

  try {
    if (role.isRegistrationDefault) {
      await adminRoleApi.clearRegistrationDefault(role.code)
      await afterMutate('已取消注册默认角色')
    } else {
      await adminRoleApi.setRegistrationDefault(role.code)
      await afterMutate(`之后注册的账号将自动获得「${role.title}」`)
    }
  } catch (error) {
    Message.error(error instanceof Error ? error.message : String(error))
  } finally {
    defaultSaving.value = null
  }
}

// ── 新建 ────────────────────────────────────────────────

/// 新建表单。a-form 的 model 是必填的（类型上如此），它也就是"表单里有哪些字段"的声明。
interface RoleCreateForm {
  code: string
  title: string
}

function freshCreateForm(): RoleCreateForm {
  return { code: '', title: '' }
}

const createOpen = ref(false)
const createForm = ref<RoleCreateForm>(freshCreateForm())
const creating = ref(false)
const createError = ref('')

const canSubmitCreate = computed(
  () => createForm.value.code.trim() !== '' && createForm.value.title.trim() !== '',
)

function openCreate(): void {
  createForm.value = freshCreateForm()
  createError.value = ''
  createOpen.value = true
}

async function submitCreate(): Promise<void> {
  const code = createForm.value.code.trim()
  const title = createForm.value.title.trim()

  if (code === '' || title === '') {
    createError.value = '角色编码与名称都要填——编码是权限主体的标识（role:编码），名称是它在界面上显示的样子'
    return
  }

  creating.value = true
  createError.value = ''

  try {
    // 用既有的 roleApi.create()（POST /api/roles）。新角色建出来是空权限，
    // 要有菜单还得到「配置菜单」里授权——这里不顺手替它勾任何东西。
    await roleApi.create({ code, title })
    createOpen.value = false
    await afterMutate('角色已创建')
  } catch (error) {
    createError.value = error instanceof Error ? error.message : String(error)
  } finally {
    creating.value = false
  }
}

// ── 改名 ────────────────────────────────────────────────

/// 改名表单只有名称一个字段（编码是标识，不可改）。同上，单独收一个 model。
interface RoleRenameForm {
  title: string
}

const renameOpen = ref(false)
const renameTarget = ref<AdminRoleSummary | null>(null)
const renameForm = ref<RoleRenameForm>({ title: '' })
const renaming = ref(false)
const renameError = ref('')

const canSubmitRename = computed(() => renameForm.value.title.trim() !== '')

/// 只改显示名。编码是角色的标识（也是权限主体 role:{code} 的那一段），
/// 改名界面不提供它——合同上的 PUT 也只收 title。
function openRename(role: AdminRoleSummary): void {
  renameTarget.value = role
  renameForm.value = { title: role.title }
  renameError.value = ''
  renameOpen.value = true
}

async function submitRename(): Promise<void> {
  if (renameTarget.value === null) return

  const title = renameForm.value.title.trim()

  if (title === '') {
    renameError.value = '名称不能为空'
    return
  }

  renaming.value = true
  renameError.value = ''

  try {
    await adminRoleApi.rename(renameTarget.value.code, title)
    renameOpen.value = false
    await afterMutate('角色已改名')
  } catch (error) {
    renameError.value = error instanceof Error ? error.message : String(error)
  } finally {
    renaming.value = false
  }
}

// ── 删除 ────────────────────────────────────────────────

const deleteOpen = ref(false)
const deleteTarget = ref<AdminRoleSummary | null>(null)
const deleting = ref(false)
const deleteError = ref('')

function requestDelete(role: AdminRoleSummary): void {
  deleteTarget.value = role
  deleteError.value = ''
  deleteOpen.value = true
}

async function confirmDelete(): Promise<void> {
  if (deleteTarget.value === null) return

  deleting.value = true
  deleteError.value = ''

  try {
    await adminRoleApi.remove(deleteTarget.value.code)

    deleteOpen.value = false
    deleteTarget.value = null
    await afterMutate('角色已删除')
  } catch (error) {
    // 系统角色、还有成员……后端回 400。把它的原话留在弹窗里——关掉弹窗，人就不知道是被什么挡住的
    deleteError.value = error instanceof Error ? error.message : String(error)
  } finally {
    deleting.value = false
  }
}

// ── 配置菜单 ──────────────────────────────────────────────

const menusOpen = ref(false)
const menusTarget = ref<AdminRoleSummary | null>(null)
const menuTreeData = ref<TreeNodeData[]>([])
const checkedMenuCodes = ref<string[]>([])
const menusLoading = ref(false)
const menusSaving = ref(false)
const menusError = ref('')

/// 「配置菜单」在超管行上禁用。与"没有权限"是两种原因，提示语分开说——
/// 同一个禁用样式下把原因说错，比不说更让人困惑。
function menusEntryDisabled(role: AdminRoleSummary): boolean {
  return !canEdit.value || role.code === SUPERADMIN_CODE
}

function menusEntryReason(role: AdminRoleSummary): string {
  if (role.code === SUPERADMIN_CODE) return '超管恒有全部权限，无需配置'

  return canEdit.value ? '' : '没有编辑权限'
}

/// 管理视图的菜单树 → Arco 勾选树的节点。**key 放菜单 code**：它就是回传时要的东西，
/// 勾选状态与保存都按 code 对齐；父子关系只影响显示（缩进与展开）。
function toTreeData(nodes: readonly MenuNodeDto[]): TreeNodeData[] {
  return nodes.map((node) => ({
    key: node.code,
    title: node.title,
    ...(node.children.length > 0 ? { children: toTreeData(node.children) } : {}),
  }))
}

/// 勾选结果的收口：Arco 的事件类型是 `(string | number)[]`，而树里的 key 全部是
/// 菜单 code（字符串）——在这里统一转一次，保存那一侧就只面对 string[]。
function onMenuCheck(keys: Array<string | number>): void {
  checkedMenuCodes.value = keys.map((key) => String(key))
}

async function openMenus(role: AdminRoleSummary): Promise<void> {
  menusTarget.value = role
  menusError.value = ''
  menusLoading.value = true

  try {
    // 全量树与"这个角色已授权的 code"并行拉。每次都拉新的：另一边（菜单管理页）
    // 可能刚改过树，而这是本机的两次小请求，不值得为它做缓存与失效。
    const [tree, assignment] = await Promise.all([
      adminMenuApi.tree(),
      adminRoleApi.menus(role.code),
    ])

    menuTreeData.value = toTreeData(tree)
    checkedMenuCodes.value = [...assignment.menuCodes]
    menusOpen.value = true
  } catch (error) {
    Message.error(error instanceof Error ? error.message : String(error))
  } finally {
    menusLoading.value = false
  }
}

async function saveMenus(): Promise<void> {
  if (menusTarget.value === null) return

  menusSaving.value = true
  menusError.value = ''

  try {
    // 整体替换：勾了什么就是什么，一个都不勾 = 这个角色什么菜单都拿不到
    await adminRoleApi.setMenus(menusTarget.value.code, checkedMenuCodes.value)
    menusOpen.value = false
    await afterMutate('菜单授权已保存')
  } catch (error) {
    menusError.value = error instanceof Error ? error.message : String(error)
  } finally {
    menusSaving.value = false
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
          <span>角色管理</span>
          <a-tag v-if="!loading" size="small">{{ roles.length }} 个角色</a-tag>
        </a-space>
      </template>

      <template #extra>
        <a-space>
          <a-button
            type="primary"
            size="small"
            :disabled="!canEdit"
            :title="canEdit ? '' : '当前账号没有编辑角色的权限'"
            @click="openCreate"
          >
            新建角色
          </a-button>
          <a-button size="small" :loading="loading" @click="load">刷新</a-button>
        </a-space>
      </template>

      <div class="hint-line">
        角色是权限的容器：其成员通过它拿到菜单上的动作（一个账号可有多个角色，权限取并集）。
        superadmin 恒有全部权限（由策略投影常驻补齐），改不了也删不掉，这里也不给它配菜单。
        <b>注册默认</b>是之后注册的账号自动获得的角色，全库最多一个；没有指定时，新账号不会带任何权限。
        「菜单数」是这个角色被授权到的菜单节点数——改完授权，相关成员<b>下一次请求即生效</b>，
        不需要重新登录。
      </div>

      <a-alert v-if="errorMessage" type="error" :title="errorMessage" style="margin-bottom: 12px" />

      <div
        v-if="!loading && !errorMessage && roles.length > 0 && registrationDefault === null"
        class="default-note"
      >
        当前没有设置注册默认角色：新注册的账号不会自动获得任何角色，
        需要在「用户管理」里逐个分配，或在这里指定一个默认角色。
      </div>

      <div v-if="!canEdit && !canDelete" class="readonly-line">
        当前账号对本页只有查看权限。
      </div>

      <a-table
        :data="roles"
        :loading="loading"
        row-key="code"
        :bordered="false"
        size="small"
        :scroll="{ x: 1050 }"
      >
        <template #columns>
          <a-table-column title="编码" :width="160">
            <template #cell="{ record }">
              <span class="mono">{{ (record as AdminRoleSummary).code }}</span>
            </template>
          </a-table-column>

          <a-table-column title="名称" :width="180">
            <template #cell="{ record }">
              <span class="cell-strong">{{ (record as AdminRoleSummary).title }}</span>
            </template>
          </a-table-column>

          <a-table-column title="类型" :width="110" align="center">
            <template #cell="{ record }">
              <!-- 标签只标系统角色（例外的那一个）；其余行写"自定义"就够，不需要再加一层标签 -->
              <a-tag v-if="(record as AdminRoleSummary).isSystem" size="small" color="gray">系统</a-tag>
              <span v-else class="muted">自定义</span>
            </template>
          </a-table-column>

          <a-table-column title="注册默认" :width="100" align="center">
            <template #cell="{ record }">
              <a-tag v-if="(record as AdminRoleSummary).isRegistrationDefault" size="small" color="green">
                默认
              </a-tag>
              <span v-else class="muted">—</span>
            </template>
          </a-table-column>

          <a-table-column title="成员数" :width="90" align="right">
            <template #cell="{ record }">{{ (record as AdminRoleSummary).memberCount }}</template>
          </a-table-column>

          <a-table-column title="菜单数" :width="90" align="right">
            <template #cell="{ record }">{{ (record as AdminRoleSummary).menuCount }}</template>
          </a-table-column>

          <a-table-column title="操作" :width="320" fixed="right" align="center">
            <template #cell="{ record }">
              <a-space :size="12">
                <a-link
                  :disabled="!canEdit"
                  :title="canEdit ? '' : '没有编辑权限'"
                  @click="openRename(record as AdminRoleSummary)"
                >
                  改名
                </a-link>
                <a-link
                  :disabled="menusEntryDisabled(record as AdminRoleSummary)"
                  :title="menusEntryReason(record as AdminRoleSummary)"
                  @click="openMenus(record as AdminRoleSummary)"
                >
                  配置菜单
                </a-link>
                <a-link
                  :disabled="defaultEntryDisabled(record as AdminRoleSummary)"
                  :title="defaultEntryReason(record as AdminRoleSummary)"
                  @click="toggleRegistrationDefault(record as AdminRoleSummary)"
                >
                  {{ (record as AdminRoleSummary).isRegistrationDefault ? '取消默认' : '设为默认' }}
                </a-link>
                <a-link
                  status="danger"
                  :disabled="!canDelete"
                  :title="canDelete ? '' : '没有删除权限'"
                  @click="requestDelete(record as AdminRoleSummary)"
                >
                  删除
                </a-link>
              </a-space>
            </template>
          </a-table-column>
        </template>

        <template #empty>
          <a-empty description="还没有任何角色" />
        </template>
      </a-table>

      <!-- 只有系统角色时的那句话：空表是"什么都没读到"，这一种是"还没建过自定义角色"——
           两者在界面上长得一样，必须说清是哪一种 -->
      <div
        v-if="!loading && !errorMessage && roles.length > 0 && customRoleCount === 0"
        class="empty-note"
      >
        除系统角色（超级管理员）外，还没有任何角色——点右上角「新建角色」建一个，
        再到菜单上把它授权给需要的页面。
      </div>
    </a-card>

    <!-- ── 新建角色 ─────────────────────────────────────── -->
    <a-modal
      v-model:visible="createOpen"
      title="新建角色"
      :width="'min(480px, 94vw)'"
      :footer="false"
      :mask-closable="false"
      unmount-on-close
    >
      <a-form :model="createForm" layout="vertical">
        <a-form-item
          label="编码"
          required
          tooltip="权限主体用它（role:编码），建成之后不可改；建议小写加连字符。"
        >
          <a-input v-model="createForm.code" placeholder="如 viewer" />
        </a-form-item>

        <a-form-item label="名称" required tooltip="界面上显示的名字（可随时改，不影响权限）。">
          <a-input v-model="createForm.title" placeholder="如 只读" />
        </a-form-item>
      </a-form>

      <div class="hint-line">新角色建出来是空权限，不会自动拿到任何页面。</div>

      <a-alert v-if="createError" type="error" :title="createError" />

      <div class="modal-foot">
        <a-space>
          <a-button @click="createOpen = false">取消</a-button>
          <a-button
            type="primary"
            :disabled="!canSubmitCreate"
            :loading="creating"
            @click="submitCreate"
          >
            {{ creating ? '创建中…' : '创建' }}
          </a-button>
        </a-space>
      </div>
    </a-modal>

    <!-- ── 改名 ─────────────────────────────────────────── -->
    <a-modal
      v-model:visible="renameOpen"
      :title="`改名 · ${renameTarget?.code ?? ''}`"
      :width="'min(480px, 94vw)'"
      :footer="false"
      :mask-closable="false"
      unmount-on-close
    >
      <a-form :model="renameForm" layout="vertical">
        <a-form-item
          label="名称"
          required
          tooltip="编码是角色的标识（权限主体 role:编码 的那一段），改名不提供它。"
        >
          <a-input v-model="renameForm.title" placeholder="角色名称" />
        </a-form-item>
      </a-form>

      <a-alert v-if="renameError" type="error" :title="renameError" />

      <div class="modal-foot">
        <a-space>
          <a-button @click="renameOpen = false">取消</a-button>
          <a-button
            type="primary"
            :disabled="!canSubmitRename"
            :loading="renaming"
            @click="submitRename"
          >
            {{ renaming ? '保存中…' : '保存' }}
          </a-button>
        </a-space>
      </div>
    </a-modal>

    <!-- ── 删除确认 ─────────────────────────────────────── -->
    <a-modal
      v-model:visible="deleteOpen"
      title="删除这个角色？"
      :ok-loading="deleting"
      ok-text="删除"
      cancel-text="取消"
      @ok="confirmDelete"
    >
      <div class="delete-body">
        将删除角色「{{ deleteTarget?.title }}」（<span class="mono">{{ deleteTarget?.code }}</span>）。
        系统角色与还有成员的角色会被后端拒绝——原因会显示在这句话下面。
      </div>

      <a-alert v-if="deleteError" type="error" :title="deleteError" style="margin-top: 12px" />
    </a-modal>

    <!-- ── 配置菜单 ─────────────────────────────────────── -->
    <a-modal
      v-model:visible="menusOpen"
      :title="`配置菜单 · ${menusTarget?.title ?? ''}`"
      :width="'min(560px, 94vw)'"
      :footer="false"
      :mask-closable="false"
      unmount-on-close
    >
      <a-spin :loading="menusLoading" style="width: 100%">
        <div class="hint-line">
          勾上的节点，它的<b>全部动作</b>（view / edit / delete）都授权给这个角色；保存是整体替换。
          分组与子页各自独立——只勾子页不勾分组也有效（子页会在导航里升到根）。
        </div>

        <!-- 独立勾选（check-strictly）：每个节点的 view 本来就是独立判定的，勾了什么存什么，
             打开再保存不会偷偷动数据。关联勾选会把"勾分组"顺带展开成勾全部子页，等于改数据 -->
        <a-tree
          :data="menuTreeData"
          checkable
          check-strictly
          default-expand-all
          :checked-keys="checkedMenuCodes"
          class="menu-tree"
          @check="onMenuCheck"
        >
          <template #title="node">
            <span class="tree-node">
              <span>{{ node.title }}</span>
              <span class="mono muted">{{ node.key }}</span>
            </span>
          </template>
        </a-tree>

        <a-alert v-if="menusError" type="error" :title="menusError" style="margin-top: 12px" />

        <div class="modal-foot">
          <a-space>
            <a-button @click="menusOpen = false">取消</a-button>
            <a-button type="primary" :loading="menusSaving" @click="saveMenus">保存授权</a-button>
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

/* 没设注册默认是"开放注册 + 新账号无权限"，不是错误，但必须说出来 */
.default-note {
  margin-bottom: 12px;
  font-size: 12px;
  line-height: 1.8;
  color: var(--color-text-3);
}

/* 「只有系统角色」的说明挂在表格下面：它是一句注脚，不是页级提示 */
.empty-note {
  margin-top: 12px;
  font-size: 12px;
  line-height: 1.8;
  color: var(--color-text-3);
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

/* 勾选树给一个可滚动的高度上限：菜单多起来之后，弹窗不该长过屏幕 */
.menu-tree {
  max-height: 52vh;
  overflow: auto;
  padding: 4px 8px;
  border: 1px solid var(--color-border-2);
  border-radius: 6px;
}

.tree-node {
  display: inline-flex;
  align-items: baseline;
  gap: 8px;
}

.modal-foot {
  display: flex;
  justify-content: flex-end;
  margin-top: 8px;
}

.delete-body {
  font-size: 13px;
  line-height: 1.8;
  color: var(--color-text-2);
}
</style>
