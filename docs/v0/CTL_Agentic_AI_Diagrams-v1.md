# Cascade 2.0 — CTL Agent: Agentic AI Architecture Diagrams

**Solution:** Asset Clear-To-List (CTL) Determination Agent  
**Focus:** Agentic AI Component Design  
**Aligned to:** Cascade.CTL.AgentSolution (.NET 8) · cascade2_ctl_agent_solution_architecture.md · CTL_Architecture_Readout.md  
**Date:** March 29, 2026

---

## 1. CTL Agent — System Overview

A single evaluation flow: event in, verdict out. Two processes — the Host orchestrates agents via LLM, the MCP Server exposes tools over HTTP/SSE.

```mermaid
graph TD
    SB["① Azure Service Bus<br/>CTLEvaluationRequestedEvent"]
    HOST["② CTL Agent Host<br/>.NET 8 · IChatClient Pipeline"]
    AOAI["③ Azure OpenAI<br/>GPT-4o · Temp 0.1"]
    MCP["④ MCP Tool Server<br/>8 Tools · ASP.NET Core"]
    TOOLS["⑤ Tool Backends<br/>Mock / Real APIs"]
    VERDICT["⑥ CTLVerdictDto<br/>→ CamundaGateway"]

    SB --> HOST
    HOST --> AOAI
    HOST --> MCP
    MCP --> TOOLS
    AOAI --> VERDICT

    linkStyle default stroke:#000000,stroke-width:2.5px
```

---

## 2. 4-Phase Orchestration Pattern

The core agentic pattern — Plan, Investigate, Reflect, Decide. This is the `CTLEvaluationOrchestrator.EvaluateAsync()` method.

```mermaid
graph TD
    START(["Asset ID"])

    subgraph PHASE1["Phase 1 — PLAN"]
        P1["Orchestrator Agent<br/>PlanningSystemPrompt"]
        T1["Calls: GetAssetProfile + QueryKnowledgeBase"]
        PLAN["Output: VerificationPlan<br/>domains · policies · rationale"]
        P1 --> T1
        T1 --> PLAN
    end

    subgraph PHASE2["Phase 2 — INVESTIGATE (Task.WhenAll)"]
        LEGAL["Legal Agent<br/>LegalAgentSystemPrompt"]
        VAL["Valuation Agent<br/>ValuationAgentSystemPrompt"]
        OCC["Occupancy Agent<br/>OccupancyAgentSystemPrompt"]
    end

    subgraph PHASE3["Phase 3 — REFLECT"]
        P3["Orchestrator Agent<br/>ReflectionSystemPrompt"]
        REF["Critique findings<br/>Detect contradictions<br/>Apply confidence rules"]
        P3 --> REF
    end

    subgraph PHASE4["Phase 4 — VERDICT"]
        P4["ParseVerdict()"]
        V["CTLVerdictDto<br/>verdict · confidence · conditions<br/>evidenceTrail · reflectionLog"]
        P4 --> V
    end

    START --> P1
    PLAN --> PHASE2
    PHASE2 --> P3
    REF --> P4

    style PHASE1 fill:#e8f4fd,stroke:#4A90D9,stroke-width:2px,color:#1a3a5c
    style PHASE2 fill:#e8fde8,stroke:#7BB661,stroke-width:2px,color:#2d5a1e
    style PHASE3 fill:#fff3e0,stroke:#F5A623,stroke-width:2px,color:#7a4a00
    style PHASE4 fill:#fde8e8,stroke:#D94A4A,stroke-width:2px,color:#7a1a1a
    linkStyle default stroke:#000000,stroke-width:2.5px
```

---

## 3. Agent Topology — Who Calls What

Four agents, eight tools. Each agent sees only its assigned tools via `McpToolProvider` role-based filtering. Investigation agents run concurrently and return structured findings to the Orchestrator.

```mermaid
graph TD
    O_PLAN["Orchestrator: Plan Phase<br/>Calls GetAssetProfile + QueryKnowledgeBase<br/>Produces VerificationPlan"]
    O_DISPATCH["Orchestrator: Dispatch Phase<br/>Launches 3 investigation agents in parallel via Task.WhenAll"]

    O_PLAN --> O_DISPATCH

    O_DISPATCH --> LEGAL_IN
    O_DISPATCH --> VAL_IN
    O_DISPATCH --> OCC_IN

    subgraph LEGAL["Legal & Title Agent"]
        direction TB
        LEGAL_IN["Receives: VerificationPlan context"]
        L_TOOLS["Tools: SearchTitle · CheckHOADelinquency<br/>LookupCodeViolations · QueryKnowledgeBase"]
        L_OUT["Produces: LegalFindingsReport<br/>domainVerdict · confidence · evidence"]
        LEGAL_IN --> L_TOOLS
        L_TOOLS --> L_OUT
    end

    subgraph VALUATION["Valuation Agent"]
        direction TB
        VAL_IN["Receives: VerificationPlan context"]
        V_TOOLS["Tools: RetrieveBPO · GetAVM<br/>QueryKnowledgeBase"]
        V_OUT["Produces: ValuationFindingsReport<br/>domainVerdict · confidence · evidence"]
        VAL_IN --> V_TOOLS
        V_TOOLS --> V_OUT
    end

    subgraph OCCUPANCY["Occupancy Agent"]
        direction TB
        OCC_IN["Receives: VerificationPlan context"]
        OC_TOOLS["Tools: GetOccupancyStatus<br/>QueryKnowledgeBase"]
        OC_OUT["Produces: OccupancyFindingsReport<br/>domainVerdict · confidence · evidence"]
        OCC_IN --> OC_TOOLS
        OC_TOOLS --> OC_OUT
    end

    L_OUT --> COLLECT["Orchestrator: Reflect Phase<br/>Aggregates all 3 FindingsReports<br/>Detects contradictions · Scores confidence"]
    V_OUT --> COLLECT
    OC_OUT --> COLLECT

    COLLECT --> O_VERDICT["Orchestrator: Verdict Phase<br/>ParseVerdict() → CTLVerdictDto<br/>verdict · confidence · conditions"]

    style LEGAL fill:#d5f5d0,stroke:#7BB661,stroke-width:2px,color:#2d5a1e
    style VALUATION fill:#d5f5d0,stroke:#7BB661,stroke-width:2px,color:#2d5a1e
    style OCCUPANCY fill:#d5f5d0,stroke:#7BB661,stroke-width:2px,color:#2d5a1e
    style O_PLAN fill:#dae8fc,stroke:#4A90D9,stroke-width:2px,color:#1a3a5c
    style O_DISPATCH fill:#dae8fc,stroke:#4A90D9,stroke-width:2px,color:#1a3a5c
    style COLLECT fill:#fff3e0,stroke:#F5A623,stroke-width:2px,color:#7a4a00
    style O_VERDICT fill:#fde8e8,stroke:#D94A4A,stroke-width:2px,color:#7a1a1a
    style L_TOOLS fill:#fff3e0,stroke:#F5A623,stroke-width:1px,color:#7a4a00
    style V_TOOLS fill:#fff3e0,stroke:#F5A623,stroke-width:1px,color:#7a4a00
    style OC_TOOLS fill:#fff3e0,stroke:#F5A623,stroke-width:1px,color:#7a4a00
    linkStyle default stroke:#000000,stroke-width:2.5px
```

---

## 4. MCP Client-Server Architecture

Two-process model. The Host connects to the MCP Server over HTTP/SSE using `McpClient.CreateAsync()`. Tools are auto-discovered via `[McpServerToolType]` attributes. `McpClientTool` implements `AITool` — direct use with `IChatClient`.

```mermaid
graph TD
    subgraph HOST["CTL Agent Host Process"]
        direction LR
        MTP["McpToolProvider"] --> CLIENT["McpClient<br/>HttpClientTransport<br/>HttpTransportMode.Sse"]
        CLIENT --> FILTER["Role-Based Filtering<br/>ListToolsAsync()<br/>GetToolsFor*Agent()"]
        FILTER --> CHATOPT["ChatOptions.Tools<br/>= IList＜AITool＞"]
    end

    CLIENT -->|"HTTP/SSE<br/>CallTool → JSON"| MCPAPI

    subgraph SERVER["MCP Tool Server — :5100"]
        MCPAPI["MapMcp() · AddMcpServer()<br/>.WithHttpTransport()<br/>.WithToolsFromAssembly()"]
        MCPAPI --> A["AssetProfileTools<br/>GetAssetProfile"]
        MCPAPI --> B["LegalTools<br/>SearchTitle · CheckHOADelinquency<br/>LookupCodeViolations"]
        MCPAPI --> C["ValuationTools<br/>RetrieveBPO · GetAVM"]
        MCPAPI --> D["OccupancyTools<br/>GetOccupancyStatus"]
        MCPAPI --> E["RAGTools<br/>QueryKnowledgeBase"]
    end

    A --> PROV["Infrastructure Providers<br/>Mock ↔ Real (configurable via DI)"]
    B --> PROV
    C --> PROV
    D --> PROV
    E --> PROV

    style HOST fill:#e8f4fd,stroke:#4A90D9,stroke-width:2px,color:#1a3a5c
    style SERVER fill:#fde8e8,stroke:#D94A4A,stroke-width:2px,color:#7a1a1a
    linkStyle default stroke:#000000,stroke-width:2.5px
```

---

## 5. IChatClient Middleware Pipeline

Every LLM call passes through this pipeline. Built in `ServiceRegistration.ConfigureCTLAgent()` via `ChatClientBuilder`. The guardrails wrap the entire pipeline as a `DelegatingChatClient`. This diagram shows the **registration order** (outer to inner). See **Section 5a** below for how it actually executes at runtime, including the tool-calling loop.

```mermaid
graph TD
    APP["CTLEvaluationOrchestrator<br/>GetResponseAsync()"]

    APP --> G1

    subgraph G["GuardrailsMiddleware (DelegatingChatClient)"]
        G1["① Token Budget Check<br/>Block if ≥ 50,000 consumed"]
        G2["② Content Safety Screen<br/>10 injection patterns · Azure AI Content Safety"]
        G3["③ PII Masking<br/>SSN · CC · Email · Phone"]
        G1 --> G2
        G2 --> G3
    end

    G3 --> FI["④ FunctionInvocation<br/>Auto-executes tool calls<br/>McpClientTool → MCP Server"]
    FI --> OT["⑤ OpenTelemetry<br/>Spans · Metrics<br/>SensitiveData = false"]
    OT --> AOAI["⑥ Azure OpenAI<br/>GPT-4o · Structured Outputs"]
    AOAI --> RESP["⑦ Response + Token Tracking<br/>Interlocked.Add (thread-safe)"]
    RESP --> APP2["Return to Orchestrator"]

    style G fill:#ffe0e0,stroke:#D94A4A,stroke-width:2px,color:#7a1a1a
    style FI fill:#e8fde8,stroke:#7BB661,stroke-width:2px,color:#2d5a1e
    style OT fill:#fff3e0,stroke:#F5A623,stroke-width:2px,color:#7a4a00
    style AOAI fill:#e0e8ff,stroke:#4A90D9,stroke-width:2px,color:#1a3a5c
    linkStyle default stroke:#000000,stroke-width:2.5px
```

---

## 5a. Middleware Nesting & Tool-Calling Loop — How It Actually Executes

Diagram 5 shows the **registration order** (outer → inner) of the `DelegatingChatClient` chain. At runtime, these layers don't execute top-to-bottom — they wrap each other like nested function calls. Each layer delegates inward on the request path, and processes the response on the way back out. The `FunctionInvocation` layer is critical: it intercepts LLM responses containing `tool_call` requests, executes those tools via MCP, and **re-submits to the LLM** automatically — looping until the model returns a final text response.

This is how `CTLEvaluationOrchestrator` calls `_chatClient.GetResponseAsync()` once, yet the LLM may be invoked multiple times internally (e.g., Plan phase: LLM calls `GetAssetProfile` → tool result fed back → LLM calls `QueryKnowledgeBase` → tool result fed back → LLM returns the VerificationPlan text).

### The LLM Tool-Calling Loop — What Happens Inside `GetResponseAsync()`

`FunctionInvocation` middleware inspects every LLM response for `FunctionCallContent` items (the SDK's deserialization of OpenAI's `tool_calls` JSON array). When found, it executes the matching `AITool` and re-submits the result to the LLM — looping until the response contains only text (no more `FunctionCallContent`).

**The key concept:** Your code calls `GetResponseAsync()` **once**. Inside that single call, the middleware pipeline may invoke the LLM **multiple times** — each time the LLM asks for a tool, the middleware executes it and sends the result back. Your code never sees this loop.

![alt text](image.png)

```
 YOUR CODE                    MIDDLEWARE PIPELINE (inside IChatClient)                 CLOUD
 ──────────                   ──────────────────────────────────────                   ─────
 
 Orchestrator                 GuardrailsChatClient    FunctionInvocation    OpenTelemetry    Azure OpenAI
     │                              │                       │                    │              │
     │  GetResponseAsync(           │                       │                    │              │
     │    prompt, tools)            │                       │                    │              │
     │─────────────────────────────>│                       │                    │              │
     │                              │                       │                    │              │
     │                     ① Token budget OK?               │                    │              │
     │                     ② Prompt injection scan          │                    │              │
     │                     ③ PII mask input                 │                    │              │
     │                              │                       │                    │              │
     │                              │  Delegates inward     │                    │              │
     │                              │──────────────────────>│                    │              │
     │                              │                       │                    │              │
     │                              │                       │  Start trace span  │              │
     │                              │                       │───────────────────>│              │
     │                              │                       │                    │  HTTP POST   │
     │                              │                       │                    │  prompt +    │
     │                              │                       │                    │  tool schemas│
     │                              │                       │                    │─────────────>│
     │                              │                       │                    │              │
     │                              │                       │                    │   Response:  │
     │                              │                       │                    │   tool_calls:│
     │                              │                       │                    │   [{name:    │
     │                              │                       │                    │   "GetAsset  │
     │                              │                       │                    │    Profile"}]│
     │                              │                       │                    │<─────────────│
     │                              │                       │  End trace span    │              │
     │                              │                       │<───────────────────│              │
     │                              │                       │                    │              │
     │                              │          ┌────────────────────────┐         │              │
     │                              │          │ LOOP: tool_call found  │         │              │
     │                              │          │                        │         │              │
     │                              │          │ SDK deserializes into  │         │              │
     │                              │          │ FunctionCallContent    │         │              │
     │                              │          │                        │         │              │
     │                              │          │ Finds matching AITool  │         │              │
     │                              │          │ (McpClientTool)        │         │              │
     │                              │          │         │              │         │              │
     │                              │          │         ▼              │         │              │
     │                              │          │  ┌─────────────┐      │         │              │
     │                              │          │  │ MCP Server  │      │         │              │
     │                              │          │  │ :5100       │      │         │              │
     │                              │          │  │ HTTP/SSE    │      │         │              │
     │                              │          │  │ Tool executes│     │         │              │
     │                              │          │  │ Returns JSON │     │         │              │
     │                              │          │  └─────────────┘      │         │              │
     │                              │          │         │              │         │              │
     │                              │          │         ▼              │         │              │
     │                              │          │ Appends tool result    │         │              │
     │                              │          │ to message history     │         │              │
     │                              │          │                        │         │              │
     │                              │          │ Re-submits to LLM ────────────────────────────>│
     │                              │          │                        │         │              │
     │                              │          │ LLM responds with      │         │              │
     │                              │          │ EITHER:                │         │              │
     │                              │          │  • another tool_call   │         │              │
     │                              │          │    → loop again ↑      │         │              │
     │                              │          │  • final text          │         │              │
     │                              │          │    → exit loop ↓       │         │              │
     │                              │          └────────────────────────┘         │              │
     │                              │                       │                    │              │
     │                              │   Final text response │                    │              │
     │                              │<──────────────────────│                    │              │
     │                              │                       │                    │              │
     │                     ⑦ Track token usage              │                    │              │
     │                       PII mask output                │                    │              │
     │                              │                       │                    │              │
     │  ChatResponse (final text)   │                       │                    │              │
     │<─────────────────────────────│                       │                    │              │
     │                              │                       │                    │              │
```

> **1 call from your code → N LLM round-trips handled invisibly by `FunctionInvocation` middleware. Your orchestrator never loops over tool calls.**

### Detailed Mermaid Diagram

```mermaid
graph TD
    ORCH["CTLEvaluationOrchestrator<br/>calls GetResponseAsync() once"]

    ORCH --> GR_REQ

    subgraph OUTER["OUTER — GuardrailsMiddleware (wraps everything)"]
        GR_REQ["PRE-CALL Screening<br/>① Token Budget: block if ≥ 50K consumed<br/>② Content Safety: 10 injection patterns<br/>③ PII Masking: SSN · CC · Email · Phone"]
        GR_RESP["POST-CALL Processing<br/>⑦ Token Tracking: Interlocked.Add<br/>Update running total for next budget check"]
    end

    GR_REQ --> FI_REQ

    subgraph MIDDLE["MIDDLE — FunctionInvocation (wraps inner layers)"]
        FI_REQ["Pass request inward to LLM"]
        FI_CHECK{"LLM response contains<br/>tool_call?"}
        FI_EXEC["Execute tool via McpClientTool<br/>→ HTTP/SSE to MCP Server :5100<br/>→ Tool returns JSON result"]
        FI_RESEND["Append tool result to messages<br/>Re-submit to LLM (goes through ⑤→⑥ again)"]
        FI_DONE["Final text response<br/>(no more tool_call)<br/>Pass response back outward"]
    end

    FI_REQ --> OT_REQ

    subgraph INNER["INNER — OpenTelemetry + Azure OpenAI"]
        OT_REQ["⑤ Start trace span<br/>Record request metrics"]
        LLM_CALL["⑥ HTTP POST to Azure OpenAI<br/>GPT-4o · Structured Outputs<br/>THE ACTUAL LLM INFERENCE"]
        OT_RESP["⑤ End trace span<br/>Record response metrics + latency"]
        OT_REQ --> LLM_CALL
        LLM_CALL --> OT_RESP
    end

    OT_RESP --> FI_CHECK

    FI_CHECK -->|"Yes — tool_call found"| FI_EXEC
    FI_EXEC --> FI_RESEND
    FI_RESEND --> OT_REQ

    FI_CHECK -->|"No — final text response"| FI_DONE

    FI_DONE --> GR_RESP

    GR_RESP --> RETURN["Return ChatResponse<br/>to Orchestrator"]

    style OUTER fill:#ffe0e0,stroke:#D94A4A,stroke-width:2px,color:#7a1a1a
    style MIDDLE fill:#e8fde8,stroke:#7BB661,stroke-width:2px,color:#2d5a1e
    style INNER fill:#e0e8ff,stroke:#4A90D9,stroke-width:2px,color:#1a3a5c
    style LLM_CALL fill:#c9d9f7,stroke:#2c5ea0,stroke-width:3px,color:#1a3a5c
    style FI_EXEC fill:#fff3e0,stroke:#F5A623,stroke-width:2px,color:#7a4a00
    linkStyle default stroke:#000000,stroke-width:2.5px
```

**Concrete example — Plan phase for a Texas foreclosure asset:**

| Loop | What happens | LLM calls |
|------|-------------|-----------|
| **Request 1** | Orchestrator sends PlanningSystemPrompt + asset ID. LLM responds with `tool_call: GetAssetProfile(assetId)` | LLM call #1 |
| **Tool exec** | FunctionInvocation intercepts → McpClientTool → MCP Server → MockAssetProfileProvider returns Asset JSON | — |
| **Request 2** | FunctionInvocation appends tool result to messages, re-submits to LLM. LLM responds with `tool_call: QueryKnowledgeBase(query, state:"TX")` | LLM call #2 |
| **Tool exec** | FunctionInvocation intercepts → McpClientTool → MCP Server → InMemoryRAGService returns CTL-POLICY-TX-001 | — |
| **Request 3** | FunctionInvocation appends tool result, re-submits. LLM returns final text: the VerificationPlan JSON (no more tool_calls) | LLM call #3 |
| **Return** | FunctionInvocation passes text response outward → GuardrailsMiddleware tracks tokens → Orchestrator receives response | — |

> **Key insight:** The Orchestrator calls `GetResponseAsync()` **once** per phase. The `FunctionInvocation` middleware handles multi-turn tool calling transparently — the Orchestrator never manually loops over tool calls.

---

## 6. Reflection & Verdict Decision Logic

The Orchestrator's reflection phase — how contradictions, confidence, and unverified fields determine the final verdict.

```mermaid
graph TD
    IN["Investigation Agent Findings<br/>Legal + Valuation + Occupancy"]

    IN --> R1["① Aggregate Evidence<br/>Merge findings from all 3 domains"]
    R1 --> R2["② Contradiction Detection<br/>Legal says Clear but Occupancy says Blocker?"]

    R2 -->|"Contradictions found"| PEN["③ Confidence Penalty<br/>≥ −0.15 per contradiction"]
    R2 -->|"No contradictions"| R3["④ Unverified Field Assessment<br/>Tool failures flagged as gaps"]

    PEN --> R3

    R3 --> R4["⑤ Apply Confidence Thresholds"]

    R4 --> PASS_OR_FAIL{"Any domain-level<br/>Blocker found?"}

    PASS_OR_FAIL -->|"Yes — Any Blocker"| NOTCLEAR["NotClear"]
    PASS_OR_FAIL -->|"No Blockers"| CONFIDENCE{"Confidence Score?"}

    CONFIDENCE -->|"≥ 0.90 and no conditions"| CLEAR["Clear"]
    CONFIDENCE -->|"≥ 0.90 with conditions"| CLEARC["ClearWithConditions"]
    CONFIDENCE -->|"0.75 – 0.89"| CLEARC2["ClearWithConditions<br/>(forced disclosure)"]
    CONFIDENCE -->|"< 0.75"| HUMAN["NeedsHumanReview"]

    style CLEAR fill:#d5f5d0,stroke:#7BB661,stroke-width:2px,color:#2d5a1e
    style CLEARC fill:#fff3e0,stroke:#F5A623,stroke-width:2px,color:#7a4a00
    style CLEARC2 fill:#fff3e0,stroke:#F5A623,stroke-width:2px,color:#7a4a00
    style HUMAN fill:#ede0f5,stroke:#9B59B6,stroke-width:2px,color:#5b2d82
    style NOTCLEAR fill:#fde0e0,stroke:#D94A4A,stroke-width:2px,color:#7a1a1a
    linkStyle default stroke:#000000,stroke-width:2.5px
```

---

## 7. Tool Failure Cascade

Not all tool failures are equal. Blocking tools abort; non-blocking tools reduce confidence.

```mermaid
graph TD
    CALL["Tool Invoked"] --> TYPE{"Tool Category?"}

    subgraph BLOCKING["Blocking Tools: GetAssetProfile · RetrieveBPO"]
        BF{"Succeeds?"}
        ABORT["Outcome: NeedsHumanReview<br/>Immediate — confidence = 0"]
        CONT["Outcome: Continue Evaluation"]
        BF -->|"No"| ABORT
        BF -->|"Yes"| CONT
    end

    subgraph NONBLOCKING["Non-Blocking Tools: SearchTitle · CheckHOA · LookupCodeViolations · GetAVM · GetOccupancyStatus"]
        NBF{"Succeeds?"}
        FLAG_NB["Outcome: Flag as Unverified<br/>→ UnverifiedFields[]<br/>→ Confidence penalty in Reflection"]
        USE_NB["Outcome: Use Result in FindingsReport"]
        NBF -->|"No"| FLAG_NB
        NBF -->|"Yes"| USE_NB
    end

    subgraph RAGTOOL["RAG Tool: QueryKnowledgeBase (3 retries)"]
        RAGF{"Succeeds?"}
        RETRY1["Retry (attempt 2)"]
        RETRY2["Retry (attempt 3)"]
        FLAG_RAG["Outcome: Flag as Unverified<br/>after 3 failures"]
        USE_RAG["Outcome: Use Result in FindingsReport"]
        RAGF -->|"No"| RETRY1
        RETRY1 -->|"No"| RETRY2
        RETRY2 -->|"No"| FLAG_RAG
        RAGF -->|"Yes"| USE_RAG
        RETRY1 -->|"Yes"| USE_RAG
        RETRY2 -->|"Yes"| USE_RAG
    end

    TYPE -->|"Blocking"| BF
    TYPE -->|"Non-Blocking"| NBF
    TYPE -->|"RAG"| RAGF

    style BLOCKING fill:#fde8e8,stroke:#D94A4A,stroke-width:2px,color:#7a1a1a
    style NONBLOCKING fill:#fff3e0,stroke:#F5A623,stroke-width:2px,color:#7a4a00
    style RAGTOOL fill:#e8f4fd,stroke:#4A90D9,stroke-width:2px,color:#1a3a5c
    style ABORT fill:#fde0e0,stroke:#D94A4A,stroke-width:2px,color:#7a1a1a
    style CONT fill:#d5f5d0,stroke:#7BB661,stroke-width:2px,color:#2d5a1e
    style FLAG_NB fill:#fff3e0,stroke:#F5A623,stroke-width:2px,color:#7a4a00
    style USE_NB fill:#d5f5d0,stroke:#7BB661,stroke-width:2px,color:#2d5a1e
    style FLAG_RAG fill:#fff3e0,stroke:#F5A623,stroke-width:2px,color:#7a4a00
    style USE_RAG fill:#d5f5d0,stroke:#7BB661,stroke-width:2px,color:#2d5a1e
    linkStyle default stroke:#000000,stroke-width:2.5px
```

---

## 8. RAG-Grounded Planning — Component Flow

The Planner pattern — how the Orchestrator dynamically builds a verification plan using RAG policy retrieval before dispatching investigation agents. Each step is attributed to the owning component/project in the solution. **Both tool calls (`GetAssetProfile` and `QueryKnowledgeBase`) travel the same path: GPT-4o emits `tool_call` → FunctionInvocation intercepts → McpClientTool → HTTP/SSE → MCP Server → Provider.** There are no direct API calls.

```mermaid
graph TD
    ORCH["① CTLEvaluationOrchestrator<br/>calls GetResponseAsync() once<br/>with PlanningSystemPrompt + asset ID"]

    ORCH --> LLM1["② GPT-4o (LLM call #1)<br/>Reads prompt, decides it needs asset data<br/>Responds: tool_call: GetAssetProfile(assetId)"]

    subgraph TOOLCALL1["Tool Call #1 — GetAssetProfile (same MCP path)"]
        direction TB
        FI1["③a FunctionInvocation intercepts tool_call<br/>→ McpClientTool → HTTP/SSE"]
        MCP1["③b MCP Server :5100<br/>AssetProfileTools.GetAssetProfile()"]
        PROV1["③c IAssetProfileProvider<br/>Returns Asset JSON:<br/>state:TX · type:Foreclosure · county:Dallas · tier:1"]
        FI1 --> MCP1
        MCP1 --> PROV1
    end

    LLM1 --> FI1
    PROV1 --> RET1["③d Result returns through MCP<br/>→ FunctionInvocation appends to conversation"]

    RET1 --> LLM2["④ GPT-4o (LLM call #2)<br/>Reads Asset profile, derives filters<br/>Responds: tool_call: QueryKnowledgeBase<br/>(query, stateCode:'TX', assetType:'Foreclosure')"]

    subgraph TOOLCALL2["Tool Call #2 — QueryKnowledgeBase (same MCP path)"]
        direction TB
        FI2["⑤a FunctionInvocation intercepts tool_call<br/>→ McpClientTool → HTTP/SSE"]
        MCP2["⑤b MCP Server :5100<br/>RAGTools.QueryKnowledgeBase()"]
        subgraph RAG["InMemoryRAGService.QueryAsync()"]
            R1["⑤c Metadata Filter<br/>State · County · AssetType<br/>(exact match or 'ALL' wildcard)"]
            R2["⑤d Keyword Scoring<br/>Title match: +0.3 per term<br/>Content occurrence: +0.1 per hit<br/>Score capped at 1.0"]
            R3["⑤e Relevance Threshold<br/>Exclude docs with score ≤ 0.05"]
            R4["⑤f Top-5 Selection<br/>OrderByDescending(score).Take(5)"]
            R1 --> R2
            R2 --> R3
            R3 --> R4
        end
        FI2 --> MCP2
        MCP2 --> R1
    end

    LLM2 --> FI2
    R4 --> RET2["⑥ RAGQueryResult (JSON)<br/>returns through MCP<br/>→ FunctionInvocation appends to conversation"]

    RET2 --> LLM3["⑦ GPT-4o (LLM call #3)<br/>Now has: Asset profile + policy docs<br/>Synthesizes VerificationPlan JSON<br/>(no more tool_calls → final text response)"]

    LLM3 --> RETURN["⑧ FunctionInvocation passes response outward<br/>→ GuardrailsMiddleware tracks tokens<br/>→ Orchestrator receives VerificationPlan"]

    RETURN --> DISPATCH["⑨ Dispatch Investigation Agents<br/>Each also calls QueryKnowledgeBase<br/>via the same MCP path"]

    style TOOLCALL1 fill:#e8fde8,stroke:#7BB661,stroke-width:2px,color:#2d5a1e
    style TOOLCALL2 fill:#e8fde8,stroke:#7BB661,stroke-width:2px,color:#2d5a1e
    style RAG fill:#ede0f5,stroke:#9B59B6,stroke-width:2px,color:#5b2d82
    style ORCH fill:#dae8fc,stroke:#4A90D9,stroke-width:2px,color:#1a3a5c
    style LLM1 fill:#e0e8ff,stroke:#4A90D9,stroke-width:2px,color:#1a3a5c
    style LLM2 fill:#e0e8ff,stroke:#4A90D9,stroke-width:2px,color:#1a3a5c
    style LLM3 fill:#e0e8ff,stroke:#4A90D9,stroke-width:2px,color:#1a3a5c
    style RET1 fill:#f0f0f0,stroke:#888888,stroke-width:2px,color:#333333
    style RET2 fill:#f0f0f0,stroke:#888888,stroke-width:2px,color:#333333
    style RETURN fill:#fff3e0,stroke:#F5A623,stroke-width:2px,color:#7a4a00
    style DISPATCH fill:#fde8e8,stroke:#D94A4A,stroke-width:2px,color:#7a1a1a
    linkStyle default stroke:#000000,stroke-width:2.5px
```
**Brief Summary of the above flow**

Step ①  Engine(GPT-4o ) receives "evaluate asset ASSET-TX-001"
        Engine says: "I need the asset profile first" → tool_call: GetAssetProfile("ASSET-TX-001")
        
Step ②  Broker (FunctionInvocation) intercepts, calls Asset API, returns JSON:
        { state: "TX", type: "Foreclosure", county: "Dallas", tier: 1 }
        
Step ③  Engine now has context. Says: "TX Foreclosure — I need state-specific policies"
        → tool_call: QueryKnowledgeBase(query: "Texas foreclosure CTL", state: "TX", assetType: "Foreclosure")
        
Step ④  Broker intercepts, calls RAG search API on MCP Server
        
Step ⑤  RAG search (InMemoryRAGService) runs:
        a. Filter: state=TX or ALL → 4 docs remain
        b. Score: keyword match "Texas" +0.3, "foreclosure" +0.3 in title → CTL-POLICY-TX-001 scores 0.9
        c. Threshold: drop anything ≤ 0.05
        d. Return top 5 sorted by score
        
Step ⑥  Policy docs returned through broker back to engine
        
Step ⑦  Engine now has: asset profile + relevant policies
        Synthesizes a work order: "Check Legal (TX §51.002), Valuation (60-day BPO), Occupancy"
        Output: VerificationPlan JSON
        
Step ⑧  Orchestrator takes that plan, fans out 3 parallel service calls
        (each investigation agent is just another engine instance with different config + different API access)


Engine = GPT-4o (Azure OpenAI). It's the LLM — the hosted AI model your solution calls over HTTPS. Your code never runs it locally; it sends text to https://YOUR-RESOURCE.openai.azure.com/ and gets text back. Configured in CTLAgentOptions.AzureOpenAI.Endpoint + DeploymentName: "gpt-4o".

Broker = FunctionInvocation — the built-in Microsoft.Extensions.AI middleware registered via .UseFunctionInvocation() in ServiceRegistration.cs. It sits in the IChatClient pipeline between your orchestrator code and Azure OpenAI. It intercepts tool_call responses from the LLM and routes them to the matching McpClientTool automatically.

The MCP server is created in Program.cs — it calls builder.Services.AddMcpServer() and app.MapMcp() to stand up the MCP endpoint on port 5100.

Decorate custom method with [McpServerTool] to register it as a tool within that MCP server

The tool classes that register onto it are in Tools.

**Component ownership summary:**

| Step | Component / Class | Project | Responsibility |
|------|-------------------|---------|----------------|
| ① Orchestrator call | `CTLEvaluationOrchestrator.GetResponseAsync()` | Application | Single call — FunctionInvocation handles all tool loops internally |
| ② LLM call #1 | GPT-4o (Azure OpenAI) | Cloud service | Reads prompt, emits `tool_call: GetAssetProfile` |
| ③a–d GetAssetProfile | `FunctionInvocation` → `McpClientTool` → MCP Server → `AssetProfileTools` → `IAssetProfileProvider` | Host → McpServer → Infrastructure | **Same MCP path as all tools** — returns Asset JSON |
| ④ LLM call #2 | GPT-4o (Azure OpenAI) | Cloud service | Reads Asset profile, derives filters, emits `tool_call: QueryKnowledgeBase` |
| ⑤a–f QueryKnowledgeBase | `FunctionInvocation` → `McpClientTool` → MCP Server → `RAGTools` → `InMemoryRAGService.QueryAsync()` | Host → McpServer → Infrastructure | **Same MCP path** — metadata filter → keyword scoring → threshold → top-5 |
| ⑥ Result return | MCP protocol → `FunctionInvocation` | Host (pipeline middleware) | RAGQueryResult JSON appended to LLM conversation |
| ⑦ LLM call #3 | GPT-4o (Azure OpenAI) | Cloud service | Synthesizes `VerificationPlan` from Asset + policies (final text, no tool_call) |
| ⑧ Response return | `FunctionInvocation` → `GuardrailsMiddleware` → Orchestrator | Host (pipeline middleware) | Token tracking, then deliver to caller |
| ⑨ Dispatch | `CTLEvaluationOrchestrator` | Application | `Task.WhenAll` dispatches 3 investigation agents with plan context |

> **Key insight:** `QueryKnowledgeBase` is not exclusive to the Plan phase. All 4 agents have it in their tool set (`McpToolProvider.GetToolsFor*()` — see Diagram 3). The Orchestrator calls it during **PLAN** and **REFLECT**, and each investigation agent calls it during **INVESTIGATE** for domain-specific policy lookups. Every tool call — regardless of which tool — travels the identical path: `FunctionInvocation → McpClientTool → HTTP/SSE → MCP Server → Tool Class → Provider`.

**Policy Corpus (InMemoryRAGService — 6 built-in documents, extensible via `/config/rag-knowledge/*.json`):**

| ID | Scope | Coverage |
|----|-------|----------|
| CTL-POLICY-001 | All States | Baseline CTL requirements — title, BPO staleness, occupancy, confidence thresholds |
| CTL-POLICY-TX-001 | Texas Foreclosure | Property Code §51.002, no redemption, HOA Ch. 209, 60-day BPO |
| CTL-POLICY-CA-001 | California REO | Civil Code §2924, 1-year redemption, SB-1079, LA RSO, NHD |
| CTL-POLICY-HOA-001 | All States | HOA delinquency — $5k blocker, $1-5k conditional, super lien states |
| CTL-POLICY-VAL-001 | All States | BPO mandatory, staleness thresholds, AVM variance by state |
| CTL-POLICY-OCC-001 | All States | Occupancy clearance — vacant, eviction, unknown, cash-for-keys |

---

## 9. End-to-End Evaluation — Numbered Steps

The complete evaluation lifecycle from event trigger to verdict delivery, with numbered steps matching the implementation.

```mermaid
graph TD
    S1["① CTLEvaluationRequestedEvent<br/>Azure Service Bus"]
    S2["② CTLEvaluationOrchestrator<br/>.EvaluateAsync(request)"]
    S3["③ PLAN<br/>GetAssetProfile + QueryKnowledgeBase<br/>→ VerificationPlan"]

    S1 --> S2
    S2 --> S3
    S3 --> S4A
    S3 --> S4B
    S3 --> S4C

    S4A["④a Legal Agent<br/>Tools: SearchTitle · CheckHOADelinquency<br/>LookupCodeViolations · QueryKnowledgeBase"]
    S4B["④b Valuation Agent<br/>Tools: RetrieveBPO · GetAVM<br/>QueryKnowledgeBase"]
    S4C["④c Occupancy Agent<br/>Tools: GetOccupancyStatus<br/>QueryKnowledgeBase"]

    S4A --> S5
    S4B --> S5
    S4C --> S5

    S5["⑤ REFLECT<br/>Contradiction detection · Confidence scoring<br/>Evidence synthesis · QueryKnowledgeBase<br/>(policy lookups to resolve contradictions)"]

    S5 --> S6["⑥ VERDICT<br/>ParseVerdict() → CTLVerdictDto"]

    S6 --> S7A["⑦a DocumentService<br/>Evidence Report"]
    S6 --> S7B["⑦b CamundaGateway<br/>CTLVerdictReceived"]
    S6 --> S7C["⑦c AuditService<br/>Full trace"]

    style S1 fill:#e8f4fd,stroke:#4A90D9,stroke-width:2px,color:#1a3a5c
    style S3 fill:#e8f4fd,stroke:#4A90D9,stroke-width:2px,color:#1a3a5c
    style S4A fill:#e8fde8,stroke:#7BB661,stroke-width:2px,color:#2d5a1e
    style S4B fill:#e8fde8,stroke:#7BB661,stroke-width:2px,color:#2d5a1e
    style S4C fill:#e8fde8,stroke:#7BB661,stroke-width:2px,color:#2d5a1e
    style S5 fill:#fff3e0,stroke:#F5A623,stroke-width:2px,color:#7a4a00
    style S6 fill:#fde8e8,stroke:#D94A4A,stroke-width:2px,color:#7a1a1a
    style S7A fill:#f0f0f0,stroke:#888888,stroke-width:2px,color:#333333
    style S7B fill:#f0f0f0,stroke:#888888,stroke-width:2px,color:#333333
    style S7C fill:#f0f0f0,stroke:#888888,stroke-width:2px,color:#333333
    linkStyle default stroke:#000000,stroke-width:2.5px
```

---

## 10. Solution Project Map

How the .NET solution projects map to the agentic architecture layers.

```mermaid
graph TD
    subgraph HOST["Cascade.CTL.Agent.Host"]
        H1["Program.cs — CLI entry point"]
        H2["ServiceRegistration.cs — DI root"]
    end

    HOST --> APP

    subgraph APP["Cascade.CTL.Agent.Application"]
        A1["CTLEvaluationOrchestrator"]
        A2["McpToolProvider"]
        A3["OrchestratorPrompts · InvestigationAgentPrompts"]
    end

    APP --> DOMAIN
    APP -.->|"HTTP/SSE"| MCPS

    HOST --> GUARD

    subgraph GUARD["Cascade.CTL.Agent.Guardrails"]
        GR1["GuardrailsMiddleware"]
        GR2["PromptInjection · PII · TokenBudget"]
    end

    GUARD --> DOMAIN

    subgraph DOMAIN["Cascade.CTL.Agent.Domain"]
        D1["Models: Asset · CTLVerdictDto · FindingsReports"]
        D2["Contracts: 8 Provider Interfaces"]
    end

    HOST --> INFRA

    subgraph INFRA["Cascade.CTL.Agent.Infrastructure"]
        I1["7 Mock Providers"]
        I2["InMemoryRAGService (6 policies)"]
        I3["ConsoleAuditService · Telemetry"]
    end

    INFRA --> DOMAIN

    subgraph MCPS["Cascade.CTL.Agent.McpServer"]
        M1["8 MCP Tools (5 tool classes)"]
        M2["ASP.NET Core :5100"]
    end

    MCPS --> INFRA

    style HOST fill:#dae8fc,stroke:#4A90D9,stroke-width:2px,color:#1a3a5c
    style APP fill:#d6eaf8,stroke:#2E86C1,stroke-width:2px,color:#1a4971
    style GUARD fill:#fde0e0,stroke:#E74C3C,stroke-width:2px,color:#7a1a1a
    style DOMAIN fill:#ede0f5,stroke:#8E44AD,stroke-width:2px,color:#5b2d82
    style INFRA fill:#d5f5d0,stroke:#27AE60,stroke-width:2px,color:#1a5c30
    style MCPS fill:#fff3e0,stroke:#F39C12,stroke-width:2px,color:#7a4a00
    linkStyle default stroke:#000000,stroke-width:2.5px
```

---

