<script setup lang="ts">
import { computed } from 'vue'
import { marked } from 'marked'
import DOMPurify from 'dompurify'
import type { ChatMessage } from '../types'

const props = defineProps<{ message: ChatMessage }>()

marked.setOptions({ breaks: true, gfm: true })

const renderedContent = computed(() => {
  return DOMPurify.sanitize(marked.parse(props.message.content) as string)
})

function roleColor(role: string) {
  switch (role) {
    case 'user': return 'var(--accent-blue)'
    case 'agent': return 'var(--accent-green)'
    case 'evaluator': return 'var(--accent-amber)'
    case 'system': return 'var(--text-secondary)'
    default: return 'var(--text-primary)'
  }
}

function roleIcon(role: string) {
  switch (role) {
    case 'user': return 'pi pi-user'
    case 'agent': return 'pi pi-android'
    case 'evaluator': return 'pi pi-chart-bar'
    case 'system': return 'pi pi-info-circle'
    default: return 'pi pi-circle'
  }
}
</script>

<template>
  <div :class="['chat-bubble', message.role]">
    <div class="bubble-header">
      <i :class="roleIcon(message.role)" :style="{ color: roleColor(message.role) }"></i>
      <span class="bubble-role" :style="{ color: roleColor(message.role) }">
        {{ message.agentName || message.role }}
      </span>
      <span class="bubble-time">{{ new Date(message.timestamp).toLocaleTimeString() }}</span>
    </div>
    <div class="bubble-content markdown-body" v-html="renderedContent"></div>
  </div>
</template>

<style scoped>
.chat-bubble {
  padding: 0.75rem 1rem;
  border-radius: 12px;
  margin-bottom: 0.75rem;
  max-width: 85%;
  background: var(--bg-glass);
  border: 1px solid var(--border-glass);
}

.chat-bubble.user {
  margin-left: auto;
  background: rgba(59, 130, 246, 0.08);
  border-color: rgba(59, 130, 246, 0.15);
}

.chat-bubble.evaluator {
  background: rgba(245, 158, 11, 0.06);
  border-color: rgba(245, 158, 11, 0.12);
}

.bubble-header {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  margin-bottom: 0.4rem;
  font-size: 0.75rem;
}

.bubble-role {
  font-weight: 600;
  text-transform: capitalize;
}

.bubble-time {
  margin-left: auto;
  color: var(--text-secondary);
  font-size: 0.65rem;
}

.bubble-content {
  font-size: 0.85rem;
  line-height: 1.5;
  word-break: break-word;
}

.bubble-content :deep(p) {
  margin-bottom: 0.5rem;
}
.bubble-content :deep(p:last-child) {
  margin-bottom: 0;
}
.bubble-content :deep(pre) {
  background: rgba(0, 0, 0, 0.3);
  border-radius: 6px;
  padding: 0.75rem;
  overflow-x: auto;
  margin: 0.5rem 0;
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.8rem;
}
.bubble-content :deep(code) {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.8rem;
}
.bubble-content :deep(:not(pre) > code) {
  background: rgba(255, 255, 255, 0.08);
  padding: 0.15rem 0.35rem;
  border-radius: 4px;
}
.bubble-content :deep(ul), .bubble-content :deep(ol) {
  padding-left: 1.25rem;
  margin: 0.4rem 0;
}
.bubble-content :deep(h1), .bubble-content :deep(h2), .bubble-content :deep(h3) {
  margin: 0.75rem 0 0.35rem;
  font-weight: 600;
}
.bubble-content :deep(h1) { font-size: 1.1rem; }
.bubble-content :deep(h2) { font-size: 1rem; }
.bubble-content :deep(h3) { font-size: 0.9rem; }
.bubble-content :deep(blockquote) {
  border-left: 3px solid var(--accent-blue);
  padding-left: 0.75rem;
  margin: 0.5rem 0;
  color: var(--text-secondary);
}
.bubble-content :deep(table) {
  border-collapse: collapse;
  margin: 0.5rem 0;
  font-size: 0.8rem;
}
.bubble-content :deep(th), .bubble-content :deep(td) {
  border: 1px solid var(--border-glass);
  padding: 0.35rem 0.6rem;
}
.bubble-content :deep(th) {
  background: rgba(255, 255, 255, 0.05);
}
</style>
