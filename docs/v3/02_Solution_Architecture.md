# Reference Architecture


## 1. Architecture at a Glance

```
╔════════════════════════════════════════════════════════════════════════════════════╗
║                                   CTL - AI Agent                                   ║
║                                                                                    ║
║  ┌──────────────────────────────────────────────────────────────────────────────┐  ║
║  │                             Workflow Orchestrator                            │  ║
║  │                                                                              │  ║
║  │  ┌─────────┐   ┌─────────┐   ┌──────────────┐                                │  ║
║  │  │  Asset  │──►│ Planner │──►│ Investigation│                                │  ║
║  │  │ Profile │   │  Agent  │   │   (fan-out)  │                                │  ║
║  │  └─────────┘   └─────────┘   └──────┬───────┘                                │  ║
║  │                  ┌──────────────────┼──────────────────┐                     │  ║
║  │                  ▼                  ▼                  ▼                     │  ║
║  │            ┌──────────┐       ┌───────────┐      ┌───────────┐               │  ║
║  │            │  Legal   │       │ Occupancy │      │ Valuation │               │  ║
║  │            │  Agent   │       │   Agent   │      │   Agent   │               │  ║
║  │            └────┬─────┘       └─────┬─────┘      └─────┬─────┘               │  ║
║  │                 └─────────┬─────────┴──────────────────┘                     │  ║
║  │                           ▼                                                  │  ║
║  │                  ┌──────────────┐    ┌──────────┐    ┌──────────────┐        │  ║
║  │                  │  Reflection  │───►│  Policy  │───►│   Quality    │        │  ║
║  │                  │    Agent     │    │ Enforcer │    │     Gate     │        │  ║
║  │                  └──────────────┘    └──────────┘    └──────┬───────┘        │  ║
║  │                                                      ┌──────▼──────┐         │  ║
║  │                                                      │ Human In The│         │  ║
║  │                                                      │     Loop    │         │  ║
║  │                                                      └──────┬──────┘         │  ║
║  │                                                             ▼                │  ║
║  │                                                          Verdict             │  ║
║  └──────────────────────────────────────────────────────────────────────────────┘  ║
║                                                                                    ║
║  ┌──────────────────────────────────────────────────────────────────────────────┐  ║
║  │                              MCP Tool Server                                 │  ║
║  │  ┌────────────┐  ┌────────────┐  ┌──────────┐  ┌────────────┐  ┌──────────┐  │  ║
║  │  │ Title/HOA/ │  │ Occupancy  │  │  AVM /   │  │   Asset    │  │ Knowledge│  │  ║
║  │  │   Code     │  │Verification│  │   BPO    │  │  Profiler  │  │   Base   │  │  ║
║  │  │ Violation  │  │            │  │          │  │            │  │  Query   │  │  ║
║  │  └────────────┘  └────────────┘  └──────────┘  └────────────┘  └──────────┘  │  ║
║  └──────────────────────────────────────────────────────────────────────────────┘  ║
║                                                                                    ║
║  ┌──────────────────────────────────────────────────────────────────────────────┐  ║
║  │                                    RAG                                       │  ║
║  │  ┌────────────┐   ┌────────────┐   ┌────────────┐   ┌────────────────────┐   │  ║
║  │  │   Policy   │   │  Indexing  │   │  Hybrid    │   │     Embedding      │   │  ║
║  │  │  Knowledge │   │  Pipeline  │   │ Retriever  │   │     Generator      │   │  ║
║  │  │    Base    │   │ (chunker)  │   │ (BM25+vec) │   │                    │   │  ║
║  │  └────────────┘   └────────────┘   └────────────┘   └────────────────────┘   │  ║
║  └──────────────────────────────────────────────────────────────────────────────┘  ║
║                                                                                    ║
║  ┌──────────────────────────────────────────────────────────────────────────────┐  ║
║  │                               Infrastructure                                 │  ║
║  │  ┌──────────┐ ┌─────────┐ ┌──────────┐ ┌──────────┐ ┌─────────┐ ┌──────────┐ │  ║
║  │  │  Azure   │ │  Azure  │ │ Azure AI │ │ Azure AI │ │  Azure  │ │ Azure AI │ │  ║
║  │  │ Content  │ │  Entra  │ │  Foundry │ │  Foundry │ │  App    │ │  Search  │ │  ║
║  │  │  Safety  │ │         │ │   LLM    │ │   LLM    │ │ Insights│ │ (Vector  │ │  ║
║  │  │          │ │         │ │ (Worker) │ │ (Judge)  │ │         │ │  Index)  │ │  ║
║  │  └──────────┘ └─────────┘ └──────────┘ └──────────┘ └─────────┘ └──────────┘ │  ║
║  └──────────────────────────────────────────────────────────────────────────────┘  ║
╚════════════════════════════════════════════════════════════════════════════════════╝
```

Two AI deployments are used: a **Worker** model that plans, investigates, and reflects, and an **independent Judge** model that scores groundedness in the Quality Gate. All external data access flows through the **MCP Tool Server**, and all policy knowledge flows through the **RAG layer** — adding a vendor or refreshing a policy never touches orchestration code.

---

## 2. Building Blocks

| Block                     | Responsibility                                                                              |
| ------------------------- | ------------------------------------------------------------------------------------------- |
| **Workflow Orchestrator** | Drives the 6-phase decision flow; per-phase timeouts; parallel investigation                |
| **Planner Agent**         | Reads the asset, consults RAG, decides which domain checks apply for *this* asset           |
| **Specialist Agents**     | Legal · Occupancy · Valuation — each with its own scoped toolset, run in parallel. Each emits a **domain-scoped verdict** (`domainVerdict`: Clear / ClearWithConditions / NotClear / NeedsHumanReview) plus confidence, findings, and unverified fields, bounded to its own domain.           |
| **Reflection Agent**      | Fan-in step — the sole join point where the three specialists' domain verdicts and findings meet. Applies cross-domain policy (via RAG) to the combined picture and emits the **asset-level verdict**. *Example:* Legal returns `domainVerdict: ClearWithConditions` (HOA $4,200 delinquent, payable per HOA-verification policy), Valuation returns `Clear`, Occupancy returns `Clear` → Reflection emits asset-level `ClearWithConditions`, carrying Legal's condition forward. A deterministic post-step (`DomainVerdictConflict`) audits any case where a specialist's domain verdict diverges from the final verdict. |
| **Policy Enforcer**       | Deterministic step: validates the verdict JSON, snaps confidence to a fixed scale, applies the verdict policy (e.g., low-confidence → forced human review) |
| **Quality Gate**          | LLM-as-Judge \u2014 an *independent* model (separate deployment, no tools) checks whether Reflection's verdict actually matches the specialists' findings, on a 1\u20135 groundedness scale. Below threshold \u2192 escalated to `NeedsHumanReview`. Catches hallucinated citations, ignored findings, and confidence/evidence mismatches. *Example:* verdict cites "BPO dated 2026-02" but no BPO appears in findings \u2192 score 2/5 \u2192 verdict blocked. |
| **Human In The Loop**     | Routes flagged verdicts to an analyst with the full evidence package; override is captured  |
| **MCP Tool Server**       | Single, governed entrypoint for every external lookup (title, HOA, code, BPO, AVM, occupancy, asset profile, knowledge-base query) |
| **RAG Layer**             | Policy Knowledge Base · Indexing Pipeline (chunker) · Hybrid Retriever (BM25 + vector) · Embedding Generator |
| **Guardrails Middleware** | Token budget, prompt-injection screening, PII masking on input *and* output                 |
| **Resilience Pipelines**  | Typed retries, exponential backoff, per-phase timeouts, circuit-breakers on Azure deps      |
| **Audit Sink**            | Every step / tool call / safety check captured as a structured event; one decision = one replayable record |
| **Memory**                | Stateless per asset by design — only working memory (per-run blackboard across Plan→Investigate→Reflect) and semantic memory (RAG policy corpus); no cross-run conversational memory; episodic recall deferred to Phase 2 |
| **Evaluation Harness** *(a.k.a. "evals")* | Offline scoring of groundedness and relevance against a curated asset set       |

---

## 3. Tech Stack

| Layer                  | Choice                                                                  |
| ---------------------- | ----------------------------------------------------------------------- |
| Runtime                | **.NET 9**, C#                                                          |
| AI abstraction         | **Microsoft.Extensions.AI** (`IChatClient`, `AIAgent`) — provider-portable |
| LLM provider           | **Azure AI Foundry** (worker + separate judge deployment)               |
| Tools transport        | **Model Context Protocol (MCP)** over HTTP, API-key authenticated       |
| Retrieval              | **Azure AI Search** — hybrid BM25 + vector (text-embedding-3-small)     |
| Safety                 | **Azure AI Content Safety** + Prompt Shields, **Azure Text Analytics PII** |
| Resilience             | **Polly v8** pipelines (retry, timeout, circuit-breaker)                |
| Observability          | **OpenTelemetry** → **Application Insights**, plus per-session JSONL    |
| Evaluation             | **Microsoft.Extensions.AI.Evaluation** (Groundedness, Relevance)        |
| Identity (optional)    | **DefaultAzureCredential** / Managed Identity                           |
| Tests                  | xUnit + NSubstitute                                                     |

---

## 4. Agentic AI Patterns Used

| Pattern                         | Where it’s applied in this solution                                                     |
| ------------------------------- | --------------------------------------------------------------------------------------- |
| **Planner → Executor**          | Phase 1 plans the investigation; phase 2 executes only what the plan asked for          |
| **Multi-Agent (parallel specialists)** | Legal, Valuation, Occupancy run concurrently, each with a scoped toolset and prompt |
| **Tool Use (function calling)** | All external data access is via discrete MCP tools — no free-form web access            |
| **Retrieval-Augmented Generation** | Policy knowledge is retrieved per call, not baked into the model                     |
| **Reflection**                  | The Reflection Agent reconciles specialist findings, re-grounds against policy, and emits a calibrated verdict + evidence trail |
| **LLM-as-Judge (independent)**  | A second, separate model deployment scores the verdict for groundedness in the Quality Gate    |
| **Schema-Constrained Generation** | Verdicts must conform to a strict JSON schema — no free-text drift                            |
| **Deterministic Seeding**       | Stable seed per asset → reproducible decisions                                                  |
| **Confidence Bucketing**        | Continuous AI confidence is snapped to a fixed scale by the Policy Enforcer before policy is applied |
| **Human-In-The-Loop**           | Low-confidence outcomes route to an analyst with full evidence; override is auditable           |
| **Defense-in-Depth Guardrails** | Multiple safety layers (token, prompt-injection, PII) on both input and output         |

---

## 5. Non-Functional Requirements

| NFR                | How it’s met (grounded in code)                                                                          |
| ------------------ | -------------------------------------------------------------------------------------------------------- |
| **Determinism**    | Temp = 0, fixed top-p, asset-derived seed, JSON-schema response, discrete confidence buckets             |
| **Trustworthiness**| Tools are the only source of facts; independent judge model verifies grounding before release            |
| **Security**       | Layered guardrails on every request, MCP API-key auth, optional Managed Identity, no PII in prompts/logs |
| **Resilience**     | Polly retry + exponential backoff for AI and tool calls; per-phase timeout; circuit-breakers on Azure deps; local fallbacks for safety services |
| **Observability**  | OpenTelemetry traces & metrics, structured audit events to Application Insights and JSONL                |
| **Auditability**   | One session id ties every step, tool call, and safety event into a single replayable record             |
| **Cost control**   | Hard token budget per session; only the domains the plan requires are actually executed                 |
| **Extensibility**  | New vendor = new MCP tool; new policy = new document indexed; new model = config change, not a rewrite   |
| **Portability**    | Microsoft.Extensions.AI abstraction isolates the orchestration from any single AI provider               |
| **Testability**    | Mock providers for every tool; unit tests across guardrails, orchestration, RAG, resilience; offline evaluation harness for groundedness/relevance |

---

> **In one line:** A controlled multi-agent workflow on .NET 9, with Azure AI for reasoning, MCP for tools, Azure AI Search for policy grounding, an independent judge for trust, layered guardrails for safety, and end-to-end audit for defensibility — designed to slot in alongside Cascade 2.0, not replace it.

---

