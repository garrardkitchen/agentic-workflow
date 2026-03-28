import { ref, reactive } from 'vue'
import type { SessionState, StreamEvent, AgentNode, OrchestratorState, PromptConfig, AcceptedResponse } from '../types'

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
            agent.status = 'complete'
            agent.elapsedMs = event.agentResult.elapsedMs
          }
          currentSession.value?.agentResults.push(event.agentResult)
          currentSession.value?.chatHistory.push({
            role: 'agent',
            content: event.agentResult.responseText,
            agentName: event.agentResult.agentName,
            timestamp: event.timestamp,
          })
        }
        break

      case 'evaluation':
        if (event.evaluation) {
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

          currentSession.value?.chatHistory.push({
            role: 'evaluator',
            content: winnerContent,
            agentName: event.evaluation.winner,
            timestamp: event.timestamp,
          })
        }
        break

      case 'error':
        if (event.agentName) {
          const agent = agents.find(a => a.name === event.agentName)
          if (agent) agent.status = 'failed'
        }
        break
    }
  }

  async function submitDecision(sessionId: string, decision: 'accept' | 'decline' | 'restart') {
    if (decision === 'decline') {
      // Cancel — just return to chat input, keep session visible
      orchestratorState.value = 'idle'
      agents.forEach(a => { a.status = 'idle'; a.elapsedMs = undefined })
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
    if (res.ok) currentSession.value = await res.json()
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
  }

  return {
    sessions,
    currentSession,
    orchestratorState,
    agents,
    isProcessing,
    error,
    acceptedResponse,
    submitPrompt,
    submitDecision,
    recoverSession,
    loadSessions,
    loadSession,
    getPromptConfig,
    savePromptConfig,
    resetState,
  }
}
