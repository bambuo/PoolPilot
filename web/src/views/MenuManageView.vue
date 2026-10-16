<script setup lang="ts">
import { Message } from '@arco-design/web-vue'
import { computed, onMounted, ref } from 'vue'

import {
  adminMenuApi,
  roleApi,
  type MenuKind,
  type MenuNodeDto,
  type MenuPayload,
  type RoleSummary,
} from '@/api/client'
import { iconRegistry, isKnownIconName, resolveIcon } from '@/components/iconRegistry'
import { useMenu } from '@/composables/useMenu'
import { usePermission } from '@/composables/usePermission'

/// 系统 → 菜单管理。菜单树与角色授权。
///
/// 这一页既是"有哪些页面"的登记处，也是权限的分配台：菜单节点决定谁能看到/进入哪个页面
/// （`kind` 决定进不进导航、`path` 决定路由），角色勾选决定"谁"。
///
/// 三件与其他管理页一致的口径：
///   · **保存后要刷新导航**：菜单树是顶栏导航的数据源（共用 useMenu 的那一份），
///     只刷本页的表格，会出现"页面里改了、导航没变"的错觉——所以 every 写操作都同时刷两处。
///   · **删除的拒绝原因要留在弹窗里**：有子节点时后端回 400，关掉弹窗就看不见原因了。
///   · **按钮按动作禁用**：本页的权限键是 `menu:system-menus`，edit / delete 各自控制对应按钮。

/// superadmin 恒被授权、不可撤销（服务端也这么强制）。这里禁用它的勾选框，
/// 让界面上"取消不掉"这件事与服务端一致，而不是等一次被拒的提交。
const SUPERADMIN_CODE = 'superadmin'

/// 本页自身的权限键。服务端按 `menu:{code}` 生成，前端只读不写。
const PAGE_PERMISSION = 'menu:system-menus'

/// 「角色管理」的权限键：本页的"新建角色"按钮要问的是它（服务端 POST /api/roles 的门）。
const ROLE_PERMISSION = 'menu:system-roles'

/// 动作清单。view 是"能看见这个菜单"的前提，edit / delete 是页内的写操作。
const ACTION_LABELS: Readonly<Record<string, string>> = {
  view: '查看',
  edit: '编辑',
  delete: '删除',
}

interface KindOption {
  readonly value: MenuKind
  readonly label: string
  readonly hint: string
}

const KIND_OPTIONS: readonly KindOption[] = [
  { value: 'group', label: '分组', hint: '导航里的一级标题，不可点击，下面挂子菜单' },
  { value: 'menu', label: '菜单', hint: '可进入的页面，进导航' },
  { value: 'hidden', label: '隐藏', hint: '可进入的页面，不进导航（详情页等）' },
]

const tree = ref<MenuNodeDto[]>([])
const loading = ref(false)
const errorMessage = ref('')

const { can, refresh: refreshPermissions } = usePermission()
const { refresh: refreshNav } = useMenu()

const canEdit = computed(() => can(PAGE_PERMISSION, 'edit'))

/// 「新建角色」按钮的门：**不是本页的权限点**——角色是谁建的属于「角色管理」，
/// 服务端 POST /api/roles 要的是 menu:system-roles 的 edit（见 RolesController）。
const canCreateRole = computed(() => can(ROLE_PERMISSION, 'edit'))
const canDelete = computed(() => can(PAGE_PERMISSION, 'delete'))

function kindLabel(kind: MenuKind): string {
  return KIND_OPTIONS.find((option) => option.value === kind)?.label ?? kind
}

function kindColor(kind: MenuKind): string {
  return kind === 'group' ? 'purple' : kind === 'hidden' ? 'gray' : 'arcoblue'
}

function actionLabel(action: string): string {
  return ACTION_LABELS[action] ?? action
}

/// 权限键。**只用于显示**——真正的键由服务端按 code 生成，前端不回传（见 client.ts 的 MenuPayload）。
function permissionKeyOf(node: MenuNodeDto): string {
  return `menu:${node.code}`
}

// ── 树数据 ────────────────────────────────────────────────

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = ''

  try {
    tree.value = await adminMenuApi.tree()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    loading.value = false
  }
}

/// 写操作成功后统一的收尾：重拉管理树**并刷新导航**。
/// 两处共用 useMenu 里的那份树，不刷它就会出现"页面里改了、顶栏还是旧的"。
async function afterMutate(message: string): Promise<void> {
  Message.success(message)
  await Promise.all([load(), refreshNav()])
}

function findNode(nodes: readonly MenuNodeDto[], id: number): MenuNodeDto | null {
  for (const node of nodes) {
    if (node.id === id) return node

    const hit = findNode(node.children, id)
    if (hit !== null) return hit
  }

  return null
}

/// 一个节点连同它的全部后代。用于"不能把节点挂到它自己或它的后代下面"。
function collectSubtreeIds(node: MenuNodeDto, into: Set<number>): void {
  into.add(node.id)

  for (const child of node.children) {
    collectSubtreeIds(child, into)
  }
}

/// 反查某节点挂在谁下面。MenuNodeDto 不带 parentId，只能从树上找。
function locateParent(
  nodes: readonly MenuNodeDto[],
  id: number,
  parentId: number | null,
): { found: boolean; parentId: number | null } {
  for (const node of nodes) {
    if (node.id === id) return { found: true, parentId }

    const hit = locateParent(node.children, id, node.id)
    if (hit.found) return hit
  }

  return { found: false, parentId: null }
}

// ── 新增 / 编辑 ──────────────────────────────────────────

interface MenuForm {
  id: number | null
  code: string
  title: string
  path: string
  icon: string
  kind: MenuKind
  sortOrder: number
  isVisible: boolean
  actions: string[]
}

function freshForm(): MenuForm {
  return {
    id: null,
    code: '',
    title: '',
    path: '',
    icon: '',
    kind: 'menu',
    sortOrder: 0,
    isVisible: true,
    // 默认带上 view：一个没有 view 动作的菜单，对谁都不可见，通常不是本意
    actions: ['view'],
  }
}

const form = ref<MenuForm>(freshForm())
const formOpen = ref(false)
const saving = ref(false)
const formError = ref('')

/// 父节点选择的值。**空（undefined）表示一级节点**——下拉上开了 allow-clear，
/// 清空就是没有父节点，不再需要"（无 · 一级节点）"这样一个占位选项。
const parentIdInput = ref<number | undefined>(undefined)

/// 下拉回传的值收口到一处：数字（选了某个父节点）与 undefined（清空了）是仅有的两种情况，
/// 其他形状（多选数组等）在本页不可能出现，一律当"没有选"处理。
///
/// 之所以不用 `v-model`：Arco 的 `modelValue` 声明在 `exactOptionalPropertyTypes` 下
/// **不接受显式的 undefined**，而"清空"在运行时就是 undefined——用显式绑定把两侧分开，
/// 只在传进去的那一侧收窄（见模板注释），语义不用打折扣。
function onParentChange(value: unknown): void {
  parentIdInput.value = typeof value === 'number' ? value : undefined
}

const isEditing = computed(() => form.value.id !== null)
const isGroup = computed(() => form.value.kind === 'group')

const canSubmit = computed(() => form.value.code.trim() !== '' && form.value.title.trim() !== '')

const iconPreview = computed(() => resolveIcon(form.value.icon))
const iconKnown = computed(() => isKnownIconName(form.value.icon))

/// 图标下拉的候选项 = 注册表里的全部名字。**不是限制**：下拉允许输入表外的名字
/// （allow-create），那种名字会被照存、渲染时回退到中性图标并在下方给出提示。
const ICON_NAMES: readonly string[] = Object.keys(iconRegistry).sort()

/// 图标字段的绑定代理：Arco 的下拉在"清空"时给的是 undefined，
/// 而表单里同一件事一律用空串表示（提交时再转成 null）。
const iconInput = computed({
  get: () => form.value.icon,
  set: (value: string | undefined) => {
    form.value.icon = value ?? ''
  },
})

interface ParentOption {
  readonly id: number
  readonly label: string
  readonly indent: string
  /// 该节点自己的图标名（可能为 null = 没配）。下拉里照节点的样子画出来——
  /// 光有名字，两个同名不同图标的节点在下拉里分不出谁是谁。
  readonly icon: string | null
  readonly disabled: boolean
}

/// 父节点选项（拍平成带缩进的一列）：**排除正在编辑的节点及其后代**——
/// 一个节点不能挂到自己下面。不排除的话，后端要么报错、要么把整棵子树从树上摘掉。
const parentOptions = computed<ParentOption[]>(() => {
  const excluded = new Set<number>()

  if (form.value.id !== null) {
    const self = findNode(tree.value, form.value.id)
    if (self !== null) collectSubtreeIds(self, excluded)
  }

  const options: ParentOption[] = []

  const walk = (nodes: readonly MenuNodeDto[], depth: number): void => {
    for (const node of nodes) {
      options.push({
        id: node.id,
        label: `${node.title}（${node.code}）`,
        indent: '　'.repeat(depth),
        icon: node.icon,
        disabled: excluded.has(node.id),
      })

      if (node.children.length > 0) walk(node.children, depth + 1)
    }
  }

  walk(tree.value, 0)

  return options
})

function openCreate(): void {
  form.value = freshForm()
  parentIdInput.value = undefined
  formError.value = ''
  formOpen.value = true
}

function openEdit(node: MenuNodeDto): void {
  const located = locateParent(tree.value, node.id, null)

  form.value = {
    id: node.id,
    code: node.code,
    title: node.title,
    path: node.path ?? '',
    icon: node.icon ?? '',
    kind: node.kind,
    sortOrder: node.sortOrder,
    isVisible: node.isVisible,
    actions: [...node.actions],
  }

  parentIdInput.value = located.parentId ?? undefined
  formError.value = ''
  formOpen.value = true
}

async function submit(): Promise<void> {
  const code = form.value.code.trim()
  const title = form.value.title.trim()

  if (code === '' || title === '') {
    formError.value = '编码与名称都要填——编码是路由与权限键的一部分，名称是它在界面上显示的样子'
    return
  }

  const payload: MenuPayload = {
    parentId: parentIdInput.value ?? null,
    code,
    title,
    // 分组的 path 无意义（它不可点击），一律清空，免得留下一个指向不存在页面的路径
    path: isGroup.value || form.value.path.trim() === '' ? null : form.value.path.trim(),
    icon: form.value.icon.trim() === '' ? null : form.value.icon.trim(),
    kind: form.value.kind,
    sortOrder: form.value.sortOrder,
    isVisible: form.value.isVisible,
    actions: [...form.value.actions],
  }

  saving.value = true
  formError.value = ''

  try {
    if (form.value.id === null) {
      await adminMenuApi.create(payload)
      formOpen.value = false
      await afterMutate('菜单已创建')
    } else {
      await adminMenuApi.update(form.value.id, payload)
      formOpen.value = false
      await afterMutate('菜单已保存')
    }
  } catch (error) {
    formError.value = error instanceof Error ? error.message : String(error)
  } finally {
    saving.value = false
  }
}

// ── 删除 ──────────────────────────────────────────────────

const deleteOpen = ref(false)
const deleteTarget = ref<MenuNodeDto | null>(null)
const deleting = ref(false)
const deleteError = ref('')

function requestDelete(node: MenuNodeDto): void {
  deleteTarget.value = node
  deleteError.value = ''
  deleteOpen.value = true
}

async function confirmDelete(): Promise<void> {
  if (deleteTarget.value === null) return

  deleting.value = true
  deleteError.value = ''

  try {
    await adminMenuApi.remove(deleteTarget.value.id)

    deleteOpen.value = false
    deleteTarget.value = null
    await afterMutate('菜单已删除')
  } catch (error) {
    // 有子节点时后端回 400。把它的原话留在弹窗里——关掉弹窗，人就不知道是被什么挡住的
    deleteError.value = error instanceof Error ? error.message : String(error)
  } finally {
    deleting.value = false
  }
}

// ── 授权角色 ──────────────────────────────────────────────

const rolesOpen = ref(false)
const rolesTarget = ref<MenuNodeDto | null>(null)
const allRoles = ref<RoleSummary[]>([])
const assignedRoles = ref<string[]>([])
const rolesLoading = ref(false)
const rolesSaving = ref(false)
const rolesError = ref('')

const newRoleCode = ref('')
const newRoleTitle = ref('')
const creatingRole = ref(false)

async function ensureRoles(): Promise<void> {
  if (allRoles.value.length === 0) {
    allRoles.value = await roleApi.list()
  }
}

/// superadmin 恒被授权：把它的勾选补上，免得后端拒绝一次"漏了它"的提交。
function withSuperadmin(codes: readonly string[]): string[] {
  const hasSuper = allRoles.value.some((role) => role.code === SUPERADMIN_CODE)

  return hasSuper && !codes.includes(SUPERADMIN_CODE) ? [...codes, SUPERADMIN_CODE] : [...codes]
}

async function openRoles(node: MenuNodeDto): Promise<void> {
  rolesTarget.value = node
  rolesError.value = ''
  rolesLoading.value = true

  try {
    await ensureRoles()
    const assignment = await adminMenuApi.roles(node.id)
    assignedRoles.value = withSuperadmin(assignment.roleCodes)
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
    await adminMenuApi.setRoles(rolesTarget.value.id, withSuperadmin(assignedRoles.value))
    rolesOpen.value = false
    await afterMutate('角色授权已保存')
  } catch (error) {
    rolesError.value = error instanceof Error ? error.message : String(error)
  } finally {
    rolesSaving.value = false
  }
}

/// 最简的"新建角色"：只收编码与名称。更细的权限配置留给以后，
/// 这里存在的理由是"要给菜单授权时，往往还没有那个角色可勾"。
async function createRole(): Promise<void> {
  const code = newRoleCode.value.trim()
  const title = newRoleTitle.value.trim()

  if (code === '' || title === '') {
    rolesError.value = '角色编码与名称都要填'
    return
  }

  creatingRole.value = true
  rolesError.value = ''

  try {
    await roleApi.create({ code, title })
    allRoles.value = await roleApi.list()
    newRoleCode.value = ''
    newRoleTitle.value = ''
    Message.success('角色已创建')
  } catch (error) {
    rolesError.value = error instanceof Error ? error.message : String(error)
  } finally {
    creatingRole.value = false
  }
}

onMounted(async () => {
  // 权限缓存按会话共享：本页是第一个用到按钮级控制的页面，进来时拉一次
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
          <span>菜单管理</span>
          <a-tag v-if="!loading" size="small">{{ tree.length }} 个一级节点</a-tag>
        </a-space>
      </template>

      <template #extra>
        <a-space>
          <a-button
            type="primary"
            size="small"
            :disabled="!canEdit"
            :title="canEdit ? '' : '当前账号没有编辑菜单的权限'"
            @click="openCreate"
          >
            新增菜单
          </a-button>
          <a-button size="small" :loading="loading" @click="load">刷新</a-button>
        </a-space>
      </template>

      <div class="hint-line">
        菜单节点决定"站内有哪些页面、谁能进"：<b>分组</b>是导航里的一级标题（不可点击），
        <b>菜单</b>是可进入的页面并进导航，<b>隐藏</b>是可进入但不进导航的页面（如各种详情页）。
        节点的<b>编码</b>是路由名与权限键（<code>menu:编码</code>）的一部分，改动会同时影响两者。
        未授予任何角色的菜单节点，对没有它的账号等于不存在——连它的页面代码都不会被下载。
      </div>

      <a-alert v-if="errorMessage" type="error" :title="errorMessage" style="margin-bottom: 12px" />

      <div v-if="!canEdit && !canDelete" class="readonly-line">
        当前账号对本页只有查看权限。
      </div>

      <a-table
        :data="tree"
        :loading="loading"
        row-key="id"
        :default-expand-all-rows="true"
        :hide-expand-button-on-empty="true"
        :bordered="false"
        size="small"
        :scroll="{ x: 1500 }"
      >
        <template #columns>
          <a-table-column title="名称" :width="200" fixed="left">
            <template #cell="{ record }">
              <span class="cell-strong">{{ (record as MenuNodeDto).title }}</span>
            </template>
          </a-table-column>

          <a-table-column title="编码" :width="150">
            <template #cell="{ record }">
              <span class="mono">{{ (record as MenuNodeDto).code }}</span>
            </template>
          </a-table-column>

          <a-table-column title="路由" :width="180">
            <template #cell="{ record }">
              <span class="mono muted">{{ (record as MenuNodeDto).path ?? '—' }}</span>
            </template>
          </a-table-column>

          <a-table-column title="类型" :width="90" align="center">
            <template #cell="{ record }">
              <a-tag size="small" :color="kindColor((record as MenuNodeDto).kind)">
                {{ kindLabel((record as MenuNodeDto).kind) }}
              </a-tag>
            </template>
          </a-table-column>

          <a-table-column title="图标" :width="70" align="center">
            <template #cell="{ record }">
              <!-- 只画图标，不显示图标名：名字在看不清图标时帮不上忙，反倒把这一列撑得很宽。
                   没配图标的行给一个破折号——直接画兜底图标的话，读的人会以为这里配过东西。 -->
              <component
                v-if="(record as MenuNodeDto).icon !== null"
                :is="resolveIcon((record as MenuNodeDto).icon)"
                class="cell-icon"
              />
              <span v-else class="muted">—</span>
            </template>
          </a-table-column>

          <a-table-column title="排序" :width="70" align="right">
            <template #cell="{ record }">{{ (record as MenuNodeDto).sortOrder }}</template>
          </a-table-column>

          <a-table-column title="可见" :width="80" align="center">
            <template #cell="{ record }">
              <a-tag size="small" :color="(record as MenuNodeDto).isVisible ? 'green' : 'gray'">
                {{ (record as MenuNodeDto).isVisible ? '显示' : '不显示' }}
              </a-tag>
            </template>
          </a-table-column>

          <a-table-column title="权限键" :width="200">
            <template #cell="{ record }">
              <span class="mono muted">{{ permissionKeyOf(record as MenuNodeDto) }}</span>
            </template>
          </a-table-column>

          <a-table-column title="动作" :width="170">
            <template #cell="{ record }">
              <a-space :size="4">
                <a-tag v-for="action in (record as MenuNodeDto).actions" :key="action" size="small">
                  {{ actionLabel(action) }}
                </a-tag>
              </a-space>
            </template>
          </a-table-column>

          <a-table-column title="操作" :width="210" fixed="right" align="center">
            <template #cell="{ record }">
              <a-space :size="12">
                <a-link
                  :disabled="!canEdit"
                  :title="canEdit ? '' : '没有编辑权限'"
                  @click="openEdit(record as MenuNodeDto)"
                >
                  编辑
                </a-link>
                <a-link
                  :disabled="!canEdit"
                  :title="canEdit ? '' : '没有编辑权限'"
                  @click="openRoles(record as MenuNodeDto)"
                >
                  授权角色
                </a-link>
                <a-link
                  status="danger"
                  :disabled="!canDelete"
                  :title="canDelete ? '' : '没有删除权限'"
                  @click="requestDelete(record as MenuNodeDto)"
                >
                  删除
                </a-link>
              </a-space>
            </template>
          </a-table-column>
        </template>

        <template #empty>
          <a-empty description="还没有任何菜单节点" />
        </template>
      </a-table>
    </a-card>

    <!-- ── 新增 / 编辑 ─────────────────────────────────── -->
    <a-modal
      v-model:visible="formOpen"
      :title="isEditing ? '编辑菜单' : '新增菜单'"
      :width="'min(720px, 94vw)'"
      :footer="false"
      :mask-closable="false"
      unmount-on-close
    >
      <a-form :model="form" layout="vertical">
        <div class="section-title">位置</div>

        <a-form-item
          label="父节点"
          tooltip="选谁，这一项就排在谁下面；不能选它自己或它的子节点——那会把整棵子树从树上摘下来。清空 = 一级节点。"
        >
          <!-- `as number` 是给类型检查看的收窄：清空时这个值就是 undefined，
               而 Arco 的 modelValue 声明不收显式 undefined（exactOptionalPropertyTypes）；
               运行时它照收不误，所以只在类型这一侧让一步，不改语义。 -->
          <a-select
            :model-value="parentIdInput as number"
            allow-clear
            placeholder="（无 · 一级节点）"
            @update:model-value="onParentChange"
          >
            <a-option
              v-for="option in parentOptions"
              :key="option.id"
              :value="option.id"
              :disabled="option.disabled"
            >
              <!-- 缩进（全角空格）与图标都在这一个行内盒子里：flex 下三个部分的对齐
                   才是一行，不会各写各的 -->
              <span class="parent-option">
                <span class="parent-option-indent">{{ option.indent }}</span>
                <component :is="resolveIcon(option.icon)" class="parent-option-icon" />
                <span>{{ option.label }}</span>
              </span>
            </a-option>
          </a-select>
        </a-form-item>

        <a-form-item
          label="类型"
          required
          tooltip="分组＝导航里的可展开分组（不可进入）；菜单＝可进入的页面，进导航；隐藏＝可进入但不进导航的页面（如详情页）。"
        >
          <a-radio-group v-model="form.kind" type="button">
            <a-radio v-for="option in KIND_OPTIONS" :key="option.value" :value="option.value">
              {{ option.label }}
            </a-radio>
          </a-radio-group>
        </a-form-item>

        <div class="section-title">标识</div>

        <div class="form-grid">
          <a-form-item label="名称" required>
            <a-input v-model="form.title" placeholder="导航与表格里显示的名字" />
          </a-form-item>

          <a-form-item
            label="编码"
            required
            :tooltip="`路由名与权限键都用它，改动会同时影响两者。当前权限键 = menu:${form.code.trim() || '编码'}（由服务端生成，前端不回传）。`"
          >
            <a-input v-model="form.code" placeholder="如 system-menus" />
          </a-form-item>

          <a-form-item
            class="span-2"
            label="路由路径"
            :tooltip="
              isGroup
                ? '分组不注册路由，路径无意义（保存时会清空）。'
                : '路径里的参数名要与页面读取的一致，如 :accountIndex；详情页的路径带参数。'
            "
          >
            <a-input
              v-model="form.path"
              :disabled="isGroup"
              placeholder="如 /system/menus；详情页可带参数，如 /pools/:accountIndex"
            />
          </a-form-item>

          <a-form-item
            label="图标"
            tooltip="留空则用中性默认图标；名字取自图标注册表，表外的名字会被照存但渲染时回退。"
          >
            <a-select
              v-model="iconInput"
              allow-search
              allow-create
              allow-clear
              placeholder="留空则用默认图标"
            >
              <a-option v-for="name in ICON_NAMES" :key="name" :value="name">
                <span class="icon-option">
                  <component :is="resolveIcon(name)" />
                  <span class="mono">{{ name }}</span>
                </span>
              </a-option>
            </a-select>
            <div v-if="form.icon.trim() !== ''" class="icon-preview">
              <component :is="iconPreview" class="icon-preview-mark" />
              <span class="mono">{{ form.icon.trim() }}</span>
              <span v-if="!iconKnown" class="icon-preview-warn">未识别，将回退到默认图标</span>
            </div>
          </a-form-item>

          <a-form-item label="排序" tooltip="小的排前面（同一层级内比较）。">
            <a-input-number v-model="form.sortOrder" :min="0" :step="1" />
          </a-form-item>

          <a-form-item
            class="span-2"
            label="进导航"
            tooltip="关掉只是不显示在导航里，路由仍注册（可被深链命中）。"
          >
            <a-switch v-model="form.isVisible" />
          </a-form-item>
        </div>

        <div class="section-title">权限动作</div>

        <a-form-item
          label="动作"
          tooltip="view 是「能看见这个菜单」的前提；edit / delete 供页面做按钮级控制。只有 view 的菜单，对其成员等于只读页。"
        >
          <a-checkbox-group v-model="form.actions" class="action-group">
            <a-checkbox v-for="(label, action) in ACTION_LABELS" :key="action" :value="action">
              {{ label }} <span class="mono muted">{{ action }}</span>
            </a-checkbox>
          </a-checkbox-group>
        </a-form-item>
      </a-form>

      <div v-if="formError" class="modal-error">{{ formError }}</div>

      <div class="modal-foot">
        <a-space>
          <a-button @click="formOpen = false">取消</a-button>
          <a-button type="primary" :disabled="!canSubmit" :loading="saving" @click="submit">
            {{ saving ? '保存中…' : '保存' }}
          </a-button>
        </a-space>
      </div>
    </a-modal>

    <!-- ── 删除确认 ─────────────────────────────────────── -->
    <a-modal
      v-model:visible="deleteOpen"
      title="删除这个菜单节点？"
      :ok-loading="deleting"
      ok-text="删除"
      cancel-text="取消"
      @ok="confirmDelete"
    >
      <div class="delete-body">
        将删除「{{ deleteTarget?.title }}」（<span class="mono">{{ deleteTarget?.code }}</span>）。
        <template v-if="(deleteTarget?.children.length ?? 0) > 0">
          它下面还有 <b>{{ deleteTarget?.children.length }}</b> 个子节点——后端会拒绝删除，
          请先处理子节点。
        </template>
      </div>

      <a-alert v-if="deleteError" type="error" :title="deleteError" style="margin-top: 12px" />
    </a-modal>

    <!-- ── 授权角色 ─────────────────────────────────────── -->
    <a-modal
      v-model:visible="rolesOpen"
      :title="`授权角色 · ${rolesTarget?.title ?? ''}`"
      :width="480"
      :footer="false"
      :mask-closable="false"
      unmount-on-close
    >
      <a-spin :loading="rolesLoading" style="width: 100%">
        <div class="hint-line">
          勾选的角色的成员，才能在这个菜单上看到它、进入它的页面。superadmin 恒被授权、不可取消。
        </div>

        <a-checkbox-group v-model="assignedRoles" class="role-list">
          <a-checkbox
            v-for="role in allRoles"
            :key="role.code"
            :value="role.code"
            :disabled="role.code === SUPERADMIN_CODE"
          >
            {{ role.title }}
            <span class="mono muted">{{ role.code }}</span>
            <a-tag v-if="role.isSystem" size="small" color="gray">系统</a-tag>
          </a-checkbox>
        </a-checkbox-group>

        <div v-if="allRoles.length === 0" class="muted">还没有任何角色，先在下面建一个。</div>

        <a-divider :margin="12" />

        <div class="new-role">
          <div class="new-role-fields">
            <a-input v-model="newRoleCode" size="small" placeholder="角色编码，如 viewer" />
            <a-input v-model="newRoleTitle" size="small" placeholder="角色名称，如 只读" />
            <!-- 建角色要的是「角色管理」的 edit（服务端 RolesController 的门），
                 不是本页的「菜单管理」edit——两者是分开的权限点，别按本页推断 -->
            <a-button
              size="small"
              :loading="creatingRole"
              :disabled="!canCreateRole"
              :title="canCreateRole ? '' : '需要「角色管理」的编辑权限'"
              @click="createRole"
            >
              新建角色
            </a-button>
          </div>
        </div>

        <a-alert v-if="rolesError" type="error" :title="rolesError" style="margin-top: 12px" />

        <div class="modal-foot">
          <a-space>
            <a-button @click="rolesOpen = false">取消</a-button>
            <a-button type="primary" :loading="rolesSaving" @click="saveRoles">保存授权</a-button>
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

/* 弹窗内的分组小标题：右侧一条细线把分组边界画出来，
   与字段标签区分开（同级灰阶里靠字重和字距分层）。 */
.section-title {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 2px 0 12px;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.08em;
  color: var(--color-text-2);
}

.section-title::after {
  content: '';
  flex: 1;
  height: 1px;
  background: var(--color-border-2);
}

/* 两列网格：短字段并排，长字段（路径、父节点）整行跨两列 */
.form-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  column-gap: 16px;
}

.form-grid .span-2 {
  grid-column: 1 / -1;
}

@media (max-width: 620px) {
  .form-grid {
    grid-template-columns: 1fr;
  }

  .form-grid .span-2 {
    grid-column: auto;
  }
}

/* 父节点下拉的一行：缩进 + 该节点自己的图标 + 名字（code） */
.parent-option {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.parent-option-indent {
  white-space: pre;
}

.parent-option-icon {
  font-size: 14px;
  color: var(--color-text-3);
}

.icon-option {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.action-group {
  display: flex;
  gap: 20px;
  flex-wrap: wrap;
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

.cell-icon {
  font-size: 16px;
  color: var(--color-text-2);
}

.icon-preview {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  margin-top: 6px;
  font-size: 12px;
  color: var(--color-text-3);
}

.icon-preview-mark {
  font-size: 18px;
  color: var(--color-text-1);
}

.icon-preview-warn {
  color: rgb(var(--warning-6));
}

.modal-error {
  margin-bottom: 12px;
  font-size: 12px;
  line-height: 1.7;
  color: rgb(var(--danger-6));
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

.role-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.new-role-fields {
  display: flex;
  gap: 8px;
}

.new-role-fields :deep(.arco-input-wrapper) {
  flex: 1;
}
</style>
