# 🤖 Agentic Workflow

> **A distributed multi-agent AI orchestration system** — fan-out a prompt to three LLM agents, evaluate responses with a dedicated evaluator agent, and present the winner to a human for approval — all streaming in real-time.

Built with **.NET 10**, **Aspire 13.2**, **Microsoft Agent Framework**, **GitHub Copilot SDK**, and a **Vue 3 + PrimeVue** frontend.

---

## ✨ Key Features

| Feature | Description |
|---------|-------------|
| 🔀 **Fan-Out** | Single prompt dispatched in parallel to 3 independent agent APIs |
| 🧠 **Multi-Model** | Each agent uses a different LLM: **Claude Sonnet 4.6**, **GPT-5.3 Codex**, **GPT-5.4** |
| ⚖️ **Evaluator Agent** | A 4th agent (Sonnet 4.6) scores all responses on accuracy, completeness, clarity, and relevance |
| 🧑‍⚖️ **Human-in-the-Loop** | User reviews the winner and can **accept**, **decline**, or **restart** |
| 📡 **Real-Time SSE** | Gateway streams status, agent completions, and evaluation to the frontend as Server-Sent Events |
| 💾 **Session Persistence** | Every session state transition is saved to disk as JSON with structured logging |
| 🔭 **Aspire Dashboard** | Distributed tracing, metrics, and health checks across all services out of the box |
| 🎨 **Dark Glassmorphism UI** | Animated SVG orchestration visualizer with live agent status and winner highlights |
| 🧩 **Safe Code Highlighting** | Shared markdown renderer with bounded syntax-highlighting work to keep large responses responsive |
| ✏️ **Editable Prompts** | Driving system prompt, per-agent overrides, and evaluator prompt — all editable in the Settings page |

---

## 🏗️ Architecture

```mermaid
%%{init: {
  'theme': 'dark',
  'themeVariables': {
    'primaryColor': '#6366f1',
    'primaryTextColor': '#f8fafc',
    'primaryBorderColor': '#818cf8',
    'secondaryColor': '#10b981',
    'tertiaryColor': '#f59e0b',
    'lineColor': '#94a3b8',
    'textColor': '#e2e8f0',
    'mainBkg': '#1e1b4b',
    'nodeBorder': '#818cf8',
    'clusterBkg': '#0f172a',
    'clusterBorder': '#334155',
    'titleColor': '#f8fafc',
    'edgeLabelBackground': '#1e293b',
    'nodeTextColor': '#f8fafc'
  }
}}%%

flowchart TB
    subgraph ASPIRE["☁️ .NET Aspire 13.2 — Orchestration & Observability"]
        direction TB

        subgraph FE["🖥️ Frontend — Vue 3 + PrimeVue + Vite"]
            UI["💬 Chat View\n🎯 Orchestrator Visualizer\n⚙️ Settings Editor"]
        end

        subgraph GW["🌐 Gateway API — Orchestrator"]
            ORCH["🔀 Fan-Out\nEngine"]
            EVAL["⚖️ Evaluator Agent\n🧠 Claude Sonnet 4.6"]
            SESS["💾 Session Store\n📁 JSON on Disk"]
        end

        subgraph AGENTS["🤖 Agent APIs — Separate Processes"]
            A1["🟣 Agent Sonnet\nClaude Sonnet 4.6"]
            A2["🔵 Agent GptCodex\nGPT-5.3 Codex"]
            A3["🟠 Agent Gpt54\nGPT-5.4"]
        end

        subgraph SDK["🔑 GitHub Copilot SDK"]
            CP["☁️ GitHub Copilot\nCloud LLMs"]
        end

        UI -->|"① POST /api/orchestrate\n📡 SSE Stream"| ORCH
        ORCH -->|"② Fan-Out\n⚡ Parallel HTTP"| A1
        ORCH -->|"② Fan-Out\n⚡ Parallel HTTP"| A2
        ORCH -->|"② Fan-Out\n⚡ Parallel HTTP"| A3
        A1 -->|"③ AgentResult"| ORCH
        A2 -->|"③ AgentResult"| ORCH
        A3 -->|"③ AgentResult"| ORCH
        ORCH -->|"④ All Responses"| EVAL
        EVAL -->|"⑤ Winner + Scores"| ORCH
        ORCH -->|"⑥ SSE: evaluation"| UI
        UI -->|"⑦ Accept / Decline / Restart"| ORCH
        ORCH <-->|"💾 Save State"| SESS
        A1 <-->|"🔑 CopilotClient"| CP
        A2 <-->|"🔑 CopilotClient"| CP
        A3 <-->|"🔑 CopilotClient"| CP
        EVAL <-->|"🔑 CopilotClient"| CP
    end

    style ASPIRE fill:#0f172a,stroke:#6366f1,stroke-width:3px,color:#f8fafc
    style FE fill:#1e1b4b,stroke:#818cf8,stroke-width:2px,color:#f8fafc
    style GW fill:#1a2332,stroke:#10b981,stroke-width:2px,color:#f8fafc
    style AGENTS fill:#1a2332,stroke:#f59e0b,stroke-width:2px,color:#f8fafc
    style SDK fill:#1a2332,stroke:#ec4899,stroke-width:2px,color:#f8fafc
    style UI fill:#312e81,stroke:#a5b4fc,color:#f8fafc
    style ORCH fill:#064e3b,stroke:#34d399,color:#f8fafc
    style EVAL fill:#78350f,stroke:#fbbf24,color:#f8fafc
    style SESS fill:#1e3a5f,stroke:#38bdf8,color:#f8fafc
    style A1 fill:#4c1d95,stroke:#a78bfa,color:#f8fafc
    style A2 fill:#1e3a5f,stroke:#60a5fa,color:#f8fafc
    style A3 fill:#78350f,stroke:#fb923c,color:#f8fafc
    style CP fill:#831843,stroke:#f472b6,color:#f8fafc
```

---

## 🔄 Orchestration Flow

```mermaid
%%{init: {
  'theme': 'dark',
  'themeVariables': {
    'primaryColor': '#6366f1',
    'primaryTextColor': '#f8fafc',
    'primaryBorderColor': '#818cf8',
    'lineColor': '#94a3b8',
    'textColor': '#e2e8f0',
    'mainBkg': '#1e293b',
    'labelBackground': '#1e293b',
    'nodeBorder': '#818cf8',
    'nodeTextColor': '#f8fafc',
    'actorBkg': '#312e81',
    'actorBorder': '#818cf8',
    'actorTextColor': '#f8fafc',
    'activationBorderColor': '#818cf8',
    'activationBkgColor': '#1e1b4b',
    'signalColor': '#94a3b8',
    'signalTextColor': '#e2e8f0',
    'loopTextColor': '#e2e8f0'
  }
}}%%

sequenceDiagram
    autonumber

    actor User as 🧑 User
    participant FE as 🖥️ Frontend
    participant GW as 🌐 Gateway
    participant S as 🟣 Sonnet 4.6
    participant C as 🔵 GPT-5.3 Codex
    participant G as 🟠 GPT-5.4
    participant EV as ⚖️ Evaluator
    participant FS as 💾 Session Store

    User ->>+ FE: Enter prompt
    FE ->>+ GW: POST /api/orchestrate (SSE)
    GW ->> FS: 💾 Create session
    GW -->> FE: 📡 SSE: status=Created

    rect rgba(99, 102, 241, 0.15)
        Note over GW,G: ⚡ Parallel Fan-Out
        GW -->> FE: 📡 SSE: status=AgentsRunning
        par Agent Sonnet
            GW ->>+ S: POST /api/run
            S ->> S: 🧠 CopilotClient → Claude Sonnet 4.6
            S -->>- GW: AgentResult
            GW -->> FE: 📡 SSE: agent_complete (Sonnet)
        and Agent GptCodex
            GW ->>+ C: POST /api/run
            C ->> C: 🧠 CopilotClient → GPT-5.3 Codex
            C -->>- GW: AgentResult
            GW -->> FE: 📡 SSE: agent_complete (GptCodex)
        and Agent Gpt54
            GW ->>+ G: POST /api/run
            G ->> G: 🧠 CopilotClient → GPT-5.4
            G -->>- GW: AgentResult
            GW -->> FE: 📡 SSE: agent_complete (Gpt54)
        end
    end

    GW ->> FS: 💾 Save agent results

    rect rgba(245, 158, 11, 0.15)
        Note over GW,EV: ⚖️ Evaluation Phase
        GW -->> FE: 📡 SSE: status=Evaluating
        GW ->>+ EV: All 3 responses + original prompt
        EV ->> EV: 🧠 CopilotClient → Claude Sonnet 4.6
        EV -->>- GW: Winner + Scores + Reasoning
    end

    GW -->> FE: 📡 SSE: evaluation (winner + scores)
    GW -->> FE: 📡 SSE: status=AwaitingApproval
    GW ->> FS: 💾 Save evaluation
    FE -->>- User: 🏆 Show winner + scores

    rect rgba(16, 185, 129, 0.15)
        Note over User,FS: 🧑‍⚖️ Human-in-the-Loop
        alt ✅ Accept
            User ->> FE: Click Accept
            FE ->> GW: POST /decide {accept}
            GW ->> FS: 💾 status=Accepted
        else ❌ Decline
            User ->> FE: Click Decline
            FE ->> GW: POST /decide {decline}
            GW ->> FS: 💾 status=Declined
        else 🔄 Restart
            User ->> FE: Click Restart
            FE ->> GW: POST /decide {restart}
            GW ->> FS: 💾 status=Restarted
            Note over User,FE: User can re-submit
        end
    end
```

---

## 📁 Solution Structure

```
agentic-workflow/
├── 📄 README.md
├── 📄 CHANGELOG.md
├── 📄 research.md                        # MS Agent Framework research
├── 📁 sessions/                           # JSON session files (git-ignored)
└── 📁 src/
    ├── 📄 AgenticWorkflow.sln
    │
    ├── 📁 AgenticWorkflow.AppHost/        # ☁️  Aspire 13.2 orchestrator
    │   └── Program.cs                     #     Registers all services + frontend
    │
    ├── 📁 AgenticWorkflow.ServiceDefaults/# 🔧 Aspire defaults
    │   └── Extensions.cs                  #     OpenTelemetry, health checks, resilience
    │
    ├── 📁 AgenticWorkflow.Shared/         # 📦 Shared library
    │   ├── Models/
    │   │   ├── AgentRequest.cs            #     Fan-out request DTO
    │   │   ├── AgentResult.cs             #     Agent response DTO
    │   │   ├── EvaluationResult.cs        #     Evaluator output with scores
    │   │   ├── SessionState.cs            #     Full session lifecycle model
    │   │   ├── PromptConfig.cs            #     Editable system prompts
    │   │   └── StreamEvent.cs             #     SSE event envelope
    │   └── Services/
    │       ├── ISessionStore.cs           #     Session CRUD interface
    │       ├── JsonFileSessionStore.cs    #     File-based persistence + locking
    │       ├── IPromptConfigStore.cs      #     Prompt config interface
    │       └── JsonFilePromptConfigStore.cs
    │
    ├── 📁 AgenticWorkflow.Gateway/        # 🌐 Orchestrator API
    │   └── Program.cs                     #     Fan-out, evaluation, SSE, decisions
    │
    ├── 📁 AgenticWorkflow.Agent.Sonnet/   # 🟣 Claude Sonnet 4.6
    │   ├── Program.cs                     #     /api/run + /api/run-stream
    │   └── appsettings.json               #     Model + system prompt config
    │
    ├── 📁 AgenticWorkflow.Agent.GptCodex/ # 🔵 GPT-5.3 Codex
    │   ├── Program.cs
    │   └── appsettings.json
    │
    ├── 📁 AgenticWorkflow.Agent.Gpt54/    # 🟠 GPT-5.4
    │   ├── Program.cs
    │   └── appsettings.json
    │
    └── 📁 AgenticWorkflow.Frontend/       # 🖥️  Vue 3 + PrimeVue + Vite
        ├── package.json
        ├── vite.config.ts
        └── src/
            ├── main.ts
            ├── App.vue
            ├── style.css                  #     Dark glassmorphism theme
            ├── types/index.ts
            ├── composables/
            │   └── useOrchestrator.ts     #     SSE client + reactive state
            ├── views/
            │   ├── ChatView.vue           #     Main chat interface
            │   └── SettingsView.vue       #     Prompt editor
            └── components/
                ├── OrchestratorVisualizer.vue  # Animated SVG pipeline
                └── ChatBubble.vue              # Role-styled messages
```

---

## 🏁 Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js 20+](https://nodejs.org/) (for the Vue frontend)
- [GitHub Copilot subscription](https://github.com/features/copilot) (Business or Enterprise)
- Authenticated via `gh auth login` or `GITHUB_TOKEN` environment variable

### Run

```bash
# 1. Clone
git clone <repo-url> && cd agentic-workflow

# 2. Install frontend dependencies
cd src/AgenticWorkflow.Frontend && npm install && cd ../..

# 3. Launch via Aspire
cd src/AgenticWorkflow.AppHost
dotnet run
```

The Aspire dashboard opens automatically — typically at `https://localhost:17145`. From there you can access:

| Service | Description |
|---------|-------------|
| **Frontend** | Vue chat UI + settings |
| **Gateway** | Orchestration API |
| **Agent-Sonnet** | Claude Sonnet 4.6 agent |
| **Agent-Codex** | GPT-5.3 Codex agent |
| **Agent-Gpt54** | GPT-5.4 agent |

---

## 🔌 API Reference

### Gateway Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/orchestrate` | Start orchestration (returns SSE stream) |
| `GET` | `/api/sessions` | List all sessions |
| `GET` | `/api/sessions/{id}` | Get session by ID |
| `POST` | `/api/sessions/{id}/decide` | Submit decision: `accept`, `decline`, `restart` |
| `POST` | `/api/sessions/{id}/recover` | Retry a failed/restarted session |
| `GET` | `/api/config/prompt` | Get prompt configuration |
| `PUT` | `/api/config/prompt` | Update prompt configuration |

### Agent Endpoints (per agent)

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/run` | Synchronous completion (used by Gateway fan-out) |
| `GET` | `/api/run-stream` | SSE streaming completion |

### SSE Event Types

```jsonc
// Status update
{ "type": "status", "sessionId": "...", "status": "AgentsRunning", "content": "Agents running" }

// Agent completed
{ "type": "agent_complete", "sessionId": "...", "agentName": "Agent-Sonnet", "agentResult": { ... } }

// Evaluation result
{ "type": "evaluation", "sessionId": "...", "evaluation": { "winner": "Agent-Gpt54", "scores": { ... } } }

// Error
{ "type": "error", "sessionId": "...", "content": "Agent agent-codex failed: ..." }
```

---

## ⚖️ Evaluator

The evaluator agent uses **Claude Sonnet 4.6** via the GitHub Copilot SDK. It receives all agent responses and the original prompt, then scores each on four dimensions:

| Dimension | Scale | Description |
|-----------|-------|-------------|
| **Accuracy** | 1–10 | Factual correctness |
| **Completeness** | 1–10 | Thoroughness of coverage |
| **Clarity** | 1–10 | Coherence and readability |
| **Relevance** | 1–10 | Alignment with the original prompt |

The default evaluator prompt is editable in the **Settings** page. If the evaluator fails, a fallback heuristic selects the longest successful response.

---

## 💾 Session Persistence

Sessions are stored as individual JSON files in the `sessions/` directory:

```
sessions/
├── abc123.json
├── def456.json
└── prompt-config.json
```

Each session file captures the full lifecycle:

```jsonc
{
  "id": "abc123",
  "prompt": "Explain quantum computing",
  "systemPrompt": "You are a helpful assistant.",
  "status": "Accepted",           // Created → AgentsRunning → Evaluating → AwaitingApproval → Accepted
  "agentResults": [ /* 3 results */ ],
  "evaluation": { "winner": "Agent-Gpt54", "scores": { ... } },
  "chatHistory": [ /* user, agent, evaluator, system messages */ ],
  "createdAt": "2025-07-17T10:00:00Z",
  "updatedAt": "2025-07-17T10:00:15Z"
}
```

**Safety features:**
- Path traversal prevention (session IDs sanitised to alphanumeric + hyphens)
- Per-session `SemaphoreSlim` file locking (no concurrent write corruption)
- All state transitions logged with session ID and timestamp

---

## 🛡️ Session Status State Machine

```mermaid
%%{init: {
  'theme': 'dark',
  'themeVariables': {
    'primaryColor': '#6366f1',
    'primaryTextColor': '#f8fafc',
    'primaryBorderColor': '#818cf8',
    'lineColor': '#94a3b8',
    'textColor': '#e2e8f0',
    'mainBkg': '#1e293b',
    'nodeBorder': '#818cf8',
    'nodeTextColor': '#f8fafc'
  }
}}%%

stateDiagram-v2
    [*] --> Created : POST /api/orchestrate
    Created --> AgentsRunning : Fan-out started
    AgentsRunning --> Evaluating : All agents complete
    AgentsRunning --> Failed : All agents failed
    Evaluating --> AwaitingApproval : Evaluation complete
    Evaluating --> AwaitingApproval : Evaluator failed (fallback)
    AwaitingApproval --> Accepted : ✅ User accepts
    AwaitingApproval --> Declined : ❌ User declines
    AwaitingApproval --> Restarted : 🔄 User restarts
    Failed --> Created : 🔧 POST /recover
    Restarted --> Created : 🔧 POST /recover
    Accepted --> [*]
    Declined --> [*]
```

---

## 🧰 Tech Stack

| Layer | Technology | Version |
|-------|-----------|---------|
| **Runtime** | .NET | 10.0 |
| **Orchestration** | Aspire | 13.2.0 |
| **Agent Framework** | Microsoft.Agents.AI.GitHub.Copilot | 1.0.0-preview.260311.1 |
| **LLM SDK** | GitHub.Copilot.SDK | 0.2.1-preview.1 |
| **Frontend** | Vue 3 + TypeScript | 3.5 |
| **UI Library** | PrimeVue (Aura Dark) | 4.5 |
| **Bundler** | Vite | 8.x |
| **Observability** | OpenTelemetry (via Aspire) | Built-in |
| **Persistence** | JSON files on disk | — |

---

## 🎨 Frontend Screenshots

The frontend features a **dark glassmorphism** design with:

- **Chat View** — Message bubbles colour-coded by role (user, agent, evaluator, system)
- **Orchestrator Visualizer** — Animated SVG showing the pipeline state in real-time:
  - 🔘 Grey = idle | 🔵 Blue pulse = running | 🟢 Green = complete | 🔴 Red = failed | 🟡 Gold = winner
- **Settings View** — Full prompt editor for driving prompt, evaluator prompt, and per-agent overrides
- **Approval Bar** — Accept ✅ / Decline ❌ / Restart 🔄 buttons appear after evaluation

---

## 📝 Configuration

### Agent Configuration (`appsettings.json`)

Each agent reads its model and system prompt from configuration:

```json
{
  "Agent": {
    "Name": "Agent-Sonnet",
    "Model": "claude-sonnet-4.6",
    "SystemPrompt": "You are a helpful assistant. Provide thorough, well-reasoned responses."
  }
}
```

### Prompt Configuration (Runtime — via Settings UI)

The driving system prompt, evaluator prompt, and per-agent overrides are stored in `sessions/prompt-config.json` and editable through the Settings page at runtime.

---

## 🚧 Known Limitations

- **CORS** is `AllowAnyOrigin()` — suitable for development only
- **No authentication** — add auth middleware before production use
- **No CI/CD pipeline** — tests, scanning, and release automation are not yet configured
- **Evaluator model** is hardcoded to `claude-sonnet-4.6` — make configurable if needed
- **SemaphoreSlim** per-session in the session store is never evicted (unbounded for long-running instances)

### Resilience & Timeouts

The Gateway's HTTP clients for agent APIs use a **custom Polly resilience handler** (overriding Aspire's default 10s/30s timeouts) because LLM calls can take minutes:

| Setting | Value | Reason |
|---------|-------|--------|
| Total request timeout | 5 min | LLM generation can be slow |
| Per-attempt timeout | 5 min | Same — no early kill |
| Max retries | 1 | Retry once on transient failure |
| Circuit breaker sampling | 10 min | Avoid false-tripping on slow calls |

---

## 📄 Licence

Private — not yet published.
