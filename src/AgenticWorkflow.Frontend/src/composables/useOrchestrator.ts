import { ref, reactive } from 'vue'
import type {
  SessionState,
  StreamEvent,
  AgentNode,
  OrchestratorState,
  PromptConfig,
  AcceptedResponse,
  UserQuestion,
} from '../types'

const sessions = ref<SessionState[]>([])
const currentSession = ref<SessionState | null>(null)
const orchestratorState = ref<OrchestratorState>('idle')
const agents = reactive<AgentNode[]>([
  { name: 'Agent-Sonnet', model: 'claude-sonnet-4.6', status: 'idle' },
  { name: 'Agent-GptCodex', model: 'gpt-5.3-codex', status: 'idle' },
  { name: 'Agent-Gpt54', model: 'gpt-5.4', status: 'idle' },
])
const isProcessing = ref(false)
const error = ref<string | null>(null)
const acceptedResponse = ref<AcceptedResponse | null>(null)
const pendingQuestion = ref<UserQuestion | null>(null)
let lastSubmittedPrompt = ''

export function useOrchestrator() {
  async function submitPrompt(prompt: string, systemPromptOverride?: string) {
    isProcessing.value = true
    error.value = null
    orchestratorState.value = 'fan-out'
    agents.forEach(a => { a.status = 'running'; a.elapsedMs = undefined })
    lastSubmittedPrompt = prompt

    // Show user message immediately in chat
    if (!currentSession.value) {
      currentSession.value = {
        id: '',
        prompt,
        status: 'Created',
        agentResults: [],
        chatHistory: [],
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      }
    }
    currentSession.value.chatHistory.push({
      role: 'user',
      content: prompt,
      timestamp: new Date().toISOString(),
    })

    try {
      const response = await fetch('/api/orchestrate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          prompt,
          sessionId: '',
          systemPromptOverride,
        }),
      })

      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      if (!response.body) throw new Error('No response body')

      const reader = response.body.getReader()
      const decoder = new TextDecoder()
      let buffer = ''

      while (true) {
        const { done, value } = await reader.read()
        if (done) break

        buffer += decoder.decode(value, { stream: true })
        const lines = buffer.split('\n')
        buffer = lines.pop() || ''

        for (const line of lines) {
          if (!line.startsWith('data: ')) continue
          try {
            const event: StreamEvent = JSON.parse(line.slice(6))
            handleEvent(event)
          } catch (e) {
            console.warn('SSE event parse/handle error:', e)
          }
        }
      }
    } catch (e: any) {
      error.value = e.message
      orchestratorState.value = 'idle'
    } finally {
      isProcessing.value = false
    }
  }

  function handleEvent(event: StreamEvent) {
    switch (event.type) {
      case 'status':
        if (event.status === 'AgentsRunning') {
          orchestratorState.value = 'fan-out'
        } else if (event.status === 'Evaluating') {
          orchestratorState.value = 'evaluating'
          agents.forEach(a => { if (a.status === 'running') a.status = 'complete' })
        } else if (event.status === 'AwaitingInput') {
          orchestratorState.value = 'awaiting-input'
          agents.forEach(a => { if (a.status === 'running') a.status = 'complete' })
        } else if (event.status === 'AwaitingApproval') {
          orchestratorState.value = 'awaiting-approval'
        } else if (event.status === 'Failed') {
          orchestratorState.value = 'idle'
          agents.forEach(a => { if (a.status === 'running') a.status = 'failed' })
        }
        if (event.sessionId && currentSession.value) {
          if (!currentSession.value.id) {
            currentSession.value.id = event.sessionId
          }
        } else if (event.sessionId && !currentSession.value) {
          currentSession.value = {
            id: event.sessionId,
            prompt: lastSubmittedPrompt,
            status: event.status || 'Created',
            agentResults: [],
            chatHistory: [{ role: 'user', content: lastSubmittedPrompt, timestamp: new Date().toISOString() }],
            createdAt: event.timestamp,
            updatedAt: event.timestamp,
          }
        }
        if (currentSession.value && event.status) {
          currentSession.value.status = event.status
        }
        break

      case 'agent_complete':
        if (event.agentResult) {
          const agent = agents.find(a => a.name === event.agentResult!.agentName)
          if (agent) {
            agent.status = event.agentResult.failed ? 'failed' : 'complete'
            agent.elapsedMs = event.agentResult.elapsedMs
          }
          if (currentSession.value) {
            currentSession.value.agentResults.push(event.agentResult)
            if (event.agentResult.failed) break

            const existing = event.messageId
              ? currentSession.value.chatHistory.find(
                m => m.role === 'agent' && m.messageId === event.messageId
              )
              : undefined

            if (existing) {
              existing.content = event.agentResult.responseText
              existing.agentName = event.agentResult.agentName
              existing.timestamp = event.timestamp
            } else {
              currentSession.value.chatHistory.push({
                role: 'agent',
                messageId: event.messageId,
                content: event.agentResult.responseText,
                agentName: event.agentResult.agentName,
                timestamp: event.timestamp,
              })
            }
          }
        }
        break

      case 'agent_token':
        if (!currentSession.value || !event.content) break
        {
          const agentName = event.agentName ?? 'agent'
          const agent = agents.find(a => a.name === agentName)
          if (agent?.status === 'failed') break
          const messageId = event.messageId

          let target = messageId
            ? currentSession.value.chatHistory.find(
              m => m.role === 'agent' && m.messageId === messageId
            )
            : undefined

          if (!target) {
            target = {
              role: 'agent',
              messageId,
              content: '',
              agentName,
              timestamp: event.timestamp,
            }
            currentSession.value.chatHistory.push(target)
          }

          target.content += event.content
          target.agentName = agentName
          target.timestamp = event.timestamp

          if (agent) agent.status = 'running'
        }
        break

      case 'evaluation':
        if (event.evaluation) {
          pendingQuestion.value = null
          orchestratorState.value = 'awaiting-approval'
          if (currentSession.value) {
            currentSession.value.evaluation = event.evaluation
            currentSession.value.status = 'AwaitingApproval'
          }
          const winnerAgent = agents.find(a => a.name === event.evaluation!.winner)
          if (winnerAgent) winnerAgent.status = 'winner'

          // Find the winning agent's full response to display in chat
          const winnerResult = event.evaluation.allResults?.find(
            r => r.agentName === event.evaluation!.winner
          )
          const winnerContent = winnerResult?.responseText ?? 'No response available'

          const alreadyHasEvaluator = currentSession.value?.chatHistory.some(
            m => m.role === 'evaluator' && m.agentName === event.evaluation!.winner && m.content === winnerContent
          )
          if (!alreadyHasEvaluator) {
            currentSession.value?.chatHistory.push({
              role: 'evaluator',
              content: winnerContent,
              agentName: event.evaluation.winner,
              timestamp: event.timestamp,
            })
          }
        }
        break

      case 'question_required':
        if (event.question) {
          orchestratorState.value = 'awaiting-input'
          if (currentSession.value) {
            currentSession.value.status = 'AwaitingInput'
            const queue = currentSession.value.pendingQuestions ?? []
            const exists = queue.some(q => q.questionId === event.question!.questionId)
            if (!exists) queue.push(event.question)
            currentSession.value.pendingQuestions = queue
            if (!pendingQuestion.value) {
              pendingQuestion.value = queue[0] ?? null
            }
          }
          currentSession.value?.chatHistory.push({
            role: 'question',
            parentMessageId: event.question.contextMessageId,
            content: event.question.prompt,
            agentName: event.question.sourceName || event.question.source,
            timestamp: event.timestamp,
          })
        }
        break

      case 'error':
        if (event.agentName) {
          const normalizedAgentName = event.agentName.startsWith('agent-')
            ? event.agentName === 'agent-sonnet'
              ? 'Agent-Sonnet'
              : event.agentName === 'agent-codex'
                ? 'Agent-GptCodex'
                : event.agentName === 'agent-gpt54'
                  ? 'Agent-Gpt54'
                  : event.agentName
            : event.agentName
          const agent = agents.find(a => a.name === normalizedAgentName)
          if (agent) agent.status = 'failed'
        }
        break
    }
  }

  async function submitDecision(sessionId: string, decision: 'accept' | 'decline' | 'restart') {
    if (decision === 'decline') {
      // Skip current clarification and keep orchestration alive.
      try {
        const res = await fetch(`/api/sessions/${sessionId}/decide`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ decision: 'decline' }),
        })
        if (res.ok) {
          const updated: SessionState = await res.json()
          currentSession.value = updated
          pendingQuestion.value = updated.pendingQuestions?.[0] ?? null
          orchestratorState.value =
            updated.status === 'AwaitingInput'
              ? 'awaiting-input'
              : updated.status === 'AwaitingApproval'
                ? 'awaiting-approval'
                : updated.status === 'Failed'
                  ? 'failed'
                : 'idle'

          if (updated.status === 'AwaitingApproval' && updated.evaluation) {
            const winnerAgent = agents.find(a => a.name === updated.evaluation!.winner)
            if (winnerAgent) winnerAgent.status = 'winner'
          }
        }
      } catch { /* non-critical */ }
      return
    }

    if (decision === 'accept') {
      // Accept — populate the right pane with the winning response
      const evaluation = currentSession.value?.evaluation
      if (evaluation) {
        const winnerResult = evaluation.allResults?.find(r => r.agentName === evaluation.winner)
        if (winnerResult) {
          acceptedResponse.value = {
            agentName: winnerResult.agentName,
            model: winnerResult.model,
            responseText: winnerResult.responseText,
            elapsedMs: winnerResult.elapsedMs,
          }
        }
      }
      orchestratorState.value = 'accepted'

      // Persist decision to backend
      try {
        await fetch(`/api/sessions/${sessionId}/decide`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ decision }),
        })
      } catch { /* non-critical — UI already updated */ }
      return
    }

    // Retry — resubmit the original prompt
    const prompt = currentSession.value?.prompt || lastSubmittedPrompt
    orchestratorState.value = 'idle'
    agents.forEach(a => { a.status = 'idle'; a.elapsedMs = undefined })
    acceptedResponse.value = null
    currentSession.value = null  // Reset session so submitPrompt creates a fresh one

    // Persist restart decision
    try {
      await fetch(`/api/sessions/${sessionId}/decide`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ decision: 'restart' }),
      })
    } catch { /* non-critical */ }

    await submitPrompt(prompt)
  }

  async function submitQuestionAnswer(
    sessionId: string,
    questionId: string,
    answerText: string,
    selectedChoices: string[],
  ): Promise<boolean> {
    error.value = null

    try {
      const response = await fetch(`/api/sessions/${sessionId}/answer`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          questionId,
          answerText: answerText || null,
          selectedChoices,
        }),
      })

      if (!response.ok) {
        let details = ''
        try {
          const body = await response.json() as { error?: string }
          details = body?.error ? ` - ${body.error}` : ''
        } catch {
          // Ignore parse failure and fall back to status-only message.
        }
        throw new Error(`Failed to submit answer: HTTP ${response.status}${details}`)
      }

      const updated: SessionState = await response.json()
      currentSession.value = updated

      const remaining = updated.pendingQuestions ?? []
      pendingQuestion.value = remaining[0] ?? null
      orchestratorState.value = pendingQuestion.value
        ? 'awaiting-input'
        : updated.status === 'AwaitingApproval'
          ? 'awaiting-approval'
          : updated.status === 'Failed'
            ? 'failed'
            : 'fan-out'

      if (pendingQuestion.value) {
        // More questions remain; do not restart orchestration yet.
        return true
      }

      // Backend now continues only the originating agent branch and returns updated state.
      if (updated.status === 'AwaitingApproval' && updated.evaluation) {
        const winnerAgent = agents.find(a => a.name === updated.evaluation!.winner)
        if (winnerAgent) winnerAgent.status = 'winner'
      }
      return true
    } catch (e: any) {
      if (e?.message?.includes('Question') && e?.message?.includes('not found')) {
        try {
          await loadSession(sessionId)
          if (currentSession.value) {
            pendingQuestion.value = currentSession.value.pendingQuestions?.[0] ?? null
            orchestratorState.value =
              currentSession.value.status === 'AwaitingInput'
                ? 'awaiting-input'
                : currentSession.value.status === 'AwaitingApproval'
                  ? 'awaiting-approval'
                  : currentSession.value.status === 'Failed'
                    ? 'failed'
                    : 'idle'
          }
        } catch {
          // Keep original error if refresh fails.
        }
      }
      error.value = e.message ?? 'Failed to submit question answer'
      return false
    }
  }

  async function recoverSession(sessionId: string) {
    const res = await fetch(`/api/sessions/${sessionId}/recover`, {
      method: 'POST',
    })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    const updated = await res.json()
    currentSession.value = updated
    orchestratorState.value = 'idle'
    agents.forEach(a => { a.status = 'idle'; a.elapsedMs = undefined })
  }

  async function loadSessions() {
    const res = await fetch('/api/sessions')
    if (res.ok) sessions.value = await res.json()
  }

  async function loadSession(id: string) {
    const res = await fetch(`/api/sessions/${id}`)
    if (!res.ok) return

    const loaded: SessionState | null = await res.json()
    if (!loaded) {
      currentSession.value = null
      pendingQuestion.value = null
      orchestratorState.value = 'idle'
      return
    }

    currentSession.value = loaded
    pendingQuestion.value = loaded.pendingQuestions?.[0] ?? null

    switch (loaded.status) {
      case 'AgentsRunning':
        orchestratorState.value = 'fan-out'
        break
      case 'Evaluating':
        orchestratorState.value = 'evaluating'
        break
      case 'AwaitingInput':
        orchestratorState.value = 'awaiting-input'
        break
      case 'AwaitingApproval':
        orchestratorState.value = 'awaiting-approval'
        break
      case 'Accepted':
        orchestratorState.value = 'accepted'
        break
      case 'Failed':
        orchestratorState.value = 'failed'
        break
      default:
        orchestratorState.value = 'idle'
        break
    }
  }

  async function getPromptConfig(): Promise<PromptConfig> {
    const res = await fetch('/api/config/prompt')
    if (!res.ok) throw new Error('Failed to load config')
    return res.json()
  }

  async function savePromptConfig(config: PromptConfig): Promise<void> {
    const res = await fetch('/api/config/prompt', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(config),
    })
    if (!res.ok) throw new Error('Failed to save config')
  }

  function resetState() {
    currentSession.value = null
    orchestratorState.value = 'idle'
    agents.forEach(a => { a.status = 'idle'; a.elapsedMs = undefined })
    error.value = null
    acceptedResponse.value = null
    pendingQuestion.value = null
  }

  return {
    sessions,
    currentSession,
    orchestratorState,
    agents,
    isProcessing,
    error,
    acceptedResponse,
    pendingQuestion,
    submitPrompt,
    submitDecision,
    submitQuestionAnswer,
    recoverSession,
    loadSessions,
    loadSession,
    getPromptConfig,
    savePromptConfig,
    resetState,
  }
}
