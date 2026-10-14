import ArcoVue from '@arco-design/web-vue'
import '@arco-design/web-vue/dist/arco.css'
import { createApp } from 'vue'

import App from './App.vue'
import { bindUnauthorizedHandler, useSession } from './composables/useSession'
import { router } from './router'

const { loaded } = useSession()

// 任何请求收到 401（会话过期、账号被删）都回到登录页。
// 挂在这里而不是 client.ts 内部：请求层不该知道路由存在，而"401 之后去哪儿"是界面的事。
bindUnauthorizedHandler(() => {
  // **首个导航还没落定时不在这里做决定**：守卫正在问 /me，那一刻 currentRoute 仍是初始值，
  // 判不出目标页是不是公开页。而登录 / 注册 / MFA 在未登录时本来就会收到 401——
  // 由这里插一脚就会把人从注册页弹回登录页，看上去像注册入口坏了。
  // 首次由守卫自己判（它知道目标），之后的 401 才是真的"掉线"。
  if (!loaded.value) {
    return
  }

  if (router.currentRoute.value.meta.public === true) {
    return
  }

  void router.push({ name: 'login' })
})

createApp(App).use(ArcoVue).use(router).mount('#app')
