<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useOrchestrator } from '../composables/useOrchestrator'
import Button from 'primevue/button'
import Textarea from 'primevue/textarea'
import type { PromptConfig } from '../types'

const { getPromptConfig, savePromptConfig } = useOrchestrator()

const config = ref<PromptConfig>({
  drivingSystemPrompt: '',
  agentPromptOverrides: {},
  evaluatorPrompt: '',
})
const saving = ref(false)
const saved = ref(false)

const agentNames = ['agent-sonnet', 'agent-codex', 'agent-gpt54']

onMounted(async () => {
  try {
    config.value = await getPromptConfig()
    // Ensure overrides exist for all agents
    for (const name of agentNames) {
      if (!config.value.agentPromptOverrides[name]) {
        config.value.agentPromptOverrides[name] = ''
      }
    }
  } catch (e) {
    console.error('Failed to load config:', e)
  }
})

async function handleSave() {
  saving.value = true
  saved.value = false
  try {
    await savePromptConfig(config.value)
    saved.value = true
    setTimeout(() => { saved.value = false }, 2000)
  } catch (e) {
    console.error('Failed to save:', e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="settings-page">
    <div class="settings-header">
      <h1><i class="pi pi-cog"></i> Settings</h1>
      <Button
        :label="saved ? 'Saved ✓' : 'Save All'"
        :icon="saved ? '' : 'pi pi-save'"
        :loading="saving"
        :severity="saved ? 'success' : 'info'"
        @click="handleSave"
      />
    </div>

    <div class="settings-grid">
      <!-- Driving System Prompt -->
      <section class="setting-card glass-card">
        <h2>Driving System Prompt</h2>
        <p class="setting-desc">The main system prompt sent to all agents. This shapes the overall behaviour.</p>
        <Textarea
          v-model="config.drivingSystemPrompt"
          rows="8"
          class="prompt-editor mono"
          autoResize
        />
      </section>

      <!-- Evaluator Prompt -->
      <section class="setting-card glass-card">
        <h2>Evaluator Prompt</h2>
        <p class="setting-desc">Instructions for the evaluator agent that selects the best response.</p>
        <Textarea
          v-model="config.evaluatorPrompt"
          rows="10"
          class="prompt-editor mono"
          autoResize
        />
      </section>

      <!-- Per-Agent Overrides -->
      <section class="setting-card glass-card full-width">
        <h2>Agent Prompt Overrides</h2>
        <p class="setting-desc">Override the system prompt for individual agents. Leave blank to use the driving prompt.</p>
        <div class="agent-overrides">
          <div v-for="name in agentNames" :key="name" class="agent-override">
            <label class="override-label">
              <span class="override-name">{{ name }}</span>
            </label>
            <Textarea
              v-model="config.agentPromptOverrides[name]"
              rows="4"
              :placeholder="`Uses driving system prompt if empty`"
              class="prompt-editor mono"
              autoResize
            />
          </div>
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.settings-page {
  height: 100%;
  overflow-y: auto;
  padding: 2rem;
}

.settings-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 2rem;
}

.settings-header h1 {
  font-size: 1.5rem;
  font-weight: 600;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.settings-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 1.5rem;
}

.setting-card {
  padding: 1.5rem;
}

.setting-card.full-width {
  grid-column: 1 / -1;
}

.setting-card h2 {
  font-size: 1rem;
  font-weight: 600;
  margin-bottom: 0.25rem;
}

.setting-desc {
  font-size: 0.8rem;
  color: var(--text-secondary);
  margin-bottom: 1rem;
}

.prompt-editor {
  width: 100%;
  background: var(--bg-secondary) !important;
  border-color: var(--border-glass) !important;
  color: var(--text-primary) !important;
  font-size: 0.85rem;
  border-radius: 8px;
}

.agent-overrides {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 1rem;
}

.override-label {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-bottom: 0.5rem;
}

.override-name {
  font-weight: 500;
  font-size: 0.85rem;
  color: var(--accent-blue);
}
</style>
