# Architecture

## Pattern Realization

### Multi-Agent Orchestration with 5 AIAgents + Quality Gate

The CTL Agent separates **orchestration** (workflow composition, data retrieval, security screening) from **agent reasoning** (LLM-driven intelligence). The orchestrator is not an agent — it is deterministic control logic that composes a DAG of 5 AIAgents, followed by a post-Reflection **LLM-as-judge quality gate** that verifies verdict groundedness.

```
                      CTL AGENT STACK — how a "Clear to List" decision is made

                    ╔══════════════════ OUR APPLICATION ══════════════════╗
  Asset request     ║ ┌─────────────────────────────────────────────────┐ ║     ┌─────────────────┐
  (from queue or ──►║ │ 1. ORCHESTRATOR  (plain .NET — no AI)           │ ║     │ OBSERVABILITY   │
   API)             ║ │    Traffic controller: validates request,       │ ║     │  (sidecar)      │
                    ║ │    fetches asset data, routes to AI, parses     │ ║     │─────────────────│
                    ║ │    verdict, escalates to human if unsure.       │ ║     │ Every step is   │
                    ║ └──────────────────────┬──────────────────────────┘ ║     │ logged & timed: │
                    ║                        ▼                            ║     │  • each AI call │
                    ║ ┌─────────────────────────────────────────────────┐ ║     │  • each tool    │
                    ║ │ 2. PLANNING AI — "What do we need to verify     │ ║     │    call         │
                    ║ │    for this specific asset?"                    │ ║     │  • each policy  │
                    ║ └──────────────────────┬──────────────────────────┘ ║     │    lookup       │
                    ║           ┌────────────┼────────────┐               ║     │  • each safety  │
                    ║           ▼            ▼            ▼               ║     │    check        │
                    ║      ┌─────────┐  ┌──────────┐  ┌───────────┐       ║     │                 │
                    ║      │3a.LEGAL │  │3b.VALUA- │  │3c.OCCUPY- │  ←    ║     │ Feeds:          │
                    ║      │   AI    │  │   TION AI│  │    AI     │ run   ║     │  OpenTelemetry  │
                    ║      │         │  │          │  │           │ in    ║     │  App Insights   │
                    ║      │ title / │  │  price / │  │ vacancy / │ para- ║     │  Audit trail    │
                    ║      │ liens / │  │  value   │  │ condition │ llel  ║     │                 │
                    ║      │ HOA /   │  │  checks  │  │ checks    │       ║     │ (Compliance &   │
                    ║      │ code    │  │          │  │           │       ║     │  troubleshoot.) │
                    ║      └────┬────┘  └────┬─────┘  └─────┬─────┘       ║     │                 │
                    ║           └────────────┼──────────────┘             ║     │                 │
                    ║                        ▼                            ║     │                 │
                    ║ ┌─────────────────────────────────────────────────┐ ║     │                 │
                    ║ │ 4. REVIEW AI — cross-checks all findings,       │ ║     │                 │
                    ║ │    flags contradictions, sets confidence.       │ ║     │                 │
                    ║ └──────────────────────┬──────────────────────────┘ ║     │                 │
                    ║                        ▼                            ║     │                 │
                    ║ ┌─────────────────────────────────────────────────┐ ║     │                 │
                    ║ │ 5. QUALITY GATE (LLM-as-Judge)                  │ ║     │                 │
                    ║ │    Separate LLM scores verdict groundedness     │ ║     │                 │
                    ║ │    (1-5). Below threshold → escalate to human.  │ ║     │                 │
                    ║ └──────────────────────┬──────────────────────────┘ ║     │                 │
                    ║                        ▼                            ║     │                 │
                    ║ ┌─────────────────────────────────────────────────┐ ║     │                 │
                    ║ │ 6. VERDICT  →  confidence ≥ 0.75 ?              │ ║     │                 │
                    ║ │       YES → return decision                     │ ║     │                 │
                    ║ │       NO  → pause, escalate to HUMAN REVIEWER   │ ║     │                 │
                    ║ └─────────────────────────────────────────────────┘ ║     │                 │
                    ║                                                     ║     │                 │
                    ║  ▓▓▓ Guardrails middleware (our code, runs on every ║     │                 │
                    ║      AI call): PII masking · token-budget cap ·     ║     │                 │
                    ║      input+output screening ▓▓▓                     ║     │                 │
                    ╚═════════════════════════╤═══════════════════════════╝     └─────────────────┘
                                              │ agents reach out through the three pillars below
                   ┌──────────────────────────┼──────────────────────────────┐
                   ▼                          ▼                              ▼
     ┌───────────────────────────┐ ┌──────────────────────────┐ ┌──────────────────────────────┐
     │ TOOLS layer  (MCP)        │ │ KNOWLEDGE layer  (RAG)   │ │ SAFETY layer                 │
     │  = our code               │ │  = our code (today)      │ │  = external Azure AI service │
     │───────────────────────────│ │──────────────────────────│ │──────────────────────────────│
     │ Lets agents call out to   │ │ Lets agents look up      │ │ Inspects every AI prompt &   │
     │ data lookups:             │ │ policy documents:        │ │ tool result for:             │
     │  • Title search           │ │  • Foreclosure rules     │ │  • Prompt-injection attacks  │
     │  • HOA / lien check       │ │  • REO / CWCOT policy    │ │  • Hate / violence / etc.    │
     │  • Code violations        │ │  • Valuation standards   │ │                              │
     │  • BPO / AVM (pricing)    │ │  • Title clearance rules │ │ (Azure AI Content Safety +   │
     │  • Occupancy status       │ │  • …10 policy docs total │ │  Prompt Shields REST APIs)   │
     │  • KnowledgeBase search   │ │                          │ │                              │
     │                           │ │ Today: JSON files        │ │                              │
     │ Each agent only sees the  │ │ Future: Azure AI Search  │ │                              │
     │ tools it's allowed to use │ │  (no code change — DI    │ │                              │
     │ (per-agent allow-list).   │ │   swap only)             │ │                              │
     │                           │ │                          │ │                              │
     │ Works with mocks (dev)    │ │                          │ │                              │
     │ or real vendor APIs (prod)│ │                          │ │                              │
     └─────────────┬─────────────┘ └────────────┬─────────────┘ └──────────────────────────────┘
                   │                            │
                   ▼                            ▼
     ┌───────────────────────────┐ ┌────────────────────────────────────────────────────────────┐
     │ MCP Server (our process)  │ │ RAG Knowledge Pipeline   (production-ready, toggle via DI) │
     │ Hosts the tools listed    │ │                                                            │
     │ on the left. Each tool    │ │  Policy JSONs ─► Chunker ─► Embed ─► Azure AI Search      │
     │ delegates to a provider.  │ │  (config/rag-   (paragraph  (Azure   (HNSW vector +        │
     │                           │ │   knowledge/    + sentence   OpenAI   BM25 hybrid w/       │
     │                           │ │   *.json)       + overlap)   text-emb OData filter)        │
     └─────────────┬─────────────┘ │                              3-small)                      │
                   │               │                                                            │
                   ▼               │  Two modes (feature flag CTLAgent:RAG:AzureSearch:Enabled):│
                                   │    OFF (default) -> InMemoryRAGService (hash-lookup, dev)  │
                                   │    ON            -> AzureSearchRAGService (vector + BM25)  │
                                   │  Indexer:  dotnet run --project Cascade.CTL.RAG.Indexer    │
     ┌──────────────────────────────────────────────────────────────────────────────────────────┐
     │ Data Providers  (configurable per environment)                                           │
     │   DEV   →  Mock providers (built-in fake data, 3 sample assets)                          │
     │   PROD  →  Real vendor APIs  +  our Asset Domain REST service (Docker, API-key secured)  │
     └──────────────────────────────────────────────────────────────────────────────────────────┘

     Hosting:  Agent Host  ·  MCP Server  ·  Asset Domain API (Docker)  ·  Azure Container Apps (production)
```

**Legend** — AI boxes (Planning, Legal, Valuation, Occupancy, Review) are the **5 LLM-driven agents**. The Quality Gate is a **6th LLM call** (LLM-as-judge) that verifies verdict groundedness. Numbered boxes are the **end-to-end flow** for a single request. The ▓▓▓ band is the **in-process safety middleware** that every AI call transparently passes through. The three pillars underneath (**Tools / Knowledge / Safety**) clearly separate *our code* from the *external Azure AI service*.

**Tools per agent** — Legal: `SearchTitle`, `HOADelinquency`, `CodeViolation`, `QueryKnowledgeBase`  ·  Valuation: `RetrieveBPO`, `GetAVM`, `QueryKnowledgeBase`  ·  Occupancy: `GetOccupancyStatus`, `QueryKnowledgeBase`.

<details>
<summary>Detailed view (expand)</summary>

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           ORCHESTRATOR                                       │
│  (Deterministic workflow composer — NOT an agent)                            │
│                                                                              │
│  • Validates CTL request (CTLRequestValidator)                               │
│  • Retrieves asset profile (IAssetProfileProvider)                           │
│  • Screens external data for injection (ContentSafetyGuard)                  │
│  • Composes WorkflowBuilder DAG                                              │
│  • Parses verdict JSON from reflection output                                │
│  • HITL gate: escalates NeedsHumanReview to IHumanReviewService              │
└──────────────────────────────┬──────────────────────────────────────────────┘
                               │ Launches DAG
                               ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                        5 AIAgents (IChatClient.AsAIAgent)                     │
│                                                                              │
│  ┌─────────────────┐                                                         │
│  │  Planning Agent  │  Phase 1: Generates verification plan (JSON)           │
│  │  (AIAgent)       │  Tools: QueryKnowledgeBase (asset profile pre-injected) │
│  └────────┬────────┘                                                         │
│           │ PlanResult (requiredDomains, verificationSteps)                   │
│           ▼                                                                  │
│  ┌────────────────┬────────────────┬────────────────┐                        │
│  │  Legal Agent   │ Valuation Agent│ Occupancy Agent│  Phase 2: Fan-Out      │
│  │  (AIAgent)     │  (AIAgent)     │  (AIAgent)     │  (Task.WhenAll)        │
│  │                │                │                │                        │
│  │  Tools:        │  Tools:        │  Tools:        │  Only agents the plan  │
│  │  TitleSearch   │  BPORetrieval  │  Occupancy     │  identifies are        │
│  │  HOADelinq.    │  AVM           │  Status        │  dispatched            │
│  │  CodeViolation │  RAGQuery      │  RAGQuery      │                        │
│  │  RAGQuery      │                │                │                        │
│  └───────┬────────┘───────┬────────┘───────┬────────┘                        │
│          └────────────────┼────────────────┘                                 │
│                           │ InvestigationPhaseResult                          │
│                           ▼                                                  │
│  ┌──────────────────┐                                                        │
│  │ Reflection Agent │  Phase 3: Critiques findings + asset profile           │
│  │  (AIAgent)       │  Detects contradictions, applies confidence penalties  │
│  │                  │  Outputs CTLVerdictDto JSON                             │
│  └────────┬─────────┘                                                        │
│           │                                                                  │
│  ┌────────▼─────────┐                                                        │
│  │ Quality Gate     │  Phase 4: LLM-as-judge groundedness check              │
│  │ (VerdictGrounded-│  Separate LLM scores verdict (1-5) against findings   │
│  │  nessEvaluator)  │  Below threshold → escalate to NeedsHumanReview       │
│  └──────────────────┘                                                        │
│                                                                              │
└──────────────────────────────────────────────────────────────────────────────┘
                               │
                               ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                     INFRASTRUCTURE LAYERS                                    │
│                                                                              │
│  ┌───────────────────────────────┐  ┌──────────────────────────────────────┐ │
│  │         MCP (Transport)       │  │         RAG (Knowledge Retrieval)    │ │
│  │                               │  │                                      │ │
│  │  • Protocol: HTTP/SSE         │  │  • Pattern: Retrieval-Augmented Gen  │ │
│  │  • McpToolProvider manages    │  │  • InMemoryRAGService (dev default) │ │
│  │    multi-endpoint connections │  │  • AzureSearchRAGService (prod;     │ │
│  │  • Tool discovery at runtime  │  │    HNSW vector + BM25 hybrid)       │ │
│  │  • McpClientTool → AITool     │  │  • Metadata filter (state/type)     │ │
│  │  • Hot-swappable backends     │  │  • Toggle: CTLAgent:RAG:AzureSearch │ │
│  │                               │  │    :Enabled (auto-fallback on err)  │ │
│  │  Agents: Legal, Valuation,    │  │  • Indexer: Cascade.CTL.RAG.Indexer │ │
│  │  Occupancy, AssetProfile      │  │                                      │ │
│  │                               │  │  Policy docs: 10 JSON files in      │ │
│  │                               │  │  config/rag-knowledge/               │ │
│  └───────────────────────────────┘  └──────────────────────────────────────┘ │
│                                                                              │
│  ┌──────────────────────────────────────────────────────────────────────────┐ │
│  │              Content Safety (Azure ML APIs)                             │ │
│  │  • Prompt Shields — ML-based prompt injection detection (REST API)      │ │
│  │  • Content Moderation — hate/violence/self-harm/sexual classification   │ │
│  └──────────────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────────┘
```

</details>

**Why this pattern?**
- **Orchestrator ≠ Agent** — the orchestrator is deterministic control logic (validation, DAG composition, verdict parsing, HITL gate). It never calls the LLM directly. All LLM reasoning lives in the 5 AIAgents.
- **Planning Agent** enables dynamic tool selection based on asset type, state, and regulatory context — it is a first-class AIAgent with its own system prompt and tools, not a method on the orchestrator.
- **Plan-driven routing** — the orchestrator parses `requiredDomains` from the Planning Agent's JSON output via `ParseRequiredDomains()` and only dispatches the investigation agents the plan identifies. If plan parsing fails, a safety fallback runs all 3 agents.
- **Fan-out** provides latency reduction through concurrent investigation agent execution (only for required domains)
- **Reflection Agent** is a standalone AIAgent that receives raw asset profile metadata alongside investigation findings, catches contradictions, applies confidence penalties, and enables self-correction grounded in actual asset characteristics — it is not "orchestration logic", it is LLM reasoning.
- **Quality Gate (LLM-as-judge)** — after Reflection, `VerdictGroundednessEvaluator` sends the verdict and findings to a separate LLM call that scores groundedness (1-5). Below-threshold verdicts are auto-escalated to `NeedsHumanReview`. Fail-open design: if the judge call fails, the gate defaults to pass.
- **HITL gate** — after verdict parsing, the orchestrator escalates `NeedsHumanReview` verdicts (confidence < 0.75) to `IHumanReviewService` for human decision before returning.
- **MCP and RAG are distinct concerns** — MCP is the transport protocol for tool discovery and invocation; RAG is a knowledge retrieval pattern that happens to run inside one MCP tool (`QueryKnowledgeBase`). Other MCP tools are direct data lookups, not RAG.
- **Real tool call counting** — `CountActualToolCalls()` counts `FunctionCallContent` items from `ChatResponse.Messages` instead of string heuristics
- This is a proven pattern from the Microsoft Agent Framework SDK guidance for complex decision-making workflows

### Framework Decision: Microsoft Agent Framework

Microsoft Agent Framework was chosen as the foundation for this agentic AI solution. As of 2026, Microsoft has consolidated AutoGen and Semantic Kernel into the Agent Framework as the unified SDK for building AI agents and workflows. Official migration guides confirm this direction:

- [AutoGen → Agent Framework Migration](https://learn.microsoft.com/en-us/agent-framework/migration-guide/from-autogen/)
- [Semantic Kernel → Agent Framework Migration](https://learn.microsoft.com/en-us/agent-framework/migration-guide/from-semantic-kernel/)

**Key capabilities used:**
- `AIAgent` / `ChatClientAgent` — unified agent abstraction with automatic tool-calling loops via `IChatClient.AsAIAgent()`
- `WorkflowBuilder` + `Executor` — typed DAG orchestration for the Plan → Investigate → Reflect pipeline
- `Microsoft.Extensions.AI` (`IChatClient`) — provider-agnostic LLM abstraction (Azure OpenAI)
- MCP tool integration — first-class support for Model Context Protocol servers

**Implementation note:** The workflow executors use the higher-level `AIAgent` API (via `IChatClient.AsAIAgent`), which handles tool-calling loops and session management automatically — replacing raw `IChatClient.GetResponseAsync` calls with manual message construction.

### MCP Architecture (Client-Server Tool Integration)

```
┌─────────────────────────────┐     HTTP/SSE      ┌─────────────────────────────┐
│     Application Layer       │◄──────────────────►│       MCP Server            │
│                             │                    │                             │
│  McpToolProvider            │   ListTools        │  AssetProfileTools          │
│    → HttpClientTransport    │───────────────────►│  LegalTools                 │
│    → McpClient.CreateAsync  │   CallTool         │  ValuationTools             │
│    → ListToolsAsync()       │───────────────────►│  OccupancyTools             │
│    → McpClientTool as       │   Result           │  RAGTools                   │
│      AITool                 │◄───────────────────│                             │
│                             │                    │  DI: IProviders → Mock/Real │
└─────────────────────────────┘                    └─────────────────────────────┘
```

**Why MCP?**
- **Decoupled tool execution**: Tools run in a separate process with independent lifecycle
- **Protocol standardization**: MCP is the emerging standard for AI tool integration
- **Hot-swappable backends**: Replace mock providers with real APIs (title search, AVM, etc.) without changing agents
- **Tool discovery**: Agents discover available tools at runtime via `ListToolsAsync()`
- **Type-safe integration**: `McpClientTool` implements `AITool` → direct integration with `IChatClient`
- **N:M integration**: Real vendor MCP providers (e.g., `McpTitleSearchProvider`) connect to external vendor MCP servers, replacing direct REST/SOAP calls with a single MCP transport layer

### MCP Authentication

- **MCP Server** (inbound): Bearer token middleware in `McpServer/Program.cs` checks `McpServer:ApiKey` config against `Authorization: Bearer <token>` header on all `/mcp`, `/sse`, and `/` paths
- **MCP Client** (outbound): `McpToolProvider` and `McpTitleSearchProvider` inject Bearer tokens via `AdditionalHeaders` on `HttpClientTransportOptions`
- Both sides are configurable; auth is skipped if no API key is configured (dev/test mode)

### IMcpToolProvider Interface

`McpToolProvider` is a sealed class supporting **multiple MCP server endpoints**. Each logical server (Legal, Valuation, Occupancy, AssetProfile, KnowledgeBase) can map to a separate vendor endpoint. Connections are deduplicated when multiple logical servers share the same endpoint (development mode). `IMcpToolProvider` interface (`InitializeAsync`, `GetToolsForOrchestrator`, `GetToolsForLegalAgent`, `GetToolsForValuationAgent`, `GetToolsForOccupancyAgent`, `GetAllTools`) is extracted for unit test mockability with NSubstitute. The orchestrator depends on `IMcpToolProvider`, not the concrete class.

### MCP Provider Pattern (Replacing Direct API Calls)

Instead of each provider making direct REST/SOAP calls to vendor APIs, providers are implemented as MCP clients:

```
┌──────────────────────────┐      MCP (StreamableHttp)      ┌─────────────────────────┐
│  McpTitleSearchProvider  │ ────────────────────────────►  │  Vendor Title MCP Server │
│  (ITitleSearchProvider)  │      Bearer token auth          │  (search_title tool)     │
│  Lazy init + SemaphoreSlim  │                              │                          │
│  Timeout per call        │ ◄────────────────────────────   │  Real title search data  │
└──────────────────────────┘      JSON response              └─────────────────────────┘
```

The `InfrastructureRegistration.cs` supports three modes via config flags:
- `useMockProviders: true` — All 7 mock providers (default, for development)
- `useMcpProviders: true` — `McpTitleSearchProvider` + mock fallbacks for vendors not yet onboarded
- Configuration: `McpProviderOptions` binds from `CTLAgent:McpProviders` section (`TitleSearchMcpEndpoint`, `ApiKey`, `TimeoutSeconds`, etc.)

Additionally, `IAssetProfileProvider` supports a **direct HTTP mode** independent of the mock/MCP toggle:
- When `AssetDomainService:BaseUrl` is configured, `HttpAssetProfileProvider` is registered with `IHttpClientFactory` typed-client pattern
- Resilience pipeline (retry + circuit breaker + timeout) via `Microsoft.Extensions.Http.Resilience` `AddStandardResilienceHandler()`
- Auth via `AzureIdentityAuthHandler` (`DelegatingHandler`): acquires OAuth 2.0 tokens from `DefaultAzureCredential` per-request (production), or `ApiKeyAuthHandler` which sends the shared key as `X-Api-Key` (dev/test) — controlled by `UseAzureIdentity` toggle
- Short-TTL in-process response cache guards against accidental duplicate calls within a single evaluation. `CacheTtlSeconds` (default 600s, set to 0 to disable) and `CacheMaxEntries` (default 256) are configurable.
- Falls back to `MockAssetProfileProvider` when `BaseUrl` is empty/missing

### Asset Domain Service — Self-hosted REST backend (Docker)

The solution ships a self-hosted **Asset Domain Service** (`src/Cascade.CTL.AssetService`) so the orchestrator's HTTP provider can be exercised against a production-shaped backend (containerized, API-key-authenticated, health-checked) rather than an in-process mock. It is a minimal ASP.NET Core Web API that `HttpAssetProfileProvider` targets when `AssetDomainService:BaseUrl` is set.

```
┌──────────────────────────┐      ┌──────────────────────────┐      ┌────────────────────────────┐
│  CTLWorkflowOrchestrator │      │  HttpAssetProfileProvider│      │  AssetService (Docker)       │
│  (pre-fetch once per     │─────▶│  (typed HttpClient)      │─────▶│  Minimal API (:5100→:8080) │
│   evaluation)            │      │  + ApiKeyAuthHandler     │      │  ApiKeyAuthenticationMw    │
│                          │      │  + resilience + cache    │      │  InMemoryAssetRepository   │
└──────────────────────────┘      └──────────────────────────┘      └────────────────────────────┘
                                              │                                  ▲
                                              │  GET /api/assets/{id}            │
                                              │  X-Api-Key: <shared-key>         │
                                              └──────────────────────────────────┘
```

**Design decisions:**
- **Single pre-fetch, deterministic grounding.** The orchestrator retrieves the asset profile exactly once per evaluation via `IAssetProfileProvider` and injects the full JSON into both the Planning and Reflection prompts. `GetAssetProfile` is **not** in `GetToolsForOrchestrator()` — the agent never calls it. This eliminates redundant tool-call round trips, saves input/output tokens, and mitigates the "LLM skips tool call" threat documented in the threat catalog (the model can't skip a tool that isn't offered, and it can't miss data that's already inlined). The `AssetProfileTools` MCP tool class remains in the server catalog as a general capability but is not advertised to any agent. See `ToolFilters.IsOrchestratorTool` for the enforced allow-list.
- **Authentication.** The server validates the `X-Api-Key` header using `CryptographicOperations.FixedTimeEquals` to prevent timing attacks. `/health` is allow-listed for Docker health checks. Anonymous access is disabled — a missing or wrong key returns `401`.
- **Containerization.** Multi-stage Dockerfile on `mcr.microsoft.com/dotnet/sdk:8.0` → `aspnet:8.0`, non-root user, container health check hits `/health`. `docker-compose.yml` at repo root maps container `:8080` to host `:5100` and injects `ASSETDOMAIN_API_KEY` via environment.
- **Enabling the backend.** Set `AssetDomainService:BaseUrl=http://localhost:5100` and `AssetDomainService:ApiKey=<same-as-compose>` in configuration to switch `IAssetProfileProvider` from the in-memory mock to the real HTTP backend. No other code changes required.

> **Where the genuine agent-driven MCP experience lives:** the Legal, Valuation, and Occupancy tools — each chosen dynamically by the investigation agents based on the plan produced by the orchestrator. Those are the calls that genuinely require tool discovery and agent reasoning. `GetAssetProfile` was never in that category (it was always a fixed up-front read), so routing it through an agent tool added cost without adding agency.

### Post-Reflection Quality Gate — LLM-as-Judge Groundedness Check

After the orchestrator's Reflection phase produces a verdict (Phase 3), a **production quality gate** verifies that the verdict is actually grounded in the investigation findings. `VerdictGroundednessEvaluator` sends the verdict and findings to a separate LLM call acting as an impartial judge, scoring groundedness from 1 (fabricated) to 5 (fully grounded). If the score falls below the configured threshold (`QualityGate:MinGroundednessScore`, default 3), the verdict is automatically escalated to `NeedsHumanReview`.

- **Fail-open design:** If the judge LLM call fails (timeout, rate limit, etc.), the gate defaults to pass — production flow is never blocked by a non-critical evaluation.
- **Skipped for `NeedsHumanReview`:** Verdicts already requiring human review bypass the gate (no value in judging an already-escalated verdict).
- **Configurable:** `CTLAgent:QualityGate:Enabled` (default `true`) and `CTLAgent:QualityGate:MinGroundednessScore` (default `3`).
- **Audit trail:** Every quality gate evaluation is recorded as a `QualityGateEvaluated` audit entry with score, threshold, and judge reasoning.

### MCP vs RAG — How They Work Together

MCP and RAG are not competing concepts — they operate at different layers of the architecture and work together.

- **MCP** (Model Context Protocol) is the **transport layer** — it defines how agents discover and invoke tools over HTTP
- **RAG** (Retrieval-Augmented Generation) is a **data retrieval pattern** — it defines how knowledge is searched and returned to ground LLM reasoning

In this system, RAG runs **inside** one of the MCP tools:

```
┌──────────────────────────────────────────────────────────────────┐
│  CTL Agent (LLM)                                                 │
│  "I need Texas foreclosure policies for this asset"              │
│                         │                                        │
│                         ▼                                        │
│  MCP Tool Call: RAGTools.QueryKnowledgeBase(                     │
│      query: "foreclosure requirements",                          │
│      stateCode: "TX", assetType: "Foreclosure"                   │
│  )                                                               │
└─────────────────────────┬────────────────────────────────────────┘
                          │  MCP protocol (HTTP/SSE)
                          ▼
┌──────────────────────────────────────────────────────────────────┐
│  MCP Server (:5100)                                              │
│                         │                                        │
│                         ▼                                        │
│  RAGTools.cs receives the MCP call                               │
│  → delegates to IRAGQueryService.QueryAsync()                    │
│                         │                                        │
│                         ▼                                        │
│  InMemoryRAGService                                              │
│  → loads policy docs from config/rag-knowledge/*.json            │
│  → filters by state/county/assetType metadata                    │
│  → keyword scoring against query terms                           │
│  → returns top-5 matching documents                              │
└──────────────────────────────────────────────────────────────────┘
```

The other MCP tools (`SearchTitle`, `GetBPO`, `GetOccupancyStatus`, etc.) are **not RAG** — they are direct data lookups against provider APIs (mock or real). Only `QueryKnowledgeBase` implements the RAG pattern.

**Summary of concerns:**

| Layer | Technology | Purpose |
|-------|-----------|--------|
| Transport | MCP (HTTP/SSE) | Agent discovers and invokes tools |
| Tool execution | MCP Server + DI providers | Tools run business logic |
| Knowledge retrieval | RAG (inside QueryKnowledgeBase tool) | Searches policy documents to ground LLM reasoning |
| Data lookup | Provider interfaces (ITitleSearchProvider, etc.) | Returns structured data from vendors or mocks |

### IChatClient Middleware Pipeline

```
Azure AI Foundry Endpoint
        │
        ▼
┌─────────────────────────┐
│   OpenAIClient          │  OpenAI SDK
│   .GetChatClient()      │
│   .AsIChatClient()      │  Microsoft.Extensions.AI.OpenAI
└───────────┬─────────────┘
            │
┌───────────▼─────────────┐
│   OpenTelemetry         │  .UseOpenTelemetry()
│   Tracing + Metrics     │
└───────────┬─────────────┘
            │
┌───────────▼─────────────┐
│   FunctionInvocation    │  .UseFunctionInvocation()
│   Auto tool calling     │  Handles MCP tool execution
└───────────┬─────────────┘
            │
┌───────────▼─────────────┐
│   GuardrailsMiddleware    │  DelegatingChatClient
│   - Input screening     │
│   - PII masking         │
│   - Token budget        │
│   - Output screening    │
└─────────────────────────┘
            │
            ▼
      Application Code
   (Orchestrator/Agents)
```

**Why DelegatingChatClient?**
- `Microsoft.Extensions.AI` provides the middleware pattern via `ChatClientBuilder`
- Every LLM call passes through the pipeline transparently
- Guardrails are enforced consistently without agent code needing to know
- The pipeline is composable and testable

### Guardrails Pipeline

The guardrails implement a **3-tier defense model** against prompt injection:

```
┌──────────────────────────────────────────────────────────────┐
│ Tier 3: System Prompt Hardening                              │
│   All 5 prompts include ## Security Constraints section      │
│   Rules: ADVISORY ONLY, no role deviation, no prompt leaks,  │
│          no code execution, ignore suspicious tool output    │
└──────────────────────────────────────────────────────────────┘
                            │
Input Text                  │
    │                       │
    ├──► CTLRequestValidator (system boundary)
    │      → Validate asset ID format, required fields
    │      → Reject malformed requests before LLM call
    │
    ├──► Tier 1: Local Prompt Injection Detection (10 regex patterns)
    │      → Zero-latency regex-based blocking
    │      → Catches role hijack, instruction override, delimiter escape, etc.
    │
    ├──► Tier 2: Azure Prompt Shields (ML-based, REST API)
    │      → POST /contentsafety/text:shieldPrompt?api-version=2024-09-01
    │      → Direct attack detection on user prompts
    │      → Indirect attack detection on tool outputs (documents parameter)
    │      → Auth via DefaultAzureCredential (cognitiveservices.azure.com scope)
    │      → Enabled/disabled via ContentSafety:PromptShieldsEnabled config
    │
    ├──► PII Masking (SSN, CC, email, phone)
    │      → Mask sensitive data in user/tool messages before sending to LLM
    │      → Also masks PII in LLM output responses
    │
    ├──► Content Safety (Azure AI Content Safety + circuit breaker, or local fallback)
    │      → Block if content safety flags triggered
    │      → Circuit breaker opens after 5 consecutive Azure failures
    │
    └──► Input Max-Length Validation (MCP Server tools)
           → parcelId/assetId ≤ 50 chars, propertyAddress ≤ 500, county ≤ 100, query ≤ 2000
           → Returns structured error JSON on violation

Tool Results (indirect injection surface)
    │
    └──► Tier 2: Prompt Shields (ScreenToolResultAsync)
           → Tool output passed as documents[] parameter
           → Detects indirect prompt injection in external data

LLM Response
    │
    ├──► PII Masking (output)
    │      → Masks SSN, CC, email, phone in LLM responses
    │
    ├──► Token Budget Enforcement
    │      → Track cumulative usage, block if budget exceeded
    │
    └──► Audit Logging
           → Record all interactions for compliance
```

Key enterprise hardening:
- **Exception messages never leak** to client responses — agent errors return generic degraded JSON
- **CTLRequestValidator** validates at orchestrator entry point, not just guardrails middleware
- **PiiFilter** is fully wired into `GuardrailsMiddleware` (both input and output paths)
- **All MCP tools** enforce max-length on every string parameter

## Design Decisions

### 1. Dual Orchestration: Imperative (`Task.WhenAll`) and Workflow (Agent Framework)

Two orchestration strategies are available, selected at runtime via `CTLAgent:UseWorkflowOrchestrator`:

| Aspect | Imperative (default) | Workflow |
|--------|---------------------|----------|
| Implementation | `CTLEvaluationOrchestrator` | `CTLWorkflowOrchestrator` |
| Fan-out | `Task.WhenAll()` | Per-phase `WorkflowBuilder` → `InProcessExecution.RunAsync()` |
| Executor model | Inline async methods | Typed `Executor` subclasses (`PlanningExecutor`, `InvestigationPhaseExecutor`, `ReflectionExecutor`) |
| Framework | Raw .NET async | Microsoft Agent Framework Workflows (`Microsoft.Agents.AI.Workflows` v1.1.0) |
| Interface | `ICTLEvaluationOrchestrator` | `ICTLEvaluationOrchestrator` |

**Why both?** The imperative approach is simpler for the current 4-phase linear pipeline. The workflow approach demonstrates Agent Framework Workflows integration and provides a foundation for more complex graph topologies (e.g., conditional edges, fan-in barriers). Runtime flip allows evaluation of both without code changes.

**Runtime flip:**
- `appsettings.json`: `"UseWorkflowOrchestrator": true`
- Environment variable: `CTL_CTLAgent__UseWorkflowOrchestrator=true`
- DI resolves `ICTLEvaluationOrchestrator` to the appropriate concrete type at startup

### Workflow Executor Architecture

The Workflow orchestrator wraps the 5 AIAgents inside typed `Executor` nodes connected via a `WorkflowBuilder` DAG. The orchestrator owns the DAG wiring — the executors own the LLM reasoning.

```
CTLWorkflowOrchestrator (deterministic control)
    │
    ├── Validates request, retrieves asset profile, screens for injection
    │
    ├── Composes WorkflowBuilder DAG:
    │
    │   ┌────────────────────────────────────────────────────────────────┐
    │   │  PlanningExecutor (Executor<PlanRequest, PlanResult>)         │
    │   │    └─ Planning AIAgent (IChatClient.AsAIAgent) + MCP Tools    │
    │   └─────────────────────┬──────────────────────────────────────────┘
    │                         │ PlanResult flows via edge
    │   ┌─────────────────────▼──────────────────────────────────────────┐
    │   │  InvestigationPhaseExecutor (receives PlanResult)             │
    │   │    └─ Internal Task.WhenAll fan-out across 3 AIAgents         │
    │   │       (Legal, Valuation, Occupancy — per required domains)    │
    │   └─────────────────────┬──────────────────────────────────────────┘
    │                         │ InvestigationPhaseResult flows via edge
    │   ┌─────────────────────▼──────────────────────────────────────────┐
    │   │  ReflectionExecutor (receives InvestigationPhaseResult)       │
    │   │    └─ Reflection AIAgent (IChatClient.AsAIAgent)              │
    │   │       Generates CTLVerdictDto JSON                            │
    │   └────────────────────────────────────────────────────────────────┘
    │
    ├── Parses verdict JSON from reflection output
    │
    └── HITL gate: if NeedsHumanReview → IHumanReviewService
```

Each executor extends `Microsoft.Agents.AI.Workflows.Executor` with `ConfigureProtocol()` defining typed input/output routes via `RouteBuilder.AddHandler<TInput, TResult>()`. The three executors are wired into a single connected graph:

```csharp
var workflow = new WorkflowBuilder(planningExecutor)
    .AddEdge(planningExecutor, investigationExecutor)   // PlanResult flows via edge
    .AddEdge(investigationExecutor, reflectionExecutor) // InvestigationPhaseResult flows via edge
    .WithOutputFrom(reflectionExecutor)
    .Build();

var run = await InProcessExecution.RunAsync(workflow, input, sessionId, ct);
```

One `Build()`, one `RunAsync()` — the framework manages the entire plan → investigate → reflect pipeline.

### 2. Concrete `McpClient` over `IMcpClient`

MCP SDK 1.2.0 provides `McpClient` as a concrete class with `CreateAsync` factory method. The `IMcpClient` interface exists but all client-facing APIs (`ListToolsAsync`, `CreateAsync`) are on the concrete class. We follow the SDK's intended usage pattern.

### 3. `HttpClientTransport` with SSE mode

MCP SDK 1.2.0 unified transports under `HttpClientTransport` with `HttpTransportMode` enum (AutoDetect, StreamableHttp, Sse). Our MCP server uses `WithHttpTransport()` which supports SSE, so we specify `HttpTransportMode.Sse` explicitly rather than relying on auto-detection.

### 4. InMemory RAG over Azure AI Search

For local development and testing, `InMemoryRAGService` provides policy documents with keyword-based scoring and metadata filtering. This allows the solution to demonstrate RAG-grounded planning without requiring Azure AI Search provisioning. The `IRAGQueryService` interface enables production replacement.

**Current (demo):** 9 original policy documents covering CTL baseline, CWCOT program, FHA timelines, Texas foreclosure, California REO, valuation standards, occupancy/preservation, HOA verification, title clearance, and REO disposition. Documents are loaded from `config/rag-knowledge/*.json` at startup. If no JSON files are found, hardcoded fallback documents are used.

**Document format:** Each JSON file follows the `RAGDocument` schema (`Id`, `Title`, `Content`, `State`, `County`, `AssetType`, `PolicyType`). Metadata fields enable pre-filtering before keyword scoring — a Texas foreclosure query won't return California REO policies.

## Production RAG Architecture

The demo uses in-memory keyword scoring. A production system uses a **two-pipeline architecture** — the ingestion pipeline and the query pipeline are completely separate:

```
═══════════════════════════════════════════════════════════════════
  INGESTION PIPELINE (runs on schedule — daily/weekly/on-change)
═══════════════════════════════════════════════════════════════════

  ┌─────────────┐    ┌──────────────┐    ┌────────────────┐
  │ Source       │    │ Parser       │    │ Chunker        │
  │              │    │              │    │                │
  │ HUD.gov      │───►│ HTML / PDF   │───►│ Split into     │
  │ State sites  │    │ extraction   │    │ ~512-token     │
  │ Internal     │    │              │    │ clause-level   │
  │ policy docs  │    │              │    │ chunks         │
  └─────────────┘    └──────────────┘    └───────┬────────┘
                                                  │
                                                  ▼
                                         ┌────────────────┐
                                         │ Embedding      │
                                         │ Model          │
                                         │                │
                                         │ text-embedding │
                                         │ -3-large       │
                                         │ (Azure OpenAI) │
                                         └───────┬────────┘
                                                  │
                                                  ▼
                                         ┌────────────────┐
                                         │ Vector Index   │
                                         │                │
                                         │ Azure AI Search│
                                         │ (BM25 + vector │
                                         │  hybrid index) │
                                         └────────────────┘

═══════════════════════════════════════════════════════════════════
  QUERY PIPELINE (runs on every agent request — real-time)
═══════════════════════════════════════════════════════════════════

  ┌─────────────┐    ┌──────────────┐    ┌────────────────┐
  │ Agent        │    │ Embedding    │    │ Azure AI Search│
  │ query:       │───►│ Model        │───►│                │
  │ "TX forc-    │    │ (same model  │    │ Hybrid search: │
  │  losure      │    │  as ingest)  │    │ vector cosine  │
  │  HOA lien"   │    │              │    │ + BM25 keyword │
  │              │    │              │    │ + metadata     │
  └─────────────┘    └──────────────┘    │   filter by    │
                                         │   state/county │
                                         └───────┬────────┘
                                                  │
                                                  ▼
                                         ┌────────────────┐
                                         │ Top-K chunks   │
                                         │ returned to    │
                                         │ agent as       │
                                         │ RAG context    │
                                         └────────────────┘
```

**Key principle:** Data is indexed **once** (or on a schedule). Queries search the **pre-built index**, never fetching from source on every request.

**Why two pipelines?**

| Concern | Live fetch (anti-pattern) | Pre-indexed (correct) |
|---------|--------------------------|----------------------|
| Latency | 2-10s per external fetch | <100ms index query |
| Availability | Source site down = app broken | Index always available |
| Rate limiting | External sites throttle scrapers | No external dependency |
| Search quality | Raw text, no semantic understanding | Vector + keyword hybrid ranking |
| Content preparation | Raw HTML/PDF noise | Pre-chunked, cleaned, metadata-tagged |

**Migration path from demo to production:**

| Aspect | Demo (default) | Production (built, toggle via config) |
|--------|---------------|--------------------|
| Storage | JSON files on disk | Azure AI Search index |
| Search | Keyword scoring (`InMemoryRAGService`) | Hybrid vector + BM25 (`AzureSearchRAGService`) |
| Embedding | None | `text-embedding-3-small` via Azure OpenAI |
| Content | 10 curated policy JSONs | Same 10 JSONs (chunked + embedded) |
| Ingestion | In-memory at startup | `Cascade.CTL.RAG.Indexer` console (on-demand / CI) |
| Interface | `IRAGQueryService` | Same `IRAGQueryService` — DI switches implementation |

**How the swap works:**

1. Run the provisioning script with default params (provisions Azure AI Search + Azure OpenAI embedding endpoint).
2. Run the indexer console to chunk, embed, and push the JSONs to Azure AI Search:

    ```powershell
    dotnet run --project src/Cascade.CTL.RAG.Indexer -- `
        --knowledge-path ./config/rag-knowledge `
        --recreate-index
    ```

3. Set `CTLAgent:RAG:AzureSearch:Enabled = true` in `appsettings.json` (the provisioning script does this automatically).
4. Restart the host — `InfrastructureRegistration.CreateRAGService` inspects the flag and swaps in `AzureSearchRAGService`. On initialization failure the factory logs a warning and falls back to `InMemoryRAGService` so the solution never hard-crashes on a bad Search config.

**What the production pipeline does concretely:**

- **Chunker (`PolicyDocumentChunker`)** — paragraph → sentence → hard-split fallback with 1500-char chunks and 150-char overlap. Chunks smaller than `MinCharsToChunk` (1200) are emitted whole to preserve short policy sections verbatim.
- **Embedding (`AzureOpenAIEmbeddingGenerator`)** — wraps Azure OpenAI `text-embedding-3-small` (1536 dims, free-tier friendly). Batch-embeds up to 100 chunks per call.
- **Index schema (`SearchIndexSchema`)** — HNSW vector config (M=4, EfConstruction=400, EfSearch=500, cosine metric) + BM25 scoring profile on `content`, searchable facets `state`, `county`, `assetType`, `tags`.
- **Query (`AzureSearchRAGService`)** — embeds the query, issues a `VectorizedQuery` + BM25 keyword query in a single hybrid search, with an OData filter that mirrors `InMemoryRAGService` "ALL" tolerance (documents match if any tagged state/county/asset type equals the filter).

### 5. Central Package Management

`Directory.Packages.props` governs all NuGet versions across 8 projects. This ensures version consistency and simplifies upgrades. Key constraint: MCP SDK 1.2.0 requires `Microsoft.Extensions.AI.Abstractions >= 10.4.1`.

### 6. Mock Provider Strategy

Each mock provider returns different results based on asset ID prefixes:
- `TX-001`: Clean path (all clear, no issues)
- `CA-002`: Contradictions (clear title but HOA delinquency, stale BPO, occupied)
- `FL-003`: Unknowns (title defect, no BPO, unknown occupancy)

This enables testing all three verdict outcomes (Clear, ClearWithConditions/NotClear, NeedsHumanReview) without Azure services.

## Production Readiness Checklist

- [x] Multi-agent orchestration with concurrent execution
- [x] MCP client/server for tool integration
- [x] Enterprise guardrails (injection detection, PII masking, content safety, token budget)
- [x] 3-tier prompt injection defense (local regex, Azure Prompt Shields, system prompt hardening)
- [x] Azure Prompt Shields integration (direct + indirect attack detection via REST API)
- [x] System prompt hardening (all 5 prompts with Security Constraints sections)
- [x] Structured audit logging
- [x] Microsoft AI Evaluators (Groundedness + Relevance) for reflection quality scoring
- [x] OpenTelemetry observability
- [x] 242 unit tests passing
- [x] 2 evaluation cases
- [x] Central Package Management
- [x] Configuration-driven provider selection
- [x] Error handling with graceful degradation
- [x] IMcpToolProvider interface for testability
- [x] Plan-driven agent routing (ParseRequiredDomains)
- [x] Human-in-the-Loop (HITL) gate for NeedsHumanReview verdicts
- [x] Distributed resilience (see below)
- [ ] Azure AI Search integration (replace InMemoryRAG)
- [ ] Azure Cosmos DB audit persistence
- [ ] Azure Service Bus trigger
- [ ] Integration tests with live Azure AI Foundry
- [ ] Load testing and token budget tuning

## Resilience & Fault Handling

The solution implements enterprise-grade distributed resilience at every layer, following industry best practices for transient and non-transient fault tolerance.

### Transient Fault Classification

All resilience decisions use a centralized `IsTransient(Exception)` classifier:
- **Transient (retried)**: HTTP 429/5xx, `TimeoutException`, `IOException`, `SocketException`, `TaskCanceledException` with timeout inner
- **Non-transient (fail-fast)**: HTTP 4xx (except 429), `OperationCanceledException` (caller cancellation), `ArgumentException`, `InvalidOperationException`

### Layer-by-Layer Resilience

| Layer | Pattern | Configuration |
|-------|---------|---------------|
| **Orchestrator (Phase)** | Per-phase timeout (CancellationTokenSource) | `OrchestratorPhaseTimeoutSeconds` (default: 90s) |
| **Orchestrator (Agent)** | Retry with exponential backoff + audit trail | `AgentMaxRetryAttempts` (default: 2 retries = 3 total attempts) |
| **MCP Tool Provider Init** | Retry with exponential backoff + timeout | `McpInitMaxRetryAttempts` (default: 3), `McpInitTimeoutSeconds` (default: 30s) |
| **Content Safety Guard** | Circuit breaker + per-call timeout | `CircuitBreakerThreshold` (default: 5), `CircuitBreakerDurationSeconds` (default: 60s), `TimeoutSeconds` (default: 10s) |
| **MCP Server Tools** | Try/catch per provider call, structured error JSON | Error includes `transient` flag for agent reasoning |
| **MCP Title Search Provider** | Timeout per-call, safe fallback on empty response | `TimeoutSeconds` (default: 30s) |

### Orchestrator Agent Retry Flow

```
Agent call attempt 1
  ├─ Success → return result
  └─ Transient failure →
       ├─ Audit "AgentRetry" event
       ├─ Exponential backoff (200ms × 2^attempt)
       └─ Agent call attempt 2
            ├─ Success → Audit "AgentRetrySucceeded" → return
            └─ Transient failure → ... up to MaxRetryAttempts
                 └─ All exhausted → Audit "AgentExhaustedRetries"
                      → Return degraded NeedsHumanReview verdict
```

Non-transient failures (e.g., HTTP 400, ArgumentException) skip retry and immediately return a degraded verdict.

### Circuit Breaker (Content Safety)

```
CLOSED (normal) → failure count tracks consecutive failures
  └─ failures ≥ threshold → OPEN (fast-fail for duration)
       └─ duration elapsed → HALF-OPEN (one probe allowed)
            ├─ probe succeeds → CLOSED
            └─ probe fails → OPEN again
```

Falls back to local prompt injection detection when circuit is open.

### MCP Server Tool Error Handling

All MCP Server tool methods wrap provider calls in try/catch, returning structured error JSON:
```json
{"error": "Title search failed", "transient": true, "detail": "HttpRequestException"}
```
The `transient` flag allows the LLM agent to reason about whether to retry the tool call.

### Configuration (appsettings.json)

All resilience parameters are externalized under the `Resilience` section:
```json
{
  "Resilience": {
    "LlmCallTimeoutSeconds": 60,
    "LlmMaxRetryAttempts": 3,
    "McpInitTimeoutSeconds": 30,
    "McpInitMaxRetryAttempts": 3,
    "OrchestratorPhaseTimeoutSeconds": 90,
    "AgentMaxRetryAttempts": 2,
    "ContentSafetyCircuitBreakerThreshold": 5,
    "ContentSafetyCircuitBreakerDurationSeconds": 60,
    "ContentSafetyTimeoutSeconds": 10
  }
}
```
