<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick, watch } from 'vue'
import { useOrchestrator } from '../composables/useOrchestrator'
import OrchestratorVisualizer from '../components/OrchestratorVisualizer.vue'
import ChatBubble from '../components/ChatBubble.vue'
import { renderMarkdown } from '../utils/markdown'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Textarea from 'primevue/textarea'
import Accordion from 'primevue/accordion'
import AccordionPanel from 'primevue/accordionpanel'
import AccordionHeader from 'primevue/accordionheader'
import AccordionContent from 'primevue/accordioncontent'

const {
  currentSession, orchestratorState, agents, isProcessing, error, acceptedResponse,
  pendingQuestion, submitPrompt, submitDecision, submitQuestionAnswer, recoverSession, loadSessions, sessions, loadSession, resetState,
} = useOrchestrator()

const promptInput = ref('')
const promptInputEl = ref<HTMLElement>()
const chatMainEl = ref<HTMLElement>()
const chatContainer = ref<HTMLElement>()
const expandedAgents = ref<Set<string>>(new Set())
const copySuccess = ref(false)
const activePanel = ref<string | null>('orchestration')
const questionAnswerText = ref('')
const questionChoice = ref('')
const questionChoices = ref<Set<string>>(new Set())
const isSubmittingQuestionAnswer = ref(false)
const isAcceptedResponseExpanded = ref(false)
const isResizingPanels = ref(false)
const rightPanelWidth = ref(480)

function isAwaitingQuestionForMessage(messageId?: string) {
  if (orchestratorState.value !== 'awaiting-input' || !messageId) return false
  return pendingQuestion.value?.contextMessageId === messageId
}

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
  if (d === 'decline' && isSubmittingQuestionAnswer.value) return
  if (!currentSession.value) return
  await submitDecision(currentSession.value.id, d)
  if (d === 'decline') {
    questionAnswerText.value = ''
    questionChoice.value = ''
    questionChoices.value = new Set()
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

function toggleQuestionChoice(choice: string) {
  const next = new Set(questionChoices.value)
  if (next.has(choice)) next.delete(choice)
  else next.add(choice)
  questionChoices.value = next
}

async function handleSubmitQuestionAnswer() {
  if (isSubmittingQuestionAnswer.value) return
  if (!currentSession.value || !pendingQuestion.value) return
  const q = pendingQuestion.value
  if (q.inputType === 'SingleChoice' && !questionChoice.value) return
  if (q.inputType === 'MultiChoice' && questionChoices.value.size === 0) return
  if (q.inputType === 'FreeText' && !questionAnswerText.value.trim()) return

  const selectedChoices =
    q.inputType === 'SingleChoice'
      ? [questionChoice.value]
      : q.inputType === 'MultiChoice'
        ? Array.from(questionChoices.value)
        : []

  const answerText = q.inputType === 'FreeText' ? questionAnswerText.value.trim() : ''
  isSubmittingQuestionAnswer.value = true
  try {
    const submitted = await submitQuestionAnswer(currentSession.value.id, q.questionId, answerText, selectedChoices)
    if (submitted) {
      questionAnswerText.value = ''
      questionChoice.value = ''
      questionChoices.value = new Set()
    }
  } finally {
    isSubmittingQuestionAnswer.value = false
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

        <!-- Chat Messages -->
        <template v-if="currentSession">
          <template v-for="(msg, i) in currentSession.chatHistory" :key="i">
            <ChatBubble :message="msg" />

            <!-- Inline AG-UI question response (attached to originating agent pane) -->
            <div v-if="msg.role === 'agent' && isAwaitingQuestionForMessage(msg.messageId) && pendingQuestion" class="inline-question-card">
            <div class="question-header">
              <span class="question-title">
                <i class="pi pi-question-circle" style="color: var(--accent-purple)"></i>
                {{ pendingQuestion.sourceName || pendingQuestion.source }} needs your input
              </span>
              <span class="question-type">{{ pendingQuestion.inputType }}</span>
            </div>
            <div class="question-prompt">{{ pendingQuestion.prompt }}</div>
            <div v-if="(currentSession?.pendingQuestions?.length ?? 0) > 1" class="question-queue-hint">
              {{ currentSession?.pendingQuestions?.length }} questions pending
            </div>

            <div v-if="pendingQuestion.inputType === 'SingleChoice'" class="question-choices">
              <button
                v-for="choice in pendingQuestion.choices"
                :key="choice"
                :class="['choice-chip', { selected: questionChoice === choice }]"
                :disabled="isSubmittingQuestionAnswer"
                @click="questionChoice = choice"
              >{{ choice }}</button>
            </div>

            <div v-else-if="pendingQuestion.inputType === 'MultiChoice'" class="question-choices">
              <button
                v-for="choice in pendingQuestion.choices"
                :key="choice"
                :class="['choice-chip', { selected: questionChoices.has(choice) }]"
                :disabled="isSubmittingQuestionAnswer"
                @click="toggleQuestionChoice(choice)"
              >{{ choice }}</button>
            </div>

            <Textarea
              v-else
              v-model="questionAnswerText"
              rows="3"
              autoResize
              class="question-input mono"
              placeholder="Type your answer..."
              :disabled="isSubmittingQuestionAnswer"
            />

            <div class="approval-buttons">
              <Button
                :label="isSubmittingQuestionAnswer ? 'Submitting...' : 'Submit Answer'"
                icon="pi pi-check"
                severity="success"
                size="small"
                @click="handleSubmitQuestionAnswer"
                :loading="isSubmittingQuestionAnswer"
                :disabled="
                  isSubmittingQuestionAnswer ||
                  (pendingQuestion.inputType === 'FreeText' && !questionAnswerText.trim()) ||
                  (pendingQuestion.inputType === 'SingleChoice' && !questionChoice) ||
                  (pendingQuestion.inputType === 'MultiChoice' && questionChoices.size === 0)
                "
              />
              <Button label="Cancel" icon="pi pi-times" severity="secondary" size="small" outlined :disabled="isSubmittingQuestionAnswer" @click="handleDecision('decline')" />
            </div>
          </div>
          </template>

          <div
            v-if="orchestratorState === 'awaiting-input' && pendingQuestion && !pendingQuestion.contextMessageId"
            class="inline-question-card"
          >
            <div class="question-header">
              <span class="question-title">
                <i class="pi pi-question-circle" style="color: var(--accent-purple)"></i>
                {{ pendingQuestion.sourceName || pendingQuestion.source }} needs your input
              </span>
              <span class="question-type">{{ pendingQuestion.inputType }}</span>
            </div>
            <div class="question-prompt">{{ pendingQuestion.prompt }}</div>
            <div v-if="(currentSession?.pendingQuestions?.length ?? 0) > 1" class="question-queue-hint">
              {{ currentSession?.pendingQuestions?.length }} questions pending
            </div>

            <div v-if="pendingQuestion.inputType === 'SingleChoice'" class="question-choices">
              <button
                v-for="choice in pendingQuestion.choices"
                :key="choice"
                :class="['choice-chip', { selected: questionChoice === choice }]"
                :disabled="isSubmittingQuestionAnswer"
                @click="questionChoice = choice"
              >{{ choice }}</button>
            </div>

            <div v-else-if="pendingQuestion.inputType === 'MultiChoice'" class="question-choices">
              <button
                v-for="choice in pendingQuestion.choices"
                :key="choice"
                :class="['choice-chip', { selected: questionChoices.has(choice) }]"
                :disabled="isSubmittingQuestionAnswer"
                @click="toggleQuestionChoice(choice)"
              >{{ choice }}</button>
            </div>

            <Textarea
              v-else
              v-model="questionAnswerText"
              rows="3"
              autoResize
              class="question-input mono"
              placeholder="Type your answer..."
              :disabled="isSubmittingQuestionAnswer"
            />

            <div class="approval-buttons">
              <Button
                :label="isSubmittingQuestionAnswer ? 'Submitting...' : 'Submit Answer'"
                icon="pi pi-check"
                severity="success"
                size="small"
                @click="handleSubmitQuestionAnswer"
                :loading="isSubmittingQuestionAnswer"
                :disabled="
                  isSubmittingQuestionAnswer ||
                  (pendingQuestion.inputType === 'FreeText' && !questionAnswerText.trim()) ||
                  (pendingQuestion.inputType === 'SingleChoice' && !questionChoice) ||
                  (pendingQuestion.inputType === 'MultiChoice' && questionChoices.size === 0)
                "
              />
              <Button label="Cancel" icon="pi pi-times" severity="secondary" size="small" outlined :disabled="isSubmittingQuestionAnswer" @click="handleDecision('decline')" />
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
          <Button label="Cancel" icon="pi pi-times" severity="secondary" size="small" outlined @click="handleDecision('decline')" />
        </div>
      </div>
      <div v-else-if="orchestratorState === 'awaiting-input'" class="approval-bar">
        <span class="approval-text">
          <i class="pi pi-comment" style="color: var(--accent-purple)"></i>
          Respond inline in the chat above
        </span>
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

.question-bar,
.inline-question-card {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
}
.inline-question-card {
  margin: 0.25rem 0 0.9rem;
  max-width: 85%;
  background: rgba(139, 92, 246, 0.05);
  border: 1px solid rgba(139, 92, 246, 0.2);
  border-radius: 12px;
  padding: 0.85rem 1rem;
}
.question-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.question-title {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  font-size: 0.9rem;
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
.question-prompt {
  color: var(--text-primary);
  font-size: 0.85rem;
}
.question-queue-hint {
  font-size: 0.72rem;
  color: var(--text-secondary);
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
.question-input {
  width: 100%;
  background: var(--bg-glass) !important;
  border-color: var(--border-glass) !important;
  color: var(--text-primary) !important;
}
</style>
