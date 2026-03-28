# Microsoft Agent Framework — Technical Deep-Dive Research Report

**Date**: 2026-03-28  
**Repository**: [microsoft/agent-framework](https://github.com/microsoft/agent-framework)  
**Stars**: ~8,256 | **Created**: 2025-04-28 | **License**: MIT  
**Languages**: Python, C#/.NET | **PyPI**: `agent-framework` | **NuGet**: `Microsoft.Agents.AI`

---

## Executive Summary

Microsoft Agent Framework is a comprehensive, multi-language (Python and C#/.NET) framework for building, orchestrating, and deploying AI agents and multi-agent workflows. Released in April 2025, it unifies and succeeds Microsoft's earlier agent-oriented projects — Semantic Kernel and AutoGen — into a single cohesive framework[^1]. The framework provides a layered architecture spanning single-agent chat, tool use, middleware pipelines, multi-agent orchestration patterns (sequential, concurrent, group chat, handoff, Magentic), and graph-based workflow engines with checkpointing and time-travel capabilities. It supports multiple LLM providers (OpenAI, Azure OpenAI, Azure AI Foundry, Anthropic, AWS Bedrock, Ollama) and integrates with open protocols including A2A (Agent-to-Agent) and MCP (Model Context Protocol)[^2].

---

## Architecture Overview

```
┌──────────────────────────────────────────────────────────────────────┐
│                    Microsoft Agent Framework                         │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌─────────────┐  ┌──────────────┐  ┌────────────────────────────┐  │
│  │  DevUI      │  │  Declarative │  │  AF Labs (Experimental)    │  │
│  │  (Debug UI) │  │  (YAML/JSON) │  │  benchmarks, RL, research  │  │
│  └──────┬──────┘  └──────┬───────┘  └────────────┬───────────────┘  │
│         │                │                        │                  │
│  ┌──────▼────────────────▼────────────────────────▼───────────────┐  │
│  │              Orchestrations Layer                               │  │
│  │  SequentialBuilder │ ConcurrentBuilder │ GroupChatBuilder       │  │
│  │  HandoffBuilder    │ MagenticBuilder                           │  │
│  └───────────────────────────┬────────────────────────────────────┘  │
│                              │                                       │
│  ┌───────────────────────────▼────────────────────────────────────┐  │
│  │           Workflow Engine (Graph-based)                         │  │
│  │  Workflow → Edges → Executors → Checkpoints → State            │  │
│  │  Streaming │ Time-travel │ Human-in-the-loop                   │  │
│  └───────────────────────────┬────────────────────────────────────┘  │
│                              │                                       │
│  ┌───────────────────────────▼────────────────────────────────────┐  │
│  │              Agent Core                                        │  │
│  │  AIAgent (abstract) → Agent (concrete)                         │  │
│  │  AgentSession │ AgentResponse │ AgentResponseUpdate             │  │
│  │  Middleware Pipeline │ Context Providers │ History Providers     │  │
│  └───────────────┬───────────────────┬────────────────────────────┘  │
│                  │                   │                                │
│  ┌───────────────▼───────┐  ┌───────▼────────────────────────────┐  │
│  │   Tools Layer         │  │   Chat Client Providers            │  │
│  │  FunctionTool │ MCP   │  │  OpenAI │ AzureOpenAI │ Anthropic  │  │
│  │  A2A │ Skills         │  │  Bedrock │ Ollama │ CopilotStudio  │  │
│  └───────────────────────┘  │  AzureAI (Foundry) │ GitHub Copilot│  │
│                              └────────────────────────────────────┘  │
│                                                                      │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │  Cross-Cutting: OpenTelemetry │ Purview │ Mem0 │ CosmosDB     │  │
│  │  DurableTask │ Azure Functions │ Redis                         │  │
│  └────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────┘
```

---

## Agent Core

### The `AIAgent` Base Class (.NET)

The `AIAgent` class in `Microsoft.Agents.AI.Abstractions` is the foundational abstraction for all agents[^3]. It defines:

- **`Id`**: Unique identifier (GUID by default, service-assigned for cloud agents)
- **`Name`**: Human-readable display name
- **`Description`**: Purpose/capabilities description
- **`CurrentRunContext`**: Static `AsyncLocal<AgentRunContext?>` that flows context across async calls
- **`CreateSessionAsync()`**: Creates a new `AgentSession` for conversation state
- **`SerializeSessionAsync()` / `DeserializeSessionAsync()`**: Session persistence for checkpointing
- **`RunAsync()`**: Non-streaming agent execution returning `AgentResponse`
- **`RunStreamingAsync()`**: Streaming execution returning `IAsyncEnumerable<AgentResponseUpdate>`
- **`GetService<T>()`**: Service locator pattern for accessing agent capabilities

The class includes extensive security documentation warning that messages cross trust boundaries and that LLM outputs should be treated as untrusted[^3].

### The `Agent` Class (Python)

The Python `Agent` class in `agent_framework._agents` mirrors the .NET design[^4]:

```python
agent = Agent(
    client=OpenAIChatClient(),
    name="MyAgent",
    instructions="You are a helpful assistant.",
    tools=[get_weather, get_menu_specials]
)
result = await agent.run("What's the weather?")
```

Key constructor parameters:
- **`client`**: A `SupportsChatGetResponse` implementation (any chat client)
- **`instructions`**: System prompt / behavioral instructions
- **`tools`**: List of callable functions, `FunctionTool` instances, or MCP tools
- **`middleware`**: Pipeline of `AgentMiddlewareLayer` instances
- **`context_providers`**: Providers for injecting additional context per invocation
- **`history_provider`**: Storage backend for conversation history
- **`compaction_strategy`**: Strategy for managing context window limits

### Agent Response Design (ADR-0001)

The framework went through a thorough design process for response types, documented in ADR-0001[^5]. The accepted approach (Option 1.1a) uses custom `AgentResponse` / `AgentResponseUpdate` types where:

- **Primary content** (the agent's answer) uses `TextContent`, `DataContent`, `UriContent`
- **Secondary content** (tool calls, reasoning, progress) uses distinct content types
- `AgentResponse.Text` aggregates all primary text content automatically
- This provides a clean "hello world" experience: `print(await agent.run("Hello"))` just works

The design was benchmarked against AutoGen, OpenAI Agent SDK, Google ADK, AWS Strands, LangGraph, and Agno[^5].

---

## Chat Client Providers

The framework supports multiple LLM backends through provider-specific packages:

| Provider | Python Package | .NET Package | Protocol |
|----------|---------------|-------------|----------|
| OpenAI | `agent_framework.openai` | `Microsoft.Agents.AI.OpenAI` | Chat Completions + Responses API |
| Azure OpenAI | `agent_framework.azure` | `Microsoft.Agents.AI.OpenAI` | Azure-hosted OpenAI |
| Azure AI Foundry | `agent_framework.foundry` | `Microsoft.Agents.AI.AzureAI` | Azure AI Foundry Agents |
| Anthropic | `agent_framework.anthropic` | `Microsoft.Agents.AI.Anthropic` | Claude API |
| AWS Bedrock | `agent_framework.amazon` | — | Bedrock Runtime |
| Ollama | `agent_framework.ollama` | — | Local models |
| Copilot Studio | `agent_framework.microsoft` | `Microsoft.Agents.AI.CopilotStudio` | Power Platform |
| GitHub Copilot | `agent_framework.github` | `Microsoft.Agents.AI.GitHub.Copilot` | GitHub Models |

Each provider implements `SupportsChatGetResponse` (Python) or wraps the official SDK client with an `.AsAIAgent()` extension method (.NET)[^6].

---

## Middleware Pipeline

The middleware system (documented in ADR-0007[^7]) provides three types of interception:

### Middleware Types

1. **Agent Middleware** (`AgentMiddlewareLayer`): Intercepts the full agent `run()` / `run_streaming()` lifecycle. Has access to the agent, session, messages, and can modify or terminate execution.

2. **Chat Middleware**: Intercepts calls to the underlying chat client (`get_response()`). Can modify prompts, add context, or post-process LLM responses.

3. **Function Invocation Middleware** (`FunctionInvocationLayer`): Intercepts tool/function calls. Enables logging, validation, user approval gates, retry logic, etc.

### Middleware Context Objects

- **`AgentContext`**: Contains the agent, session, messages, tools, and options for the current invocation[^8]
- **`FunctionInvocationContext`**: Contains function metadata, arguments, and result

### Example: User Approval Middleware

The framework includes a user approval pattern (ADR-0006) where function calls can be gated by human confirmation before execution[^9].

```python
class ApprovalMiddleware(AgentMiddlewareLayer):
    async def on_agent_run(self, context, next):
        # Inspect, modify, or block before/after
        await next(context)
```

---

## Tools & Function Calling

### Tool Types

Tools are normalized through `_tools.py` and can be[^10]:

- **Python callables**: Regular `def` or `async def` functions with type annotations
- **`FunctionTool` instances**: Wrapped functions with metadata
- **MCP tools**: External tools via Model Context Protocol servers
- **Agent-as-tool**: An agent itself can be exposed as a tool for other agents (Skills — ADR-0021)

### MCP Integration

The `_mcp.py` module provides `MCPTool` which wraps MCP server connections[^4]. Agents can connect to MCP servers for external tool access:

```python
agent = Agent(
    client=OpenAIChatClient(),
    tools=[MCPTool("filesystem", server_url="http://localhost:8080")]
)
```

### A2A (Agent-to-Agent) Protocol

Both Python (`python/packages/a2a`) and .NET (`Microsoft.Agents.AI.A2A`) packages implement the A2A protocol for cross-framework agent communication[^11]. ASP.NET Core hosting is available via `Microsoft.Agents.AI.Hosting.A2A.AspNetCore`.

---

## Sessions & State Management

### AgentSession

`AgentSession` is a lightweight container for conversation state[^12]:

- **`session_id`**: Unique session identifier
- **`state`**: Arbitrary key-value state dictionary with serialization support
- **History Provider**: Pluggable storage for conversation messages
- **Context Providers**: Pipeline of providers that inject context per invocation

### History Providers

- **`InMemoryHistoryProvider`**: Default, in-process storage
- **`CosmosNoSql`**: Azure Cosmos DB persistence (`python/packages/azure-cosmos`, `Microsoft.Agents.AI.CosmosNoSql`)
- **`Redis`**: Redis-based storage (`python/packages/redis`)

### Context Providers

`BaseContextProvider` allows injecting additional context into each agent invocation through the `SessionContext` object[^12]. This enables RAG patterns, user profile injection, and dynamic system prompts.

### Serialization

Sessions support full serialization via `to_dict()` / `from_dict()` with a type registry for custom state objects[^12]. The `_STATE_TYPE_REGISTRY` enables Pydantic models and custom types to survive round-trip serialization.

---

## Multi-Agent Orchestration

The orchestrations layer (`agent_framework.orchestrations`) provides five patterns[^13]:

### 1. Sequential (`SequentialBuilder`)
Agents execute one after another in a defined order. Each agent's output feeds into the next.

### 2. Concurrent (`ConcurrentBuilder`)
Multiple agents execute in parallel, and results are aggregated.

### 3. Group Chat (`GroupChatBuilder`)
Multiple agents participate in a conversation. An orchestrator agent selects which agent speaks next via `GroupChatSelectionFunction`. Supports custom `TerminationCondition` logic.

### 4. Handoff (`HandoffBuilder`)
An agent can transfer control to another agent mid-conversation. Uses `HandoffSentEvent` to signal transitions. Includes `HandoffConfiguration` for defining valid handoff targets.

### 5. Magentic (`MagenticBuilder`)
An advanced orchestration pattern (from Magentic-One research[^14]) with:
- `MagenticManagerBase` / `StandardMagenticManager`: Manages the orchestration
- `MagenticProgressLedger`: Tracks task progress
- `MagenticPlanReviewRequest/Response`: Human-in-the-loop plan review
- `MagenticOrchestratorEvent`: Events for orchestration lifecycle

---

## Graph-Based Workflow Engine

The workflow engine (`agent_framework._workflows`) provides a DAG-based execution model[^15]:

### Core Components

- **`Workflow`**: Defines a directed graph of executors connected by edges
- **`WorkflowBuilder`**: Fluent API for constructing workflows
- **`Executor`**: A node in the workflow graph (can wrap agents or pure functions)
- **`WorkflowAgent`**: Wraps an agent as a workflow executor
- **`Edge` / `EdgeGroup` / `FanOutEdgeGroup`**: Connections between nodes with data routing
- **`State`**: Workflow-wide state container
- **`Runner` / `RunnerContext`**: Execution runtime

### Key Features

- **Streaming**: Workflows emit `WorkflowEvent` objects during execution
- **Checkpointing**: `CheckpointStorage` enables saving/restoring workflow state
- **Time-travel**: Replay from any checkpoint
- **Human-in-the-loop**: `request_info` events pause execution waiting for external input
- **Max iterations**: Configurable via `DEFAULT_MAX_ITERATIONS`

### WorkflowRunResult

The `WorkflowRunResult` extends `list[WorkflowEvent]` and provides[^15]:
- `get_outputs()`: All workflow outputs
- `get_request_info_events()`: Human-in-the-loop requests
- `get_final_state()`: Terminal state (IDLE, IDLE_WITH_PENDING_REQUESTS, etc.)
- `status_timeline()`: Complete status event history

### .NET Workflow Engine

The .NET counterpart lives in `Microsoft.Agents.AI.Workflows` with[^16]:
- `AIAgentBinding.cs`: Binds agents to workflow nodes
- Declarative workflow support via `Microsoft.Agents.AI.Workflows.Declarative`
- Source generators via `Microsoft.Agents.AI.Workflows.Generators`
- Durable Task integration via `Microsoft.Agents.AI.DurableTask`

---

## Observability & Telemetry

Built-in OpenTelemetry integration (ADR-0003)[^17]:

- **`AgentTelemetryLayer`**: Auto-instruments agent runs with spans
- **`OtelAttr`**: Standard attribute names for agent telemetry
- **Workflow spans**: `create_workflow_span()` for workflow-level tracing
- Distributed tracing across multi-agent orchestrations
- Integration with Azure Monitor, Application Insights

---

## Hosting & Deployment

### ASP.NET Core Hosting
- `Microsoft.Agents.AI.Hosting`: Base hosting abstractions
- `Microsoft.Agents.AI.Hosting.A2A.AspNetCore`: A2A protocol endpoint
- `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`: AG-UI protocol endpoint
- `Microsoft.Agents.AI.Hosting.OpenAI`: OpenAI-compatible API endpoint

### Azure Functions
- `Microsoft.Agents.AI.Hosting.AzureFunctions` (.NET)
- `python/packages/azurefunctions` (Python)

### Durable Task (Long-running)
- `Microsoft.Agents.AI.DurableTask` (.NET)
- `python/packages/durabletask` (Python)
- Entity state schema in `schemas/durable-agent-entity-state.json`[^18]

---

## Declarative Agents

Both languages support defining agents declaratively via YAML/JSON[^19]:
- `Microsoft.Agents.AI.Declarative` (.NET)
- `python/packages/declarative` (Python)
- MCP integration: `Microsoft.Agents.AI.Workflows.Declarative.Mcp`
- Azure AI integration: `Microsoft.Agents.AI.Workflows.Declarative.AzureAI`

---

## DevUI

An interactive developer UI for testing and debugging agents[^20]:
- `python/packages/devui`
- `Microsoft.Agents.AI.DevUI` (.NET)
- Provides a web-based interface for real-time agent interaction during development

---

## AF Labs (Experimental)

The `python/packages/lab` directory contains experimental packages[^21]:
- Benchmarking utilities
- Reinforcement learning integrations
- Research initiatives

---

## Additional Integrations

| Component | Purpose | Packages |
|-----------|---------|----------|
| **Purview** | Data governance | `python/packages/purview`, `Microsoft.Agents.AI.Purview` |
| **Mem0** | Memory management | `python/packages/mem0`, `Microsoft.Agents.AI.Mem0` |
| **Foundry Memory** | Azure Foundry memory | `Microsoft.Agents.AI.FoundryMemory` |
| **Azure AI Search** | RAG / search | `python/packages/azure-ai-search` |
| **Cosmos DB** | Persistent sessions | `python/packages/azure-cosmos`, `Microsoft.Agents.AI.CosmosNoSql` |
| **Redis** | Session caching | `python/packages/redis` |
| **ChatKit** | Chat UI components | `python/packages/chatkit` |
| **AG-UI** | Agent-GUI protocol | `python/packages/ag-ui`, `Microsoft.Agents.AI.AGUI` |
| **Foundry Local** | Local model serving | `python/packages/foundry_local` |

---

## Relationship to Semantic Kernel and AutoGen

Microsoft Agent Framework is the **successor** to both:

- **Semantic Kernel**: Enterprise-grade kernel for AI orchestration. Agent Framework provides a migration guide[^22].
- **AutoGen**: Research-oriented multi-agent framework. Agent Framework also provides a migration guide[^23].

The framework combines Semantic Kernel's enterprise patterns with AutoGen's multi-agent research capabilities into a unified SDK. The `microsoft/spec-to-agents` sample repo demonstrates combining both paradigms[^24].

---

## Key NuGet / PyPI Packages Summary

### .NET Packages (NuGet)

| Package | Purpose |
|---------|---------|
| `Microsoft.Agents.AI` | Core agent abstractions + concrete `Agent` |
| `Microsoft.Agents.AI.Abstractions` | `AIAgent` base class, sessions, responses |
| `Microsoft.Agents.AI.OpenAI` | OpenAI + Azure OpenAI provider |
| `Microsoft.Agents.AI.Anthropic` | Anthropic Claude provider |
| `Microsoft.Agents.AI.AzureAI` | Azure AI Foundry provider |
| `Microsoft.Agents.AI.Workflows` | Graph-based workflow engine |
| `Microsoft.Agents.AI.A2A` | Agent-to-Agent protocol |
| `Microsoft.Agents.AI.Hosting` | ASP.NET Core hosting |
| `Microsoft.Agents.AI.DurableTask` | Long-running agent support |
| `Microsoft.Agents.AI.Declarative` | YAML/JSON agent definitions |

### Python Packages (PyPI)

| Package | Purpose |
|---------|---------|
| `agent-framework` | Meta-package installing all sub-packages |
| `agent-framework-core` | Core agent, sessions, middleware, types |
| `agent-framework-openai` | OpenAI / Azure OpenAI provider |
| `agent-framework-anthropic` | Anthropic provider |
| `agent-framework-foundry` | Azure AI Foundry provider |
| `agent-framework-orchestrations` | Multi-agent orchestration patterns |
| `agent-framework-a2a` | Agent-to-Agent protocol |
| `agent-framework-devui` | Developer testing UI |

---

## Architectural Decision Records (ADRs)

The project maintains 22+ ADRs documenting key design decisions[^25]:

| ADR | Title | Status |
|-----|-------|--------|
| 0001 | Agent Run Response Design | Accepted |
| 0002 | Agent Tools | — |
| 0003 | OpenTelemetry Instrumentation | — |
| 0004 | Foundry SDK Extensions | — |
| 0005 | Python Naming Conventions | — |
| 0006 | User Approval | — |
| 0007 | Agent Filtering/Middleware | Proposed |
| 0008 | Python Subpackages | — |
| 0009 | Long Running Operations | — |
| 0010 | AG-UI Support | — |
| 0011 | Create/Get Agent API | — |
| 0014 | Feature Collections | — |
| 0015 | Agent Run Context | — |
| 0016 | Structured Output | — |
| 0018 | AgentThread Serialization | — |
| 0019 | Context Compaction Strategy | — |
| 0020 | Foundry Evals Integration | — |
| 0021 | Agent Skills Design | — |
| 0022 | Chat History Persistence | — |

---

## Confidence Assessment

| Aspect | Confidence | Notes |
|--------|------------|-------|
| Repository structure & packages | **High** | Directly verified from source |
| Core architecture (AIAgent, Agent, middleware) | **High** | Verified from source code |
| ADR design decisions | **High** | Read primary ADR documents |
| Orchestration patterns | **High** | Verified from exports and source |
| Workflow engine internals | **High** | Read workflow source files |
| Relationship to SK/AutoGen | **High** | Confirmed via README migration guides |
| Specific API signatures | **Medium-High** | Read from source, may evolve (framework is in preview) |
| AF Labs capabilities | **Medium** | Directory exists but not deeply explored |
| .NET workflow details | **Medium** | Directory listing verified, individual files not fully read |

**Note**: This framework is in **preview** (pre-release packages). APIs are subject to change.

---

## Footnotes

[^1]: `README.md` — [microsoft/agent-framework](https://github.com/microsoft/agent-framework) root README, migration guides section
[^2]: `TRANSPARENCY_FAQ.md` — [microsoft/agent-framework](https://github.com/microsoft/agent-framework) — "What can Microsoft Agent Framework do?" section
[^3]: `dotnet/src/Microsoft.Agents.AI.Abstractions/AIAgent.cs` — AIAgent class definition with security remarks
[^4]: `python/packages/core/agent_framework/_agents.py` — Python Agent class implementation
[^5]: `docs/decisions/0001-agent-run-response.md` — Agent Run Responses Design ADR (accepted 2025-07-10)
[^6]: `README.md` — Quick start examples showing `.AsAIAgent()` and `AzureOpenAIResponsesClient` patterns
[^7]: `docs/decisions/0007-agent-filtering-middleware.md` — Agent Filtering Middleware Design ADR (proposed 2025-09-15)
[^8]: `python/packages/core/agent_framework/_middleware.py` — AgentContext, FunctionInvocationContext, MiddlewareType enum
[^9]: `docs/decisions/0006-userapproval.md` — User Approval ADR
[^10]: `python/packages/core/agent_framework/_tools.py` — Tool normalization and FunctionTool
[^11]: `python/packages/a2a/` and `dotnet/src/Microsoft.Agents.AI.A2A/` — A2A protocol packages
[^12]: `python/packages/core/agent_framework/_sessions.py` — SessionContext, BaseContextProvider, BaseHistoryProvider, AgentSession, serialization registry
[^13]: `python/packages/core/agent_framework/orchestrations/__init__.py` — Lazy imports for all orchestration builders
[^14]: Magentic-One orchestration — `MagenticBuilder`, `StandardMagenticManager`, `MagenticProgressLedger` exports
[^15]: `python/packages/core/agent_framework/_workflows/_workflow.py` — WorkflowRunResult, Workflow class
[^16]: `dotnet/src/Microsoft.Agents.AI.Workflows/` — .NET workflow engine directory listing
[^17]: `docs/decisions/0003-agent-opentelemetry-instrumentation.md` — OpenTelemetry instrumentation ADR
[^18]: `schemas/durable-agent-entity-state.json` — Durable task entity state schema
[^19]: `dotnet/src/Microsoft.Agents.AI.Declarative/` and `python/packages/declarative/` — Declarative agent packages
[^20]: `python/packages/devui/` and `dotnet/src/Microsoft.Agents.AI.DevUI/` — DevUI packages
[^21]: `python/packages/lab/` — AF Labs experimental directory
[^22]: README migration guide link: `https://learn.microsoft.com/en-us/agent-framework/migration-guide/from-semantic-kernel`
[^23]: README migration guide link: `https://learn.microsoft.com/en-us/agent-framework/migration-guide/from-autogen`
[^24]: [microsoft/spec-to-agents](https://github.com/microsoft/spec-to-agents) — Multi-agent event planning workflow sample
[^25]: `docs/decisions/` — Directory containing 22+ ADR files
