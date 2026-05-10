# Cascade 2.0 — CTL Agent Solution Architecture Readout

**Prepared:** April, 2026  
**Source Document:** cascade2_ctl_agent_solution_architecture.md (ARB Submission — Draft v1.0)  
**Purpose:** Executive readout and technical summary of the Asset Clear-To-List (CTL) Determination Agent architecture

---

## 1. What Is This Solution?

The **CTL Agent** is a **multi-agent AI system** that automates the Clear-To-List determination for real estate assets (foreclosures, REO, short sales) before they can be listed on Xome.com. Today, this is a manual, analyst-driven process requiring an asset manager to individually query title systems, AVM providers, field service vendors, and municipal records, stitch findings together, and make a judgment call. The CTL Agent replaces that manual investigation with an autonomous, auditable, AI-driven evaluation.

**Core outcome:** Given an asset ID, the agent produces a structured **CTL verdict** — `Clear`, `ClearWithConditions`, `NotClear`, or `NeedsHumanReview` — with a full evidence trail, confidence score, and reflection log suitable for compliance audit.

---

## 2. Technology Stack

| Layer | Technology | Version / Status |
|-------|-----------|-----------------|
| **Agent Framework** | Microsoft Agent Framework SDK | GA — October 2, 2025 (`Microsoft.Agents.AI`) |
| **AI Platform** | Azure AI Foundry (Microsoft Foundry) | Managed platform |
| **LLM** | Azure OpenAI GPT-4o | Structured outputs, Temperature 0.1 |
| **AI Abstractions** | Microsoft.Extensions.AI (`IChatClient`) | Model portability layer |
| **Foundry SDK** | Azure.AI.Projects v2 | `2.0.0b3` (Jan 2026) — unified package |
| **RAG** | Azure AI Search | Hybrid BM25 + Vector with RRF reranking |
| **Content Safety** | Azure AI Content Safety | Prompt Shields, PII detection |
| **Host Runtime** | .NET 8 Worker Service | Azure Container Apps with KEDA |
| **Workflow** | Camunda 8 | Retains deterministic workflow ownership |
| **State** | Azure Cosmos DB (Serverless) | Session-scoped, 72h TTL |
| **Observability** | OpenTelemetry + Azure Application Insights | Full distributed tracing |
| **Networking** | Private Endpoints + Azure API Management | Zero public egress to AI services |

---

## 3. Architectural Principles

The solution inherits and adheres to Cascade 2.0 platform principles:

- **Domain Driven Design** — CTL Agent is bounded within the Asset domain; no direct cross-domain data access
- **Clean Architecture** — Layered structure: API → Application → Domain → Infrastructure
- **Event-Driven** — Activation exclusively via Azure Service Bus (`CTLEvaluationRequestedEvent`)
- **Workflow-Oriented** — Camunda 8 retains complete workflow state ownership; agent is advisory only
- **Observability by Default** — Every agent step, tool call, and investigation agent boundary is traced via OpenTelemetry
- **Strictly Microsoft Azure** — No Python, no LangChain, no CrewAI, no AutoGen standalone

---

## 4. Agent Topology

### 4.1 Orchestrator + Five AIAgents

The CTL Agent separates **orchestration** (deterministic workflow composition) from **agent reasoning** (LLM-driven intelligence). The orchestrator is not an agent — it validates requests, retrieves asset profiles, screens data, composes the DAG, parses verdicts, and runs the HITL gate.

| Component | Role | Tools | Output |
|-----------|------|-------|--------|
| **CTL Orchestrator** | Deterministic workflow composer — validates, composes DAG, parses verdict, HITL gate | None (delegates to agents) | `CTLEvaluationResult` |
| **Planning Agent** | RAG-grounded dynamic verification plan generation | `RAGQueryTool`, `AssetProfileTool` | `PlanResult` (JSON) |
| **Legal & Title Investigation Agent** | Title, lien, HOA, code violation reasoning | `TitleSearchTool`, `HOADelinquencyTool`, `CodeViolationTool`, `RAGQueryTool` | `LegalFindingsReport` |
| **Valuation Readiness Investigation Agent** | BPO quality, AVM variance, staleness | `BPORetrievalTool`, `AVMTool`, `RAGQueryTool` | `ValuationFindingsReport` |
| **Occupancy & Condition Investigation Agent** | Occupancy status, property condition | `OccupancyStatusTool`, `RAGQueryTool` | `OccupancyFindingsReport` |
| **Reflection Agent** | Critiques aggregated findings against raw asset profile, detects contradictions, applies confidence penalties | None (reasoning-only) | `CTLVerdictDto` (JSON) |

All 5 agents are created via `IChatClient.AsAIAgent()` (Agent Framework SDK), GPT-4o, temperature 0.1, structured output enforcement.

### 4.2 Workflow Pattern

```
CTLEvaluationRequestedEvent (Service Bus)
    │
    ▼
Orchestrator — Validate request, retrieve asset profile, screen data
    │
    ▼
Planning Agent — Phase 1: PLAN (RAG-grounded dynamic verification plan)
    │
    ├──────────────────┼──────────────────┐
    ▼                  ▼                  ▼
Legal Agent       Valuation Agent    Occupancy Agent    ← Phase 2: CONCURRENT (Task.WhenAll)
    │                  │                  │              (only plan-required domains)
    └──────────────────┼──────────────────┘
                       ▼
Reflection Agent — Phase 3: REFLECT (critique, contradiction detection, confidence penalties)
    │
    ▼
Orchestrator — Phase 4: VERDICT (parse JSON) + Phase 5: HITL gate (if NeedsHumanReview)
    │
    ▼
Store Evidence Report → Send verdict to CamundaGateway
```

---

## 5. Four Agentic Patterns

### 5.1 Planner Pattern
The **Planning Agent** (a standalone AIAgent, not the orchestrator) dynamically constructs a verification plan by querying the RAG knowledge store against the asset's profile (type, state, county, occupancy, seller tier). A Texas Foreclosure requires different checks than a California REO. Plans evolve with the knowledge base — no code changes needed for new states or policies. The orchestrator then parses `requiredDomains` from the plan JSON to decide which investigation agents to dispatch.

### 5.2 Multi-Agent Pattern
Three specialized investigation agents run **concurrently** via `ConcurrentWorkflow`. Each has a bounded domain, its own tool set, and its own system prompt. They share no state directly — all coordination flows through the Orchestrator. Concurrency reduces total latency by ~60% vs sequential execution.

### 5.3 Tooling Pattern
Tool selection is **dynamic** — agents reason at runtime about which tools to invoke based on intermediate results. The Legal Agent invokes `HOADelinquencyTool` only if the title search indicates a potential HOA issue. Tools are `AIFunction`-wrapped, strongly typed (C# records), resilient (Polly circuit breakers), and auditable.

### 5.4 Reflection Pattern
After investigation agents return findings, the **Reflection Agent** (a standalone AIAgent, not orchestrator logic) performs a **reflection pass** — a reasoning turn that critiques aggregated evidence alongside **raw asset profile metadata** (injected via the orchestrator), detects contradictions between domains, and applies confidence penalties before committing to a verdict. Including the raw asset profile ensures the reflection is grounded in actual asset characteristics (type, state, seller tier, occupancy status) rather than relying on LLM-summarized planning output.

### 5.5 Human-in-the-Loop Pattern
When the Reflection Agent produces a `NeedsHumanReview` verdict (confidence < 0.75), the orchestrator escalates the decision to `IHumanReviewService` before returning the final result. The human reviewer can **Confirm** the verdict, **OverrideVerdict** with a different determination, or **RequestReEvaluation**. This gate is deterministic orchestrator logic, not agent reasoning — the orchestrator decides *when* to escalate, the human decides *what* to do.

**Confidence thresholds:**
- ≥ 0.90 → `Clear` or `ClearWithConditions`
- 0.75 – 0.89 → `ClearWithConditions` (forced additional disclosure)
- < 0.75 → `NeedsHumanReview` (escalation to human asset manager)

---

## 6. Tool Architecture

| Tool | External Dependency | Timeout | Failure Policy |
|------|-------------------|---------|---------------|
| `AssetProfileTool` | AssetService (internal) | 5s | **Blocking** — abort if fails |
| `RAGQueryTool` | Azure AI Search | 3s | Retry x3 → NeedsHumanReview |
| `TitleSearchTool` | Title data provider (external) | 15s | NeedsHumanReview |
| `HOADelinquencyTool` | HOA data provider (external) | 10s | Flag as unverified |
| `CodeViolationTool` | Municipal API (external) | 10s | Flag as unverified |
| `AVMTool` | AVM provider (external) | 10s | Use BPO only; flag |
| `BPORetrievalTool` | DocumentService (internal) | 5s | **Blocking** — NeedsHumanReview |
| `OccupancyStatusTool` | Field services API (external) | 15s | Flag as unverified |

**Blocking vs. Non-blocking:** If blocking tools fail, evaluation cannot proceed. Non-blocking tool failures reduce confidence score and flag unverified fields in the reflection pass.

---

## 7. RAG Architecture

- **Store:** Azure AI Search with hybrid retrieval (BM25 + Vector) and RRF reranking
- **Why hybrid:** Legal content has exact statute references that pure vector search misses; BM25 catches keywords, vector handles semantic intent
- **Metadata filtering:** Pre-filtered by `state`, `county`, `assetType` before ranking — prevents cross-jurisdiction contamination
- **Chunk size:** Clause-level (~512 tokens) for precise, actionable retrieval
- **Corpus:** CTL policies, state foreclosure statutes, county rules, seller overlays, valuation standards, occupancy procedures, historical verdicts

---

## 8. State & Memory

- **Session-scoped:** Each evaluation runs in an isolated Agent Framework session keyed by `{assetId}:{workflowInstanceId}`
- **Stored in:** Azure Cosmos DB (Serverless), 72-hour TTL
- **Checkpointing:** Agent Framework built-in — container restart resumes from last checkpoint
- **No cross-session memory (v1):** Deliberate isolation to prevent verdict contamination. Each evaluation is independently justifiable. Long-term memory deferred to v2.

---

## 9. Infrastructure

- **Hosting:** Azure Container Apps with KEDA Service Bus scaler — scale-to-zero for cost efficiency, supports long-running evaluations (up to 90s)
- **Authentication:** Azure Managed Identity for all services — zero secrets in code
- **Networking:** Private Endpoints for all Azure AI services; Azure API Management for external tool calls (rate limiting, auth, audit)
- **Idempotency:** `IdempotencyGuard` checks Cosmos DB for existing session before activation — no duplicate evaluations

---

## 10. Security — Agentic AI-Specific Threats

| Threat | Mitigation |
|--------|-----------|
| **Prompt Injection** | Azure AI Content Safety Prompt Shields with Spotlighting; external tool results treated as untrusted data, separated from system prompt |
| **Tool Misuse** | Tool input validation at AIFunction layer; all tools are read-only |
| **Data Exfiltration** | PII detection; structured logging excludes raw LLM context; App Insights sampling excludes full prompts |
| **Verdict Drift** | Foundry Evaluation monitors verdict distribution over time; model update gating process |
| **Context Window Poisoning** | Investigation agents return bounded structured JSON, not raw conversation history |

---

## 11. Observability

- **Distributed Tracing:** OpenTelemetry → Application Insights — full activity tree from Service Bus trigger through every agent step, tool call, and investigation agent boundary
- **Agent Step Tracing:** Agent Framework SDK emits OpenTelemetry spans per agent turn, tool invocation, and workflow transition natively
- **Structured Logging:** `ILogger<T>` with `assetId`, `sessionId`, `agentName`, `correlationId` on every event
- **Custom Metrics:** Evaluation duration, verdict distribution, tool call latency, tool failure rate, reflection resolution rate
- **Foundry Observability:** Azure AI Foundry portal for agent thread inspection, reasoning traces, tool call sequences

### Key Alerts

| Alert | Threshold | Severity |
|-------|----------|----------|
| High NeedsHumanReview rate | > 15% in 1 hour | Warning |
| Tool failure rate | > 5% on any tool in 15 min | Critical |
| P95 evaluation latency | > 90 seconds | Warning |
| Dead-lettered events | > 0 in 1 hour | Critical |
| LLM token consumption | > 80% hourly quota | Warning |

---

## 12. Non-Functional Requirements

| NFR | Target | Design Response |
|-----|--------|----------------|
| **Availability** | 99.5% (business hours) | Container Apps multi-replica; Service Bus buffering |
| **Latency P50** | < 45 seconds | Concurrent investigation agents; tool timeouts |
| **Latency P95** | < 90 seconds | Circuit breakers; NeedsHumanReview escalation |
| **Throughput** | 500 evaluations/day peak | KEDA auto-scaling; Azure OpenAI PTU reserved |
| **Auditability** | 7-year retention | Evidence Report in DocumentService; App Insights 90-day + archive |
| **Idempotency** | No duplicate evaluations | Cosmos DB session check before activation |
| **Graceful Degradation** | Single tool failure ≠ abort | Non-blocking failure policy; confidence penalties |
| **Explainability** | Human-readable verdict reasoning | Evidence Report with plan, findings, reflection, conditions, confidence |
| **Model Portability** | Swap LLM without code changes | `IChatClient` abstraction; configuration-only model swap |
| **Cost Predictability** | Budgeted token consumption | PTU deployment; Azure Cost Management alerts |

---

## 13. Why Agentic AI Over Traditional Rules-Based Approach?

The CTL determination could be built as a deterministic rules engine — hard-coded `if/else` branches per state, asset type, and policy. Below is a candid comparison of why the agentic (probabilistic) approach was chosen for this use case, and where it genuinely outperforms the rules-based alternative.

### Where Agentic AI Wins

| Dimension | Rules Engine (Deterministic) | Agentic AI (This Solution) |
|-----------|-----------------------------|-----------------------------|
| **Policy Complexity** | Every state × asset-type × county combination requires an explicit branch. 50 states × 3 asset types × varying county rules = thousands of hand-written rules. | The LLM reads policy documents at call time via RAG. Adding Texas-specific foreclosure rules = drop a Markdown file into the knowledge base. No code change. |
| **Ambiguity Handling** | Rules must be binary — yes/no. When a title report says "lien *possibly* released pending county recording," a rules engine cannot reason about it. | The LLM interprets nuance, weighs evidence, and expresses uncertainty via confidence scores (0.0–1.0) and `ClearWithConditions` verdicts. |
| **Cross-Domain Reasoning** | Legal findings, valuation data, and occupancy status are evaluated in isolated rule chains. Contradictions between domains (e.g., "title is clear but property is occupied by hostile party") require explicit cross-domain rules that are hard to anticipate. | The Reflection phase synthesises all three investigation agent reports together. The LLM spots contradictions the rule author never coded for, applies confidence penalties, and flags them. |
| **Regulatory Change Velocity** | New CFPB guidance or a state law change = developer sprint to update branches, write tests, deploy. Lead time: days to weeks. | New guidance = update the policy document in the RAG knowledge base. The LLM picks it up on the next evaluation. Lead time: minutes. |
| **Long-Tail Edge Cases** | Every edge case needs a rule. Unmapped scenarios fall through to a default that may be wrong. | The LLM generalises from policy context. Unmapped scenarios get lower confidence and route to `NeedsHumanReview` — a safe, graceful fallback rather than a silent wrong answer. |
| **Explainability** | Trace shows "Rule 4.2.1.b fired → NotClear." Human must reverse-engineer what the rule means. | Evidence Report contains the LLM's reasoning in natural language: *"Property has outstanding HOA super-lien under TX Property Code §209.0092; lien amount ($14,200) exceeds Cascade threshold ($10,000); recommend NotClear."* |
| **Maintenance Burden** | Rule count grows linearly with policy surface. After 2–3 years, the rule base becomes a liability — brittle, hard to test, and understood by few. | Policy surface grows in Markdown files. The LLM prompt and orchestration code remain stable. The code footprint stays small. |

### Where Rules Still Win (and This Solution Accounts for It)

| Dimension | Why Rules Are Better | How This Solution Handles It |
|-----------|---------------------|------------------------------|
| **Determinism** | Same input → guaranteed same output. Critical for compliance audit. | Temperature set to 0.1 (near-deterministic). Structured JSON output enforced. Reflection phase catches drift. Full evidence trail logged for audit. |
| **Latency** | Rules execute in milliseconds. | P50 target is 45s (acceptable for batch/async CTL workflow — not a real-time UX path). |
| **Cost** | Rules engine = CPU cycles. No per-token cost. | Token budget guard caps spend. PTU deployment provides cost predictability. Trade-off accepted: cost of one analyst hour >> cost of one LLM evaluation. |
| **Testability** | Deterministic = easy to assert exact outputs. | Evaluation test suite uses semantic assertion (confidence ranges, verdict categories) rather than exact string match. 60+ unit tests cover guardrails, domain logic, and providers. |
| **Simple Cases** | If the check is truly binary (e.g., "is the deed recorded? yes/no"), a rule is simpler and better. | Camunda workflow retains ownership of simple deterministic gates. The agent handles only the complex judgment calls that justify LLM reasoning. |

### The Bottom Line

A rules engine works when the decision space is **small, stable, and binary**. CTL determination is none of those — it spans 50+ state regulatory frameworks, three intersecting investigation domains, ambiguous third-party data, and policy documents that change quarterly. The agentic approach turns the policy documents themselves into the "rules" — the LLM reads them, reasons over them, and produces an auditable verdict, while Camunda retains workflow control and the guardrails layer enforces safety boundaries.

---

## 13. Key Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-001 | **Agent Framework SDK** over SK/AutoGen/LangChain | GA successor to both SK + AutoGen; pure .NET; enterprise-grade observability; single strategic framework |
| DD-002 | **Concurrent** investigation agents (not sequential) | Three domains are independent; ~60% latency reduction; native `ConcurrentWorkflow` support |
| DD-003 | **Structured outputs** on all agent responses | Machine-parseable for downstream systems; eliminates fragile JSON extraction; GPT-4o 100% reliable JSON conformance |
| DD-004 | **Container Apps** over App Service | Scale-to-zero; KEDA Service Bus scaler; no HTTP timeout constraints on long evaluations |
| DD-005 | **Hybrid RAG** (BM25 + Vector) | Captures both exact statute references and semantic policy understanding |
| DD-006 | **Camunda retains workflow ownership** | Agent is advisory only; no direct state mutation; compliance audit trail preserved; clean rollback path |
| DD-007 | **No cross-session memory in v1** | Prevents verdict contamination; each evaluation independently justifiable; deferred to v2 |

---

## 14. Integration Points

### Inbound
- **Trigger:** `CTLEvaluationRequestedEvent` via Azure Service Bus topic subscription
- **Payload:** `assetId`, `workflowInstanceId`, `sellerCode`, `eventTimestamp`
- **Dead letter:** Max 3 delivery attempts → dead-letter → alert (no auto-retry)

### Outbound
- `AssetService` — HTTP GET `/assets/{assetId}` (asset profile retrieval)
- `DocumentService` — HTTP POST `/documents/store` (CTL Evidence Report storage)
- `CamundaGatewayService` — HTTP POST `/workflow/message` (`CTLVerdictReceived` message + variables)

---

## 15. Risks

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|-----------|
| LLM hallucination on policy | Medium | High | RAG grounding; reflection cross-validation; temperature 0.1 |
| External tool API availability | Medium | Medium | Circuit breakers; non-blocking failure; confidence penalties |
| Azure OpenAI rate limits at peak | Medium | High | PTU reservation; KEDA queue-based scaling |
| Agent Framework breaking changes | Low | Medium | Pin NuGet version; GA follows semver |
| Verdict drift after model updates | Low | High | Foundry Evaluation weekly monitoring; model update gating |
| Prompt injection via tool responses | Low | High | Content Safety Prompt Shields; untrusted data isolation |
| Developer unfamiliarity with agentic patterns | High | Medium | Architecture doc with code examples; phased rollout |

---

## 16. ARB Compliance Summary

| Criterion | Status |
|-----------|--------|
| Cascade 2.0 principles alignment (DDD, Clean Architecture, Event-Driven, Observability) | ✅ |
| Microsoft Azure-only technology stack | ✅ |
| Agent Framework SDK — current, non-obsolete | ✅ |
| Azure OpenAI within tenant boundary (Private Endpoint) | ✅ |
| Camunda 8 retains workflow ownership | ✅ |
| Responsible AI guardrails (Content Safety, PII, Prompt Shields) | ✅ |
| Zero secrets in code (Managed Identity everywhere) | ✅ |
| Network isolation for AI services (Private Endpoints, APIM) | ✅ |
| Full auditability of agent reasoning | ✅ |
| NFRs defined and addressable | ✅ |
| Graceful degradation defined | ✅ |
| Cost model addressed (PTU, scale-to-zero, serverless) | ✅ |
| Rollback strategy (agent offline → manual task fallback) | ✅ |

---

*End of readout.*
