<script setup lang="ts">
import { computed } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { useOrchestrator } from './composables/useOrchestrator'
import Button from 'primevue/button'

const router = useRouter()
const route = useRoute()
const currentRoute = computed(() => route.name === 'settings' ? 'settings' : 'chat')
const { resetState } = useOrchestrator()

function navigate(routeName: string) {
  router.push(routeName === 'chat' ? '/' : '/settings')
}

function handleNewConversation() {
  resetState()
  navigate('chat')
  // Emit a custom event so ChatView can clear + focus input
  window.dispatchEvent(new CustomEvent('new-conversation'))
}
</script>

<template>
  <div class="app-shell dark-mode">
    <nav class="app-nav glass-card-sm">
      <div class="nav-brand">
        <i class="pi pi-bolt" style="color: var(--accent-blue); font-size: 1.2rem"></i>
        <span class="nav-title">Agentic Workflow</span>
      </div>
      <div class="nav-links">
        <Button
          icon="pi pi-plus"
          label="New Chat"
          severity="info"
          size="small"
          outlined
          @click="handleNewConversation"
          class="new-chat-btn"
        />
        <button
          :class="['nav-link', { active: currentRoute === 'chat' }]"
          @click="navigate('chat')"
        >
          <i class="pi pi-comments"></i>
          <span>Chat</span>
        </button>
        <button
          :class="['nav-link', { active: currentRoute === 'settings' }]"
          @click="navigate('settings')"
        >
          <i class="pi pi-cog"></i>
          <span>Settings</span>
        </button>
      </div>
    </nav>
    <main class="app-main">
      <router-view v-slot="{ Component }">
        <transition name="fade" mode="out-in">
          <component :is="Component" />
        </transition>
      </router-view>
    </main>
  </div>
</template>

<style scoped>
.app-shell {
  display: flex;
  flex-direction: column;
  height: 100vh;
  overflow: hidden;
}

.app-nav {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0.75rem 1.5rem;
  border-radius: 0;
  border-bottom: 1px solid var(--border-glass);
  background: rgba(10, 10, 15, 0.8);
  backdrop-filter: blur(20px);
  z-index: 100;
}

.nav-brand {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.nav-title {
  font-weight: 600;
  font-size: 1rem;
  letter-spacing: -0.02em;
}

.nav-links {
  display: flex;
  gap: 0.25rem;
}

.nav-link {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.5rem 1rem;
  border: none;
  background: transparent;
  color: var(--text-secondary);
  font-family: inherit;
  font-size: 0.875rem;
  border-radius: 8px;
  cursor: pointer;
  transition: all 0.2s;
}

.nav-link:hover {
  background: var(--bg-glass);
  color: var(--text-primary);
}

.nav-link.active {
  background: rgba(59, 130, 246, 0.1);
  color: var(--accent-blue);
}

.new-chat-btn {
  margin-right: 0.5rem;
}

.app-main {
  flex: 1;
  overflow: hidden;
}

.fade-enter-active, .fade-leave-active {
  transition: opacity 0.2s ease;
}
.fade-enter-from, .fade-leave-to {
  opacity: 0;
}
</style>
