import { fileURLToPath, URL } from 'node:url'

import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5273,
    // 开发期把 /api 反代到后端，避免前端承担 CORS 配置
    proxy: {
      '/api': {
        target: 'http://localhost:5274',
        changeOrigin: true,
      },
      // SignalR 需要 WebSocket 升级
      '/hubs': {
        target: 'http://localhost:5274',
        changeOrigin: true,
        ws: true,
      },
    },
  },
})
