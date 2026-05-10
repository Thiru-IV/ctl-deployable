# CTL Agent — Agentic AI Building Blocks

This document maps the CTL Agent solution to the **core building blocks (capabilities/layers) of an agentic AI system**.

---

## Layered Architecture Overview

```
┌──────────────────────────────────────────────────────────────────────────────────────────┐
│                                                                                          │
│                            ┌─────────────────────────────┐                               │
│    CONSUMER                │  Asset Evaluation Request    │                               │
│                            │  (queue, API, or CLI)        │                               │
│                            └─────────────┬───────────────┘                               │
│                                          │                                               │
│  ═══════════════════════════════════════════════════════════════════════════════════════   │
│                                          │                                               │
│    SYSTEM BOUNDARY                       ▼                                               │
│    VALIDATION             ┌──────────────────────────────┐                               │
│                           │  CTLRequestValidator          │                               │
│                           │  Asset ID format, required    │                               │
│                           │  fields, structural checks    │                               │
│                           └──────────────┬───────────────┘                               │
│                                          │                                               │
│  ═══════════════════════════════════════════════════════════════════════════════════════   │
│                                          │                                               │
│    ORCHESTRATION                         ▼                                               │
│    (Deterministic          ┌─────────────────────────────────────────────────────┐        │
│     control logic —        │  CTLWorkflowOrchestrator                            │        │
│     NOT an agent)          │  ─────────────────────────────────────────────────  │        │
│                            │  • WorkflowBuilder DAG + typed Executors            │        │
│                            │  • Retrieves asset profile (IAssetProfileProvider)  │        │
│                            │  • Screens external data (ContentSafetyGuard)       │        │
│                            │  • Composes DAG of agents                           │        │
│                            │  • Parses verdict JSON                              │        │
│                            │  • HITL gate (IHumanReviewService)                  │        │
│                            └──────────────────────┬──────────────────────────────┘        │
│                                                   │                                      │
│  ═══════════════════════════════════════════════════════════════════════════════════════   │
│                                                   │                                      │
│    AGENT REASONING         ┌──────────────────────▼──────────────────────────────┐        │
│    (5 AIAgents via         │                                                     │        │
│     IChatClient.AsAIAgent) │  Phase 1: PLANNING AGENT                            │        │
│                            │    Generates verification plan (JSON)               │        │
│                            │    Outputs: requiredDomains, verificationSteps      │        │
│                            └──────────────────────┬──────────────────────────────┘        │
│                                    ┌──────────────┼──────────────┐                        │
│                                    ▼              ▼              ▼                        │
│                            ┌────────────┐ ┌────────────┐ ┌────────────┐                  │
│                            │ Phase 2a:  │ │ Phase 2b:  │ │ Phase 2c:  │  Fan-out         │
│                            │ LEGAL      │ │ VALUATION  │ │ OCCUPANCY  │  (Task.WhenAll)  │
│                            │ AGENT      │ │ AGENT      │ │ AGENT      │                  │
│                            │            │ │            │ │            │  Plan-driven:    │
│                            │ title,     │ │ price,     │ │ vacancy,   │  only agents     │
│                            │ liens,     │ │ value      │ │ condition  │  the plan        │
│                            │ HOA, code  │ │ checks     │ │ checks     │  identifies      │
│                            └─────┬──────┘ └─────┬──────┘ └─────┬──────┘                  │
│                                  └──────────────┼──────────────┘                          │
│                                                 ▼                                        │
│                            ┌────────────────────────────────────────────┐                 │
│                            │ Phase 3: REFLECTION AGENT                  │                 │
│                            │   Cross-checks findings + asset profile    │                 │
│                            │   Detects contradictions                   │                 │
│                            │   Applies confidence penalties             │                 │
│                            │   Outputs CTLVerdictDto JSON               │                 │
│                            └────────────────────────────────────────────┘                 │
│                                                                                          │
│  ═══════════════════════════════════════════════════════════════════════════════════════   │
│                                                   │                                      │
│    QUALITY ASSURANCE                              ▼                                      │
│    (LLM-as-Judge)          ┌────────────────────────────────────────────┐                 │
│                            │ VerdictGroundednessEvaluator               │                 │
│                            │   Separate LLM scores verdict 1–5         │                 │
│                            │   against investigation findings           │                 │
│                            │   Below threshold → NeedsHumanReview       │                 │
│                            │   Fail-open: gate defaults to pass on err  │                 │
│                            └────────────────────────────────────────────┘                 │
│                                                                                          │
│  ═══════════════════════════════════════════════════════════════════════════════════════   │
│                                                   │                                      │
│    HUMAN-IN-THE-LOOP                              ▼                                      │
│                            ┌────────────────────────────────────────────┐                 │
│                            │ IHumanReviewService                        │                 │
│                            │   Confidence < 0.75  → escalate           │                 │
│                            │   Quality gate fail  → escalate           │                 │
│                            │   Contradictions     → escalate           │                 │
│                            └────────────────────────────────────────────┘                 │
│                                                                                          │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## Cross-Cutting Capabilities

The following building blocks wrap around the entire stack and apply transparently to every LLM call, tool invocation, and data flow.

```
┌──────────────────────────────────────────────────────────────────────────────────────────┐
│                                                                                          │
│  ┌────────────────────────────────────────────────────────────────────────────────────┐   │
│  │  FOUNDATION MODEL (LLM)                                                            │   │
│  │                                                                                    │   │
│  │  Azure AI Foundry  →  GPT-4o  (OpenAI SDK 2.9.1)                                  │   │
│  │                                                                                    │   │
│  │  IChatClient (Microsoft.Extensions.AI)                                             │   │
│  │    Provider-agnostic abstraction — swap to Phi-4, Llama, Mistral                   │   │
│  │    without changing agent code                                                     │   │
│  │                                                                                    │   │
│  │  IChatClient.AsAIAgent()  →  AIAgent with automatic tool-calling loops             │   │
│  │                                                                                    │   │
│  │  6 LLM calls per evaluation:                                                       │   │
│  │    Planning · Legal · Valuation · Occupancy · Reflection · Quality Gate            │   │
│  └────────────────────────────────────────────────────────────────────────────────────┘   │
│                                                                                          │
│  ┌────────────────────────────────────────────────────────────────────────────────────┐   │
│  │  GUARDRAILS & SAFETY  (DelegatingChatClient middleware — every call passes through) │   │
│  │                                                                                    │   │
│  │  IChatClient Pipeline:                                                             │   │
│  │                                                                                    │   │
│  │    Azure AI Foundry                                                                │   │
│  │         │                                                                          │   │
│  │    ┌────▼────────────────────┐                                                     │   │
│  │    │ OpenTelemetry tracing   │  .UseOpenTelemetry()                                │   │
│  │    └────┬────────────────────┘                                                     │   │
│  │    ┌────▼────────────────────┐                                                     │   │
│  │    │ FunctionInvocation      │  .UseFunctionInvocation()                           │   │
│  │    │ (auto tool-calling)     │  Handles MCP tool execution                         │   │
│  │    └────┬────────────────────┘                                                     │   │
│  │    ┌────▼────────────────────┐                                                     │   │
│  │    │ GuardrailsMiddleware    │  DelegatingChatClient                               │   │
│  │    │  ├ Tier 1: Local regex  │  10 patterns — zero-latency injection blocking      │   │
│  │    │  ├ Tier 2: Azure Prompt │  ML-based direct + indirect attack detection        │   │
│  │    │  │         Shields      │  (user input AND tool output screening)             │   │
│  │    │  ├ Tier 3: System prompt│  All 5 prompts: ## Security Constraints sections    │   │
│  │    │  │         hardening    │                                                     │   │
│  │    │  ├ PII masking          │  SSN, CC, email, phone — input AND output           │   │
│  │    │  ├ Content Safety       │  Azure AI: hate/violence/self-harm/sexual           │   │
│  │    │  │ (circuit breaker)    │  5 failures → 60s open → fallback to local          │   │
│  │    │  └ Token budget         │  Per-session tracking, configurable max (50K)       │   │
│  │    └─────────────────────────┘                                                     │   │
│  │         │                                                                          │   │
│  │    Application Code (Orchestrator / Agents)                                        │   │
│  └────────────────────────────────────────────────────────────────────────────────────┘   │
│                                                                                          │
│  ┌────────────────────────────────────────────────────────────────────────────────────┐   │
│  │  TOOL INTEGRATION (MCP — Model Context Protocol 1.2.0)                             │   │
│  │                                                                                    │   │
│  │  ┌──────────────────────────┐  HTTP/SSE   ┌──────────────────────────────────────┐ │   │
│  │  │ MCP Client               │◄───────────►│ MCP Server (separate process)        │ │   │
│  │  │ (McpToolProvider)        │             │                                      │ │   │
│  │  │                          │             │ LegalTools:                           │ │   │
│  │  │ Multi-endpoint support   │  ListTools  │   SearchTitle                        │ │   │
│  │  │ HttpClientTransport      │────────────►│   CheckHOADelinquency               │ │   │
│  │  │ Per-agent tool filtering:│  CallTool   │   LookupCodeViolations              │ │   │
│  │  │  GetToolsForLegalAgent() │────────────►│                                      │ │   │
│  │  │  GetToolsForValuation..()│  Result     │ ValuationTools:                      │ │   │
│  │  │  GetToolsForOccupancy..()│◄────────────│   RetrieveBPO, GetAVM               │ │   │
│  │  │                          │             │                                      │ │   │
│  │  │ McpClientTool → AITool   │             │ OccupancyTools:                      │ │   │
│  │  │ (direct framework        │             │   GetOccupancyStatus                 │ │   │
│  │  │  integration)            │             │                                      │ │   │
│  │  └──────────────────────────┘             │ RAGTools:                             │ │   │
│  │                                           │   QueryKnowledgeBase                 │ │   │
│  │  Provider backends (3 modes):             │                                      │ │   │
│  │    DEV  → 7 Mock providers                │ AssetProfileTools:                   │ │   │
│  │    MCP  → McpTitleSearchProvider          │   GetAssetProfile (not agent-facing) │ │   │
│  │           + mock fallbacks                │                                      │ │   │
│  │    HTTP → HttpAssetProfileProvider        │ DI: IProviders → Mock / Real         │ │   │
│  │           (IHttpClientFactory + resilience)│ Bearer token auth                   │ │   │
│  │                                           └──────────────────────────────────────┘ │   │
│  └────────────────────────────────────────────────────────────────────────────────────┘   │
│                                                                                          │
│  ┌────────────────────────────────────────────────────────────────────────────────────┐   │
│  │  KNOWLEDGE & MEMORY (RAG — Retrieval-Augmented Generation)                         │   │
│  │                                                                                    │   │
│  │  Exposed via MCP tool: QueryKnowledgeBase                                          │   │
│  │  Interface: IRAGQueryService — DI-swappable                                        │   │
│  │                                                                                    │   │
│  │  ┌───────────────────────────────┐    ┌───────────────────────────────────────────┐ │   │
│  │  │  InMemoryRAGService (dev)     │    │  AzureSearchRAGService (prod)             │ │   │
│  │  │                               │    │                                           │ │   │
│  │  │  10 JSON policy docs          │    │  Hybrid search:                           │ │   │
│  │  │  config/rag-knowledge/*.json  │    │    HNSW vector (text-embedding-3-small)   │ │   │
│  │  │  Keyword scoring              │    │    + BM25 keyword                         │ │   │
│  │  │  Metadata filtering           │    │    + OData metadata filters               │ │   │
│  │  │  (state, county, assetType)   │    │                                           │ │   │
│  │  └───────────────────────────────┘    │  Indexer: Cascade.CTL.RAG.Indexer         │ │   │
│  │                                       │    PolicyDocumentChunker → Embed → Index  │ │   │
│  │  Toggle: CTLAgent:RAG:AzureSearch     │                                           │ │   │
│  │          :Enabled                     │  Auto-fallback to InMemory on failure     │ │   │
│  │                                       └───────────────────────────────────────────┘ │   │
│  │                                                                                    │   │
│  │  10 policy domains: CTL baseline, CWCOT, FHA timelines, TX foreclosure,            │   │
│  │  CA REO, valuation standards, occupancy/preservation, HOA, title clearance,        │   │
│  │  REO disposition                                                                   │   │
│  └────────────────────────────────────────────────────────────────────────────────────┘   │
│                                                                                          │
│  ┌────────────────────────────────────────────────────────────────────────────────────┐   │
│  │  OBSERVABILITY                                                                     │   │
│  │                                                                                    │   │
│  │  ┌─────────────────────────────┐    ┌──────────────────────────────────────┐        │   │
│  │  │  OpenTelemetry              │    │  Structured Audit Trail              │        │   │
│  │  │                             │    │  (IAuditService)                     │        │   │
│  │  │  Traces + Metrics           │    │                                      │        │   │
│  │  │  Sources:                   │    │  Events:                             │        │   │
│  │  │    Cascade.CTL.Agent        │    │    EvaluationStarted                 │        │   │
│  │  │    Microsoft.Extensions.AI  │    │    PlanGenerated                     │        │   │
│  │  │                             │    │    AgentCompleted                    │        │   │
│  │  │  Every LLM call traced     │    │    AgentRetry                        │        │   │
│  │  │  Every tool call timed     │    │    QualityGateEvaluated              │        │   │
│  │  │  HTTP client instrumented  │    │    EvaluationCompleted               │        │   │
│  │  │                             │    │                                      │        │   │
│  │  │  Dev:  Console exporter    │    │  Fields: SessionId, AssetId,         │        │   │
│  │  │  Prod: App Insights        │    │    AgentName, StepType, TokensUsed,  │        │   │
│  │  │                             │    │    Duration, OutputHash              │        │   │
│  │  └─────────────────────────────┘    └──────────────────────────────────────┘        │   │
│  └────────────────────────────────────────────────────────────────────────────────────┘   │
│                                                                                          │
│  ┌────────────────────────────────────────────────────────────────────────────────────┐   │
│  │  RESILIENCE & FAULT HANDLING                                                       │   │
│  │                                                                                    │   │
│  │  ┌─────────────────────────┐ ┌─────────────────────────┐ ┌──────────────────────┐  │   │
│  │  │ Orchestrator            │ │ MCP Init                │ │ Content Safety       │  │   │
│  │  │                         │ │                         │ │                      │  │   │
│  │  │ Per-phase timeout (90s) │ │ Retry: 3 attempts       │ │ Circuit breaker      │  │   │
│  │  │ Agent retry: exp back-  │ │ Timeout: 30s per init   │ │ (5 fail → 60s open)  │  │   │
│  │  │  off (200ms × 2^n)     │ │                         │ │                      │  │   │
│  │  │ Max 2 retries           │ │                         │ │ Fallback: local      │  │   │
│  │  │ Transient classifier    │ │                         │ │ regex detection      │  │   │
│  │  │  429/5xx → retry        │ │                         │ │                      │  │   │
│  │  │  4xx → fail-fast        │ │                         │ │ Per-call timeout     │  │   │
│  │  │ Degraded verdict on     │ │                         │ │ (10s)                │  │   │
│  │  │  exhaustion             │ │                         │ │                      │  │   │
│  │  └─────────────────────────┘ └─────────────────────────┘ └──────────────────────┘  │   │
│  │                                                                                    │   │
│  │  ┌─────────────────────────┐ ┌─────────────────────────┐                           │   │
│  │  │ HTTP Providers          │ │ MCP Server Tools        │  Centralized transient    │   │
│  │  │                         │ │                         │  fault classification:    │   │
│  │  │ AddStandardResilience   │ │ try/catch per call      │  IsTransient(Exception)   │   │
│  │  │ Handler (retry +        │ │ Structured error JSON   │  → 429/5xx, Timeout,      │   │
│  │  │ circuit breaker +       │ │ { transient: true }     │     IO, Socket errors     │   │
│  │  │ timeout)                │ │ Agent can reason about  │                           │   │
│  │  │                         │ │ retry from error flag   │                           │   │
│  │  └─────────────────────────┘ └─────────────────────────┘                           │   │
│  └────────────────────────────────────────────────────────────────────────────────────┘   │
│                                                                                          │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## Building Block Interaction Map

How a single "Clear to List" evaluation flows through all building blocks:

```
 REQUEST
    │
    ▼
┌────────┐   ┌─────────────┐   ┌──────────────────────────────┐   ┌────────────┐
│VALIDATE│──►│ ORCHESTRATE │──►│ AGENT REASONING              │──►│ QUALITY    │
│        │   │             │   │                               │   │ GATE       │
│Request │   │Fetch asset  │   │ Plan → Investigate → Reflect │   │            │
│Validator│  │Screen data  │   │  (5 AIAgents, 6 LLM calls)   │   │LLM-as-Judge│
│        │   │Compose DAG  │   │                               │   │Groundedness│
└────────┘   └──────┬──────┘   └──────────────┬────────────────┘   └─────┬──────┘
                    │                          │                          │
                    │            ┌─────────────┼─────────────┐           │
                    │            ▼             ▼             ▼           │
                    │     ┌──────────┐  ┌──────────┐  ┌──────────┐      │
                    │     │  TOOLS   │  │KNOWLEDGE │  │  SAFETY  │      │
                    │     │  (MCP)   │  │  (RAG)   │  │(Guardrails│     │
                    │     │          │  │          │  │ pipeline) │     │
                    │     │ 8 tools  │  │10 policy │  │3-tier     │     │
                    │     │ 3 modes  │  │ docs     │  │injection  │     │
                    │     │ per-agent│  │ 2 impls  │  │defense    │     │
                    │     │ filtering│  │          │  │PII+safety │     │
                    │     └──────────┘  └──────────┘  └──────────┘      │
                    │                                                    │
                    ▼                                                    ▼
             ┌────────────┐                                      ┌────────────┐
             │OBSERVABILITY│                                     │   HITL     │
             │             │                                     │            │
             │ OTel traces │                                     │ Escalate   │
             │ Audit trail │                                     │ to human   │
             │ Every step  │                                     │ reviewer   │
             │ logged      │                                     │            │
             └────────────┘                                      └─────┬──────┘
                                                                       │
                                                                       ▼
                                                                   RESPONSE
                                                              (CTLVerdictDto)
```

---

## Solution Layer Map

How the .NET projects map to the agentic AI building blocks:

```
Building Block                 .NET Project                              Key Classes
─────────────────────────────────────────────────────────────────────────────────────────

FOUNDATION MODEL               Cascade.CTL.Agent.Host                    ServiceRegistration
                                  └─ IChatClient pipeline                  (OpenAIClient → ChatClientBuilder)
                                  └─ DI composition root

ORCHESTRATION                  Cascade.CTL.Agent.Application             CTLWorkflowOrchestrator
                                  └─ Orchestration/Workflow/                PlanParser
                                  └─ Configuration/                       CTLAgentOptions

AGENT REASONING                Cascade.CTL.Agent.Application             OrchestratorPrompts
                                  └─ Prompts/                             InvestigationAgentPrompts
                                  └─ Orchestration/                       McpToolProvider (tool binding)

QUALITY ASSURANCE              Cascade.CTL.Agent.Application             VerdictGroundednessEvaluator
                                  └─ Orchestration/

GUARDRAILS & SAFETY            Cascade.CTL.Agent.Guardrails              GuardrailsMiddleware
                                  └─ 6 guard classes                      ContentSafetyGuard
                                                                          LocalPromptInjectionDetector
                                                                          PiiFilter
                                                                          TokenBudgetGuard
                                                                          CTLRequestValidator

TOOL INTEGRATION (MCP)         Cascade.CTL.Agent.McpServer               LegalTools, ValuationTools
                                  └─ Tools/                               OccupancyTools, RAGTools
                                                                          AssetProfileTools

KNOWLEDGE (RAG)                Cascade.CTL.Agent.Infrastructure          InMemoryRAGService
                                  └─ RAG/                                 AzureSearchRAGService
                               Cascade.CTL.RAG.Indexer                   PolicyDocumentChunker
                                  └─ Ingestion pipeline                   AzureOpenAIEmbeddingGenerator

DATA PROVIDERS                 Cascade.CTL.Agent.Infrastructure          Mock*Provider (7 providers)
                                  └─ Providers/                           HttpAssetProfileProvider
                                                                          McpTitleSearchProvider

OBSERVABILITY                  Cascade.CTL.Agent.Infrastructure          TelemetryConfiguration
                                  └─ Observability/                       ConsoleAuditService (IAuditService)

HUMAN-IN-THE-LOOP              Cascade.CTL.Agent.Infrastructure          MockHumanReviewService
                                  └─ Providers/                           (IHumanReviewService)

DOMAIN MODEL                   Cascade.CTL.Agent.Domain                  Asset, CTLVerdictDto
                                  └─ Models/, Enums/, Contracts/          CTLVerdict, AssetType

HOSTING                        Cascade.CTL.Agent.Host                    Program.cs (console CLI)
                               Cascade.CTL.AssetService                  REST API (Docker, API-key auth)

TESTING                        Cascade.CTL.Agent.Tests                   328 unit tests (xUnit)
                               Cascade.CTL.Agent.Evals                   AI evaluation suite
                                                                          (Groundedness + Relevance)
```

---

## Capability Matrix

| Building Block | Status | Dev Mode | Prod Mode |
|----------------|--------|----------|-----------|
| Foundation Model | ✅ Built | Azure AI Foundry (GPT-4o) | Same (or Phi-4/Llama via config) |
| Orchestration | ✅ Built | WorkflowBuilder DAG | Same |
| Agent Reasoning | ✅ Built | 5 AIAgents + prompts | Same |
| Tool Integration | ✅ Built | Mock providers | Real vendor MCP servers |
| Knowledge (RAG) | ✅ Built | InMemoryRAGService | AzureSearchRAGService |
| Guardrails | ✅ Built | Local regex + PII | + Azure Prompt Shields + Content Safety |
| Quality Gate | ✅ Built | LLM-as-judge (fail-open) | Same |
| Observability | ✅ Built | Console exporter + audit | App Insights + Cosmos DB |
| HITL | ✅ Built | MockHumanReviewService | Real review queue |
| Resilience | ✅ Built | Retry + circuit breaker | Same (tunable via config) |
| System Boundary | ✅ Built | CTLRequestValidator | Same |
