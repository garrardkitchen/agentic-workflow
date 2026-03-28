import { createApp } from 'vue'
import { createRouter, createWebHistory } from 'vue-router'
import PrimeVue from 'primevue/config'
import Aura from '@primevue/themes/aura'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import Tooltip from 'primevue/tooltip'
import 'primeicons/primeicons.css'
import './style.css'
import App from './App.vue'
import { applyCodeTheme } from './utils/codeTheme'

const savedCodeTheme = localStorage.getItem('codeTheme') ?? 'github-dark'
void applyCodeTheme(savedCodeTheme)

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'chat', component: () => import('./views/ChatView.vue') },
    { path: '/settings', name: 'settings', component: () => import('./views/SettingsView.vue') },
  ]
})

const app = createApp(App)
app.use(router)
app.use(PrimeVue, {
  theme: {
    preset: Aura,
    options: {
      darkModeSelector: '.dark-mode',
      cssLayer: false
    }
  }
})
app.use(ToastService)
app.use(ConfirmationService)
app.directive('tooltip', Tooltip)
app.mount('#app')
