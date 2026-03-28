# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

### Added

- `AgenticWorkflow.Agent.Sonnet`: adds Spectre.Console CLI prompt for user name on startup using `AnsiConsole.Ask`.
- `AgenticWorkflow.Agent.Gpt54`: adds a `--cli` mode with a Spectre.Console name prompt, greeting output, and `--name` fallback for non-interactive runs.

### Added

- Add new `AgenticWorkflow.Cli` console project using `Spectre.Console`.
- Prompt for user name with validation and render a formatted greeting.

### Fixed

- Stream fan-out now calls agent SSE endpoint (`/api/run-stream`) and forwards token deltas to the frontend as `agent_token` events.
- Evaluation now always runs on successful agent responses even when other agents fail.
- Frontend chat now consumes `agent_token` events and incrementally updates the same message bubble during generation.
- Gateway SSE parser now honors the agent `done` marker to end stream consumption deterministically.
- Inline question "Submit Answer" now shows immediate loading feedback and disables controls while the request is in flight.
- Canceling a follow-up question now skips only that clarification and continues to evaluation with current agent responses instead of stopping the whole chat.
- Accepted Response panel now supports expand/collapse to full-width view for easier reading.
- Added a draggable divider between chat and right pane so either side can be resized live.
- Failed agents are now excluded from chat response bubbles and consistently shown as red in orchestration status.
- Gateway agent call timeout reduced to fail faster when one model is unresponsive.

## 2026-03-28

### Fixed

- Prevent frontend freeze during markdown rendering by centralizing highlight configuration and removing expensive `highlightAuto` fallback.
- Add guarded highlighting path that escapes very large/unknown-language code blocks instead of auto-detecting language.
- Reuse a shared markdown renderer across `ChatBubble` and `ChatView` to avoid repeated parser/plugin setup.

## 2026-03-28

### Added

- Code syntax highlighting with `highlight.js` + `marked-highlight` for chat/evaluator markdown blocks
- Settings option for code theme selection (`github-dark`, `atom-one-dark`, `night-owl`, `tokyo-night-dark`, `monokai`)
- Persisted `codeTheme` in shared `PromptConfig`, applied at frontend startup
- Message anchoring metadata (`messageId`, `parentMessageId`, `contextMessageId`) to keep HITL prompts tied to originating agent responses

### Changed

- Question answer handling now continues only the originating agent branch instead of rebuilding a global retry prompt
- Inline question card is rendered in the original agent response pane via message-context matching
- Follow-up re-questions from a continued agent response are appended as contextual child questions in chat history

### Fixed

- Cross-agent answer bleed where the last answer could affect all pending questions
- Chat context reset after answering questions; session chat history now remains intact through continuation flow

### Added

- PrimeVue Accordion for right panel — replaces custom collapse wrapper with three panels: Orchestration, Evaluator, Accepted Response
- Auto-panel switching: Orchestration expands on fan-out, Evaluator on awaiting-approval, Accepted on accept
- Status badge in Orchestration accordion header shows current state (idle/fan-out/evaluating/awaiting-approval/accepted)
- AG-UI question loop: new `question_required` event + `AwaitingInput` state; inline question card supports free-text/single-choice/multi-choice answers and resumes orchestration with user clarification
- Natural-language fallback question detection: inline AG-UI prompts now trigger on plain agent questions (e.g., lines ending with `?` and user-input cues), not only `[QUESTION]` markers
- Moved AG-UI question answering from bottom bar to inline chat card under agent/evaluator flow for per-response context
- Multi-question queue support: when multiple agents ask follow-ups, each question is queued and presented inline one-by-one until all are answered
- Visual score cards with color-coded progress bars per dimension (Accuracy, Completeness, Clarity, Relevance), winner glow highlight, sorted by total score
- Redesigned Settings page — two-column layout with icon headers, PrimeVue Tabs for per-agent prompt overrides, model badges, inherit hint indicator
- "New Chat" button in top navigation bar — resets state, clears prompt, and focuses input
- Accepted response panel in right pane — renders winning agent's full response as markdown with copy-to-clipboard
- Evaluator review panel shows winner's full response prominently with collapsible reasoning and agent comparison
- Markdown rendering in chat bubbles (agent and evaluator responses) using `marked` + `DOMPurify`
- Per-agent SVG edge animations — edges reflect individual agent status (running/complete/failed/winner)
- SVG glow filter effect on active edges
- HITL buttons: Accept (→ right pane), Retry (resubmit), Cancel (return to chat)
- `AcceptedResponse` type for tracking the winning agent's response after acceptance
- DOMPurify sanitization on all `v-html` markdown output to prevent XSS
- PrimeVue Tooltip directive registration
- Debug logging for evaluator raw/stripped JSON responses

### Changed

- SVG orchestrator nodes from circles to rounded rectangles (110×50px) — text fits cleanly
- Evaluator→User edge is now a curved path arcing below the diagram
- Evaluator chat bubble shows winner's full response (not raw reasoning JSON)
- HITL flow: Accept populates right pane locally, Cancel returns to idle, Retry resubmits original prompt
- Removed global `AddStandardResilienceHandler()` from ServiceDefaults — each project configures its own timeouts
- Gateway resilience: 5-minute timeouts, 0 retries (no Polly retry causing duplicate agent responses)

### Fixed

- User message not appearing in chat — push to `chatHistory` immediately in `submitPrompt()` before SSE fetch
- Duplicate agent responses caused by Polly retry — removed global resilience handler from ServiceDefaults, set `MaxRetryAttempts = 0` in Gateway
- Evaluator JSON parsing failing on responses with trailing markdown backticks — add robust `StripCodeFences()` with multi-format support
- SVG animation edges not reflecting individual agent status — per-agent edge functions match each agent's actual state
- Polly 10s timeout killing LLM calls — removed stacked global handler, Gateway uses single 5-min handler
- Evaluator not capturing streamed responses — subscribe to `AssistantMessageDeltaEvent` alongside `AssistantMessageEvent`
- Evaluator timeout crash — catch `TaskCanceledException` and use partial response
- SVG idle edges now more visible (`rgba(255,255,255,0.12)` up from `0.08`)
- Set reactivity for agent expand toggles (replace Set instead of mutating)

### Added (previous)

- Comprehensive README with colorful Mermaid architecture, sequence, and state machine diagrams
- GitHub Copilot SDK integration in all 3 agent APIs (real LLM calls replacing placeholders)
- GitHub Copilot SDK evaluator in Gateway (replaces heuristic with Claude Sonnet 4.6 evaluation)
- Fallback heuristic evaluator if Copilot SDK evaluator fails
- `OnPermissionRequest = PermissionHandler.ApproveAll` on all SessionConfig instances (required by SDK 0.2.x)
- `EvalJson` / `EvalScoreJson` DTOs for parsing evaluator JSON output

### Changed

- Upgrade `GitHub.Copilot.SDK` from 0.1.18 → 0.2.1-preview.1 (protocol v3 compatibility)
- Upgrade `Microsoft.Agents.AI.GitHub.Copilot` from 0.1.0-* → 1.0.0-preview.260311.1
- Gateway evaluator now calls Copilot SDK with structured JSON prompt instead of picking longest response
- Agent `/api/run` endpoint uses event-based `CopilotClient.CreateSessionAsync` + `session.On()` + `session.SendAsync()`
- Agent `/api/run-stream` endpoint streams `AssistantMessageDeltaEvent` tokens as SSE

### Fixed

- Fix Polly timeout killing LLM calls — Gateway agent HttpClients now use 5-min total/attempt timeout with custom resilience handler
- Fix `TaskCanceledException` in agents — timeout handling no longer links to request cancellation token; uses independent 5-min CTS with graceful fallback
- Upgrade `GitHub.Copilot.SDK` 0.1.18 → 0.2.1-preview.1 to resolve protocol version mismatch (SDK v2 vs server v3)
- Fix path traversal vulnerability in `JsonFileSessionStore` by adding `SanitizeId()` validation (alphanumeric + hyphens only)
- Fix concurrent file access race conditions with per-session `SemaphoreSlim` locking in `JsonFileSessionStore`
- Fix concurrent file access in `JsonFilePromptConfigStore` with `SemaphoreSlim` locking
- Add missing try-catch in `JsonFileSessionStore.GetAsync` for resilient deserialization
- Exclude `prompt-config.json` from `ListAsync` session file filter
- Fix agent name mismatch between frontend and backend (`agent-sonnet` → `Agent-Sonnet`, `agent-codex` → `Agent-GptCodex`) so visualizer correctly matches agent statuses
- Add `failed` state to `OrchestratorState` type and visualizer badge styling
- Handle `Failed` session status in SSE event handler, marking running agents as failed

### Added

- Session recovery support: `recoverSession()` function in `useOrchestrator` composable
- Retry button in ChatView for failed sessions with automatic re-submission
- `.viz-badge.failed` CSS class in OrchestratorVisualizer for failed state display

### Previously added

- Session recovery endpoint (`POST /api/sessions/{id}/recover`) for retrying failed/restarted sessions
- All-agents-failed detection in orchestrator with automatic session failure status and SSE error event
- Top-level try-catch in orchestrate handler for resilient error reporting and session failure tracking

### Fixed

- SSE race condition: add `SemaphoreSlim` to serialize concurrent `SendEvent` writes to `HttpResponse`
- Decision endpoint validation: reject invalid decision values and ensure session is in `AwaitingApproval` status

### Changed

- `SendEvent` helper now accepts `SemaphoreSlim` parameter for thread-safe SSE writes
- `SendEvent` helper swallows client-disconnect exceptions to avoid breaking fan-out

---

- Create Vue 3 + PrimeVue frontend with dark glassmorphism theme and Aura preset
- Chat view with SSE-streaming orchestrator integration, session sidebar, and approval workflow
- SVG-animated orchestrator visualizer showing fan-out → evaluate → approve pipeline
- Settings view for editing driving system prompt, evaluator prompt, and per-agent overrides
- Composable (`useOrchestrator`) for SSE state management with typed stream events
- TypeScript types for agents, sessions, evaluations, and streaming events
- Vite dev server proxy to Gateway API with Aspire service discovery env vars
- Vue Router with lazy-loaded Chat and Settings views
- Create Gateway API project (`AgenticWorkflow.Gateway`) with SSE-streaming orchestrator
- Fan-out to 3 agents (Sonnet, Codex, GPT-5.4) with parallel execution and error handling
- Prompt config endpoints (`GET/PUT /api/config/prompt`)
- Session endpoints (`GET /api/sessions`, `GET /api/sessions/{id}`)
- Main orchestration endpoint (`POST /api/orchestrate`) with Server-Sent Events streaming
- Decision endpoint (`POST /api/sessions/{id}/decide`) for accept/decline/restart
- Placeholder heuristic evaluator (to be replaced with Copilot SDK evaluator)
- CORS support for frontend
- Set up 3 agent API projects (Agent.Sonnet, Agent.GptCodex, Agent.Gpt54) with `/api/run` (POST) and `/api/run-stream` (GET SSE) endpoints
- Agent configuration via `appsettings.json` (Name, Model, SystemPrompt)
- Project references to `AgenticWorkflow.ServiceDefaults` and `AgenticWorkflow.Shared`
- Package reference to `Microsoft.Agents.AI.GitHub.Copilot` (0.1.0-*)
- Create `AgenticWorkflow.Shared` contracts library with models (`AgentRequest`, `AgentResult`, `EvaluationResult`, `SessionState`, `PromptConfig`, `StreamEvent`) and services (`ISessionStore`, `JsonFileSessionStore`, `IPromptConfigStore`, `JsonFilePromptConfigStore`)
- Add project to `AgenticWorkflow.sln`
- Add Aspire AppHost project (`AgenticWorkflow.AppHost`) with SDK 13.2.0 for .NET 10
- Register agent services (Sonnet, GptCodex, Gpt54), gateway, and npm frontend in distributed application
- Add HTTPS launch profile with Aspire dashboard and resource service endpoints

### Removed

- Template weather forecast code and `.http` files from agent projects

## [0.0.0] - 2025-03-28

### Added

- Initial commit with solution scaffold and `research.md`
