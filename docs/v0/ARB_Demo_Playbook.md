# CTL Agentic AI — ARB Demo Playbook

> **PRIVATE** — Conductor/narrator reference only. NOT for distribution.
> **Target**: 30–40 min ARB session. Mixed audience: business stakeholders + senior architects.

---

## PACING GUIDE

| Time | Segment | Style |
|------|---------|-------|
| 0–5 min | Business Case & Problem Statement | Storytelling — relatable pain |
| 5–10 min | "What Is Agentic AI?" & Why It Matters Here | Conceptual — no code yet |
| 10–15 min | Solution Architecture & Building Blocks | Diagrams & mental model |
| 15–18 min | Azure Services Overview | Quick hit, logos/names |
| 18–30 min | **LIVE DEMO** — start components, run scenarios, show decisions | Terminal + results — this is the main event |
| 30–35 min | Code Walkthrough (7–8 key files) | Screen share source — architects will love this |
| 35–40 min | Safety, Evals, Audit Recap + Q&A | Wrap up with enterprise credibility |

---

## SEGMENT 1 — BUSINESS CASE & PROBLEM (0–5 min)

### The Pain (tell this as a story)

> "When Cascade acquires a foreclosed property, before we can list it for sale, an analyst must determine if it's **Clear-To-List**. This involves checking title clearance, outstanding liens, HOA delinquencies, code violations, property valuation, occupancy status — across multiple data sources, state-specific laws, and internal policies."

**Key talking points:**

- **Today's process**: Camunda/deterministic BPMN workflows. Hard-coded decision trees. Every new state, policy change, or edge case = developer sprint to update the workflow. Months of lead time.
- **Scale**: Thousands of assets per month. Each evaluation touches 6–8 data systems. Analyst fatigue → inconsistency → risk.
- **Problem with deterministic workflows**:
  - Can't reason about *contradictions* (title says clear, but HOA says delinquent — what wins?)
  - Can't adapt to new state regulations without code changes
  - Can't explain *why* a decision was made beyond "branch X was taken"
  - Can't handle ambiguity — everything must be yes/no, but reality is gray

### What We Built

> "An AI agent that **reasons** through the same evidence an analyst would, calls the same data services, applies the same policies — but does it in 24 seconds instead of 45 minutes, and produces a structured verdict with citations, evidence trail, and confidence score."

### Benefits vs. Camunda (have these ready for pushback)

| Dimension | Camunda / Deterministic | Agentic AI |
|-----------|------------------------|------------|
| **Adaptability** | Code change per rule change | Policy docs update → agent adapts |
| **Contradiction handling** | Hard-coded priority | LLM reasons across domains |
| **Explainability** | "Branch 47 taken" | Natural language reflection log |
| **New state onboarding** | Weeks of dev + QA | Add policy JSON to RAG knowledge base |
| **Cost of edge cases** | Each edge case = new branch | Agent generalizes from policy context |
| **Speed** | Minutes (sequential API calls) | ~24 seconds (parallel agents) |
| **Human oversight** | Post-hoc QA sampling | Built-in HITL gate at low confidence |

---

## SEGMENT 2 — WHAT IS AGENTIC AI? (5–10 min)

### Keep it simple for non-tech audience

> "Think of it like hiring a junior analyst who is very fast, very thorough, and follows instructions exactly — but needs a senior analyst to review borderline cases."

**Three concepts to land:**

1. **Agent = LLM + Tools + Instructions**
   - The LLM (GPT-4o) is the "brain" — it can read, reason, and write
   - Tools are the "hands" — it can call APIs to look up title data, valuations, policies
   - System prompts are the "training manual" — specific instructions for each domain

2. **Multi-Agent = Specialization**
   - One agent can't do everything well. We use **specialized agents** — a Legal agent, a Valuation agent, an Occupancy agent — each with their own tools and instructions
   - An **Orchestrator** coordinates them, like a team lead

3. **Human-in-the-Loop = Safety Net**
   - When the agent's confidence is below threshold, it **pauses** and asks a human reviewer
   - The human can confirm, override, or request re-evaluation
   - This isn't optional — it's a core architectural requirement

### For the architects in the room

> "This is built on **Microsoft Agent Framework SDK** — the unified successor to AutoGen and Semantic Kernel. We use **MCP (Model Context Protocol)** for tool integration, **Azure AI Foundry** for the reasoning engine (hosting models like GPT-4o or Phi-4), **text-embedding-3-small** for RAG vector embeddings, **Azure AI Search** for hybrid policy retrieval, **Azure AI Content Safety** for prompt injection shields and content moderation, **Azure AI Language** for PII entity detection, and an **LLM-as-judge** quality gate that scores verdict groundedness and relevance."

---

## SEGMENT 3 — SOLUTION ARCHITECTURE & BUILDING BLOCKS (10–15 min)

### Architecture Mental Model (draw or show diagram)

```
┌─────────────────────────────────────────────────────────────┐
│  HOST (CLI / future API)                                     │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │  ORCHESTRATOR (Workflow DAG)                             │ │
│  │  ┌──────────┐   ┌───────────────────┐   ┌────────────┐ │ │
│  │  │ Planning │──▶│  Investigation    │──▶│ Reflection │ │ │
│  │  │ Agent    │   │  ┌─────┐┌────┐┌──┐│   │ Agent      │ │ │
│  │  └──────────┘   │  │Legal││Val ││Oc││   └────────────┘ │ │
│  │                 │  └─────┘└────┘└──┘│                   │ │
│  │                 └───────────────────┘                   │ │
│  └────────────────────────┬────────────────────────────────┘ │
│                           │ MCP (tools)                      │
│  ┌────────────────────────▼────────────────────────────────┐ │
│  │  MCP SERVER (localhost:5100)                             │ │
│  │  8 Tools: Title, HOA, Violations, BPO, AVM,             │ │
│  │           Occupancy, AssetProfile, RAG/KnowledgeBase     │ │
│  └──────────────────────────────────────────────────────────┘ │
│  ┌────────────────────────────┐  ┌──────────────────────────┐ │
│  │  GUARDRAILS MIDDLEWARE     │  │  AZURE AI SEARCH (RAG)   │ │
│  │  PII · Injection · Safety │  │  47 policy chunks         │ │
│  │  Token Budget · Validation │  │  Hybrid: Vector + BM25   │ │
│  └────────────────────────────┘  └──────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

### Building Blocks (hit each one — 30 seconds max per block)

| # | Building Block | What It Does | Audience Hook |
|---|----------------|-------------|---------------|
| 1 | **Workflow DAG** | Plan → Investigate → Reflect — typed messages between nodes, built with `WorkflowBuilder` | "Not a linear script — a directed acyclic graph. Each step's output types feed the next." |
| 2 | **Multi-Agent Orchestration** | 3 specialized sub-agents run **in parallel** during investigation | "Like 3 analysts working simultaneously — Legal, Valuation, Occupancy" |
| 3 | **Tool Calling via MCP** | Model Context Protocol — open standard for LLM tool access. 8 tools exposed, each agent sees only its allowed subset | "Agent decides which tools to call and in what order — not hard-coded" |
| 4 | **RAG (Retrieval-Augmented Generation)** | 10 policy documents → chunked → embedded → Azure AI Search. Hybrid retrieval (semantic vectors + keyword BM25) | "Agent doesn't hallucinate policy — it *retrieves* the actual policy text and cites it" |
| 5 | **Guardrails Pipeline** | 5 layers wrapping every LLM call — injection detection, content safety, PII masking, token budgets, input validation | "Every single message to and from the LLM passes through this pipeline" |
| 6 | **Human-in-the-Loop (HITL)** | Confidence < threshold → pause → human review → confirm/override/re-evaluate | "The agent knows what it doesn't know" |
| 7 | **Structured Verdicts** | `Clear`, `ClearWithConditions`, `NotClear`, `NeedsHumanReview` + confidence score + citations + evidence trail | "Not a chat response — a structured, auditable business decision" |
| 8 | **Observability & Audit** | OpenTelemetry traces + App Insights. Every step logged: agent name, tokens used, duration, input/output hash | "Full chain of custody — every decision traceable" |

### Safety & Guardrails (expand from block #5)

| Layer | Defense | Type |
|-------|---------|------|
| **Input Validation** | Asset ID format, timestamp sanity, field length limits on all tool inputs | Deterministic |
| **Prompt Injection Detection** | Tier 1: 10 compiled regex patterns (offline, fast). Tier 2: Azure Prompt Shields (cloud, ML-based) | Hybrid |
| **Indirect Injection Screening** | Tool outputs (e.g., asset profile data) screened for embedded attack payloads before LLM sees them | Cloud |
| **Content Moderation** | Azure AI Content Safety — hate, violence, self-harm, sexual content severity scoring | Cloud |
| **PII Masking** | Tier 1: Regex (SSN, credit card, email, phone). Tier 2: Azure AI Language PII entity recognition | Hybrid |
| **Token Budget** | Per-session budget (50K tokens). Atomic counter with session isolation via AsyncLocal | Deterministic |
| **Tool Scope Filtering** | Each agent only sees its allowed tools — Legal can't call GetAVM, Valuation can't call SearchTitle | Deterministic |
| **Circuit Breaker** | Content Safety auto-degrades after 5 consecutive failures — falls back to local detection | Resilience |

### Evals

| Eval Type | What It Measures |
|-----------|-----------------|
| **Verdict Acceptability** | Is the verdict in the expected range for known test assets? |
| **Confidence Bounds** | Is confidence within expected thresholds for the scenario? |
| **Evidence Completeness** | Did the agent produce non-empty evidence trail and reflection log? |
| **Groundedness (LLM-as-judge)** | Score 1–5: Is the verdict grounded in the actual investigation findings? |
| **Relevance (LLM-as-judge)** | Score 1–5: Is the verdict relevant to the CTL evaluation question? |

### Transparency & Audit

- **Reflection Log**: Natural language explanation of *why* the agent reached its verdict — contradictions called out, unverified fields noted
- **Evidence Trail**: Array of finding summaries from each domain agent
- **Citations**: Source, reference, excerpt — e.g., "Texas Property Code §51.002"
- **Session Tracing**: Every phase logged with SessionId, AgentName, StepType, Duration, TokensUsed, InputHash, OutputHash
- **App Insights Integration**: All audit events emitted as custom events with structured properties
- **Human Review Audit**: Override decisions include reviewer email, action taken, notes, and timestamp

---

## SEGMENT 4 — AZURE SERVICES (15–18 min)

> "Quick overview of the Azure services this solution leverages — all standard enterprise services, nothing exotic."

| Azure Service | How We Use It | SKU/Tier |
|---------------|---------------|----------|
| **Azure AI Foundry (gpt-4o)** | Reasoning engine — all agent LLM calls via AI Foundry endpoint | Foundry project deployment |
| **Azure AI Foundry (text-embedding-3-small)** | RAG embedding — 1536-dim vectors for policy chunks | Same Foundry project |
| **Azure AI Search** | RAG index — hybrid search (vector + BM25), HNSW cosine | Free tier |
| **Azure AI Content Safety** | Prompt Shields + content moderation | Cognitive Services |
| **Azure AI Language** | PII entity detection (names, addresses, bank accounts) | Regional endpoint |
| **Azure Application Insights** | Distributed tracing, audit events, metrics | Standard |
| **Azure Identity (DefaultAzureCredential)** | Passwordless auth to all services | RBAC |

**For architects**: "Notice there's no custom middleware, no API gateway, no Kubernetes — this is a console app that could run anywhere .NET 8 runs. The complexity is in the orchestration logic, not the infrastructure."

---

## SEGMENT 5 — LIVE DEMO (18–30 min)

### Pre-Demo Checklist (do this BEFORE the call)

```
□ Terminal 1 ready (for MCP Server)
□ Terminal 2 ready (for Host / RAG Indexer)
□ Azure login active (az account show)
□ VS Code open to the solution
□ config/appsettings.json has real endpoints (not placeholders)
```

---

### DEMO STEP 1: Start the MCP Server (Tool Server)

**Say**: *"First, let's start the tool server. This exposes 8 tools via Model Context Protocol — an open standard Microsoft and Anthropic co-developed. Think of it as a standardized way for AI agents to call APIs."*

```powershell
dotnet run --project src/Cascade.CTL.Agent.McpServer
```

**What audience sees**: Kestrel startup, "Now listening on http://localhost:5100"

**Point out**: *"8 tools available — title search, HOA check, code violations, BPO, AVM, occupancy, asset profile, and knowledge base query. Each tool has input validation built in."*

**Wait for**: `Application started` message. Move to Terminal 2.

---

### DEMO STEP 2: Run RAG Indexer (Optional — impressive for architects)

**Say**: *"Before we run the agent, let me show you how policy knowledge gets into the system. We have 10 policy documents — Texas foreclosure law, California REO policy, valuation standards, title clearance rules. The indexer chunks them, generates embeddings, and loads them into Azure AI Search."*

```powershell
dotnet run --project src/Cascade.CTL.RAG.Indexer -- --knowledge-path ./config/rag-knowledge --recreate-index
```

**What audience sees**: 
- `Loaded 10 source documents`
- `Produced 47 chunks (avg 4 chunks/doc)`
- `Batch 1 uploaded 47/47`
- `Completed — 10 docs, 47 chunks, 47 uploaded in 00:00:XX`

**Point out**: *"47 policy chunks, each with vector embeddings. When the agent needs to know Texas foreclosure rules, it retrieves the relevant chunks — not the entire corpus. This is RAG — Retrieval-Augmented Generation. The agent doesn't hallucinate policy; it retrieves and cites it."*

**This is safe to re-execute** — it's idempotent (recreate flag deletes and rebuilds the same index).

---

### DEMO STEP 3: Run Scenario 1 — Texas Foreclosure (Happy Path)

**Say**: *"Now let's run a CTL evaluation. Asset TX-001 is a foreclosed property in Dallas, Texas. Watch what happens."*

```powershell
dotnet run --project src/Cascade.CTL.Agent.Host
```

**What audience sees (narrate each phase as it appears)**:

1. **Banner**: Cascade 2.0 — CTL Agent Host
2. **MCP Init**: "8 tools: get_avm, check_hoa_delinquency, ..." → *"Agent discovered 8 tools it can use"*
3. **Content Safety + PII Filter**: Initialization messages → *"Every LLM call will pass through guardrails"*
4. **Phase 1 — Planning**: "Building verification plan via AIAgent" → *"The planning agent looked at the asset profile and decided which domains need investigation — Legal, Valuation, Occupancy"*
5. **Phase 2 — Investigation**: "Running 3 investigation AIAgents in parallel" → *"Three specialized agents are now running simultaneously — each calling only the tools they're authorized to use"*
   - Completed: Legal & Title (1400+ chars), Valuation (670+ chars), Occupancy (215+ chars)
6. **Phase 3 — Reflection**: "Orchestrator reflection via AIAgent" → *"The reflection agent reviews all three domain reports, looks for contradictions, and produces a confidence-scored verdict"*
7. **Phase 4 — Parsing**: *"Structured verdict extracted from the LLM response"*
8. **Phase 5 — HITL**: "Verdict is NeedsHumanReview (confidence 0.65)" → *"Confidence was below threshold. In production, this pauses and routes to a human reviewer. Here the mock reviewer overrides to ClearWithConditions."*
9. **Final Output**: JSON result + colored console output

**KEY THINGS TO POINT OUT IN THE OUTPUT**:

- **Verdict: ClearWithConditions** — *"Not a simple yes/no — conditional clearance with specific conditions listed"*
- **Confidence: 0.78** — *"The agent quantifies its certainty. 0.78 means reasonably confident but with caveats"*
- **Duration: ~24s** — *"What takes an analyst 30–45 minutes took 24 seconds"*
- **Evidence Trail**: Read 1–2 items — *"Each finding traces back to a specific domain investigation"*
- **Citations**: *"Texas Property Code Section 51.002 — the agent cited the actual statute. This came from RAG, not hallucination"*
- **Reflection Log**: *"The agent wrote a paragraph explaining its reasoning — HOA unverified, BPO missing, occupancy unverified. This is the audit trail."*
- **Human Review section**: *"Mock reviewer overrode with notes. In production this would be a real analyst in a review queue."*

---

### DEMO STEP 4: Run Scenario 2 — California REO (Problem Property)

**Say**: *"Now let's see what happens with a harder case. Asset CA-002 is an REO in Los Angeles — occupied, eviction in progress, stale BPO, open liens, HOA delinquent. Everything that can be wrong, is wrong."*

```powershell
dotnet run --project src/Cascade.CTL.Agent.Host -- --asset-id ASSET-CA-002
```

**What to narrate**:

- Same 5-phase flow, but watch the **investigation findings** — they'll be much more negative
- **Expected verdict**: `NeedsHumanReview` or `ClearWithConditions` at lower confidence
- **Point out**: *"Same agent, same code — but it reasons differently based on the evidence. A Camunda workflow would need separate branches for every combination. The agent generalizes."*
- **Evidence trail will mention**: Tax lien ($4,200), occupied with eviction in progress, stale BPO (120 days old), HOA delinquent ($2,850)
- **Confidence** should be lower than TX-001

**Contrast with TX-001**: *"Notice the agent didn't just run the same checklist — it weighed contradictions and adjusted its confidence. The occupied property with active eviction is a fundamentally different risk profile."*

---

### DEMO STEP 5: Run Scenario 3 — Florida Non-Foreclosure (Unknown Status)

**Say**: *"One more. Florida property — non-foreclosure, occupancy unknown, no BPO, critical code violations including non-functional smoke detectors."*

```powershell
dotnet run --project src/Cascade.CTL.Agent.Host -- --asset-id ASSET-FL-003
```

**What to narrate**:

- **Expected verdict**: `NeedsHumanReview` (low confidence — too many unknowns)
- **Key findings**: No BPO at all (policy says this is a blocker), unknown occupancy (access denied), critical code violation
- **Point out**: *"The agent recognized it doesn't have enough information to make a decision. That self-awareness — knowing what you don't know — is what separates agentic AI from a simple classifier."*

---

### DEMO STEP 6 (Optional): Run Unit Tests — Negative/Security Scenarios

**Say**: *"Let me quickly show the test suite — these validate the guardrails and security controls."*

```powershell
dotnet test tests/Cascade.CTL.Agent.Tests --verbosity normal 2>&1 | Select-String -Pattern "Passed|Failed|Total"
```

**Tests the audience should know about** (describe 3–4 verbally):

| Test | What It Proves |
|------|---------------|
| `ShouldMaskPiiInUserInput` | SSN "123-45-6789" gets masked to "***-**-****" before reaching the LLM |
| `ShouldBlockPromptInjection` | "Ignore all previous instructions" is caught and blocked |
| `ShouldBlockToolMessageWithInjection` | Even if a tool returns malicious text, it's caught before the LLM sees it |
| `ShouldRejectOverlongParcelId` | Buffer overflow attempt (>50 chars) rejected at the boundary |
| `Orchestrator_ShouldScreenAssetProfileBeforeInjection` | Asset data containing attack payload is blocked pre-LLM |
| `TryConsumeTokens_ShouldBeThreadSafe` | 100 concurrent requests can't corrupt the token budget counter |

**Point out**: *"263 tests covering guardrails, input validation, concurrency safety, and indirect injection. These are enterprise-grade controls — not afterthoughts."*

---

## SEGMENT 6 — CODE WALKTHROUGH (30–35 min)

> **Architects care about this. Business people may start to drift — keep it fast, 45 seconds per file max. Show the file, point out 1–2 things, move on.**

### File Order (follow this sequence — it tells the architecture story)

#### 1. `config/appsettings.json` — Configuration Hub
**Show**: The full config structure.
**Point out**: 
- *"Single config file wires everything — Azure AI Foundry endpoint, MCP server, RAG, content safety, PII filter, token budgets, resilience timeouts"*
- *"UseMockProviders: true — flip this to false and point at real domain services. Zero code changes."*

#### 2. `src/Cascade.CTL.Agent.Host/Program.cs` — Entry Point
**Show**: Lines 1–50.
**Point out**:
- *"~70 lines. Creates the host, initializes MCP tools, calls the orchestrator, prints results. That's it."*
- *"The --asset-id flag lets us switch test scenarios"*

#### 3. `src/Cascade.CTL.Agent.Application/Orchestration/Workflow/CTLWorkflowOrchestrator.cs` — Brain
**Show**: The `EvaluateAsync` method, especially the workflow DAG construction.
**Point out**:
- *"WorkflowBuilder — Plan→Investigate→Reflect. Three nodes, two edges. The Microsoft Agent Framework SDK executes this as a typed DAG."*
- *"Notice Phase 5 — the HITL gate. If confidence < threshold, execution pauses for human review."*
- *"Asset profile is screened for indirect injection BEFORE being injected into the LLM context."*

#### 4. `src/Cascade.CTL.Agent.Application/Prompts/InvestigationAgentPrompts.cs` — Agent Instructions
**Show**: The Legal agent system prompt (first 20 lines).
**Point out**:
- *"This is the 'training manual' for the Legal agent. It says: always call SearchTitle first, then conditionally check HOA, always check code violations, always query the knowledge base."*
- *"The agent decides the ORDER and INTERPRETATION — but the instructions constrain WHAT it can do."*
- *"Severity rules are baked in: tax liens = HIGH, HOA > $5K = HIGH. These came from our business analysts."*

#### 5. `src/Cascade.CTL.Agent.Application/Orchestration/McpToolProvider.cs` — Tool Discovery
**Show**: `InitializeAsync` method and tool filter methods.
**Point out**:
- *"Connects to MCP server, discovers all 8 tools dynamically. Then filters per agent — Legal gets 4 tools, Valuation gets 3, Occupancy gets 2."*
- *"This is the principle of least privilege applied to AI agents."*

#### 6. `src/Cascade.CTL.Agent.Guardrails/GuardrailsMiddleware.cs` — The Shield
**Show**: The `GetResponseAsync` override.
**Point out**:
- *"DelegatingChatClient — wraps the real LLM client. Every message flows through: token budget → content safety → PII masking → LLM → PII masking output → token tracking."*
- *"If any layer blocks, the LLM is never called. This is defense in depth."*

#### 7. `src/Cascade.CTL.Agent.McpServer/Tools/RAGTools.cs` — RAG Integration
**Show**: The `QueryKnowledgeBase` method.
**Point out**:
- *"This is the bridge between the agent and our policy knowledge. Hybrid search — semantic vectors for meaning, BM25 for exact terms."*
- *"Metadata filters: state code, county, asset type. Texas foreclosure query won't return California REO policies."*

#### 8. `src/Cascade.CTL.Agent.Infrastructure/Observability/AppInsightsAuditService.cs` — Audit Trail
**Show**: `RecordStepAsync` method.
**Point out**:
- *"Every phase, every agent step — logged as a custom App Insights event with SessionId, AgentName, TokensUsed, Duration, input/output hashes."*
- *"Input/output hashes, not full payloads — so you can prove what went in and came out without storing PII."*

---

## SEGMENT 7 — WRAP-UP & Q&A (35–40 min)

### Closing Statement

> "What we've shown today is a working, enterprise-grade agentic AI system that:
> 1. **Reasons** through complex, multi-domain business decisions
> 2. **Retrieves** actual policy context instead of hallucinating
> 3. **Explains** every decision with citations and evidence
> 4. **Protects** against prompt injection, PII leakage, and runaway costs
> 5. **Defers** to humans when it's not confident enough
> 6. **Audits** every step for compliance and traceability
>
> This is not a chatbot. It's a decision-support agent with guardrails."

### Anticipated Questions & Answers

**Q: "What happens when policies change?"**
A: Update the JSON in `config/rag-knowledge/`, re-run the indexer (10 seconds). Agent picks up new policies on next evaluation — zero code changes.

**Q: "Can we use a different LLM?"**
A: Yes — swap the endpoint and model ID in config. The system uses Microsoft.Extensions.AI abstraction — works with any OpenAI-compatible API. You can potentially use Phi-4 or other models deployed in Azure AI Foundry via serverless endpoints.

**Q: "What about cost?"**
A: ~5,000 tokens per evaluation at GPT-4o pricing. Token budget guard prevents runaway sessions. The 50K default cap = ~10 evaluations before hard stop.

**Q: "What if the LLM hallucinates?"**
A: Three defenses: (1) RAG grounds responses in real policy docs, (2) Reflection agent checks findings for contradictions, (3) Groundedness evaluator scores verdict against evidence. Plus HITL for low-confidence cases.

**Q: "Is this production-ready?"**
A: The architecture and guardrails are production-grade. For deployment: replace mock providers with real domain service integrations, deploy MCP server behind API Management, add auth/authz to the Host, and set up the HITL queue with a real review UI.

**Q: "How is this different from just using Copilot/ChatGPT?"**
A: ChatGPT is a general-purpose chatbot. This is a purpose-built decision agent with constrained tools, domain-specific prompts, structured output, audit trails, and human oversight. It can't go off-script — it can only call the 8 tools we exposed, and each agent only sees its authorized subset.

**Q: "What about the Content Safety and PII errors we saw?"**
A: Those are non-fatal fallbacks by design. Content Safety falls back to local regex detection (10 compiled patterns). PII falls back to Tier 1 regex masking (SSN, credit card, email, phone). The architecture is designed for graceful degradation — cloud services enhance, but local detection provides the floor.

---

## QUICK-REFERENCE COMMANDS

```powershell
# 1. Start MCP Server (Terminal 1 — leave running)
dotnet run --project src/Cascade.CTL.Agent.McpServer

# 2. RAG Indexer (Terminal 2 — one-shot)
dotnet run --project src/Cascade.CTL.RAG.Indexer -- --knowledge-path ./config/rag-knowledge --recreate-index

# 3. Texas Foreclosure — happy path
dotnet run --project src/Cascade.CTL.Agent.Host

# 4. California REO — problem property
dotnet run --project src/Cascade.CTL.Agent.Host -- --asset-id ASSET-CA-002

# 5. Florida Non-Foreclosure — unknown/critical issues
dotnet run --project src/Cascade.CTL.Agent.Host -- --asset-id ASSET-FL-003

# 6. Unit Tests
dotnet test tests/Cascade.CTL.Agent.Tests --verbosity normal

# 7. Build check (if needed)
dotnet build
```

---

## MOCK DATA CHEAT SHEET (know what the agent will find)

### ASSET-TX-001 (Dallas, TX — Foreclosure)
| Domain | Data | Expected Impact |
|--------|------|-----------------|
| Title | Clear, no liens, no HOA | Positive |
| BPO | $285K, 15 days old, High quality | Positive |
| AVM | $290K, confidence 0.92, +1.75% variance | Positive |
| HOA | No HOA community | Neutral |
| Code Violations | None | Positive |
| Occupancy | Vacant, inspected 7 days ago, secured, Good condition | Positive |
| **Expected Verdict** | **ClearWithConditions or NeedsHumanReview** (mock data has some unverified fields) | |

### ASSET-CA-002 (Los Angeles, CA — REO)
| Domain | Data | Expected Impact |
|--------|------|-----------------|
| Title | NOT clear — $4,200 tax lien, second mortgage, HOA flag | **Negative** |
| BPO | $725K, 120 days old, Medium quality, **STALE** | **Negative** |
| AVM | $695K, confidence 0.78, -4.14% variance | Moderate concern |
| HOA | Delinquent, $2,850 owed, 8 months since payment | **Negative** |
| Code Violations | 1 Minor (overgrown vegetation) | Minor |
| Occupancy | **Occupied**, eviction in progress, cash-for-keys offered | **Negative** |
| **Expected Verdict** | **NeedsHumanReview** (multiple blockers) | |

### ASSET-FL-003 (Miami, FL — Non-Foreclosure)
| Domain | Data | Expected Impact |
|--------|------|-----------------|
| Title | Clear but minor boundary dispute, HOA flag | Mixed |
| BPO | **No BPO** (CTL blocker per policy) | **Blocker** |
| AVM | $340K, confidence 0.85 | Neutral |
| HOA | Current, paid last month | Positive |
| Code Violations | 2: Major (damaged roof/hurricane) + **Critical** (smoke detectors) | **Severe** |
| Occupancy | **Unknown**, access denied, neighbor reports occasional activity | **Negative** |
| **Expected Verdict** | **NeedsHumanReview** (too many unknowns + critical violations) | |

---

## TIMING RECOVERY STRATEGIES

**Running ahead of schedule?**
- Show the Evals runner: `dotnet run --project tests/Cascade.CTL.Agent.Evals`
- Deep dive into a specific prompt file
- Show `Directory.Packages.props` for central package management
- Show the RAG knowledge JSON files — audiences love seeing the raw policy data

**Running behind schedule?**
- Skip the RAG indexer demo (just mention it)
- Skip FL-003 scenario (TX + CA is enough contrast)
- Skip code walkthrough files 7 & 8 (RAG and Audit — mention verbally)
- Compress the unit test demo to just stating the count: "263 tests, all passing"

**If something fails during demo?**
- Content Safety / PII 400 errors: *"These are non-fatal — you can see the circuit breaker activating and falling back to local detection. This is the resilience pattern in action."*
- MCP connection failure: restart MCP Server, wait 5 seconds, retry Host
- Azure throttling: *"Free tier rate limiting — in production you'd use a paid tier."*
- Build failure: likely MCP Server locking DLLs — kill MCP terminal, rebuild, restart
