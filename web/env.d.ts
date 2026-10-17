/// <reference types="vite/client" />

interface ImportMetaEnv {
  /// 公开页头部是否显示「登录 / 注册」入口。未设置时为显示，设为 false 或 0 时隐藏。
  readonly VITE_SHOW_AUTH_LINKS?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}

declare module '*.vue' {
  import type { DefineComponent } from 'vue'

  const component: DefineComponent<Record<string, unknown>, Record<string, unknown>, unknown>

  export default component
}
