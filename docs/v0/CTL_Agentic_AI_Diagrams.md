# Cascade 2.0 — CTL Agent: Agentic AI Architecture Diagrams

**Solution:** Asset Clear-To-List (CTL) Determination Agent  
**Focus:** Agentic AI Component Design  
**Aligned to:** Cascade.CTL.AgentSolution (.NET 8) · cascade2_ctl_agent_solution_architecture.md · CTL_Architecture_Readout.md  
**Date:** March 29, 2026

---

## 1. CTL Agent — System Overview

A single evaluation flow: event in, verdict out. Two processes — the Host orchestrates agents via LLM, the MCP Server exposes tools over HTTP/SSE.

```mermaid
graph LR
    SB["① Azure Service Bus\nCTLEvaluationRequestedEvent"] --> HOST["② CTL Agent Host\n.NET 8 · IChatClient Pipeline"]
    HOST -->|"IChatClient\nGetResponseAsync"| AOAI["③ Azure OpenAI\nGPT-4o · Temp 0.1"]
    HOST -->|"HTTP/SSE :5100\nListTools · CallTool"| MCP["④ MCP Tool Server\n8 Tools · ASP.NET Core"]
    MCP -->|"Provider Interfaces"| TOOLS["⑤ Tool Backends\nMock / Real APIs"]
    HOST --> VERDICT["⑥ CTLVerdictDto\n→ CamundaGateway"]
```

---

## 2. 4-Phase Orchestration Pattern

The core agentic pattern — Plan, Investigate, Reflect, Decide. This is the `CTLEvaluationOrchestrator.EvaluateAsync()` method.

```mermaid
graph TD
    START(["Asset ID"]) --> P1

    subgraph PHASE1["Phase 1 — PLAN"]
        P1["Orchestrator Agent\nPlanningSystemPrompt"]
        P1 -->|"calls"| T1["GetAssetProfile\nQueryKnowledgeBase"]
        T1 --> PLAN["VerificationPlan\ndomains · policies · rationale"]
    end

    PLAN --> PHASE2

    subgraph PHASE2["Phase 2 — INVESTIGATE  (Task.WhenAll)"]
        direction LR
        LEGAL["Legal Agent\nLegalAgentSystemPrompt"]
        VAL["Valuation Agent\nValuationAgentSystemPrompt"]
        OCC["Occupancy Agent\nOccupancyAgentSystemPrompt"]
    end

    PHASE2 --> P3

    subgraph PHASE3["Phase 3 — REFLECT"]
        P3["Orchestrator Agent\nReflectionSystemPrompt"]
        P3 --> REF["Critique findings\nDetect contradictions\nApply confidence rules"]
    end

    REF --> P4

    subgraph PHASE4["Phase 4 — VERDICT"]
        P4["ParseVerdict()"]
        P4 --> V["CTLVerdictDto\nverdict · confidence · conditions\nevidenceTrail · reflectionLog"]
    end

    style PHASE1 fill:#e8f4fd,stroke:#4A90D9
    style PHASE2 fill:#e8fde8,stroke:#7BB661
    style PHASE3 fill:#fff3e0,stroke:#F5A623
    style PHASE4 fill:#fde8e8,stroke:#D94A4A
```

---

## 3. Agent Topology — Who Calls What

Four agents, eight tools. Each agent sees only its assigned tools via `McpToolProvider` role-based filtering. Investigation agents run concurrently and return structured findings to the Orchestrator.

```mermaid
graph TD
    subgraph ORCH["CTL Orchestrator Agent"]
        O_PLAN["① Plan"] --> O_DISPATCH["② Dispatch"]
        O_DISPATCH --> O_REFLECT["③ Reflect"]
        O_REFLECT --> O_VERDICT["④ Verdict"]
    end

    subgraph TOOLS_O["Orchestrator Tools"]
        TO1["GetAssetProfile"]
        TO2["QueryKnowledgeBase"]
    end

    O_PLAN -.-> TOOLS_O
    O_REFLECT -.-> TOOLS_O

    O_DISPATCH -->|"Task.WhenAll"| LEGAL
    O_DISPATCH -->|"Task.WhenAll"| VALUATION
    O_DISPATCH -->|"Task.WhenAll"| OCCUPANCY

    subgraph LEGAL["Legal & Title Agent"]
        L1["LegalFindingsReport\ndomainVerdict · confidence"]
    end

    subgraph VALUATION["Valuation Agent"]
        V1["ValuationFindingsReport\ndomainVerdict · confidence"]
    end

    subgraph OCCUPANCY["Occupancy Agent"]
        OC1["OccupancyFindingsReport\ndomainVerdict · confidence"]
    end

    subgraph TOOLS_L["Legal Tools"]
        TL1["SearchTitle"]
        TL2["CheckHOADelinquency"]
        TL3["LookupCodeViolations"]
        TL4["QueryKnowledgeBase"]
    end

    subgraph TOOLS_V["Valuation Tools"]
        TV1["RetrieveBPO"]
        TV2["GetAVM"]
        TV3["QueryKnowledgeBase"]
    end

    subgraph TOOLS_OC["Occupancy Tools"]
        TC1["GetOccupancyStatus"]
        TC2["QueryKnowledgeBase"]
    end

    LEGAL -.-> TOOLS_L
    VALUATION -.-> TOOLS_V
    OCCUPANCY -.-> TOOLS_OC

    LEGAL -->|"findings"| O_REFLECT
    VALUATION -->|"findings"| O_REFLECT
    OCCUPANCY -->|"findings"| O_REFLECT

    style ORCH fill:#4A90D9,color:#fff
    style LEGAL fill:#7BB661,color:#fff
    style VALUATION fill:#7BB661,color:#fff
    style OCCUPANCY fill:#7BB661,color:#fff
    style TOOLS_O fill:#fff3e0
    style TOOLS_L fill:#fff3e0
    style TOOLS_V fill:#fff3e0
    style TOOLS_OC fill:#fff3e0
```

---

## 4. MCP Client-Server Architecture

Two-process model. The Host connects to the MCP Server over HTTP/SSE using `McpClient.CreateAsync()`. Tools are auto-discovered via `[McpServerToolType]` attributes. `McpClientTool` implements `AITool` — direct use with `IChatClient`.

```mermaid
graph LR
    subgraph HOST["CTL Agent Host Process"]
        MTP["McpToolProvider"]
        MTP -->|"① HttpClientTransport\nHttpTransportMode.Sse"| CLIENT["McpClient"]
        CLIENT -->|"② ListToolsAsync()"| FILTER["Role-Based Filtering\nGetToolsFor*Agent()"]
        FILTER --> CHATOPT["③ ChatOptions.Tools\n= IList＜AITool＞"]
    end

    subgraph SERVER["MCP Tool Server — :5100"]
        MCPAPI["MapMcp()\nAddMcpServer()\n.WithHttpTransport()\n.WithToolsFromAssembly()"]
        MCPAPI --> TCLASS["Tool Classes"]

        subgraph TCLASS[" "]
            direction TB
            A["AssetProfileTools\nGetAssetProfile"]
            B["LegalTools\nSearchTitle · CheckHOADelinquency\nLookupCodeViolations"]
            C["ValuationTools\nRetrieveBPO · GetAVM"]
            D["OccupancyTools\nGetOccupancyStatus"]
            E["RAGTools\nQueryKnowledgeBase"]
        end

        TCLASS -->|"DI"| PROV["Infrastructure Providers\nMock ↔ Real (configurable)"]
    end

    CLIENT <-->|"HTTP/SSE\nCallTool → JSON"| MCPAPI

    style HOST fill:#e8f4fd
    style SERVER fill:#fde8e8
```

---

## 5. IChatClient Middleware Pipeline

Every LLM call passes through this pipeline. Built in `ServiceRegistration.ConfigureCTLAgent()` via `ChatClientBuilder`. The guardrails wrap the entire pipeline as a `DelegatingChatClient`.

```mermaid
graph TB
    APP["CTLEvaluationOrchestrator\nGetResponseAsync()"] --> G

    subgraph G["GuardrailsMiddleware"]
        direction TB
        G1["① Token Budget Check\nBlock if ≥ 50,000 consumed"]
        G1 --> G2["② Content Safety Screen\n10 injection patterns · Azure AI Content Safety"]
        G2 --> G3["③ PII Masking\nSSN · CC · Email · Phone"]
    end

    G3 --> FI["④ FunctionInvocation\nAuto-executes tool calls\nMcpClientTool → MCP Server"]
    FI --> OT["⑤ OpenTelemetry\nSpans · Metrics\nSensitiveData = false"]
    OT --> AOAI["⑥ Azure OpenAI\nGPT-4o · Structured Outputs"]

    AOAI --> RESP["Response"]
    RESP --> G4["⑦ Token Consumption Tracking\nInterlocked.Add (thread-safe)"]
    G4 --> APP2["Return to Orchestrator"]

    style G fill:#ffe0e0
    style FI fill:#e8fde8
    style OT fill:#fff3e0
    style AOAI fill:#e0e8ff
```

---

## 6. Reflection & Verdict Decision Logic

The Orchestrator's reflection phase — how contradictions, confidence, and unverified fields determine the final verdict.

```mermaid
graph TD
    IN["Investigation Agent Findings\nLegal + Valuation + Occupancy\n+ Raw Asset Profile Metadata"] --> R1

    R1["① Aggregate Evidence\nMerge findings from all 3 domains"]
    R1 --> R2["② Contradiction Detection\nLegal says Clear but Occupancy says Blocker?"]

    R2 -->|"Contradictions found"| PEN["③ Confidence Penalty\n≥ −0.15 per contradiction"]
    R2 -->|"No contradictions"| R3

    PEN --> R3["④ Unverified Field Assessment\nTool failures flagged as gaps"]

    R3 --> R4["⑤ Apply Confidence Thresholds"]

    R4 -->|"≥ 0.90\nNo Blockers"| CLEAR["Clear"]
    R4 -->|"≥ 0.90\nWith Conditions"| CLEARC["ClearWithConditions"]
    R4 -->|"0.75 – 0.89"| CLEARC2["ClearWithConditions\n(forced disclosure)"]
    R4 -->|"< 0.75"| HUMAN["NeedsHumanReview"]
    R4 -->|"Any Blocker"| NOTCLEAR["NotClear"]

    style CLEAR fill:#7BB661,color:#fff
    style CLEARC fill:#F5A623,color:#fff
    style CLEARC2 fill:#F5A623,color:#fff
    style HUMAN fill:#9B59B6,color:#fff
    style NOTCLEAR fill:#D94A4A,color:#fff
```

---

## 7. Tool Failure Cascade

Not all tool failures are equal. Blocking tools abort; non-blocking tools reduce confidence.

```mermaid
graph TD
    CALL["Tool Invoked"] --> TYPE{Tool Category?}

    TYPE -->|"Blocking\nGetAssetProfile · RetrieveBPO"| BF{Succeeds?}
    BF -->|"No"| ABORT["NeedsHumanReview\nImmediate — confidence = 0"]
    BF -->|"Yes"| CONT["Continue Evaluation"]

    TYPE -->|"Non-Blocking\nSearchTitle · CheckHOA\nLookupCodeViolations\nGetAVM · GetOccupancyStatus"| NBF{Succeeds?}
    NBF -->|"No"| FLAG["Flag as Unverified\n→ UnverifiedFields[]\n→ Confidence penalty in Reflection"]
    NBF -->|"Yes"| USE["Use Result in\nFindingsReport"]

    TYPE -->|"RAG\nQueryKnowledgeBase"| RAGF{Succeeds?}
    RAGF -->|"No (attempt 1)"| RETRY1["Retry"]
    RETRY1 -->|"No (attempt 2)"| RETRY2["Retry"]
    RETRY2 -->|"No (attempt 3)"| FLAG
    RAGF -->|"Yes"| USE
    RETRY1 -->|"Yes"| USE
    RETRY2 -->|"Yes"| USE

    style ABORT fill:#D94A4A,color:#fff
    style FLAG fill:#F5A623,color:#fff
    style CONT fill:#7BB661,color:#fff
    style USE fill:#7BB661,color:#fff
```

---

## 8. RAG-Grounded Planning

The Planner pattern — how the Orchestrator dynamically builds a verification plan using RAG policy retrieval before dispatching investigation agents.

```mermaid
graph LR
    ASSET["① Asset Profile\nType · State · County\nSeller Tier · Occupancy"] --> RAG

    subgraph RAG["② QueryKnowledgeBase"]
        direction TB
        FILT["Metadata Filter\nstate · county · assetType"]
        FILT --> SCORE["Keyword Scoring\nTitle +0.3 · Content +0.1"]
        SCORE --> TOP5["Top 5 Documents\nRelevanceScore > 0.05"]
    end

    RAG --> LLM["③ Orchestrator LLM\nPlanningSystemPrompt"]
    LLM --> PLAN["④ VerificationPlan"]

    subgraph PLAN[" "]
        direction TB
        P1["requiredDomains:\nLegal · Valuation · Occupancy"]
        P2["relevantPolicies:\nCTL-POLICY-TX-001\nCTL-POLICY-VAL-001"]
        P3["planRationale:\nTX Foreclosure per §51.002"]
    end

    PLAN --> DISPATCH["⑤ Dispatch Investigation Agents\nwith plan context"]

    style RAG fill:#fff3e0
    style PLAN fill:#e8f4fd
```

**Policy Corpus (InMemoryRAGService):**

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
    S1["① CTLEvaluationRequestedEvent\nAzure Service Bus"] --> S2["② CTLEvaluationOrchestrator\n.EvaluateAsync(request)"]

    S2 --> S3["③ PLAN\nGetAssetProfile + QueryKnowledgeBase\n→ VerificationPlan"]

    S3 --> S4["④ INVESTIGATE (concurrent)"]

    subgraph S4[" "]
        direction LR
        S4A["Legal Agent\n4 tools"]
        S4B["Valuation Agent\n3 tools"]
        S4C["Occupancy Agent\n2 tools"]
    end

    S4 --> S5["⑤ REFLECT\nContradiction detection\nConfidence scoring\nEvidence synthesis"]

    S5 --> S6["⑥ VERDICT\nParseVerdict() → CTLVerdictDto"]

    S6 --> S7["⑦ OUTPUT"]

    subgraph S7[" "]
        direction LR
        S7A["DocumentService\nEvidence Report"]
        S7B["CamundaGateway\nCTLVerdictReceived"]
        S7C["AuditService\nFull trace"]
    end

    style S1 fill:#e8f4fd
    style S3 fill:#e8f4fd
    style S4 fill:#e8fde8
    style S5 fill:#fff3e0
    style S6 fill:#fde8e8
    style S7 fill:#f0f0f0
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

    subgraph APP["Cascade.CTL.Agent.Application"]
        A1["CTLEvaluationOrchestrator"]
        A2["McpToolProvider"]
        A3["OrchestratorPrompts · InvestigationAgentPrompts"]
    end

    subgraph GUARD["Cascade.CTL.Agent.Guardrails"]
        GR1["GuardrailsMiddleware"]
        GR2["PromptInjection · PII · TokenBudget"]
    end

    subgraph DOMAIN["Cascade.CTL.Agent.Domain"]
        D1["Models: Asset · CTLVerdictDto · FindingsReports"]
        D2["Contracts: 8 Provider Interfaces"]
    end

    subgraph INFRA["Cascade.CTL.Agent.Infrastructure"]
        I1["7 Mock Providers"]
        I2["InMemoryRAGService (6 policies)"]
        I3["ConsoleAuditService · Telemetry"]
    end

    subgraph MCPS["Cascade.CTL.Agent.McpServer"]
        M1["8 MCP Tools (5 tool classes)"]
        M2["ASP.NET Core :5100"]
    end

    HOST --> APP
    HOST --> GUARD
    HOST --> INFRA
    APP --> DOMAIN
    APP -.->|"HTTP/SSE"| MCPS
    INFRA --> DOMAIN
    GUARD --> DOMAIN
    MCPS --> INFRA

    style HOST fill:#4A90D9,color:#fff
    style APP fill:#2E86C1,color:#fff
    style GUARD fill:#E74C3C,color:#fff
    style DOMAIN fill:#8E44AD,color:#fff
    style INFRA fill:#27AE60,color:#fff
    style MCPS fill:#F39C12,color:#fff
```

---

## Rendering

All diagrams use **Mermaid** — renders natively in:
- GitHub (README, PRs, Issues)
- VS Code (Markdown Preview / Mermaid extension)
- Azure DevOps Wiki
- Confluence (Mermaid macro)
- Any Mermaid-compatible renderer

No external tools, plugins, or PlantUML servers required.
