# Solution Guide

## Overview

The CTL Agent evaluates real estate assets for **listing readiness** through a multi-agent architecture. An orchestrator plans the evaluation, dispatches parallel investigation agents (Legal, Valuation, Occupancy), reflects on findings, and produces a structured verdict.

## End-to-End Flow

```
CLI (--asset-id) → Host DI Root → Orchestrator
                                      │
                            ┌─────────┼─────────┐
                            ▼         ▼         ▼
                      ┌─────────┐ ┌─────────┐ ┌─────────────┐
                      │  Legal  │ │Valuation│ │  Occupancy  │
                      │  Agent  │ │  Agent  │ │    Agent    │
                      └────┬────┘ └────┬────┘ └──────┬──────┘
                           │           │             │
                           └─────┬─────┘─────────────┘
                                 ▼
                      MCP Server (HTTP/SSE)
                      ┌──────────────────────┐
                      │ 8 Tools:             │
                      │  GetAssetProfile     │
                      │  SearchTitle         │
                      │  CheckHOADelinquency │
                      │  LookupCodeViolations│
                      │  RetrieveBPO         │
                      │  GetAVM              │
                      │  GetOccupancyStatus  │
                      │  QueryKnowledgeBase  │
                      └──────────────────────┘
```

## Four-Phase Evaluation

### Phase 1: Planning
The orchestrator receives the asset ID, calls `GetAssetProfile` and `QueryKnowledgeBase` (RAG) via MCP tools, and produces a structured JSON plan containing:
- `requiredDomains` — which investigation domains (Legal, Valuation, Occupancy) to evaluate
- `relevantPolicies` — state/county-specific policies from the knowledge base
- `assetProfileSummary` — key asset characteristics
- `planRationale` — reasoning for domain selection

The orchestrator then parses this plan via `ParseRequiredDomains()` to determine which agents to dispatch. If plan parsing fails (invalid JSON, missing fields), a **safety fallback** runs all 3 agents.

### Phase 2: Investigation Agent Fan-Out (Plan-Driven)
Only the investigation agents required by the plan run **concurrently** via `Task.WhenAll()`:

- **Legal Agent**: Calls `SearchTitle`, `CheckHOADelinquency`, `LookupCodeViolations`, `QueryKnowledgeBase`. Produces a structured `LegalFindingsReport` with severity assessment (Clear/Warning/Blocker).
- **Valuation Agent**: Calls `RetrieveBPO`, `GetAVM`, `QueryKnowledgeBase`. Validates valuation freshness, confidence, and variance.
- **Occupancy Agent**: Calls `GetOccupancyStatus`, `QueryKnowledgeBase`. Determines occupancy category, eviction requirements, and timeline impact.

Each investigation agent receives domain-specific system prompts with tool selection logic and severity rubrics. Skipped domains are marked as "Domain not evaluated — not required by verification plan" in findings.

### Phase 3: Reflection
The orchestrator synthesizes all investigation agent findings together with the **raw asset profile metadata** (asset type, state, occupancy status, seller tier, etc.) fetched via `IAssetProfileProvider`. This grounds the reflection in actual asset characteristics rather than relying solely on LLM-summarized data from the planning phase. The reflection detects contradictions (e.g., Legal says Clear but Occupancy says Blocker), applies confidence penalty rules, and determines the final verdict. The reflection prompt enforces:

- Confidence ≥ 0.85 required for `Clear`
- Any `Blocker` forces `NotClear`
- Contradictions trigger `NeedsHumanReview` and reduce confidence by ≥ 0.15
- Missing tool calls trigger evidence sufficiency concerns

### Phase 4: Verdict Parsing
The orchestrator extracts the structured `CTLVerdictDto` (JSON) from the reflection response, including verdict enum, confidence score, conditions array, evidence trail, and reflection log.

## Components

### Domain Layer (`Cascade.CTL.Agent.Domain`)
Pure domain types with zero external dependencies:
- **Enums**: `AssetType`, `CTLVerdict`, `OccupancyStatus`, `SellerTier`, `VerificationDomain`
- **Models**: `Asset`, `CTLVerdictDto`, `CTLEvaluationRequest/Result`, `VerificationPlan`, findings reports for each domain, tool result DTOs, RAG query/document types
- **Contracts**: 8 provider interfaces (`ITitleSearchProvider`, `IHOAProvider`, etc.), `IAuditService`

### Infrastructure Layer (`Cascade.CTL.Agent.Infrastructure`)
Configurable implementations:
- **Mock Providers**: 7 providers with realistic multi-scenario data covering Texas (clean path), California (contradictions/issues), and Florida (unknowns). Each provider returns different results based on asset ID patterns.
- **HTTP Providers**: `HttpAssetProfileProvider` calls a real Asset Domain microservice over HTTP using `IHttpClientFactory` typed-client pattern with `AddStandardResilienceHandler()` (retry, circuit breaker, timeout). Auth is handled by `AzureIdentityAuthHandler` (a `DelegatingHandler` that acquires OAuth 2.0 tokens via `DefaultAzureCredential`); falls back to static API key for dev/test. Configured via `AssetDomainServiceOptions` — automatically activated when `AssetDomainService:BaseUrl` is set.
- **MCP Providers**: `McpTitleSearchProvider` connects to external vendor MCP servers for real title search data. Uses `HttpClientTransport` with `StreamableHttp` mode, Bearer token auth via `AdditionalHeaders`, lazy thread-safe init with `SemaphoreSlim`, per-call timeout, and tool verification. Configured via `McpProviderOptions`.
- **InfrastructureRegistration**: Supports three modes: `useMockProviders` (all mocks), `useMcpProviders` (real MCP providers + mock fallbacks for unboarded vendors), or custom. `IAssetProfileProvider` is wired independently — HTTP provider takes precedence when `AssetDomainService:BaseUrl` is configured, otherwise falls back to mock. Validates that `IConfiguration` is provided when MCP mode is enabled.
- **InMemoryRAGService**: Local RAG with 6 built-in policy documents (General CTL, TX Foreclosure, CA REO, HOA, Valuation, Occupancy). Uses keyword-based scoring with metadata filtering by state/domain.
- **ConsoleAuditService**: Structured logging audit with JSON serialization.
- **TelemetryConfiguration**: OpenTelemetry traces + metrics with console exporter.

### Guardrails Layer (`Cascade.CTL.Agent.Guardrails`)
Enterprise safety pipeline with **3-tier prompt injection defense**:
- **Tier 1 — LocalPromptInjectionDetector**: 10 regex patterns for common injection attacks (role hijack, instruction override, delimiter escape, etc.) with regex timeout protection. Zero-latency first line of defense.
- **Tier 2 — Azure Prompt Shields** (via `ContentSafetyGuard`): ML-based prompt injection detection using Azure AI Content Safety REST API (`POST /contentsafety/text:shieldPrompt?api-version=2024-09-01`). Detects **direct attacks** in user prompts (via `ScreenInputAsync`) and **indirect attacks** in tool outputs (via `ScreenToolResultAsync`, which passes tool text as `documents` parameter). Auth via `DefaultAzureCredential` with `cognitiveservices.azure.com` scope. Configurable via `ContentSafety:PromptShieldsEnabled` (default: `true`). Falls back gracefully if Azure is unavailable.
- **Tier 3 — System Prompt Hardening**: All 5 system prompts (Planning, Reflection, Legal, Valuation, Occupancy) include `## Security Constraints` sections enforcing: ADVISORY ONLY role, no deviation from assigned role, no system prompt disclosure, no arbitrary code execution, and instruction to ignore suspicious tool output.
- **ContentSafetyGuard**: Azure AI Content Safety wrapper with **circuit breaker** (5 consecutive failures → 60s open) and **per-call timeout** (10s). Integrates both Prompt Shields (Tier 2) and content moderation. Falls back to local detector if Azure is unavailable or circuit is open.
- **PiiFilter**: Masks SSN (123-45-6789), credit card, email, phone patterns. Wired into `GuardrailsMiddleware` for both **input masking** (before LLM) and **output masking** (LLM responses).
- **CTLRequestValidator**: Validates asset IDs, required fields, and request structure at the **orchestrator entry point** (system boundary validation).
- **TokenBudgetGuard**: Thread-safe token budget enforcement with configurable maximum (default 50,000). Per-evaluation session scoping via `AsyncLocal<string>` + `ConcurrentDictionary<string, int>` for concurrent evaluation isolation.
- **GuardrailsMiddleware**: `DelegatingChatClient` middleware that screens all inputs/outputs through the guardrails pipeline.

### MCP Server (`Cascade.CTL.Agent.McpServer`)
ASP.NET Core minimal API hosting 8 MCP tools:
- Uses `[McpServerToolType]` and `[McpServerTool]` attributes from MCP SDK 1.2.0
- Configured with `AddMcpServer().WithHttpTransport().WithToolsFromAssembly()`
- Runs on `http://localhost:5100` with SSE transport
- **Bearer token authentication**: Middleware checks `McpServer:ApiKey` config against `Authorization: Bearer <token>` header on all MCP endpoints (`/mcp`, `/sse`, `/`). Auth is skipped if no API key is configured (dev/test mode).
- **Input max-length validation**: All tool parameters enforce max-length (parcelId/assetId ≤ 50, propertyAddress ≤ 500, county ≤ 100, query ≤ 2000) and return structured error JSON on violation.
- **Error handling**: All provider calls are wrapped in try/catch, returning structured error JSON with a `transient` flag for agent reasoning.

### Application Layer (`Cascade.CTL.Agent.Application`)
Core agent logic:
- **OrchestratorPrompts**: Planning and Reflection system prompts with dynamic context injection.
- **InvestigationAgentPrompts**: Domain-specific prompts for the investigation agents (Legal, Valuation, and Occupancy).
- **IMcpToolProvider / McpToolProvider**: MCP client supporting **multiple server endpoints** — each logical server (Legal, Valuation, Occupancy, AssetProfile, KnowledgeBase) can map to a separate vendor MCP endpoint. Deduplicates connections when endpoints overlap (development mode). Uses `HttpClientTransport` with SSE mode. Provides filtered tool sets per agent role. `IMcpToolProvider` interface extracted from the sealed class for testability.
- **CTLEvaluationOrchestrator**: 4-phase evaluation with **plan-driven agent routing** (`ParseRequiredDomains`), **real tool call counting** (`CountActualToolCalls` via `FunctionCallContent`), **agent retry with exponential backoff**, **per-phase timeouts**, and a **post-Reflection quality gate** (`VerdictGroundednessEvaluator`). Implements `ICTLEvaluationOrchestrator`.
- **VerdictGroundednessEvaluator**: Production LLM-as-judge that scores whether the orchestrator's verdict is grounded in investigation findings (1-5 scale). Runs after the Reflection phase; verdicts below the threshold (`QualityGate:MinGroundednessScore`, default 3) are auto-escalated to `NeedsHumanReview`. Fail-open design — LLM failures default to pass.
- **CTLWorkflowOrchestrator**: Alternative workflow-based orchestrator using Microsoft Agent Framework Workflows (`Microsoft.Agents.AI.Workflows` v1.1.0). Uses typed `Executor` subclasses (`PlanningExecutor`, `InvestigationPhaseExecutor`, `ReflectionExecutor`) wired into a single connected graph via `AddEdge`, executed with one `InProcessExecution.RunAsync()` call. Also implements `ICTLEvaluationOrchestrator`.
- **ICTLEvaluationOrchestrator**: Shared interface enabling runtime flip between imperative and workflow orchestrators via `CTLAgent:UseWorkflowOrchestrator` config (default: `false`).
- **ResilienceOptions**: Centralized configuration for all retry, timeout, and circuit breaker parameters.
- **CTLRequestValidator**: Validates evaluation requests at system boundary (integrated into orchestrator constructor).

### Host (`Cascade.CTL.Agent.Host`)
DI composition root:
- **ServiceRegistration**: Full dependency injection setup including Azure AI Foundry `IChatClient` with middleware pipeline (`OpenTelemetry → FunctionInvocation → GuardrailsMiddleware`). Registers `IMcpToolProvider` with multi-endpoint support (`McpServers.Servers` preferred, `McpServer.Endpoint` fallback) and `ResilienceOptions` for retry/timeout. Binds `ResilienceOptions` from `Resilience` config section. Registers both `CTLEvaluationOrchestrator` and `CTLWorkflowOrchestrator`, resolving `ICTLEvaluationOrchestrator` based on `UseWorkflowOrchestrator` config flag.
- **Program.cs**: Console CLI entry point with `--asset-id` argument parsing, MCP init with retry (removed hard-coded `Task.Delay(2000)`), formatted colored output, and setup guidance on error.

## Azure Setup Guide

### Automated Provisioning

The provisioning script creates an Azure AI Foundry Hub + Project, deploys a serverless model endpoint, optionally provisions Content Safety, assigns RBAC roles, and updates config — all with graceful failure handling:

```powershell
# Provision everything with defaults (gpt-4o via AI Foundry)
.\scripts\Provision-AzureServices.ps1 -UpdateConfig

# Custom resource names and region
.\scripts\Provision-AzureServices.ps1 `
  -ResourceGroupName my-ctlagent-rg `
  -Location westus2 `
  -AIHubName my-hub `
  -AIProjectName my-project `
  -UpdateConfig

# Use an open-source model (no OpenAI approval needed)
.\scripts\Provision-AzureServices.ps1 `
  -ModelName "Phi-4" `
  -ModelPublisher "azureml" `
  -ModelVersion "4" `
  -UpdateConfig

# Skip optional Content Safety (Tier 2 guardrails)
.\scripts\Provision-AzureServices.ps1 -SkipContentSafety -UpdateConfig

# Skip RBAC (if using service principals or no AAD permissions)
.\scripts\Provision-AzureServices.ps1 -SkipRoleAssignment -UpdateConfig
```

**What it provisions** (in order, with dependency tracking):

| Step | Service | Required | What Happens on Failure |
|------|---------|----------|------------------------|
| 1 | Resource Group | Yes | Script exits — all services depend on this |
| 2 | AI Foundry Hub | Yes | Logged, project + model skipped |
| 3 | AI Foundry Project | Yes | Logged, model deployment skipped |
| 4 | Model Deployment (serverless) | Yes | Logged with fix instructions (suggests open-source alternatives) |
| 5 | Azure AI Content Safety | Optional | Logged, RBAC for it skipped |
| 6 | Azure AI Language (PII) | Optional | Logged, PII stays regex-only (Tier 1) |
| 7 | RBAC Role Assignments | Yes | Logged with required role info |
| 8 | Config Update | Optional | Prints endpoints for manual config |

The script is **idempotent** — it detects existing resources and skips re-creation. The summary report shows `[OK]`, `[FAIL]`, or `[SKIP]` for each service with actionable fix instructions.

### Why Azure AI Foundry Instead of Azure OpenAI

| Aspect | Azure AI Foundry | Standalone Azure OpenAI |
|--------|-----------------|------------------------|
| **Model catalog** | OpenAI + open-source (Phi, Llama, Mistral) | OpenAI only |
| **Trial subscription** | Hub/Project creation works; open-source models need no approval | Requires separate OpenAI access application |
| **Provisioning** | `az ml workspace create` (Hub + Project) | `az cognitiveservices account create --kind OpenAI` |
| **Endpoint pattern** | `https://{model}-{id}.{region}.models.ai.azure.com/` | `https://{name}.openai.azure.com/` |
| **Auth** | Endpoint API key (auto-provisioned) or `DefaultAzureCredential` | `DefaultAzureCredential` or resource-level API key |

> **Note**: Azure OpenAI models (gpt-4o) deployed via AI Foundry still require Microsoft approval at [aka.ms/oai/access](https://aka.ms/oai/access). Open-source models (Phi-4, Llama, Mistral) do not.

### Architecture Note: Content Safety vs. Prompt Shields

**Single Azure resource**, two API endpoints. You provision **one** `ContentSafety` kind resource:

| Feature | API Path | Client in Code | Why |
|---------|----------|----------------|-----|
| Content Moderation | `/contentsafety/text:analyze` | `ContentSafetyClient` (SDK) | SDK v1.0.0 supports this |
| Prompt Shields | `/contentsafety/text:shieldPrompt` | `HttpClient` (REST) | SDK v1.0.0 lacks `ShieldPromptAsync` — REST workaround |

Both authenticate with the same `DefaultAzureCredential` against the same endpoint.

### Optional Services (Production)

| Service | Purpose | Replaces |
|---------|---------|----------|
| **Azure AI Search** | Vector/hybrid search for RAG | `InMemoryRAGService` |
| **Azure Cosmos DB** | Audit trail persistence | `ConsoleAuditService` |
| **Azure Service Bus** | Event-driven trigger (`CTLEvaluationRequestedEvent`) | CLI `--asset-id` |
| **Application Insights** | Distributed tracing | Console OpenTelemetry exporter |

### Authentication Options

1. **Endpoint API Key** (default for AI Foundry serverless): The script auto-retrieves and sets the key in config
2. **DefaultAzureCredential**: `az login` — set `UseAzureIdentity: true`, leave `ApiKey` empty
2. **API Key** (dev shortcut): Set `ApiKey` in config — bypasses identity-based auth

## Running Locally

```bash
# Terminal 1: Start MCP Server
dotnet run --project src/Cascade.CTL.Agent.McpServer

# Terminal 2: Run evaluation
dotnet run --project src/Cascade.CTL.Agent.Host -- --asset-id ASSET-TX-001

# Run all tests
dotnet test

# Run evals (requires Azure AI Foundry)
dotnet run --project tests/Cascade.CTL.Agent.Evals
```

## Sample Output

```
╔══════════════════════════════════════════════════════════════╗
║                    CTL EVALUATION RESULT                    ║
╚══════════════════════════════════════════════════════════════╝

  VERDICT: Clear
  CONFIDENCE: 0.92
  DURATION: 4.2s
  TOKENS USED: 8432

  EVIDENCE TRAIL:
    ✓ Title search: Clear, no liens, no HOA delinquency
    ✓ BPO current (15 days old), high quality, 95% confidence
    ✓ Property vacant and secured, no occupancy barriers
    ✓ No code violations found
    ✓ All regulatory requirements met per TX foreclosure policy
```

## Reflection Quality Evaluation (Microsoft.Extensions.AI.Evaluation)

The eval suite integrates `Microsoft.Extensions.AI.Evaluation.Quality` evaluators to score the reflection phase output:

| Evaluator | What It Measures | Why It Matters |
|-----------|-----------------|----------------|
| `GroundednessEvaluator` | Is the verdict grounded in investigation findings? | Prevents hallucinated verdicts not supported by evidence |
| `RelevanceEvaluator` | Is the verdict relevant to the asset listing question? | Detects off-topic or tangential responses |

Both produce `NumericMetric` scores (1-5) with `EvaluationRating` (Unacceptable/Poor/Average/Good/Exceptional). The evaluators use an LLM-as-judge approach via `ChatConfiguration` — they send the reflection input/output to a separate LLM call that scores quality.

**Integration point**: `ReflectionQualityEvaluator` in `tests/Cascade.CTL.Agent.Evals/` runs after each eval case, scoring the verdict quality. Results are non-blocking (failures log warnings, don't fail the eval suite).

**Note**: The reflection phase itself remains LLM-driven (Phase 3 of orchestration). The evaluators score its output *after the fact* — they are an evaluation/observability tool, not a runtime control.

## Resilience Patterns

The solution implements distributed resilience following industry best practices for fault tolerance in multi-agent systems.

### Orchestrator Agent Retry

When an investigation agent (Legal, Valuation, or Occupancy) fails due to a **transient** fault (HTTP 429/5xx, timeout, socket error), the orchestrator:

1. **Classifies** the exception as transient or non-transient via `IsTransient()`
2. **Retries** with exponential backoff (200ms, 400ms, 800ms...) up to `AgentMaxRetryAttempts` (default: 2)
3. **Audits** each retry attempt (`AgentRetry`) and exhaustion (`AgentExhaustedRetries`)
4. **Degrades gracefully** — if all retries fail, returns `NeedsHumanReview` with `confidence: 0.0`
5. **Non-transient failures** (HTTP 400, ArgumentException) skip retry entirely

### MCP Tool Provider Init

The MCP client connection retries with exponential backoff (2s, 4s, 8s) and per-attempt timeout:
- `McpInitTimeoutSeconds`: 30 seconds per attempt
- `McpInitMaxRetryAttempts`: 3 retries (4 total attempts)
- Classifies `HttpRequestException`, `IOException`, `SocketException` as transient

### Content Safety Circuit Breaker

Azure Content Safety API uses a circuit breaker pattern:
- **CLOSED**: Normal operation, tracks consecutive failures
- **OPEN**: After 5 consecutive failures, fast-fails for 60 seconds (falls back to local prompt injection detector)
- **HALF-OPEN**: After duration, allows one probe request to test recovery
- Per-call timeout: 10 seconds (configurable)

### MCP Server Tool Error Handling

All MCP Server tool methods catch provider exceptions and return structured error JSON instead of throwing:
```json
{"error": "Title search failed", "transient": true, "detail": "HttpRequestException"}
```
This allows the LLM agent to reason about the error and decide whether to retry the tool call.

### Per-Phase Timeouts

Every orchestrator phase (Planning, Investigation, Reflection) has a configurable timeout (`OrchestratorPhaseTimeoutSeconds`, default: 90s) enforced via `CancellationTokenSource.CreateLinkedTokenSource`.

### Configuration

All resilience parameters are in `config/appsettings.json` under the `Resilience` section. See `ResilienceOptions.cs` for defaults.
