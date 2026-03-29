<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick, watch } from 'vue'
import { useOrchestrator } from '../composables/useOrchestrator'
import OrchestratorVisualizer from '../components/OrchestratorVisualizer.vue'
import ChatBubble from '../components/ChatBubble.vue'
import { renderMarkdown } from '../utils/markdown'
import type { UserQuestion } from '../types'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Tabs from 'primevue/tabs'
import TabList from 'primevue/tablist'
import Tab from 'primevue/tab'
import Accordion from 'primevue/accordion'
import AccordionPanel from 'primevue/accordionpanel'
import AccordionHeader from 'primevue/accordionheader'
import AccordionContent from 'primevue/accordioncontent'

const {
  currentSession, orchestratorState, agents, isProcessing, error, acceptedResponse,
  excludedAgents, includeAgent, includeAllAgents, submitPrompt, submitDecision, submitQuestionAnswer, recoverSession, loadSessions, sessions, loadSession, resetState,
} = useOrchestrator()

const promptInput = ref('')
const promptInputEl = ref<HTMLElement>()
const chatMainEl = ref<HTMLElement>()
const chatContainer = ref<HTMLElement>()
const expandedAgents = ref<Set<string>>(new Set())
const copySuccess = ref(false)
const activePanel = ref<string | null>('orchestration')
const questionAnswerTextById = ref<Record<string, string>>({})
const questionChoiceById = ref<Record<string, string>>({})
const questionChoicesById = ref<Record<string, string[]>>({})
const submittingQuestionIds = ref<Set<string>>(new Set())
const activeAgentTab = ref('')
const isAcceptedResponseExpanded = ref(false)
const isResizingPanels = ref(false)
const rightPanelWidth = ref(480)

function isQuestionSubmitting(questionId: string): boolean {
  return submittingQuestionIds.value.has(questionId)
}

function setQuestionAnswerText(questionId: string, value: string) {
  questionAnswerTextById.value = {
    ...questionAnswerTextById.value,
    [questionId]: value,
  }
}

function setQuestionChoice(questionId: string, choice: string) {
  questionChoiceById.value = {
    ...questionChoiceById.value,
    [questionId]: choice,
  }
}

function toggleQuestionChoice(questionId: string, choice: string) {
  const existing = new Set(questionChoicesById.value[questionId] ?? [])
  if (existing.has(choice)) existing.delete(choice)
  else existing.add(choice)
  questionChoicesById.value = {
    ...questionChoicesById.value,
    [questionId]: Array.from(existing),
  }
}

function clearQuestionDraftState(questionId: string) {
  const nextText = { ...questionAnswerTextById.value }
  const nextSingle = { ...questionChoiceById.value }
  const nextMulti = { ...questionChoicesById.value }
  delete nextText[questionId]
  delete nextSingle[questionId]
  delete nextMulti[questionId]
  questionAnswerTextById.value = nextText
  questionChoiceById.value = nextSingle
  questionChoicesById.value = nextMulti
}

function canSubmitQuestion(question: UserQuestion): boolean {
  if (question.inputType === 'SingleChoice') return !!questionChoiceById.value[question.questionId]
  if (question.inputType === 'MultiChoice') return (questionChoicesById.value[question.questionId]?.length ?? 0) > 0
  return !!questionAnswerTextById.value[question.questionId]?.trim()
}

function isBinaryFreeTextQuestion(question: UserQuestion): boolean {
  if (question.inputType !== 'FreeText') return false
  const prompt = question.prompt.trim().toLowerCase()
  if (/yes\s*\/\s*no|yes or no|\(y\/n\)|\by\/n\b/.test(prompt)) return true
  if (!prompt.endsWith('?')) return false
  return /^(would you like( me)? to|do you want( me)? to|should i|shall i|can i|may i|is it okay if i|is this okay|does this look good|should we proceed|are you sure)\b/.test(prompt)
}

function questionInputLabel(question: UserQuestion): string {
  if (isBinaryFreeTextQuestion(question)) return 'Yes/No'
  return question.inputType
}

function getQuestionAgentName(question: UserQuestion): string {
  return question.sourceName && agents.some(a => a.name === question.sourceName) ? question.sourceName : ''
}

const latestPendingQuestionByAgent = computed(() => {
  const latest = new Map<string, UserQuestion>()
  for (const question of currentSession.value?.pendingQuestions ?? []) {
    const agentName = getQuestionAgentName(question)
    if (!agentName) continue
    const existing = latest.get(agentName)
    if (!existing || new Date(question.timestamp).getTime() >= new Date(existing.timestamp).getTime()) {
      latest.set(agentName, question)
    }
  }
  return latest
})

const activeTabQuestion = computed(() => {
  if (!activeAgentTab.value) return undefined
  return latestPendingQuestionByAgent.value.get(activeAgentTab.value)
})

const activeTabIsBinaryQuestion = computed(() => {
  if (!activeTabQuestion.value) return false
  return isBinaryFreeTextQuestion(activeTabQuestion.value)
})

function agentTabState(agentName: string): 'awaiting' | 'ready' | 'running' | 'failed' | 'idle' {
  if (latestPendingQuestionByAgent.value.has(agentName)) return 'awaiting'
  const agentNode = agents.find(a => a.name === agentName)
  if (!agentNode) return 'idle'
  if (agentNode.status === 'winner' || agentNode.status === 'complete') return 'ready'
  if (agentNode.status === 'running') return 'running'
  if (agentNode.status === 'failed') return 'failed'
  return 'idle'
}

function agentTabLabel(agentName: string): string {
  const state = agentTabState(agentName)
  if (state === 'awaiting') return 'Awaiting input'
  if (state === 'ready') return 'Ready'
  if (state === 'running') return 'Running'
  if (state === 'failed') return 'Failed'
  return 'Idle'
}

const activeAgentMessages = computed(() => {
  if (!currentSession.value || !activeAgentTab.value) return []
  return currentSession.value.chatHistory.filter(message => {
    if (message.role === 'user' || message.role === 'system') return true
    if (message.role === 'agent' || message.role === 'question' || message.role === 'answer') {
      return message.agentName === activeAgentTab.value
    }
    return false
  })
})

// Auto-switch accordion panel based on orchestration state
watch(() => orchestratorState.value, (newState) => {
  if (newState === 'fan-out' || newState === 'evaluating') {
    activePanel.value = 'orchestration'
  } else if (newState === 'awaiting-input') {
    activePanel.value = 'orchestration'
  } else if (newState === 'awaiting-approval') {
    activePanel.value = 'evaluator'
  } else if (newState === 'accepted') {
    activePanel.value = 'accepted'
  }
})

watch(() => agents.map(a => a.name), (names) => {
  if (names.length === 0) {
    activeAgentTab.value = ''
    return
  }
  if (!activeAgentTab.value || !names.includes(activeAgentTab.value)) {
    activeAgentTab.value = names[0]
  }
}, { immediate: true })

onMounted(() => {
  loadSessions()
  window.addEventListener('new-conversation', handleNewConversation)
})

onUnmounted(() => {
  window.removeEventListener('new-conversation', handleNewConversation)
  window.removeEventListener('mousemove', handleResizeMove)
  window.removeEventListener('mouseup', stopResizePanels)
  document.body.style.removeProperty('cursor')
  document.body.style.removeProperty('user-select')
})

function handleNewConversation() {
  promptInput.value = ''
  expandedAgents.value = new Set()
  nextTick(() => {
    const input = promptInputEl.value?.querySelector('input')
    input?.focus()
  })
}

watch(() => currentSession.value?.chatHistory.length, () => {
  nextTick(() => {
    if (chatContainer.value) {
      chatContainer.value.scrollTop = chatContainer.value.scrollHeight
    }
  })
})

const promptHistory = ref<string[]>(JSON.parse(localStorage.getItem('promptHistory') || '[]'))
let historyIndex = -1
let savedInput = ''

async function handleSubmit() {
  if (!promptInput.value.trim() || isProcessing.value) return
  const prompt = promptInput.value.trim()

  // Save to history (deduplicate, most recent first, cap at 50)
  const filtered = promptHistory.value.filter(p => p !== prompt)
  filtered.unshift(prompt)
  if (filtered.length > 50) filtered.length = 50
  promptHistory.value = filtered
  localStorage.setItem('promptHistory', JSON.stringify(filtered))
  historyIndex = -1

  promptInput.value = ''
  resetState()
  await submitPrompt(prompt)
}

async function handleDecision(d: 'accept' | 'decline' | 'restart') {
  if (d === 'decline' && submittingQuestionIds.value.size > 0) return
  if (!currentSession.value) return
  await submitDecision(currentSession.value.id, d)
  if (d === 'decline') {
    questionAnswerTextById.value = {}
    questionChoiceById.value = {}
    questionChoicesById.value = {}
  }
}

async function handleRecover() {
  if (!currentSession.value) return
  const sessionId = currentSession.value.id
  await recoverSession(sessionId)
  const prompt = currentSession.value.prompt
  resetState()
  await submitPrompt(prompt)
}

async function handleSubmitQuestionAnswer(question: UserQuestion) {
  if (!currentSession.value) return
  if (isQuestionSubmitting(question.questionId)) return
  if (!canSubmitQuestion(question)) return

  const selectedChoices =
    question.inputType === 'SingleChoice'
      ? [questionChoiceById.value[question.questionId]]
      : question.inputType === 'MultiChoice'
        ? (questionChoicesById.value[question.questionId] ?? [])
        : []

  const answerText = question.inputType === 'FreeText'
    ? (questionAnswerTextById.value[question.questionId] ?? '').trim()
    : ''

  submittingQuestionIds.value = new Set([...submittingQuestionIds.value, question.questionId])
  try {
    const submitted = await submitQuestionAnswer(
      currentSession.value.id,
      question.questionId,
      answerText,
      selectedChoices,
    )
    if (submitted) {
      clearQuestionDraftState(question.questionId)
    }
  } finally {
    const next = new Set(submittingQuestionIds.value)
    next.delete(question.questionId)
    submittingQuestionIds.value = next
  }
}

async function submitActiveTabQuestion() {
  if (!activeTabQuestion.value) return
  await handleSubmitQuestionAnswer(activeTabQuestion.value)
}

async function submitBinaryChoice(question: UserQuestion, choice: 'Yes' | 'No') {
  if (isQuestionSubmitting(question.questionId)) return
  setQuestionAnswerText(question.questionId, choice)
  await handleSubmitQuestionAnswer(question)
}

function handleActiveTabQuestionKeydown(e: KeyboardEvent) {
  if (!activeTabQuestion.value || activeTabQuestion.value.inputType !== 'FreeText' || activeTabIsBinaryQuestion.value) return
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault()
    submitActiveTabQuestion()
  }
}

function handleKeydown(e: KeyboardEvent) {
  if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') {
    handleSubmit()
    return
  }
  if (e.key === 'ArrowUp') {
    if (promptHistory.value.length === 0) return
    e.preventDefault()
    if (historyIndex === -1) savedInput = promptInput.value
    if (historyIndex < promptHistory.value.length - 1) {
      historyIndex++
      promptInput.value = promptHistory.value[historyIndex]
    }
  } else if (e.key === 'ArrowDown') {
    if (historyIndex < 0) return
    e.preventDefault()
    historyIndex--
    promptInput.value = historyIndex >= 0
      ? promptHistory.value[historyIndex]
      : savedInput
  }
}

function toggleAgentExpand(agentName: string) {
  const newSet = new Set(expandedAgents.value)
  if (newSet.has(agentName)) {
    newSet.delete(agentName)
  } else {
    newSet.add(agentName)
  }
  expandedAgents.value = newSet
}

function toggleAcceptedResponseExpand() {
  isAcceptedResponseExpanded.value = !isAcceptedResponseExpanded.value
}

function startResizePanels(e: MouseEvent) {
  if (isAcceptedResponseExpanded.value) return
  e.preventDefault()
  isResizingPanels.value = true
  document.body.style.cursor = 'col-resize'
  document.body.style.userSelect = 'none'
}

function handleResizeMove(e: MouseEvent) {
  if (!isResizingPanels.value) return
  const rect = chatMainEl.value?.getBoundingClientRect()
  if (!rect) return

  const minRight = 320
  const minLeft = 320
  const maxRight = Math.max(minRight, rect.width - minLeft)
  const nextWidth = rect.right - e.clientX

  rightPanelWidth.value = Math.min(maxRight, Math.max(minRight, nextWidth))
}

function stopResizePanels() {
  if (!isResizingPanels.value) return
  isResizingPanels.value = false
  document.body.style.removeProperty('cursor')
  document.body.style.removeProperty('user-select')
}

const rightPanelStyle = computed(() => {
  if (isAcceptedResponseExpanded.value) return undefined
  return {
    width: `${rightPanelWidth.value}px`,
    minWidth: `${rightPanelWidth.value}px`,
  }
})

function truncateText(text: string, maxLen: number): string {
  if (text.length <= maxLen) return text
  return text.slice(0, maxLen) + '…'
}

const acceptedResponseHtml = computed(() => {
  if (!acceptedResponse.value) return ''
  return renderMarkdown(acceptedResponse.value.responseText)
})

async function copyToClipboard() {
  if (!acceptedResponse.value) return
  try {
    await navigator.clipboard.writeText(acceptedResponse.value.responseText)
    copySuccess.value = true
    setTimeout(() => { copySuccess.value = false }, 2000)
  } catch {
    // Fallback for non-secure contexts
    const area = document.createElement('textarea')
    area.value = acceptedResponse.value.responseText
    document.body.appendChild(area)
    area.select()
    document.execCommand('copy')
    document.body.removeChild(area)
    copySuccess.value = true
    setTimeout(() => { copySuccess.value = false }, 2000)
  }
}

const sidebarOpen = ref(false)

onMounted(() => {
  window.addEventListener('mousemove', handleResizeMove)
  window.addEventListener('mouseup', stopResizePanels)
})
</script>

<template>
  <div class="chat-layout">
    <!-- Sidebar toggle -->
    <button class="sidebar-toggle" @click="sidebarOpen = !sidebarOpen">
      <i class="pi pi-history"></i>
    </button>

    <!-- Session Sidebar -->
    <aside :class="['sidebar glass-card-sm', { open: sidebarOpen }]">
      <div class="sidebar-header">
        <span>Sessions</span>
        <button class="sidebar-close" @click="sidebarOpen = false">
          <i class="pi pi-times"></i>
        </button>
      </div>
      <div class="session-list">
        <div
          v-for="s in sessions" :key="s.id"
          :class="['session-item', { active: currentSession?.id === s.id }]"
          @click="loadSession(s.id); sidebarOpen = false"
        >
          <div class="session-prompt">{{ s.prompt.slice(0, 50) }}{{ s.prompt.length > 50 ? '...' : '' }}</div>
          <div class="session-meta">
            <span :class="['status-dot', s.status.toLowerCase()]"></span>
            {{ s.status }}
          </div>
        </div>
        <div v-if="sessions.length === 0" class="no-sessions">No sessions yet</div>
      </div>
    </aside>

    <!-- Main Content -->
    <div ref="chatMainEl" :class="['chat-main', { 'accepted-expanded': isAcceptedResponseExpanded }]">
      <!-- Chat Area -->
      <div class="chat-area" ref="chatContainer">
        <div v-if="!currentSession && !isProcessing" class="empty-state">
          <i class="pi pi-bolt" style="font-size: 3rem; color: var(--accent-blue); opacity: 0.3"></i>
          <h2>Agentic Workflow</h2>
          <p>Enter a prompt to fan out to 3 AI agents and evaluate the best response.</p>
        </div>

        <Tabs v-if="currentSession && agents.length > 0" v-model:value="activeAgentTab" class="agent-chat-tabs">
          <TabList>
            <Tab
              v-for="agent in agents"
              :key="agent.name"
              :value="agent.name"
              :class="[`state-${agentTabState(agent.name)}`]"
            >
              <div class="agent-chat-tab-label">
                <span class="agent-chat-tab-name">{{ agent.name }}</span>
                <span class="agent-chat-tab-status">{{ agentTabLabel(agent.name) }}</span>
              </div>
            </Tab>
          </TabList>
        </Tabs>

        <!-- Chat Messages -->
        <template v-if="currentSession">
          <template v-for="(msg, i) in activeAgentMessages" :key="`${msg.messageId || i}-${activeAgentTab}`">
            <ChatBubble :message="msg" />
          </template>

          <div v-if="activeTabQuestion" class="agent-question-composer">
            <div class="composer-header">
              <span class="question-type">{{ questionInputLabel(activeTabQuestion) }}</span>
            </div>

            <div v-if="activeTabQuestion.inputType !== 'FreeText'" class="question-choices">
              <button
                v-for="choice in activeTabQuestion.choices ?? []"
                :key="choice"
                :class="[
                  'choice-chip',
                  {
                    selected: activeTabQuestion.inputType === 'SingleChoice'
                      ? questionChoiceById[activeTabQuestion.questionId] === choice
                      : (questionChoicesById[activeTabQuestion.questionId] ?? []).includes(choice),
                  },
                ]"
                :disabled="isQuestionSubmitting(activeTabQuestion.questionId)"
                @click="activeTabQuestion.inputType === 'SingleChoice'
                  ? setQuestionChoice(activeTabQuestion.questionId, choice)
                  : toggleQuestionChoice(activeTabQuestion.questionId, choice)"
              >{{ choice }}</button>
            </div>

            <div v-else-if="activeTabIsBinaryQuestion" class="question-choices">
              <button
                v-for="choice in ['Yes', 'No']"
                :key="choice"
                :class="['choice-chip', { selected: questionAnswerTextById[activeTabQuestion.questionId] === choice }]"
                :disabled="isQuestionSubmitting(activeTabQuestion.questionId)"
                @click="submitBinaryChoice(activeTabQuestion, choice as 'Yes' | 'No')"
              >{{ choice }}</button>
            </div>

            <InputText
              v-else
              :modelValue="questionAnswerTextById[activeTabQuestion.questionId] ?? ''"
              @update:modelValue="setQuestionAnswerText(activeTabQuestion.questionId, String($event ?? ''))"
              class="question-composer-input"
              placeholder="Type your reply and press Enter..."
              :disabled="isQuestionSubmitting(activeTabQuestion.questionId)"
              @keydown="handleActiveTabQuestionKeydown"
            />

            <div v-if="!activeTabIsBinaryQuestion" class="composer-actions">
              <Button
                icon="pi pi-send"
                :label="isQuestionSubmitting(activeTabQuestion.questionId) ? 'Sending...' : 'Send'"
                size="small"
                severity="success"
                :loading="isQuestionSubmitting(activeTabQuestion.questionId)"
                :disabled="!canSubmitQuestion(activeTabQuestion)"
                @click="submitActiveTabQuestion"
              />
            </div>
          </div>

        </template>

        <!-- Error -->
        <div v-if="error" class="error-banner">
          <i class="pi pi-exclamation-triangle"></i> {{ error }}
        </div>
      </div>

      <!-- Right Panel -->
      <div class="panel-divider" @mousedown="startResizePanels"></div>

      <div class="right-panel" :style="rightPanelStyle">
        <Accordion v-model:value="activePanel" class="right-accordion">
          <!-- Orchestration Panel -->
          <AccordionPanel value="orchestration">
            <AccordionHeader>
              <div class="accordion-header-content">
                <i class="pi pi-sitemap"></i>
                <span>Orchestration</span>
                <span :class="['viz-mini-badge', orchestratorState]">{{ orchestratorState }}</span>
              </div>
            </AccordionHeader>
            <AccordionContent>
              <OrchestratorVisualizer :agents="agents" :state="orchestratorState" />
              <div v-if="excludedAgents.size > 0" class="excluded-agents-panel">
                <div class="excluded-agents-header">
                  <span>Excluded agents (auto on failure)</span>
                  <Button label="Re-include All" size="small" severity="secondary" outlined @click="includeAllAgents" />
                </div>
                <div class="excluded-agent-list">
                  <div v-for="agent in agents.filter(a => excludedAgents.has(a.name))" :key="agent.name" class="excluded-agent-item">
                    <span class="excluded-agent-name">{{ agent.name }}</span>
                    <Button label="Re-include" size="small" @click="includeAgent(agent.name)" />
                  </div>
                </div>
              </div>
            </AccordionContent>
          </AccordionPanel>

          <!-- Evaluator Review Panel -->
          <AccordionPanel value="evaluator" :disabled="!currentSession?.evaluation">
            <AccordionHeader>
              <div class="accordion-header-content">
                <i class="pi pi-trophy" style="color: var(--accent-amber)"></i>
                <span>Evaluator</span>
                <span v-if="currentSession?.evaluation" class="accordion-header-detail">
                  Winner: {{ currentSession.evaluation.winner }}
                </span>
              </div>
            </AccordionHeader>
            <AccordionContent>
              <template v-if="currentSession?.evaluation">
                <!-- Winner's full response -->
                <div class="winner-response markdown-body" v-html="renderMarkdown(
                  currentSession?.evaluation?.allResults?.find(r => r.agentName === currentSession?.evaluation?.winner)?.responseText || 'No response'
                )"></div>

                <!-- Evaluator reasoning (collapsible) -->
                <details class="reasoning-details">
                  <summary class="reasoning-summary">
                    <i class="pi pi-info-circle" style="color: var(--accent-amber); font-size: 0.75rem"></i>
                    Why this was chosen
                  </summary>
                  <div class="evaluator-reasoning markdown-body" v-html="renderMarkdown(currentSession.evaluation.reasoning)"></div>
                </details>

                <!-- Other agent responses (collapsible) -->
                <details class="reasoning-details">
                  <summary class="reasoning-summary">
                    <i class="pi pi-users" style="color: var(--text-secondary); font-size: 0.75rem"></i>
                    Compare all {{ currentSession.evaluation.allResults?.length || 0 }} agent responses
                  </summary>
                  <div class="comparison-section">
                    <div
                      v-for="result in currentSession.evaluation.allResults"
                      :key="result.agentName"
                      :class="['comparison-item', { winner: result.agentName === currentSession.evaluation?.winner }]"
                    >
                      <div class="comparison-item-header">
                        <span class="comparison-agent-name">
                          <i class="pi pi-android" style="font-size: 0.7rem"></i>
                          {{ result.agentName }}
                        </span>
                        <span v-if="result.agentName === currentSession.evaluation?.winner" class="winner-tag">
                          <i class="pi pi-trophy"></i> Winner
                        </span>
                        <button class="expand-toggle" @click="toggleAgentExpand(result.agentName)">
                          {{ expandedAgents.has(result.agentName) ? 'Collapse' : 'Show Full' }}
                          <i :class="expandedAgents.has(result.agentName) ? 'pi pi-chevron-up' : 'pi pi-chevron-down'" style="font-size: 0.65rem"></i>
                        </button>
                      </div>
                      <div class="comparison-preview" v-if="!expandedAgents.has(result.agentName)">
                        {{ truncateText(result.responseText, 200) }}
                      </div>
                      <div class="comparison-full markdown-body" v-else v-html="renderMarkdown(result.responseText)"></div>
                    </div>
                  </div>
                </details>

                <!-- Scores Table -->
                <div v-if="currentSession.evaluation.scores" class="scores-section">
                  <div class="scores-title">
                    <i class="pi pi-chart-bar" style="font-size: 0.75rem"></i>
                    <span>Evaluation Scores</span>
                  </div>
                  <div class="score-cards">
                    <div
                      v-for="entry in Object.entries(currentSession.evaluation.scores).map(([name, score]) => ({ name, ...score })).sort((a, b) => (b.total ?? 0) - (a.total ?? 0))"
                      :key="entry.name"
                      :class="['score-card', { 'score-card-winner': entry.name === currentSession?.evaluation?.winner }]"
                    >
                      <div class="score-card-header">
                        <span class="score-agent-name">
                          <i class="pi pi-android" style="font-size: 0.65rem"></i>
                          {{ entry.name }}
                        </span>
                        <span v-if="entry.name === currentSession?.evaluation?.winner" class="score-winner-badge">
                          <i class="pi pi-trophy" style="font-size: 0.6rem"></i> Winner
                        </span>
                        <span class="score-total">{{ entry.total ?? '—' }}</span>
                      </div>
                      <div class="score-bars">
                        <div class="score-bar-row" v-for="dim in [
                          { key: 'accuracy', label: 'Accuracy', value: entry.accuracy, color: 'var(--accent-blue)' },
                          { key: 'completeness', label: 'Completeness', value: entry.completeness, color: 'var(--accent-green)' },
                          { key: 'clarity', label: 'Clarity', value: entry.clarity, color: 'var(--accent-purple)' },
                          { key: 'relevance', label: 'Relevance', value: entry.relevance, color: 'var(--accent-amber)' },
                        ]" :key="dim.key">
                          <span class="score-bar-label">{{ dim.label }}</span>
                          <div class="score-bar-track">
                            <div class="score-bar-fill" :style="{ width: ((dim.value ?? 0) / 10 * 100) + '%', background: dim.color }"></div>
                          </div>
                          <span class="score-bar-value">{{ dim.value ?? 0 }}</span>
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </template>
            </AccordionContent>
          </AccordionPanel>

          <!-- Accepted Response Panel -->
          <AccordionPanel value="accepted" :disabled="!acceptedResponse">
            <AccordionHeader>
              <div class="accordion-header-content">
                <i class="pi pi-check-circle" style="color: var(--accent-green)"></i>
                <span>Accepted Response</span>
                <span v-if="acceptedResponse" class="accordion-header-detail">
                  {{ acceptedResponse.agentName }}
                </span>
              </div>
            </AccordionHeader>
            <AccordionContent>
              <template v-if="acceptedResponse">
                <div class="accepted-panel-header">
                  <div class="accepted-label">
                    <span class="accepted-agent">{{ acceptedResponse.agentName }}</span>
                  </div>
                  <div class="accepted-panel-actions">
                    <Button
                      :icon="isAcceptedResponseExpanded ? 'pi pi-window-minimize' : 'pi pi-window-maximize'"
                      severity="secondary"
                      size="small"
                      text
                      rounded
                      @click="toggleAcceptedResponseExpand"
                      v-tooltip.left="isAcceptedResponseExpanded ? 'Collapse width' : 'Expand to full width'"
                    />
                    <Button
                      :icon="copySuccess ? 'pi pi-check' : 'pi pi-copy'"
                      :severity="copySuccess ? 'success' : 'secondary'"
                      size="small"
                      text
                      rounded
                      @click="copyToClipboard"
                      v-tooltip.left="'Copy to clipboard'"
                    />
                  </div>
                </div>
                <div class="accepted-content markdown-body" v-html="acceptedResponseHtml"></div>
              </template>
            </AccordionContent>
          </AccordionPanel>
        </Accordion>
      </div>
    </div>

    <!-- Bottom Bar: Input + Approval -->
    <div class="bottom-bar glass-card-sm">
      <div v-if="orchestratorState === 'awaiting-approval'" class="approval-bar">
        <span class="approval-text">
          <i class="pi pi-trophy" style="color: var(--accent-amber)"></i>
          Best response from <strong>{{ currentSession?.evaluation?.winner }}</strong> — review above
        </span>
        <div class="approval-buttons">
          <Button label="Accept" icon="pi pi-check" severity="success" size="small" @click="handleDecision('accept')" />
          <Button label="Retry" icon="pi pi-refresh" severity="warning" size="small" outlined @click="handleDecision('restart')" />
          <Button
            label="Cancel"
            icon="pi pi-times"
            severity="secondary"
            size="small"
            outlined
            :disabled="submittingQuestionIds.size > 0"
            @click="handleDecision('decline')"
          />
        </div>
      </div>
      <div v-else-if="orchestratorState === 'awaiting-input'" class="approval-bar">
        <span class="approval-text">
          <i class="pi pi-comment" style="color: var(--accent-purple)"></i>
          Answer in the active agent chat tab
        </span>
        <div class="approval-buttons">
          <Button
            label="Cancel"
            icon="pi pi-times"
            severity="secondary"
            size="small"
            outlined
            :disabled="submittingQuestionIds.size > 0"
            @click="handleDecision('decline')"
          />
        </div>
      </div>
      <div v-else-if="currentSession?.status === 'Failed'" class="approval-bar">
        <span class="approval-text">
          <i class="pi pi-exclamation-triangle" style="color: var(--accent-red)"></i>
          Session failed — agents encountered errors
        </span>
        <div class="approval-buttons">
          <Button label="Retry" icon="pi pi-refresh" severity="warning" size="small" @click="handleRecover" />
        </div>
      </div>
      <div v-else class="input-bar">
        <div ref="promptInputEl" style="flex: 1">
          <InputText
            v-model="promptInput"
            placeholder="Enter your prompt... (Cmd+Enter to submit)"
            class="prompt-input"
            :disabled="isProcessing"
            @keydown="handleKeydown"
          />
        </div>
        <Button
          icon="pi pi-send"
          :loading="isProcessing"
          :disabled="!promptInput.trim() || isProcessing"
          @click="handleSubmit"
          severity="info"
          rounded
        />
      </div>
    </div>
  </div>
</template>

<style scoped>
.chat-layout {
  display: flex;
  flex-direction: column;
  height: 100%;
  position: relative;
}

.sidebar-toggle {
  position: absolute;
  top: 0.75rem;
  left: 0.75rem;
  z-index: 50;
  background: var(--bg-glass);
  border: 1px solid var(--border-glass);
  color: var(--text-secondary);
  padding: 0.5rem;
  border-radius: 8px;
  cursor: pointer;
  transition: all 0.2s;
}
.sidebar-toggle:hover { color: var(--text-primary); background: rgba(255,255,255,0.08); }

.sidebar {
  position: absolute;
  left: 0;
  top: 0;
  bottom: 0;
  width: 280px;
  z-index: 40;
  transform: translateX(-100%);
  transition: transform 0.3s ease;
  border-radius: 0;
  border-right: 1px solid var(--border-glass);
  background: rgba(10,10,15,0.95);
  backdrop-filter: blur(20px);
  display: flex;
  flex-direction: column;
}
.sidebar.open { transform: translateX(0); }

.sidebar-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 1rem;
  font-weight: 600;
  border-bottom: 1px solid var(--border-glass);
}
.sidebar-close { background: none; border: none; color: var(--text-secondary); cursor: pointer; }

.session-list { flex: 1; overflow-y: auto; padding: 0.5rem; }
.session-item {
  padding: 0.75rem;
  border-radius: 8px;
  cursor: pointer;
  transition: background 0.2s;
  margin-bottom: 0.25rem;
}
.session-item:hover { background: var(--bg-glass); }
.session-item.active { background: rgba(59,130,246,0.1); }
.session-prompt { font-size: 0.8rem; margin-bottom: 0.25rem; }
.session-meta { font-size: 0.7rem; color: var(--text-secondary); display: flex; align-items: center; gap: 0.3rem; }
.status-dot { width: 6px; height: 6px; border-radius: 50%; display: inline-block; }
.status-dot.created { background: var(--text-secondary); }
.status-dot.agentsrunning { background: var(--accent-blue); }
.status-dot.evaluating { background: var(--accent-amber); }
.status-dot.awaitinginput { background: var(--accent-purple); }
.status-dot.awaitingapproval { background: var(--accent-purple); }
.status-dot.accepted { background: var(--accent-green); }
.status-dot.declined { background: var(--accent-red); }
.no-sessions { text-align: center; color: var(--text-secondary); padding: 2rem; font-size: 0.85rem; }

.chat-main {
  flex: 1;
  display: flex;
  overflow: hidden;
  gap: 0;
}

.chat-main.accepted-expanded .chat-area {
  display: none;
}

.chat-main.accepted-expanded .panel-divider {
  display: none;
}

.chat-main.accepted-expanded .right-panel {
  width: 100%;
  min-width: 0;
  border-left: none;
}

.chat-area {
  flex: 1;
  overflow-y: auto;
  padding: 2rem 1.5rem;
  display: flex;
  flex-direction: column;
}

.empty-state {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 0.75rem;
  text-align: center;
}
.empty-state h2 { font-size: 1.5rem; font-weight: 600; }
.empty-state p { color: var(--text-secondary); font-size: 0.9rem; max-width: 400px; }

.agent-chat-tabs {
  margin-bottom: 0.9rem;
}
.agent-chat-tabs :deep(.p-tablist) {
  background: transparent;
  border: none;
}
.agent-chat-tabs :deep(.p-tablist-tab-list) {
  display: flex;
  flex-wrap: wrap;
  gap: 0.45rem;
  border: none;
  background: transparent;
}
.agent-chat-tabs :deep(.p-tab) {
  border: 1px solid var(--border-glass);
  border-radius: 999px;
  background: rgba(255,255,255,0.03);
  color: var(--text-secondary);
  padding: 0.28rem 0.65rem;
}
.agent-chat-tabs :deep(.p-tab:hover) {
  background: rgba(255,255,255,0.05);
}
.agent-chat-tabs :deep(.p-tab.p-tab-active) {
  border-color: rgba(99,102,241,0.5);
  background: rgba(99,102,241,0.08);
  color: var(--text-primary);
}
.agent-chat-tab-label {
  display: inline-flex;
  align-items: center;
  gap: 0.45rem;
  font-size: 0.73rem;
}
.agent-chat-tab-name {
  font-weight: 600;
}
.agent-chat-tab-status {
  font-size: 0.62rem;
  text-transform: uppercase;
  letter-spacing: 0.03em;
  border-radius: 999px;
  padding: 0.08rem 0.35rem;
}
.agent-chat-tabs :deep(.p-tab.state-awaiting) .agent-chat-tab-status {
  color: var(--accent-purple);
  background: rgba(139,92,246,0.14);
}
.agent-chat-tabs :deep(.p-tab.state-ready) .agent-chat-tab-status {
  color: var(--accent-green);
  background: rgba(16,185,129,0.14);
}
.agent-chat-tabs :deep(.p-tab.state-running) .agent-chat-tab-status {
  color: var(--accent-blue);
  background: rgba(59,130,246,0.14);
}
.agent-chat-tabs :deep(.p-tab.state-failed) .agent-chat-tab-status {
  color: var(--accent-red);
  background: rgba(239,68,68,0.14);
}

.right-panel {
  width: 480px;
  min-width: 400px;
  border-left: 1px solid var(--border-glass);
  overflow-y: auto;
  padding: 1rem;
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.panel-divider {
  width: 8px;
  cursor: col-resize;
  background: transparent;
  position: relative;
  transition: background 0.2s;
}
.panel-divider::before {
  content: '';
  position: absolute;
  left: 3px;
  top: 0.75rem;
  bottom: 0.75rem;
  width: 2px;
  background: rgba(255, 255, 255, 0.08);
  border-radius: 2px;
}
.panel-divider:hover::before {
  background: rgba(59, 130, 246, 0.4);
}

/* Right Panel Accordion — override PrimeVue Aura theme */
.right-accordion {
  width: 100%;
}
.right-accordion :deep(.p-accordion) {
  background: transparent;
}
.right-accordion :deep(.p-accordionpanel) {
  border: 1px solid var(--border-glass);
  border-radius: 8px;
  margin-bottom: 0.5rem;
  background: var(--bg-glass);
  overflow: hidden;
}
.right-accordion :deep(.p-accordionheader) {
  background: transparent !important;
  border: none !important;
  padding: 0.6rem 1rem;
  font-size: 0.85rem;
  color: var(--text-secondary) !important;
  transition: color 0.2s;
}
.right-accordion :deep(.p-accordionheader:hover) {
  background: rgba(255, 255, 255, 0.03) !important;
  color: var(--text-primary) !important;
}
.right-accordion :deep(.p-accordionheader:focus) {
  box-shadow: none !important;
}
.right-accordion :deep(.p-accordionpanel-active .p-accordionheader) {
  color: var(--text-primary) !important;
  background: rgba(255, 255, 255, 0.02) !important;
}
.right-accordion :deep(.p-accordioncontent) {
  background: transparent !important;
  color: var(--text-primary) !important;
}
.right-accordion :deep(.p-accordioncontent-content) {
  padding: 0.75rem 1rem;
  border-top: 1px solid var(--border-glass);
  background: transparent !important;
  color: var(--text-primary) !important;
}
.right-accordion :deep(.p-accordionpanel:not(.p-accordionpanel-active)) {
  border-color: var(--border-glass);
}
.right-accordion :deep(.p-accordionpanel-active) {
  border-color: rgba(59, 130, 246, 0.2);
}
.right-accordion :deep(.p-accordionpanel.p-disabled) {
  opacity: 0.4;
}
.right-accordion :deep(.p-accordionpanel.p-disabled .p-accordionheader) {
  cursor: not-allowed !important;
}
.right-accordion :deep(.p-accordionpanel.p-disabled .p-accordionheader:hover) {
  background: transparent !important;
  color: var(--text-secondary) !important;
}
/* Override any PrimeVue toggle icon colors */
.right-accordion :deep(.p-accordionheader-toggle-icon) {
  color: var(--text-secondary) !important;
}
.right-accordion :deep(.p-accordionpanel-active .p-accordionheader-toggle-icon) {
  color: var(--text-primary) !important;
}
.accordion-header-content {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  width: 100%;
  font-weight: 600;
}
.accordion-header-detail {
  margin-left: auto;
  font-size: 0.7rem;
  font-weight: 400;
  color: var(--text-secondary);
}
.viz-mini-badge {
  margin-left: auto;
  padding: 0.1rem 0.4rem;
  border-radius: 4px;
  font-size: 0.65rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.viz-mini-badge.idle { background: rgba(255,255,255,0.05); color: var(--text-secondary); }
.viz-mini-badge.fan-out { background: rgba(59,130,246,0.15); color: var(--accent-blue); }
.viz-mini-badge.evaluating { background: rgba(245,158,11,0.15); color: var(--accent-amber); }
.viz-mini-badge.awaiting-input { background: rgba(139,92,246,0.15); color: var(--accent-purple); }
.viz-mini-badge.awaiting-approval { background: rgba(139,92,246,0.15); color: var(--accent-purple); }
.viz-mini-badge.accepted { background: rgba(16,185,129,0.15); color: var(--accent-green); }
.excluded-agents-panel {
  margin-top: 0.75rem;
  border: 1px solid rgba(239, 68, 68, 0.25);
  border-radius: 8px;
  padding: 0.6rem;
  background: rgba(239, 68, 68, 0.05);
}
.excluded-agents-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
  font-size: 0.75rem;
  font-weight: 600;
  color: var(--accent-red);
  margin-bottom: 0.45rem;
}
.excluded-agent-list {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}
.excluded-agent-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
}
.excluded-agent-name {
  font-size: 0.75rem;
  color: var(--text-primary);
}
.scores-section {
  margin-top: 0.75rem;
  border-top: 1px solid var(--border-glass);
  padding-top: 0.75rem;
}
.scores-title {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--text-secondary);
  margin-bottom: 0.6rem;
}
.score-cards {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}
.score-card {
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid var(--border-glass);
  border-radius: 8px;
  padding: 0.6rem 0.75rem;
  transition: border-color 0.2s, box-shadow 0.2s;
}
.score-card-winner {
  border-color: rgba(245, 158, 11, 0.3);
  background: rgba(245, 158, 11, 0.04);
  box-shadow: 0 0 12px rgba(245, 158, 11, 0.08);
}
.score-card-header {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  margin-bottom: 0.5rem;
}
.score-agent-name {
  display: flex;
  align-items: center;
  gap: 0.3rem;
  font-size: 0.75rem;
  font-weight: 600;
  color: var(--text-primary);
}
.score-winner-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.2rem;
  padding: 0.1rem 0.35rem;
  border-radius: 4px;
  font-size: 0.6rem;
  font-weight: 600;
  background: rgba(245, 158, 11, 0.15);
  color: var(--accent-amber);
}
.score-total {
  margin-left: auto;
  font-size: 1rem;
  font-weight: 700;
  font-family: 'JetBrains Mono', monospace;
  color: var(--text-primary);
}
.score-card-winner .score-total {
  color: var(--accent-amber);
}
.score-bars {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}
.score-bar-row {
  display: flex;
  align-items: center;
  gap: 0.4rem;
}
.score-bar-label {
  width: 80px;
  font-size: 0.65rem;
  color: var(--text-secondary);
  flex-shrink: 0;
}
.score-bar-track {
  flex: 1;
  height: 6px;
  border-radius: 3px;
  background: rgba(255, 255, 255, 0.06);
  overflow: hidden;
}
.score-bar-fill {
  height: 100%;
  border-radius: 3px;
  transition: width 0.6s ease;
}
.score-bar-value {
  width: 18px;
  font-size: 0.65rem;
  font-weight: 600;
  font-family: 'JetBrains Mono', monospace;
  color: var(--text-secondary);
  text-align: right;
  flex-shrink: 0;
}

/* Evaluator Panel */
/* Evaluator Panel Content */
.evaluator-panel-header {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-bottom: 0.75rem;
  font-size: 0.85rem;
  font-weight: 600;
  color: var(--accent-amber);
}

/* Winner Response */
.winner-response {
  font-size: 0.85rem;
  line-height: 1.6;
  color: var(--text-primary);
  padding: 0.75rem;
  background: rgba(245, 158, 11, 0.04);
  border-radius: 8px;
  border: 1px solid rgba(245, 158, 11, 0.15);
  margin-bottom: 0.75rem;
  max-height: 400px;
  overflow-y: auto;
}

/* Collapsible reasoning/comparison */
.reasoning-details {
  margin-bottom: 0.5rem;
}
.reasoning-summary {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  font-size: 0.78rem;
  color: var(--text-secondary);
  cursor: pointer;
  padding: 0.4rem 0;
  user-select: none;
}
.reasoning-summary:hover {
  color: var(--text-primary);
}

.evaluator-reasoning {
  font-size: 0.82rem;
  line-height: 1.6;
  color: var(--text-primary);
  padding: 0.75rem;
  background: rgba(255, 255, 255, 0.02);
  border-radius: 8px;
  border: 1px solid var(--border-glass);
  margin-top: 0.5rem;
}

/* Comparison Section */
.comparison-section { margin-top: 0.5rem; }
.comparison-header {
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--text-secondary);
  margin-bottom: 0.5rem;
  text-transform: uppercase;
  letter-spacing: 0.05em;
}
.comparison-item {
  padding: 0.6rem 0.75rem;
  border-radius: 8px;
  background: rgba(255,255,255,0.02);
  border: 1px solid var(--border-glass);
  margin-bottom: 0.5rem;
}
.comparison-item.winner {
  border-color: rgba(245, 158, 11, 0.2);
  background: rgba(245, 158, 11, 0.04);
}
.comparison-item-header {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-bottom: 0.4rem;
}
.comparison-agent-name {
  font-size: 0.78rem;
  font-weight: 600;
  color: var(--accent-green);
  display: flex;
  align-items: center;
  gap: 0.3rem;
}
.winner-tag {
  font-size: 0.65rem;
  color: var(--accent-amber);
  font-weight: 600;
  display: flex;
  align-items: center;
  gap: 0.2rem;
}
.expand-toggle {
  margin-left: auto;
  background: none;
  border: 1px solid var(--border-glass);
  color: var(--text-secondary);
  font-size: 0.7rem;
  padding: 0.15rem 0.5rem;
  border-radius: 4px;
  cursor: pointer;
  display: flex;
  align-items: center;
  gap: 0.25rem;
  transition: all 0.2s;
  font-family: inherit;
}
.expand-toggle:hover {
  color: var(--text-primary);
  border-color: rgba(255,255,255,0.2);
}
.comparison-preview {
  font-size: 0.78rem;
  color: var(--text-secondary);
  line-height: 1.5;
}
.comparison-full {
  font-size: 0.78rem;
  line-height: 1.5;
  max-height: 300px;
  overflow-y: auto;
}

/* Accepted Response Panel */
.accepted-panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 0.75rem;
}
.accepted-panel-actions {
  display: flex;
  align-items: center;
  gap: 0.2rem;
}
.accepted-label {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.85rem;
  font-weight: 600;
  color: var(--accent-green);
}
.accepted-agent {
  font-size: 0.75rem;
  padding: 0.1rem 0.4rem;
  border-radius: 4px;
  background: rgba(16, 185, 129, 0.1);
  color: var(--accent-green);
  font-weight: 500;
}
.accepted-content {
  font-size: 0.85rem;
  line-height: 1.6;
  max-height: 500px;
  overflow-y: auto;
  padding: 0.75rem;
  background: rgba(0, 0, 0, 0.2);
  border-radius: 8px;
}

/* Markdown body shared styles */
.markdown-body :deep(p) { margin-bottom: 0.5rem; }
.markdown-body :deep(p:last-child) { margin-bottom: 0; }
.markdown-body :deep(pre) {
  background: rgba(0, 0, 0, 0.2);
  border-radius: 6px;
  padding: 0.75rem;
  overflow-x: auto;
  margin: 0.5rem 0;
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.8rem;
}
.markdown-body :deep(pre code.hljs) {
  display: block;
  border-radius: 6px;
}
.markdown-body :deep(code) {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.8rem;
}
.markdown-body :deep(:not(pre) > code) {
  background: rgba(255, 255, 255, 0.08);
  padding: 0.15rem 0.35rem;
  border-radius: 4px;
}
.markdown-body :deep(ul), .markdown-body :deep(ol) {
  padding-left: 1.25rem;
  margin: 0.4rem 0;
}
.markdown-body :deep(h1), .markdown-body :deep(h2), .markdown-body :deep(h3) {
  margin: 0.75rem 0 0.35rem;
  font-weight: 600;
}
.markdown-body :deep(blockquote) {
  border-left: 3px solid var(--accent-blue);
  padding-left: 0.75rem;
  margin: 0.5rem 0;
  color: var(--text-secondary);
}
.markdown-body :deep(table) {
  border-collapse: collapse;
  margin: 0.5rem 0;
  font-size: 0.8rem;
}
.markdown-body :deep(th), .markdown-body :deep(td) {
  border: 1px solid var(--border-glass);
  padding: 0.35rem 0.6rem;
}

.error-banner {
  padding: 0.75rem 1rem;
  background: rgba(239, 68, 68, 0.1);
  border: 1px solid rgba(239, 68, 68, 0.2);
  border-radius: 8px;
  color: var(--accent-red);
  font-size: 0.85rem;
}

.bottom-bar {
  padding: 0.75rem 1.5rem;
  border-radius: 0;
  border-top: 1px solid var(--border-glass);
  background: rgba(10,10,15,0.8);
  backdrop-filter: blur(20px);
}

.input-bar {
  display: flex;
  gap: 0.75rem;
  align-items: center;
}

.prompt-input {
  width: 100%;
  background: var(--bg-glass) !important;
  border-color: var(--border-glass) !important;
  color: var(--text-primary) !important;
}

.approval-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.approval-text {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.9rem;
}
.approval-buttons {
  display: flex;
  gap: 0.5rem;
}

.question-type {
  font-size: 0.65rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--accent-purple);
  border: 1px solid rgba(139,92,246,0.3);
  background: rgba(139,92,246,0.08);
  border-radius: 4px;
  padding: 0.1rem 0.35rem;
}
.question-choices {
  display: flex;
  gap: 0.4rem;
  flex-wrap: wrap;
}
.choice-chip {
  border: 1px solid var(--border-glass);
  background: var(--bg-glass);
  color: var(--text-secondary);
  border-radius: 999px;
  padding: 0.25rem 0.6rem;
  font-size: 0.75rem;
  cursor: pointer;
}
.choice-chip:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}
.choice-chip.selected {
  border-color: rgba(139,92,246,0.45);
  background: rgba(139,92,246,0.14);
  color: var(--text-primary);
}
.question-composer-input {
  width: 100%;
  background: var(--bg-glass) !important;
  border-color: var(--border-glass) !important;
  color: var(--text-primary) !important;
}
.agent-question-composer {
  margin-top: 0.35rem;
  margin-bottom: 0.9rem;
  padding: 0.8rem;
  border: 1px solid rgba(139, 92, 246, 0.2);
  border-radius: 12px;
  background: rgba(139, 92, 246, 0.05);
  display: flex;
  flex-direction: column;
  gap: 0.65rem;
}
.composer-header {
  display: flex;
  justify-content: flex-end;
}
.composer-actions {
  display: flex;
  justify-content: flex-end;
}
</style>
