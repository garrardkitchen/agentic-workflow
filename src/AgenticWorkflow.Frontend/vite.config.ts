import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    port: parseInt(process.env.PORT || '5173'),
    proxy: {
      '/api': {
        target: process.env.services__gateway__https__0 || process.env.services__gateway__http__0 || 'http://localhost:5100',
        changeOrigin: true,
        secure: false,
      }
    }
  }
})
