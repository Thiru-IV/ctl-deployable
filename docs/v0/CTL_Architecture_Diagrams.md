# Cascade 2.0 — CTL Agent: Enterprise Architecture Diagrams

**Document Type:** Architecture Diagram Companion  
**Solution:** Asset Clear-To-List (CTL) Determination Agent  
**Alignment:** Cascade.CTL.AgentSolution (.NET 8) + CTL_Architecture_Readout.md  
**Diagram Format:** PlantUML (`.puml` compatible — render via PlantUML server, VS Code plugin, or IntelliJ)  
**Prepared:** March 29, 2026

---

## Table of Contents

1. [System Context Diagram](#1-system-context-diagram)
2. [CTL Agent Host — Internal Architecture (Container Diagram)](#2-ctl-agent-host--internal-architecture)
3. [4-Phase Orchestration Sequence](#3-4-phase-orchestration-sequence)
4. [Agent Topology & Workflow](#4-agent-topology--workflow)
5. [MCP Client-Server Architecture](#5-mcp-client-server-architecture)
6. [IChatClient Middleware Pipeline](#6-ichatclient-middleware-pipeline)
7. [Guardrails Screening Pipeline](#7-guardrails-screening-pipeline)
8. [Domain Model Class Diagram](#8-domain-model-class-diagram)
9. [RAG Architecture](#9-rag-architecture)
10. [Infrastructure & Deployment Topology](#10-infrastructure--deployment-topology)
11. [Tool Failure Cascade Policy](#11-tool-failure-cascade-policy)
12. [DI Composition Root](#12-di-composition-root)

---

## 1. System Context Diagram

Shows the CTL Agent system boundary and all external actors/systems it interacts with — aligned to the Cascade 2.0 platform integration contracts.

```plantuml
@startuml C4_SystemContext
!include <C4/C4_Context>

title Cascade 2.0 — CTL Agent System Context

Person(assetManager, "Asset Manager", "Reviews NeedsHumanReview\nverdicts and overrides")
System_Boundary(ctlSystem, "CTL Agent System") {
    System(ctlHost, "CTL Agent Host Service", ".NET 8 Console/Worker Service\nMulti-agent orchestration\nMCP client")
    System(mcpServer, "CTL MCP Tool Server", "ASP.NET Core\nHTTP/SSE transport\n8 registered tools")
}

System_Ext(assetService, "AssetService", "Cascade 2.0\nAsset domain API")
System_Ext(documentService, "DocumentService", "Cascade 2.0\nDocument storage API")
System_Ext(camundaGw, "CamundaGatewayService", "Cascade 2.0\nWorkflow orchestration")
System_Ext(serviceBus, "Azure Service Bus", "CTLEvaluationRequestedEvent\ntopic subscription")
System_Ext(azureOpenAI, "Azure AI Foundry", "GPT-4o\nStructured outputs\nTemperature 0.1")
System_Ext(contentSafety, "Azure AI Content Safety", "Prompt Shields\nPII detection")
System_Ext(titleProvider, "Title Data Provider", "External — title search,\nliens, encumbrances")
System_Ext(hoaProvider, "HOA Data Provider", "External — HOA\ndelinquency status")
System_Ext(avmProvider, "AVM Provider", "External — automated\nvaluation models")
System_Ext(fieldServices, "Field Services API", "External — occupancy\ninspections")
System_Ext(municipalAPI, "Municipal API", "External — code\nviolation lookups")

Rel(serviceBus, ctlHost, "CTLEvaluationRequestedEvent", "AMQP")
Rel(ctlHost, mcpServer, "ListTools / CallTool", "HTTP/SSE :5100")
Rel(ctlHost, azureOpenAI, "IChatClient\nGetResponseAsync", "HTTPS (Private Endpoint)")
Rel(ctlHost, contentSafety, "ScreenInputAsync", "HTTPS (Private Endpoint)")
Rel(mcpServer, assetService, "GET /assets/{id}", "HTTPS")
Rel(mcpServer, documentService, "GET /documents/bpo/{id}", "HTTPS")
Rel(mcpServer, titleProvider, "SearchTitle", "HTTPS via APIM")
Rel(mcpServer, hoaProvider, "CheckHOADelinquency", "HTTPS via APIM")
Rel(mcpServer, avmProvider, "GetAVM", "HTTPS via APIM")
Rel(mcpServer, fieldServices, "GetOccupancyStatus", "HTTPS via APIM")
Rel(mcpServer, municipalAPI, "LookupCodeViolations", "HTTPS via APIM")
Rel(ctlHost, camundaGw, "POST /workflow/message\nCTLVerdictReceived", "HTTPS")
Rel(ctlHost, documentService, "POST /documents/store\nCTL Evidence Report", "HTTPS")
Rel(assetManager, ctlHost, "Reviews escalated\nverdicts", "Cascade UI")

@enduml
```

---

## 2. CTL Agent Host — Internal Architecture

Maps directly to the .NET solution project structure (`src/` folders) and the DI composition root in `ServiceRegistration.cs`.

```plantuml
@startuml CTL_Container_Architecture
!include <C4/C4_Container>

title CTL Agent Host Service — Internal Architecture (Aligned to .NET Solution Projects)

System_Boundary(hostBoundary, "CTL Agent Host Service") {

    Container(hostProject, "Cascade.CTL.Agent.Host", ".NET 8 Console App\nProgram.cs + ServiceRegistration.cs", "DI composition root\nCLI entry point\n--asset-id parsing")

    Container(appProject, "Cascade.CTL.Agent.Application", ".NET 8 Class Library", "CTLEvaluationOrchestrator\nMcpToolProvider\nOrchestratorPrompts\nInvestigationAgentPrompts\nCTLAgentOptions")

    Container(guardrailsProject, "Cascade.CTL.Agent.Guardrails", ".NET 8 Class Library", "GuardrailsMiddleware\nLocalPromptInjectionDetector\nContentSafetyGuard\nPiiFilter\nInputValidator\nTokenBudgetGuard")

    Container(domainProject, "Cascade.CTL.Agent.Domain", ".NET 8 Class Library", "Asset, CTLVerdictDto\nCTLEvaluationRequest/Result\nFindings Reports\nTool Result DTOs\n8 Provider Interfaces\nIAuditService")

    Container(infraProject, "Cascade.CTL.Agent.Infrastructure", ".NET 8 Class Library", "7 Mock Providers\nInMemoryRAGService\nConsoleAuditService\nTelemetryConfiguration")

    ContainerDb(ragStore, "InMemoryRAGService", "6 Policy Documents", "General CTL, TX Foreclosure\nCA REO, HOA, Valuation\nOccupancy policies")
}

System_Boundary(mcpBoundary, "MCP Tool Server (Separate Process)") {
    Container(mcpProject, "Cascade.CTL.Agent.McpServer", "ASP.NET Core\nhttp://localhost:5100", "8 MCP Tools\nAssetProfileTools\nLegalTools\nValuationTools\nOccupancyTools\nRAGTools")
}

Rel(hostProject, appProject, "Resolves via DI")
Rel(hostProject, guardrailsProject, "Registers guardrails")
Rel(hostProject, infraProject, "Registers providers")
Rel(appProject, domainProject, "Uses models/contracts")
Rel(appProject, guardrailsProject, "TokenBudgetGuard")
Rel(appProject, mcpProject, "HTTP/SSE\nHttpClientTransport\nMcpClient.CreateAsync()", "Port 5100")
Rel(infraProject, domainProject, "Implements interfaces")
Rel(guardrailsProject, domainProject, "Uses models")
Rel(mcpProject, infraProject, "DI: Provider interfaces\n→ Mock implementations")
Rel(infraProject, ragStore, "QueryAsync()")

@enduml
```

---

## 3. 4-Phase Orchestration Sequence

Precise sequence diagram of `CTLEvaluationOrchestrator.EvaluateAsync()` — every phase, every method call, every tool invocation, exactly as implemented.

```plantuml
@startuml CTL_Orchestration_Sequence
!theme cerulean-outline

title CTL Evaluation — 4-Phase Orchestration Sequence\n(CTLEvaluationOrchestrator.EvaluateAsync)

skinparam sequenceMessageAlign center
skinparam maxMessageSize 220

actor "CLI / Service Bus" as CLI
participant "CTLEvaluation\nOrchestrator" as ORCH
participant "IChatClient\n(Pipeline)" as LLM
participant "McpToolProvider" as MCP
participant "MCP Server\n:5100" as MCPS
participant "IAuditService" as AUDIT
participant "TokenBudgetGuard" as TBG

== Initialization ==

CLI -> ORCH : EvaluateAsync(CTLEvaluationRequest)
activate ORCH
ORCH -> AUDIT : RecordStepAsync("EvaluationStarted")

== Phase 1 — PLANNING ==

ORCH -> MCP : GetToolsForOrchestrator()
MCP --> ORCH : [GetAssetProfile, QueryKnowledgeBase]

ORCH -> LLM : GetResponseAsync(\n  system: OrchestratorPrompts.PlanningSystemPrompt,\n  user: "Evaluate asset {assetId}...",\n  tools: [GetAssetProfile, QueryKnowledgeBase],\n  Temperature: 0.1)
activate LLM

LLM -> MCPS : CallTool: GetAssetProfile(assetId)
MCPS --> LLM : Asset JSON
LLM -> MCPS : CallTool: QueryKnowledgeBase(\n  query, stateCode, assetType)
MCPS --> LLM : RAGQueryResult JSON

LLM --> ORCH : Planning response\n{requiredDomains, relevantPolicies,\nassetProfileSummary, planRationale}
deactivate LLM

ORCH -> AUDIT : RecordStepAsync("PlanGenerated")

== Phase 2 — INVESTIGATION AGENT FAN-OUT (Task.WhenAll) ==

ORCH -> AUDIT : RecordStepAsync("InvestigationAgentStarted: Legal")
ORCH -> AUDIT : RecordStepAsync("InvestigationAgentStarted: Valuation")
ORCH -> AUDIT : RecordStepAsync("InvestigationAgentStarted: Occupancy")

par Legal Agent
    ORCH -> MCP : GetToolsForLegalAgent()
    MCP --> ORCH : [SearchTitle, CheckHOADelinquency,\nLookupCodeViolations, QueryKnowledgeBase]

    ORCH -> LLM : GetResponseAsync(\n  system: InvestigationAgentPrompts.LegalAgentSystemPrompt,\n  user: planningOutput + assetContext,\n  tools: legalTools)
    activate LLM #LightBlue
    LLM -> MCPS : CallTool: SearchTitle(parcelId, stateCode)
    MCPS --> LLM : TitleSearchResult
    LLM -> MCPS : CallTool: CheckHOADelinquency(address)
    MCPS --> LLM : HOAResult
    LLM -> MCPS : CallTool: LookupCodeViolations(address, county)
    MCPS --> LLM : CodeViolationResult
    LLM --> ORCH : LegalFindingsReport JSON
    deactivate LLM

else Valuation Agent
    ORCH -> MCP : GetToolsForValuationAgent()
    MCP --> ORCH : [RetrieveBPO, GetAVM, QueryKnowledgeBase]

    ORCH -> LLM : GetResponseAsync(\n  system: InvestigationAgentPrompts.ValuationAgentSystemPrompt,\n  user: planningOutput + assetContext,\n  tools: valuationTools)
    activate LLM #LightGreen
    LLM -> MCPS : CallTool: RetrieveBPO(assetId)
    MCPS --> LLM : BPOResult
    LLM -> MCPS : CallTool: GetAVM(address, stateCode)
    MCPS --> LLM : AVMResult
    LLM --> ORCH : ValuationFindingsReport JSON
    deactivate LLM

else Occupancy Agent
    ORCH -> MCP : GetToolsForOccupancyAgent()
    MCP --> ORCH : [GetOccupancyStatus, QueryKnowledgeBase]

    ORCH -> LLM : GetResponseAsync(\n  system: InvestigationAgentPrompts.OccupancyAgentSystemPrompt,\n  user: planningOutput + assetContext,\n  tools: occupancyTools)
    activate LLM #LightCoral
    LLM -> MCPS : CallTool: GetOccupancyStatus(address)
    MCPS --> LLM : OccupancyStatusResult
    LLM --> ORCH : OccupancyFindingsReport JSON
    deactivate LLM
end

ORCH -> AUDIT : RecordStepAsync("InvestigationAgentCompleted: Legal")
ORCH -> AUDIT : RecordStepAsync("InvestigationAgentCompleted: Valuation")
ORCH -> AUDIT : RecordStepAsync("InvestigationAgentCompleted: Occupancy")

== Phase 3 — REFLECTION ==

ORCH -> MCP : GetToolsForOrchestrator()

ORCH -> LLM : GetResponseAsync(\n  system: OrchestratorPrompts.ReflectionSystemPrompt,\n  user: legalFindings + valuationFindings\n         + occupancyFindings + planningOutput\n         + assetProfileJson (raw metadata),\n  tools: [GetAssetProfile, QueryKnowledgeBase])
activate LLM #Wheat
LLM --> ORCH : Reflection JSON\n{verdict, confidenceScore,\nconditions[], evidenceTrail[],\nreflectionLog}
deactivate LLM

ORCH -> AUDIT : RecordStepAsync("ReflectionCompleted")

== Phase 4 — VERDICT PARSING ==

ORCH -> ORCH : ParseVerdict(reflectionJson)
note right
  Extract JSON substring
  Deserialize → VerdictJsonResponse
  Map → CTLVerdictDto
  Fallback: NeedsHumanReview
end note

ORCH -> TBG : CurrentUsage (for metrics)
ORCH -> AUDIT : RecordStepAsync("EvaluationCompleted",\n  duration, tokenCount, toolCount)

ORCH --> CLI : CTLEvaluationResult\n{Verdict, Duration, TokensUsed, ToolCount}
deactivate ORCH

@enduml
```

---

## 4. Agent Topology & Workflow

Shows all four agents, their tool assignments, output types, and the data flow between them — exactly matching `McpToolProvider` filtering and orchestrator dispatch.

```plantuml
@startuml Agent_Topology
!theme cerulean-outline

skinparam component {
    BackgroundColor<<orchestrator>> #4A90D9
    FontColor<<orchestrator>> White
    BackgroundColor<<subagent>> #7BB661
    FontColor<<subagent>> White
    BackgroundColor<<tool>> #F5A623
    FontColor<<tool>> White
    BackgroundColor<<mcp>> #9B59B6
    FontColor<<mcp>> White
}

title CTL Agent Topology — Tool Assignments & Data Flow

package "CTL Orchestrator Agent" <<orchestrator>> {
    [Phase 1: Planning\nOrchestratorPrompts.PlanningSystemPrompt\nTemperature: 0.1] as PLAN
    [Phase 3: Reflection\nOrchestratorPrompts.ReflectionSystemPrompt\nContradiction detection\nConfidence thresholds] as REFLECT
    [Phase 4: Verdict Parsing\nParseVerdict() → CTLVerdictDto\nFallback: NeedsHumanReview] as VERDICT
}

package "Legal & Title Agent" <<subagent>> {
    [InvestigationAgentPrompts\n.LegalAgentSystemPrompt\nOutput: LegalFindingsReport] as LEGAL
}

package "Valuation Readiness Agent" <<subagent>> {
    [InvestigationAgentPrompts\n.ValuationAgentSystemPrompt\nOutput: ValuationFindingsReport] as VALUATION
}

package "Occupancy & Condition Agent" <<subagent>> {
    [InvestigationAgentPrompts\n.OccupancyAgentSystemPrompt\nOutput: OccupancyFindingsReport] as OCCUPANCY
}

package "MCP Tool Server (:5100)" <<mcp>> {
    [GetAssetProfile] <<tool>> as T_ASSET
    [QueryKnowledgeBase] <<tool>> as T_RAG
    [SearchTitle] <<tool>> as T_TITLE
    [CheckHOADelinquency] <<tool>> as T_HOA
    [LookupCodeViolations] <<tool>> as T_CODE
    [RetrieveBPO] <<tool>> as T_BPO
    [GetAVM] <<tool>> as T_AVM
    [GetOccupancyStatus] <<tool>> as T_OCC
}

' Orchestrator tools
PLAN ..> T_ASSET : GetToolsForOrchestrator()
PLAN ..> T_RAG
REFLECT ..> T_ASSET
REFLECT ..> T_RAG

' Orchestrator → Investigation agents (Task.WhenAll)
PLAN -down-> LEGAL : " planOutput\n  + assetContext"
PLAN -down-> VALUATION : " planOutput\n  + assetContext"
PLAN -down-> OCCUPANCY : " planOutput\n  + assetContext"

' Investigation agent → Orchestrator
LEGAL -down-> REFLECT : LegalFindingsReport
VALUATION -down-> REFLECT : ValuationFindingsReport
OCCUPANCY -down-> REFLECT : OccupancyFindingsReport

' Investigation agent tools
LEGAL ..> T_TITLE : GetToolsForLegalAgent()
LEGAL ..> T_HOA
LEGAL ..> T_CODE
LEGAL ..> T_RAG

VALUATION ..> T_BPO : GetToolsForValuationAgent()
VALUATION ..> T_AVM
VALUATION ..> T_RAG

OCCUPANCY ..> T_OCC : GetToolsForOccupancyAgent()
OCCUPANCY ..> T_RAG

' Verdict
REFLECT -down-> VERDICT

note bottom of VERDICT
  Confidence ≥ 0.90 → Clear / ClearWithConditions
  0.75 – 0.89    → ClearWithConditions (forced)
  < 0.75          → NeedsHumanReview (escalation)
  Any Blocker     → NotClear
  Contradictions  → −0.15 confidence penalty
end note

@enduml
```

---

## 5. MCP Client-Server Architecture

Precise transport, discovery, and invocation flow between `McpToolProvider` (Application layer) and the `McpServer` (ASP.NET Core process).

```plantuml
@startuml MCP_Architecture
!theme cerulean-outline

title MCP Client-Server Architecture\n(ModelContextProtocol SDK 1.2.0)

skinparam rectangle {
    BackgroundColor<<client>> #E8F4FD
    BackgroundColor<<server>> #FDE8E8
    BackgroundColor<<provider>> #E8FDE8
}

rectangle "Cascade.CTL.Agent.Application" <<client>> {
    rectangle "McpToolProvider" as MCPC {
        card "HttpClientTransport" as TRANS
        card "HttpTransportMode.Sse" as MODE
        card "McpClient.CreateAsync(transport)" as CREATE
        card "client.ListToolsAsync()" as LIST
        note right of LIST
          Returns IList<McpClientTool>
          McpClientTool : AITool
          → Direct use in ChatOptions.Tools
        end note
    }

    rectangle "Tool Filtering (Role-based)" as FILTER {
        card "GetToolsForOrchestrator()\n→ GetAssetProfile, QueryKnowledgeBase" as F_ORCH
        card "GetToolsForLegalAgent()\n→ SearchTitle, CheckHOADelinquency,\n   LookupCodeViolations, QueryKnowledgeBase" as F_LEGAL
        card "GetToolsForValuationAgent()\n→ RetrieveBPO, GetAVM, QueryKnowledgeBase" as F_VAL
        card "GetToolsForOccupancyAgent()\n→ GetOccupancyStatus, QueryKnowledgeBase" as F_OCC
    }
}

rectangle "Cascade.CTL.Agent.McpServer\nhttp://localhost:5100" <<server>> {
    rectangle "ASP.NET Core Minimal API" as API {
        card "AddMcpServer()\n  .WithHttpTransport()\n  .WithToolsFromAssembly()" as SETUP
        card "app.MapMcp()" as MAP
    }

    rectangle "[McpServerToolType] Classes" as TOOLS {
        card "AssetProfileTools\n  [McpServerTool] GetAssetProfile(assetId)" as ST_ASSET
        card "LegalTools\n  [McpServerTool] SearchTitle(parcelId, stateCode)\n  [McpServerTool] CheckHOADelinquency(address)\n  [McpServerTool] LookupCodeViolations(address, county)" as ST_LEGAL
        card "ValuationTools\n  [McpServerTool] RetrieveBPO(assetId)\n  [McpServerTool] GetAVM(address, stateCode)" as ST_VAL
        card "OccupancyTools\n  [McpServerTool] GetOccupancyStatus(address)" as ST_OCC
        card "RAGTools\n  [McpServerTool] QueryKnowledgeBase(query, state?, county?, assetType?)" as ST_RAG
    }
}

rectangle "Infrastructure (DI in MCP Server)" <<provider>> {
    card "IAssetProfileProvider → MockAssetProfileProvider" as P1
    card "ITitleSearchProvider → MockTitleSearchProvider" as P2
    card "IHOAProvider → MockHOAProvider" as P3
    card "ICodeViolationProvider → MockCodeViolationProvider" as P4
    card "IBPOProvider → MockBPOProvider" as P5
    card "IAVMProvider → MockAVMProvider" as P6
    card "IOccupancyProvider → MockOccupancyProvider" as P7
    card "IRAGQueryService → InMemoryRAGService" as P8
}

TRANS -down-> MODE
MODE -down-> CREATE
CREATE -right-> LIST
LIST -down-> FILTER

MCPC -down[#blue,bold]-> API : "HTTP/SSE\nListTools → 8 tools\nCallTool → JSON result"

SETUP -down-> MAP
MAP -down-> TOOLS

TOOLS -down-> P1
TOOLS -down-> P2
TOOLS -down-> P3
TOOLS -down-> P4
TOOLS -down-> P5
TOOLS -down-> P6
TOOLS -down-> P7
TOOLS -down-> P8

@enduml
```

---

## 6. IChatClient Middleware Pipeline

Exact pipeline from `ServiceRegistration.ConfigureCTLAgent()` — every middleware layer, bottom-to-top.

```plantuml
@startuml IChatClient_Pipeline
!theme cerulean-outline

title IChatClient Middleware Pipeline\n(Host/ServiceRegistration.cs → ChatClientBuilder)

skinparam rectangle {
    RoundCorner 15
}

rectangle "Application Code\n(CTLEvaluationOrchestrator)" as APP #E8F4FD {
}

rectangle "GuardrailsMiddleware\n(DelegatingChatClient)" as GUARD #FFE0E0 {
    card "1. TokenBudgetGuard.IsWithinBudget\n    → Block if budget exceeded (50,000 default)" as G1
    card "2. ContentSafetyGuard.ScreenInputAsync\n    → LocalPromptInjectionDetector (10 regex)\n    → Azure AI Content Safety (if configured)\n    → Block if injection/unsafe content" as G2
    card "3. PiiFilter.MaskPii(message.Text)\n    → SSN, CC, Email, Phone → masked" as G3
    card "4. [POST] TryConsumeTokens\n    → response.Usage.TotalTokenCount\n    → Interlocked.Add (thread-safe)" as G4
}

rectangle ".UseFunctionInvocation()" as FUNC #E8FDE8 {
    card "Auto-executes MCP tool calls\nMcpClientTool.InvokeAsync()\n→ HTTP/SSE to MCP Server\n→ Returns tool result to LLM" as F1
}

rectangle ".UseOpenTelemetry()" as OTEL #FFF3E0 {
    card "Source: \"Cascade.CTL.Agent\"\nEnableSensitiveData = false\nTraces: spans per LLM call\nMetrics: token usage, latency" as O1
}

rectangle "OpenAIClient\n.GetChatClient(\"gpt-4o\")\n.AsIChatClient()" as AOAI #E0E8FF {
    card "Auth: DefaultAzureCredential\n   or ApiKeyCredential\nEndpoint: Private Endpoint\nStructured JSON outputs" as A1
}

APP -down-> GUARD : "GetResponseAsync(messages, options)"
GUARD -down-> FUNC : "base.GetResponseAsync()"
FUNC -down-> OTEL
OTEL -down-> AOAI : "HTTPS → Azure AI Foundry"

note right of GUARD
  **Screening Order:**
  PRE-CALL: Budget → Safety → PII Mask
  POST-CALL: Token consumption tracking
  
  Blocked? → Return error ChatResponse
  (never reaches Azure OpenAI)
end note

note right of FUNC
  **FunctionInvocation Middleware:**
  When LLM returns tool_call:
  1. Resolve AITool by name
  2. AITool.InvokeAsync(args)
  3. McpClientTool → HTTP to MCP Server
  4. toolResult → back to LLM
  5. Loop until LLM returns text
end note

@enduml
```

---

## 7. Guardrails Screening Pipeline

Detailed breakdown of every guard, pattern, and decision path in `GuardrailsMiddleware`.

```plantuml
@startuml Guardrails_Pipeline
!theme cerulean-outline

title Guardrails Screening Pipeline — GuardrailsMiddleware.GetResponseAsync()

start

:Receive GetResponseAsync(\n  messages, options, cancellationToken);

partition "**PRE-CALL SCREENING**" #FFE0E0 {

    :TokenBudgetGuard.IsWithinBudget?;
    if (Budget exceeded?\n(CurrentUsage ≥ MaxTokenBudget)) then (yes)
        #Red:BLOCK\nReturn error:\n"Token budget exceeded";
        stop
    else (no)
    endif

    :Iterate user/tool messages;

    repeat
        :ContentSafetyGuard.ScreenInputAsync(text);

        partition "Injection Detection" {
            :LocalPromptInjectionDetector.Detect(text);
            note right
              10 Regex Patterns:
              ── ignore.*(previous|above)
              ── disregard.*(instructions|rules)
              ── override.*system
              ── forget.*(everything|above)
              ── new\s+instructions
              ── \[SYSTEM\]
              ── <\s*(system|prompt|instruction)
              ── you\s+are\s+now
              ── act\s+as\s+if
              ── pretend.*(you|that)
              
              Timeout: 250ms per pattern
            end note
            if (Pattern matched?) then (yes)
                #Red:BLOCK\nReturn error:\n"Content safety violation";
                stop
            else (no)
            endif
        }

        partition "Azure Content Safety\n(if configured)" {
            :ContentSafetyClient\n.AnalyzeTextAsync(text);
            if (Severity ≥ 4?) then (yes)
                #Red:BLOCK;
                stop
            elseif (Severity ≥ 2?) then (flag)
                :FLAG — log warning\n(allowed to proceed);
            else (clean)
            endif
        }

        partition "PII Masking" {
            :PiiFilter.MaskPii(text);
            note right
              SSN: 123-45-6789 → ***-**-****
              CC:  4111-1111-... → ****-****-****-****
              Email: user@x.com → ***@***.***
              Phone: (555)123-4567 → (***)***-****
            end note
            :Replace message text\nwith masked version;
        }
    repeat while (more messages?)
}

partition "**LLM CALL**" #E8FDE8 {
    :base.GetResponseAsync(\n  maskedMessages, options, ct);
    note right
      → FunctionInvocation middleware
      → OpenTelemetry middleware
      → OpenAIClient
      → GPT-4o
    end note
}

partition "**POST-CALL TRACKING**" #FFF3E0 {
    :response.Usage?.TotalTokenCount;
    if (TotalTokenCount > 0?) then (yes)
        :TokenBudgetGuard.TryConsumeTokens(\n  (int)Math.Min(totalTokens, int.MaxValue));
        note right
          Thread-safe:
          Interlocked.Add(ref _currentTokens, tokens)
        end note
    else (no)
    endif
}

:Return ChatResponse;

stop

@enduml
```

---

## 8. Domain Model Class Diagram

Complete domain model exactly as defined in `Cascade.CTL.Agent.Domain` — enums, records, interfaces.

```plantuml
@startuml Domain_Model
!theme cerulean-outline

skinparam classAttributeIconSize 0
skinparam classFontSize 11
hide empty methods

title Cascade.CTL.Agent.Domain — Complete Model

package "Enums" #EEEEFF {
    enum CTLVerdict {
        Clear
        ClearWithConditions
        NotClear
        NeedsHumanReview
    }

    enum AssetType {
        Foreclosure
        NonForeclosure
        REO
        ShortSale
    }

    enum OccupancyStatus {
        Vacant
        Occupied
        Unknown
    }

    enum SellerTier {
        Tier1
        Tier2
        Tier3
    }

    enum VerificationDomain {
        Legal
        Valuation
        Occupancy
    }
}

package "Models" #EEFFEE {
    class Asset <<record>> {
        +AssetId : string
        +AssetType : AssetType
        +StateCode : string
        +County : string
        +SellerTier : SellerTier
        +OccupancyStatus : OccupancyStatus
        +ParcelId : string
        +PropertyAddress : string
        +SellerName? : string
        +IngestionDate? : DateTime
    }

    class CTLEvaluationRequest <<record>> {
        +AssetId : string {required}
        +WorkflowInstanceId? : string
        +RequestTimestamp : DateTime
        +RequestedBy? : string
    }

    class CTLEvaluationResult <<record>> {
        +Verdict : CTLVerdictDto
        +EvaluationDuration : TimeSpan
        +TotalTokensUsed : int
        +ToolInvocationCount : int
    }

    class CTLVerdictDto <<record>> {
        +Verdict : CTLVerdict
        +ConfidenceScore : double [0.0-1.0]
        +Conditions : string[]
        +EvidenceTrail : string[]
        +ReflectionLog : string
        +AssetId : string
        +Timestamp : DateTime
        +SessionId : string
        +LegalFindings? : LegalFindingsReport
        +ValuationFindings? : ValuationFindingsReport
        +OccupancyFindings? : OccupancyFindingsReport
    }

    class VerificationPlan <<record>> {
        +AssetId : string
        +RequiredDomains : VerificationDomain[]
        +RelevantPolicies : string[]
        +AssetProfileSummary : string
        +PlanRationale : string
    }
}

package "Findings Reports" #FFEEEE {
    class LegalFindingsReport <<record>> {
        +DomainVerdict : string
        +Confidence : double
        +Findings : string[]
        +UnverifiedFields : string[]
        +Summary : string
        +TitleResult? : TitleSearchResult
        +HOAResult? : HOAResult
        +CodeViolationResult? : CodeViolationResult
    }

    class ValuationFindingsReport <<record>> {
        +DomainVerdict : string
        +Confidence : double
        +Findings : string[]
        +UnverifiedFields : string[]
        +Summary : string
        +BPOResult? : BPOResult
        +AVMResult? : AVMResult
    }

    class OccupancyFindingsReport <<record>> {
        +DomainVerdict : string
        +Confidence : double
        +Findings : string[]
        +UnverifiedFields : string[]
        +Summary : string
        +OccupancyResult? : OccupancyStatusResult
    }
}

package "Tool Results" #FFFFEE {
    class TitleSearchResult <<record>> {
        +ParcelId, StateCode : string
        +HasClearTitle : bool
        +OpenLiens : string[]
        +Encumbrances : string[]
        +TitleDefects : string[]
        +HasHOAFlag : bool
        +SearchDate : DateTime
    }

    class HOAResult <<record>> {
        +PropertyAddress : string
        +HasHOA, IsDelinquent : bool
        +DelinquentAmount? : decimal
        +HOAName? : string
        +Status : string
    }

    class CodeViolationResult <<record>> {
        +PropertyAddress, County : string
        +HasOpenViolations : bool
        +Violations : CodeViolation[]
    }

    class BPOResult <<record>> {
        +AssetId : string
        +HasBPO : bool
        +EstimatedValue? : decimal
        +BPODate? : DateTime
        +IsStale : bool
        +DaysSinceBPO? : int
        +QualityRating : string
    }

    class AVMResult <<record>> {
        +PropertyAddress, StateCode : string
        +HasAVM : bool
        +EstimatedValue? : decimal
        +ConfidenceScore? : double
        +VariancePercentage? : double
    }

    class OccupancyStatusResult <<record>> {
        +PropertyAddress : string
        +OccupancyStatus : OccupancyStatus
        +IsVacant : bool
        +HasEvictionInProgress : bool
        +PropertyCondition? : string
        +Notes : string[]
    }

    class RAGQueryResult <<record>> {
        +Query : string
        +Documents : RAGDocument[]
        +TotalMatches : int
    }
}

package "Contracts" #EEEEFF {
    interface IAssetProfileProvider {
        +GetAssetProfileAsync(assetId) : Task<Asset>
    }
    interface ITitleSearchProvider {
        +SearchAsync(parcelId, stateCode) : Task<TitleSearchResult>
    }
    interface IHOAProvider {
        +CheckDelinquencyAsync(address) : Task<HOAResult>
    }
    interface ICodeViolationProvider {
        +LookupAsync(address, county) : Task<CodeViolationResult>
    }
    interface IBPOProvider {
        +RetrieveAsync(assetId) : Task<BPOResult>
    }
    interface IAVMProvider {
        +GetValuationAsync(address, state) : Task<AVMResult>
    }
    interface IOccupancyProvider {
        +GetStatusAsync(address) : Task<OccupancyStatusResult>
    }
    interface IRAGQueryService {
        +QueryAsync(query, state?, county?, assetType?) : Task<RAGQueryResult>
    }
    interface IAuditService {
        +RecordStepAsync(AuditEntry) : Task
    }
}

CTLEvaluationResult *-- CTLVerdictDto
CTLVerdictDto *-- LegalFindingsReport
CTLVerdictDto *-- ValuationFindingsReport
CTLVerdictDto *-- OccupancyFindingsReport
CTLVerdictDto --> CTLVerdict
Asset --> AssetType
Asset --> OccupancyStatus
Asset --> SellerTier
VerificationPlan --> VerificationDomain

@enduml
```

---

## 9. RAG Architecture

Exact implementation from `InMemoryRAGService` — documents, scoring, filtering.

```plantuml
@startuml RAG_Architecture
!theme cerulean-outline

title RAG Architecture — InMemoryRAGService\n(Cascade.CTL.Agent.Infrastructure.RAG)

skinparam rectangle {
    RoundCorner 10
}

rectangle "Agent (via MCP Tool)" as AGENT #E8F4FD {
    card "QueryKnowledgeBase(\n  query: \"Texas foreclosure title requirements\",\n  stateCode: \"TX\",\n  assetType: \"Foreclosure\"\n)" as QUERY
}

rectangle "InMemoryRAGService.QueryAsync()" as RAG #FFF3E0 {

    rectangle "Step 1: Metadata Filtering" as STEP1 {
        card "Filter by State\n(None / \"ALL\" / exact match)" as F1
        card "Filter by County\n(None / \"ALL\" / exact match)" as F2
        card "Filter by AssetType\n(None / \"ALL\" / exact match)" as F3
    }

    rectangle "Step 2: Keyword Scoring" as STEP2 {
        card "Split query into terms\nFor each surviving document:" as S1
        card "Title match: +0.3 per term\nContent occurrence: +0.1 per hit\nCap at 1.0" as S2
    }

    rectangle "Step 3: Threshold & Rank" as STEP3 {
        card "Keep: RelevanceScore > 0.05\nSort: descending by score\nReturn: Top 5" as S3
    }
}

database "6 Built-In Policy Documents" as DOCS #E8FDE8 {
    card "CTL-POLICY-001\nGeneral CTL — All States Baseline\nState=ALL  AssetType=ALL\n9 rules: title, BPO staleness,\noccupancy, HOA, AVM variance,\ntier logic, confidence" as D1

    card "CTL-POLICY-TX-001\nTexas Foreclosure CTL\nState=TX  AssetType=Foreclosure\n9 rules: Prop Code 51.002,\nno redemption, HOA 209,\ntax liens, 60-day BPO" as D2

    card "CTL-POLICY-CA-001\nCalifornia REO Listing\nState=CA  AssetType=REO\n10 rules: Civil Code 2924,\n1-yr redemption, SB-1079,\nLA RSO, NHD, super lien" as D3

    card "CTL-POLICY-HOA-001\nHOA Verification — All States\nState=ALL  AssetType=ALL\n8 rules: delinquency thresholds,\n$5k blocker, $1-5k conditional,\nsuper lien states" as D4

    card "CTL-POLICY-VAL-001\nValuation Staleness & Confidence\nState=ALL  AssetType=ALL\n9 rules: BPO mandatory,\nstaleness thresholds,\nAVM variance by state" as D5

    card "CTL-POLICY-OCC-001\nOccupancy Clearance\nState=ALL  AssetType=ALL\n10 rules: vacant fresh/stale,\neviction complete/in-progress,\nunknown, cash-for-keys" as D6
}

AGENT -down-> RAG : "IRAGQueryService.QueryAsync()"
STEP1 -down-> STEP2
STEP2 -down-> STEP3
RAG -right-> DOCS : "Score against\ncorpus"
STEP3 -down-> AGENT : "RAGQueryResult\n{Documents[], TotalMatches}"

note bottom of RAG
  **Production replacement:**
  Replace InMemoryRAGService with
  Azure AI Search (hybrid BM25 + Vector)
  via IRAGQueryService interface swap.
  No agent code changes required.
end note

@enduml
```

---

## 10. Infrastructure & Deployment Topology

Target-state deployment aligned with the ARB architecture — Azure Container Apps, Private Endpoints, Managed Identity.

```plantuml
@startuml Infrastructure_Topology
!theme cerulean-outline

title Infrastructure & Deployment Topology\n(Azure Container Apps — Target State)

skinparam cloud {
    BackgroundColor #F0F8FF
}
skinparam node {
    BackgroundColor #FFFAEF
}
skinparam database {
    BackgroundColor #F0FFF0
}

cloud "Azure Subscription — Cascade 2.0 VNet" as VNET {

    node "Azure Container Apps Environment" as ACA {
        rectangle "CTL Agent Host\n.NET 8 Worker Service\nKEDA: Service Bus scaler\nScale: 0 → N replicas" as HOST_APP #E8F4FD
        rectangle "CTL MCP Tool Server\nASP.NET Core :5100\nSidecar / separate container" as MCP_APP #FDE8E8
    }

    node "Azure OpenAI" as AOAI {
        rectangle "GPT-4o Deployment\nPTU Reserved\nStructured Outputs\nTemp 0.1" as GPT
    }

    node "Azure AI Content Safety" as AICS {
        rectangle "Prompt Shields\nPII Detection\nTask Adherence" as CS
    }

    node "Azure AI Search\n(Production)" as AIS {
        rectangle "Hybrid: BM25 + Vector\nRRF Reranking\nMetadata Filtering\n~512 token chunks" as SEARCH
    }

    database "Azure Cosmos DB\n(Serverless)" as COSMOS {
        rectangle "agent-sessions\nTTL: 72h\nPartition: /assetId" as SESSIONS
    }

    node "Azure Service Bus" as ASB {
        rectangle "ctleval-requested\ntopic subscription\nDeadLetter: 3 attempts" as TOPIC
    }

    node "Azure API Management" as APIM {
        rectangle "External Tool Gateway\nRate limiting\nAuth relay\nAudit logging" as GW
    }

    node "Azure Application Insights" as APPINS {
        rectangle "OpenTelemetry Traces\nCustom Metrics\nAgent Step Spans\n90-day hot + archive" as TRACES
    }

    node "Azure Key Vault" as KV {
        rectangle "External API keys only\nManaged Identity access" as KEYS
    }
}

cloud "External Providers" as EXT {
    rectangle "Title Data Provider" as EXT_TITLE
    rectangle "HOA Provider" as EXT_HOA
    rectangle "AVM Provider" as EXT_AVM
    rectangle "Field Services" as EXT_FIELD
    rectangle "Municipal API" as EXT_MUNI
}

cloud "Cascade 2.0 Services" as C2 {
    rectangle "AssetService" as C2_ASSET
    rectangle "DocumentService" as C2_DOC
    rectangle "CamundaGatewayService" as C2_CAM
}

TOPIC -right-> HOST_APP : "AMQP\nManaged Identity"
HOST_APP <-down-> MCP_APP : "HTTP/SSE :5100"
HOST_APP -right-> GPT : "HTTPS\nPrivate Endpoint\nManaged Identity"
HOST_APP -right-> CS : "HTTPS\nPrivate Endpoint"
HOST_APP --> TRACES : "OpenTelemetry\nOTLP"
HOST_APP --> COSMOS : "Session state R/W\nManaged Identity"
HOST_APP --> C2_CAM : "POST /workflow/message\nCTLVerdictReceived"
HOST_APP --> C2_DOC : "POST /documents/store\nEvidence Report"

MCP_APP --> C2_ASSET : "GET /assets/{id}"
MCP_APP --> AIS : "Hybrid search\nManaged Identity"
MCP_APP --> APIM : "External tool calls"

APIM --> EXT_TITLE
APIM --> EXT_HOA
APIM --> EXT_AVM
APIM --> EXT_FIELD
APIM --> EXT_MUNI
APIM --> KV : "API key retrieval"

note bottom of ACA
  **Managed Identity (Zero Secrets):**
  Cognitive Services OpenAI User → Azure OpenAI
  Search Index Data Reader → Azure AI Search
  Service Bus Data Receiver → Service Bus
  Cosmos DB Data Contributor → agent-sessions only
  Key Vault Secrets User → external API keys
end note

@enduml
```

---

## 11. Tool Failure Cascade Policy

Decision tree for tool failures — blocking vs. non-blocking, exactly as designed in the architecture.

```plantuml
@startuml Tool_Failure_Policy
!theme cerulean-outline

title Tool Failure Cascade Policy

start

:Tool invocation attempted;

if (Tool type?) then (Blocking)

    partition "**Blocking Tools**\n(AssetProfileTool, BPORetrievalTool)" #FFE0E0 {
        if (Tool succeeds?) then (yes)
            :Continue evaluation;
        else (no — timeout / error)
            #Red:Emit NeedsHumanReview\nimmediately;
            :Record in AuditService\n"BlockingToolFailure";
            :Set confidence = 0.0;
            stop
        endif
    }

else (Non-Blocking)

    partition "**Non-Blocking Tools**\n(TitleSearchTool, HOADelinquencyTool,\nCodeViolationTool, AVMTool,\nOccupancyStatusTool)" #FFF3E0 {
        if (Tool succeeds?) then (yes)
            :Use result in\nFindingsReport;
        else (no — timeout / error)
            :Flag field as\n"unverified" in report;
            :Add to UnverifiedFields[];
            :Log warning in\nAuditService;
        endif
    }

endif

partition "**RAGQueryTool**\n(Special: Retry Policy)" #E8FDE8 {
    if (RAG query succeeds?) then (yes)
        :Use documents for\ngrounded reasoning;
    else (no — attempt 1)
        :Retry (attempt 2);
        if (Retry succeeds?) then (yes)
            :Use documents;
        else (no — attempt 2)
            :Retry (attempt 3);
            if (Retry succeeds?) then (yes)
                :Use documents;
            else (no — all 3 failed)
                #Orange:Flag as unverified;\n(Reflection may → NeedsHumanReview);
            endif
        endif
    endif
}

:Investigation agent produces\nFindingsReport;

partition "**Reflection Assessment**" #E8F4FD {
    if (Too many unverified fields?) then (yes)
        :Confidence penalty\n(≥ −0.15 per unverified domain);
        if (Resulting confidence < 0.75?) then (yes)
            #Orange:→ NeedsHumanReview;
        else (no)
            :→ ClearWithConditions\n(conditions list unverified items);
        endif
    else (no)
        :Proceed with normal\nverdict determination;
    endif
}

stop

@enduml
```

---

## 12. DI Composition Root

Complete dependency injection registration flow in `ServiceRegistration.ConfigureCTLAgent()`.

```plantuml
@startuml DI_Composition
!theme cerulean-outline

title DI Composition Root — ServiceRegistration.ConfigureCTLAgent()

skinparam rectangle {
    RoundCorner 10
}

rectangle "Host.CreateDefaultBuilder(args)" as BUILDER #E8F4FD

rectangle "1. Configuration Binding" as CONFIG #FFFAEF {
    card "appsettings.json (required)" as C1
    card "appsettings.Development.json (optional)" as C2
    card "Environment: CTL_* prefix" as C3
    card "→ Configure<CTLAgentOptions>(\"CTLAgent\")" as C4
    card "→ Configure<ContentSafetyOptions>" as C5
    card "→ Configure<TokenBudgetOptions>" as C6
}

rectangle "2. Infrastructure Registration\nAddCTLInfrastructure()" as INFRA #E8FDE8 {
    card "IAssetProfileProvider → MockAssetProfileProvider" as I1
    card "ITitleSearchProvider → MockTitleSearchProvider" as I2
    card "IHOAProvider → MockHOAProvider" as I3
    card "ICodeViolationProvider → MockCodeViolationProvider" as I4
    card "IBPOProvider → MockBPOProvider" as I5
    card "IAVMProvider → MockAVMProvider" as I6
    card "IOccupancyProvider → MockOccupancyProvider" as I7
    card "IRAGQueryService → InMemoryRAGService (6 docs)" as I8
    card "IAuditService → ConsoleAuditService" as I9
    card "AddCTLTelemetry() → OpenTelemetry traces + metrics" as I10
}

rectangle "3. Guardrails Registration\nAddCTLGuardrails()" as GUARDS #FFE0E0 {
    card "LocalPromptInjectionDetector (Singleton)" as G1
    card "ContentSafetyGuard (Singleton)" as G2
    card "PiiFilter (Singleton)" as G3
    card "InputValidator (Singleton)" as G4
    card "TokenBudgetGuard (Singleton, 50K budget)" as G5
}

rectangle "4. IChatClient Pipeline" as PIPELINE #FFF3E0 {
    card "Base: OpenAIClient(endpoint, credential)\n  .GetChatClient(modelId)\n  .AsIChatClient()" as P1
    card ".UseOpenTelemetry(\"Cascade.CTL.Agent\")" as P2
    card ".UseFunctionInvocation()" as P3
    card ".Build()" as P4
    card "Wrap: new GuardrailsMiddleware(\n  pipeline, contentSafety, tokenBudget,\n  piiFilter, logger)" as P5
    card "Register as IChatClient (Singleton)" as P6
}

rectangle "5. MCP & Orchestrator" as ORCH #E8F4FD {
    card "McpToolProvider(logger, endpoint) — Singleton" as O1
    card "CTLEvaluationOrchestrator(\n  IChatClient, McpToolProvider, IAuditService,\n  TokenBudgetGuard, ILogger) — Singleton" as O2
}

BUILDER -down-> CONFIG
CONFIG -down-> INFRA
INFRA -down-> GUARDS
GUARDS -down-> PIPELINE
PIPELINE -down-> ORCH

P1 -right-> P2
P2 -right-> P3
P3 -right-> P4
P4 -right-> P5

note bottom of ORCH
  **Resolution Path:**
  Host.Program.cs
    → builder.ConfigureCTLAgent()
    → host.Build()
    → GetRequiredService<McpToolProvider>()
    → toolProvider.InitializeAsync()
    → GetRequiredService<CTLEvaluationOrchestrator>()
    → orchestrator.EvaluateAsync(request)
end note

@enduml
```

---

## Rendering Instructions

### Option A — VS Code PlantUML Extension
1. Install "PlantUML" extension by jebbs
2. Copy any `@startuml ... @enduml` block to a `.puml` file
3. `Alt+D` to preview

### Option B — PlantUML Server
1. Visit [https://www.plantuml.com/plantuml/uml](https://www.plantuml.com/plantuml/uml)
2. Paste any diagram block
3. Generate SVG/PNG

### Option C — CLI
```bash
java -jar plantuml.jar diagram.puml -tsvg
```

### Option D — Docker
```bash
docker run -v $(pwd):/data plantuml/plantuml diagram.puml -tsvg
```

---

## Diagram-to-Code Traceability Matrix

| Diagram | Primary Source Files |
|---------|-------------------|
| System Context | Host/Program.cs, McpServer/Program.cs, Application/Orchestration/*.cs |
| Internal Architecture | All .csproj files, ServiceRegistration.cs |
| 4-Phase Sequence | Application/Orchestration/CTLEvaluationOrchestrator.cs |
| Agent Topology | Application/Orchestration/McpToolProvider.cs, Application/Prompts/*.cs |
| MCP Architecture | McpServer/Program.cs, McpServer/Tools/*.cs, Application/Orchestration/McpToolProvider.cs |
| IChatClient Pipeline | Host/ServiceRegistration.cs |
| Guardrails Pipeline | Guardrails/GuardrailsMiddleware.cs, Guardrails/*.cs |
| Domain Model | Domain/Models/*.cs, Domain/Enums/*.cs, Domain/Contracts/*.cs |
| RAG Architecture | Infrastructure/RAG/InMemoryRAGService.cs |
| Infrastructure Topology | Architecture design (target-state Azure deployment) |
| Tool Failure Policy | Architecture design + CTLEvaluationOrchestrator error handling |
| DI Composition | Host/ServiceRegistration.cs, Infrastructure/InfrastructureRegistration.cs, Guardrails/GuardrailsRegistration.cs |

---

*All diagrams are verified against the implemented .NET solution at `Cascade.CTL.AgentSolution/` and the CTL_Architecture_Readout.md. No misalignment with finalized architecture, solution, or use case.*
