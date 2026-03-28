<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useOrchestrator } from '../composables/useOrchestrator'
import Button from 'primevue/button'
import Textarea from 'primevue/textarea'
import Select from 'primevue/select'
import Tabs from 'primevue/tabs'
import TabList from 'primevue/tablist'
import Tab from 'primevue/tab'
import TabPanels from 'primevue/tabpanels'
import TabPanel from 'primevue/tabpanel'
import type { PromptConfig } from '../types'
import { applyCodeTheme, CODE_THEMES, normalizeCodeTheme } from '../utils/codeTheme'

const { getPromptConfig, savePromptConfig } = useOrchestrator()

const config = ref<PromptConfig>({
  drivingSystemPrompt: '',
  agentPromptOverrides: {},
  evaluatorPrompt: '',
  codeTheme: 'github-dark',
})
const saving = ref(false)
const saved = ref(false)

const agents = [
  { key: 'agent-sonnet', label: 'Sonnet 4.6', model: 'claude-sonnet-4.6', icon: 'pi pi-bolt', color: 'var(--accent-purple)' },
  { key: 'agent-codex', label: 'GPT Codex', model: 'gpt-5.3-codex', icon: 'pi pi-code', color: 'var(--accent-blue)' },
  { key: 'agent-gpt54', label: 'GPT 5.4', model: 'gpt-5.4', icon: 'pi pi-star', color: 'var(--accent-amber)' },
]

const activeAgentTab = ref(agents[0].key)
const codeThemeOptions = CODE_THEMES.map(t => ({ label: t, value: t }))

onMounted(async () => {
  try {
    config.value = await getPromptConfig()
    for (const a of agents) {
      if (!config.value.agentPromptOverrides[a.key]) {
        config.value.agentPromptOverrides[a.key] = ''
      }
    }
    config.value.codeTheme = normalizeCodeTheme(config.value.codeTheme)
    await applyCodeTheme(config.value.codeTheme)
  } catch (e) {
    console.error('Failed to load config:', e)
  }
})

async function handleSave() {
  saving.value = true
  saved.value = false
  try {
    config.value.codeTheme = normalizeCodeTheme(config.value.codeTheme)
    localStorage.setItem('codeTheme', config.value.codeTheme)
    await applyCodeTheme(config.value.codeTheme)
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
    <!-- Header -->
    <div class="settings-header">
      <div class="header-left">
        <i class="pi pi-cog header-icon"></i>
        <div>
          <h1>Settings</h1>
          <p class="header-subtitle">Configure system prompts and agent behaviour</p>
        </div>
      </div>
      <Button
        :label="saved ? 'Saved ✓' : 'Save All'"
        :icon="saved ? '' : 'pi pi-save'"
        :loading="saving"
        :severity="saved ? 'success' : undefined"
        @click="handleSave"
        class="save-btn"
      />
    </div>

    <div class="settings-body">
      <!-- Left column: Global prompts -->
      <div class="settings-col">
        <!-- Driving System Prompt -->
        <section class="setting-card glass-card">
          <div class="card-header">
            <div class="card-icon-wrap" style="--card-accent: var(--accent-blue)">
              <i class="pi pi-microchip"></i>
            </div>
            <div>
              <h2>Driving System Prompt</h2>
              <p class="setting-desc">The main system prompt sent to all agents. This shapes overall behaviour.</p>
            </div>
          </div>
          <Textarea
            v-model="config.drivingSystemPrompt"
            rows="10"
            class="prompt-editor mono"
            autoResize
          />
        </section>

        <!-- Evaluator Prompt -->
        <section class="setting-card glass-card">
          <div class="card-header">
            <div class="card-icon-wrap" style="--card-accent: var(--accent-amber)">
              <i class="pi pi-trophy"></i>
            </div>
            <div>
              <h2>Evaluator Prompt</h2>
              <p class="setting-desc">Instructions for the evaluator that selects the best response from all agents.</p>
            </div>
          </div>
          <Textarea
            v-model="config.evaluatorPrompt"
            rows="12"
            class="prompt-editor mono"
            autoResize
          />
        </section>

        <section class="setting-card glass-card">
          <div class="card-header">
            <div class="card-icon-wrap" style="--card-accent: var(--accent-purple)">
              <i class="pi pi-palette"></i>
            </div>
            <div>
              <h2>Code Block Theme</h2>
              <p class="setting-desc">Choose syntax highlighting style for markdown/code output.</p>
            </div>
          </div>
          <Select
            v-model="config.codeTheme"
            :options="codeThemeOptions"
            optionLabel="label"
            optionValue="value"
            class="theme-select"
            placeholder="Select theme"
          />
        </section>
      </div>

      <!-- Right column: Per-Agent Overrides as Tabs -->
      <div class="settings-col">
        <section class="setting-card glass-card">
          <div class="card-header">
            <div class="card-icon-wrap" style="--card-accent: var(--accent-green)">
              <i class="pi pi-users"></i>
            </div>
            <div>
              <h2>Agent Prompt Overrides</h2>
              <p class="setting-desc">Override the system prompt per agent. Leave blank to inherit the driving prompt.</p>
            </div>
          </div>

          <Tabs v-model:value="activeAgentTab" class="agent-tabs">
            <TabList>
              <Tab v-for="a in agents" :key="a.key" :value="a.key">
                <div class="agent-tab-label">
                  <i :class="a.icon" :style="{ color: a.color }"></i>
                  <span>{{ a.label }}</span>
                  <span class="model-badge" :style="{ borderColor: a.color, color: a.color }">{{ a.model }}</span>
                </div>
              </Tab>
            </TabList>
            <TabPanels>
              <TabPanel v-for="a in agents" :key="a.key" :value="a.key">
                <div class="tab-panel-inner">
                  <div class="agent-meta">
                    <span class="agent-meta-dot" :style="{ background: a.color }"></span>
                    <span class="agent-meta-text">Model: <strong>{{ a.model }}</strong></span>
                    <span class="agent-meta-text">Key: <code>{{ a.key }}</code></span>
                  </div>
                  <Textarea
                    v-model="config.agentPromptOverrides[a.key]"
                    rows="14"
                    :placeholder="`Leave empty to use the driving system prompt for ${a.label}`"
                    class="prompt-editor mono"
                    autoResize
                  />
                  <div v-if="!config.agentPromptOverrides[a.key]" class="inherit-hint">
                    <i class="pi pi-info-circle"></i>
                    Currently inheriting the driving system prompt
                  </div>
                </div>
              </TabPanel>
            </TabPanels>
          </Tabs>
        </section>
      </div>
    </div>
  </div>
</template>

<style scoped>
.settings-page {
  height: 100%;
  overflow-y: auto;
  padding: 2rem 2.5rem;
}

/* Header */
.settings-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 2rem;
  padding-bottom: 1.5rem;
  border-bottom: 1px solid var(--border-glass);
}
.header-left {
  display: flex;
  align-items: center;
  gap: 1rem;
}
.header-icon {
  font-size: 1.8rem;
  color: var(--text-secondary);
  opacity: 0.6;
}
.settings-header h1 {
  font-size: 1.4rem;
  font-weight: 700;
  margin-bottom: 0.15rem;
}
.header-subtitle {
  font-size: 0.8rem;
  color: var(--text-secondary);
}
.save-btn {
  min-width: 120px;
}

/* Body layout */
.settings-body {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 1.5rem;
  align-items: start;
}
.settings-col {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

/* Setting cards */
.setting-card {
  padding: 1.5rem;
}
.card-header {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  margin-bottom: 1rem;
}
.card-icon-wrap {
  width: 36px;
  height: 36px;
  border-radius: 10px;
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  background: rgba(100, 100, 100, 0.12);
  background: color-mix(in srgb, var(--card-accent) 12%, transparent);
  color: var(--card-accent);
  font-size: 1rem;
}
.setting-card h2 {
  font-size: 0.95rem;
  font-weight: 600;
  margin-bottom: 0.2rem;
}
.setting-desc {
  font-size: 0.78rem;
  color: var(--text-secondary);
  line-height: 1.4;
}

/* Prompt editor */
.prompt-editor {
  width: 100%;
  background: var(--bg-secondary) !important;
  border-color: var(--border-glass) !important;
  color: var(--text-primary) !important;
  font-size: 0.82rem;
  border-radius: 8px;
  line-height: 1.6;
  transition: border-color 0.2s;
}
.prompt-editor:focus {
  border-color: var(--accent-blue) !important;
  box-shadow: 0 0 0 2px rgba(59, 130, 246, 0.15) !important;
}

.theme-select {
  width: 100%;
}

/* Agent Tabs — override PrimeVue theme */
.agent-tabs {
  margin-top: 0.25rem;
}
.agent-tabs :deep(.p-tablist) {
  background: transparent;
  border: none;
}
.agent-tabs :deep(.p-tablist-tab-list) {
  background: transparent !important;
  border: none !important;
  border-bottom: 1px solid var(--border-glass) !important;
  gap: 0;
}
.agent-tabs :deep(.p-tab) {
  background: transparent !important;
  border: none !important;
  border-bottom: 2px solid transparent !important;
  color: var(--text-secondary) !important;
  padding: 0.6rem 1rem;
  font-size: 0.82rem;
  transition: color 0.2s, border-color 0.2s;
}
.agent-tabs :deep(.p-tab:hover) {
  color: var(--text-primary) !important;
  background: rgba(255, 255, 255, 0.02) !important;
}
.agent-tabs :deep(.p-tab-active) {
  color: var(--text-primary) !important;
  border-bottom-color: var(--accent-blue) !important;
}
.agent-tabs :deep(.p-tablist-active-bar) {
  background: var(--accent-blue) !important;
}
.agent-tabs :deep(.p-tabpanels) {
  background: transparent !important;
  padding: 0;
}
.agent-tabs :deep(.p-tabpanel) {
  background: transparent !important;
  color: var(--text-primary) !important;
  padding: 1rem 0 0;
}

/* Tab label */
.agent-tab-label {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  white-space: nowrap;
}
.model-badge {
  font-size: 0.6rem;
  font-weight: 600;
  font-family: 'JetBrains Mono', monospace;
  padding: 0.1rem 0.35rem;
  border: 1px solid;
  border-radius: 4px;
  letter-spacing: 0.02em;
}

/* Tab panel inner */
.tab-panel-inner {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}
.agent-meta {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  padding: 0.5rem 0.75rem;
  background: rgba(255, 255, 255, 0.02);
  border-radius: 6px;
  border: 1px solid var(--border-glass);
}
.agent-meta-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex-shrink: 0;
}
.agent-meta-text {
  font-size: 0.72rem;
  color: var(--text-secondary);
}
.agent-meta-text strong {
  color: var(--text-primary);
}
.agent-meta-text code {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.68rem;
  background: rgba(255, 255, 255, 0.06);
  padding: 0.1rem 0.3rem;
  border-radius: 3px;
}
.inherit-hint {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  font-size: 0.72rem;
  color: var(--accent-blue);
  opacity: 0.7;
  padding: 0.4rem 0;
}

/* Responsive: stack columns on narrow viewports */
@media (max-width: 960px) {
  .settings-body {
    grid-template-columns: 1fr;
  }
  .settings-page {
    padding: 1.5rem 1rem;
  }
}
</style>
