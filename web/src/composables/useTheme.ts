import { ref, watch } from 'vue'

/// 界面主题。
///
/// 切换的手段就是 Arco 自己的那一个约定：给 `<body>` 挂 `arco-theme="dark"`。
/// 全应用的配色都取 Arco 的令牌（`--color-text-*`、`--color-bg-*`、`rgb(var(--primary-6))`…），
/// 挂上属性整页跟着换——**不维护第二套色板**，那才是会随改动漂掉的东西。
export type Theme = 'light' | 'dark'

/// localStorage 的键。**index.html 里的预涂脚本用同一个键**（那边的注释指回这里）：
/// 两处必须一起改，否则会出现"明明存过主题，下次进来仍闪一下浅色"。
const StorageKey = 'poolpilot-theme'

/// 首选项：存过的用它；没存过跟系统走（`prefers-color-scheme`）。
///
/// 只在启动时读一次：**不跟系统实时联动**——使用者在页面上手动切过之后，
/// 系统再变也不该把他的选择顶掉。
function initialTheme(): Theme {
  try {
    const saved = localStorage.getItem(StorageKey)

    if (saved === 'light' || saved === 'dark') {
      return saved
    }
  } catch {
    // 无痕模式等场景下 localStorage 会抛：主题是外观问题，不值得让它挡住应用启动
  }

  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
}

const theme = ref<Theme>(initialTheme())

/// 写到 DOM 上。浅色时**移除属性**而不是写 "light"：Arco 只认 dark 那一条规则，
/// 留一个 `arco-theme="light"` 在 DOM 里等于摆了一个不存在的开关。
function apply(value: Theme): void {
  if (value === 'dark') {
    document.body.setAttribute('arco-theme', 'dark')
  } else {
    document.body.removeAttribute('arco-theme')
  }
}

// 导入即生效：App.vue 引入这个模块时把当前主题落到 DOM 上，与 index.html 的预涂是同值幂等的
apply(theme.value)

watch(theme, (value) => {
  apply(value)

  try {
    localStorage.setItem(StorageKey, value)
  } catch {
    // 存不下就只是下次不记得，本次照常生效
  }
})

export function useTheme() {
  return {
    theme,
    toggle: (): void => {
      theme.value = theme.value === 'dark' ? 'light' : 'dark'
    },
  }
}
