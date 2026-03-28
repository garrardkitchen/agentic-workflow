export interface AgentRequest {
  prompt: string
  sessionId: string
  systemPromptOverride?: string
  metadata?: Record<string, string>
}

export interface AgentResult {
  agentName: string
  model: string
  responseText: string
  elapsedMs: number
  failed: boolean
  error?: string
}

export interface AgentScore {
  accuracy: number
  completeness: number
  clarity: number
  relevance: number
  total: number
}

export interface EvaluationResult {
  winner: string
  reasoning: string
  scores: Record<string, AgentScore>
  allResults: AgentResult[]
}

export interface ChatMessage {
  role: 'user' | 'agent' | 'evaluator' | 'system' | 'question' | 'answer'
  content: string
  messageId?: string
  parentMessageId?: string
  agentName?: string
  timestamp: string
}

export type SessionStatus =
  | 'Created'
  | 'AgentsRunning'
  | 'Evaluating'
  | 'AwaitingInput'
  | 'AwaitingApproval'
  | 'Accepted'
  | 'Declined'
  | 'Restarted'
  | 'Failed'

export type QuestionInputType = 'FreeText' | 'SingleChoice' | 'MultiChoice'

export interface UserQuestion {
  questionId: string
  source: 'agent' | 'evaluator'
  sourceName?: string
  prompt: string
  contextMessageId?: string
  inputType: QuestionInputType
  choices: string[]
  timestamp: string
}

export interface UserQuestionAnswer {
  questionId: string
  answerText?: string
  selectedChoices: string[]
  answeredAt: string
}

export interface SessionState {
  id: string
  prompt: string
  systemPrompt?: string
  status: SessionStatus
  agentResults: AgentResult[]
  evaluation?: EvaluationResult
  pendingQuestions?: UserQuestion[]
  answeredQuestions?: UserQuestionAnswer[]
  chatHistory: ChatMessage[]
  createdAt: string
  updatedAt: string
}

export interface StreamEvent {
  type: 'status' | 'agent_token' | 'agent_complete' | 'evaluation' | 'question_required' | 'error'
  sessionId: string
  agentName?: string
  messageId?: string
  content?: string
  status?: SessionStatus
  agentResult?: AgentResult
  evaluation?: EvaluationResult
  question?: UserQuestion
  timestamp: string
}

export interface PromptConfig {
  drivingSystemPrompt: string
  agentPromptOverrides: Record<string, string>
  evaluatorPrompt: string
  codeTheme?: string
}

export type OrchestratorState = 'idle' | 'fan-out' | 'evaluating' | 'awaiting-input' | 'awaiting-approval' | 'accepted' | 'declined' | 'failed'

export interface AcceptedResponse {
  agentName: string
  model: string
  responseText: string
  elapsedMs: number
}

export interface AgentNode {
  name: string
  model: string
  status: 'idle' | 'running' | 'complete' | 'failed' | 'winner'
  elapsedMs?: number
}
