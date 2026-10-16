import type { Component } from 'vue'
import {
  IconApps,
  IconBook,
  IconCommon,
  IconDashboard,
  IconFilter,
  IconHistory,
  IconHome,
  IconList,
  IconMenu,
  IconNotification,
  IconRelation,
  IconScan,
  IconSettings,
  IconStorage,
  IconTool,
  IconUser,
  IconUserGroup,
} from '@arco-design/web-vue/es/icon'

/// 图标注册表：后端在菜单节点上存的是**图标名字符串**（如 `IconDashboard`），
/// 这里把它映射到 Arco 的图标组件。后端因此不必知道前端有哪些组件，
/// 前端也不必为每个菜单写一段 v-if。
///
/// 这张表是**唯一**的图标出处：App.vue 的导航与菜单管理页的图标预览都走它，
/// 两处对同一个名字给出同一个组件，不会出现"导航里是这个图、预览里是另一个"。
export const iconRegistry: Record<string, Component> = {
  IconApps,
  IconBook,
  IconCommon,
  IconDashboard,
  IconFilter,
  IconHistory,
  IconHome,
  IconList,
  IconMenu,
  IconNotification,
  IconRelation,
  IconScan,
  IconSettings,
  IconStorage,
  IconTool,
  IconUser,
  IconUserGroup,
}

/// 中性兜底图标。**不能回退到"什么都不画"**：导航项缺了图标会明显比别的短一截、
/// 看起来像渲染失败；一个中性的通用图标至少说明"这一项这里就该有个图标"。
const fallbackIcon: Component = IconApps

/// 解析图标名。未识别（含 null / 空串）一律回退到中性图标——**不静默丢弃**。
export function resolveIcon(name: string | null | undefined): Component {
  return (name === null || name === undefined ? undefined : iconRegistry[name]) ?? fallbackIcon
}

/// 这个名字能不能被解析到。菜单管理页的图标预览据此给出"已回退到默认图标"的提示——
/// 使用者必须知道"我填的名字其实没生效"，否则会以为图标是故意那样的。
export function isKnownIconName(name: string | null | undefined): boolean {
  return name !== null && name !== undefined && iconRegistry[name] !== undefined
}
