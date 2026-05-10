# Cascade 2.0 — CTL Agent Solution

**Asset Clear-To-List (CTL) Determination Agent** — A multi-agent AI system for automated real estate asset listing readiness assessment.

Built on **Microsoft Agent Framework SDK**, **MCP (Model Context Protocol)**, and **Azure AI Foundry**.

## Quick Start

### Prerequisites

- [.NET 8 SDK](https://dot.net/download)
- Azure AI Foundry project with a model deployment (e.g., `gpt-4o`)
- Azure CLI (`az login`) or API key for authentication

### 1. Configure Azure AI Foundry

Edit `config/appsettings.Development.json`:

```json
{
  "CTLAgent": {
    "AzureAIFoundry": {
      "Endpoint": "https://YOUR-PROJECT.YOUR-REGION.models.ai.azure.com/",
      "ModelId": "gpt-4o",
      "ApiKey": ""
    }
  }
}
```

> Leave `ApiKey` empty to use `DefaultAzureCredential` (recommended). Run `az login` first.

### 2. Build

```bash
dotnet build Cascade.CTL.AgentSolution.sln
```

### 3. Start MCP Server

```bash
dotnet run --project src/Cascade.CTL.Agent.McpServer
```

The MCP server starts on `http://localhost:5100`.

### 4. Run CTL Evaluation

In a second terminal:

```bash
dotnet run --project src/Cascade.CTL.Agent.Host -- --asset-id ASSET-TX-001
```

Available test assets: `ASSET-TX-001` (TX Foreclosure), `ASSET-CA-002` (CA REO), `ASSET-FL-003` (FL Non-Foreclosure).

### 4a. (Optional) Run the Asset Domain Service in Docker

To exercise the full MCP-over-REST path with a real container instead of the in-memory mock:

```powershell
# 1. Build + start the container (exposes http://localhost:5100)
$env:ASSETDOMAIN_API_KEY = "dev-local-asset-domain-key-CHANGE-ME"
docker compose up --build -d

# 2. Smoke-test
curl http://localhost:5100/health
curl -H "X-Api-Key: $env:ASSETDOMAIN_API_KEY" http://localhost:5100/api/assets/ASSET-TX-001

# 3. Point the agent at it by setting AssetDomainService:BaseUrl in appsettings.Development.json
#    or via environment: CTLAgent__AssetDomainService__BaseUrl=http://localhost:5100
```

Requests without a valid `X-Api-Key` header are rejected with `401`. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#asset-domain-service--self-hosted-mcp-backing-rest-api-docker) for the design rationale.

### 5. Run Tests

```bash
dotnet test
```

### 6. Run Evals

```bash
dotnet run --project tests/Cascade.CTL.Agent.Evals
```

### 7. (Optional) Enable Azure AI Search RAG backend

By default the agent uses `InMemoryRAGService` (keyword scoring over `config/rag-knowledge/*.json`). To switch to a production Azure AI Search backed retriever:

1. Provision Azure resources (the script creates Search + Azure OpenAI alongside the rest):

    ```powershell
    ./scripts/Provision-AzureServices.ps1
    ```

2. Chunk, embed, and upload the policy JSONs to the index:

    ```bash
    dotnet run --project src/Cascade.CTL.RAG.Indexer -- \
        --knowledge-path ./config/rag-knowledge \
        --recreate-index
    ```

3. Flip the feature flag in `appsettings.json` (the provisioning script writes it automatically):

    ```json
    "CTLAgent": {
      "RAG": {
        "AzureSearch": {
          "Enabled": true,
          "Endpoint": "https://<your-search>.search.windows.net",
          "IndexName": "ctl-policy-knowledge",
          "AzureOpenAIEndpoint": "https://<your-openai>.openai.azure.com",
          "EmbeddingDeployment": "text-embedding-3-small",
          "EmbeddingDimensions": 1536,
          "UseAzureIdentity": true,
          "TopK": 5
        }
      }
    }
    ```

4. Restart the host. `InfrastructureRegistration.CreateRAGService` reads the flag and wires `AzureSearchRAGService`. If initialization fails, it logs a warning and falls back to `InMemoryRAGService` so the solution keeps running.

## Solution Structure

| Project | Description |
|---------|-------------|
| `Cascade.CTL.Agent.Domain` | Enums, models, contracts (zero dependencies) |
| `Cascade.CTL.Agent.Infrastructure` | Mock providers, RAG service, audit, telemetry |
| `Cascade.CTL.Agent.Guardrails` | Prompt injection detection, PII filtering, content safety, token budget |
| `Cascade.CTL.Agent.McpServer` | ASP.NET Core MCP server exposing 8 tools (Bearer token auth) |
| `Cascade.CTL.Agent.Application` | Orchestrator with plan-driven routing, MCP client, resilience |
| `Cascade.CTL.Agent.Host` | Console CLI entry point with DI composition root |
| `Cascade.CTL.Agent.Tests` | 192 unit tests (xUnit + NSubstitute + FluentAssertions) |
| `Cascade.CTL.Agent.Evals` | 2 end-to-end evaluation cases |
| `Cascade.CTL.RAG.Indexer` | Console tool that chunks `config/rag-knowledge/*.json`, embeds with Azure OpenAI, and uploads to Azure AI Search |

## Documentation

- [Solution Guide](docs/SOLUTION_GUIDE.md) — End-to-end walkthrough
- [Architecture](docs/ARCHITECTURE.md) — Design patterns and decisions
- [AI Context](docs/AI_CONTEXT.md) — Complete context artifact for AI-assisted continuation

## Key Technologies

| Component | Package | Version |
|-----------|---------|---------|
| Agent Framework | Microsoft.Agents.AI | 1.1.0 |
| AI Abstractions | Microsoft.Extensions.AI | 10.4.1 |
| Agent Workflows | Microsoft.Agents.AI.Workflows | 1.1.0 |
| AI Evaluation | Microsoft.Extensions.AI.Evaluation.Quality | 10.4.0 |
| MCP SDK | ModelContextProtocol | 1.2.0 |
| Azure AI Foundry | OpenAI | 2.9.1 |
| Content Safety | Azure.AI.ContentSafety | 1.0.0 |
| Observability | OpenTelemetry | 1.15.2 |
| Resilience | Microsoft.Extensions.Resilience | 9.3.0 |

## Resilience

Enterprise-grade distributed resilience is implemented across all layers:

- **Orchestrator**: Agent retry with exponential backoff (transient 429/5xx/timeout), per-phase timeouts, audited retry events
- **MCP Tool Provider**: Init retry with exponential backoff + per-attempt timeout
- **Content Safety**: Circuit breaker (5 failures → 60s open → half-open probe) + per-call timeout
- **MCP Server Tools**: Try/catch on all provider calls with structured error JSON (includes `transient` flag for agent reasoning)
- **Configuration**: All parameters in `config/appsettings.json` under `Resilience` section

See [ARCHITECTURE.md](docs/ARCHITECTURE.md#resilience--fault-handling) for full details.

## Plan-Driven Agent Routing

The orchestrator's planning phase produces a structured JSON plan that identifies which verification domains (Legal, Valuation, Occupancy) are required. `ParseRequiredDomains()` extracts the `requiredDomains` array and **only dispatches the agents the plan identifies**. If plan parsing fails, a safety fallback runs all 3 agents. This avoids unnecessary LLM calls and reduces cost/latency.

## MCP Authentication & Provider Pattern

- **MCP Server (inbound)**: Bearer token middleware validates `Authorization` header against `McpServer:ApiKey` config
- **MCP Client (outbound)**: `McpToolProvider` and `McpTitleSearchProvider` inject Bearer tokens via `AdditionalHeaders`
- **MCP Provider Pattern**: `McpTitleSearchProvider` replaces direct REST/SOAP API calls with MCP transport — connects to vendor MCP servers with auth, timeout, and lazy init. Configured via `McpProviderOptions`.

## Enterprise Hardening

- **CTLRequestValidator**: System-boundary validation on every orchestrator entry
- **PII masking**: Wired into `GuardrailsChatClient` for both input and output paths
- **Input max-length**: All MCP tool parameters enforce character limits and return structured error JSON
- **Exception message leak prevention**: Agent errors return generic degraded JSON, never stack traces
- **IMcpToolProvider interface**: Extracted from sealed class for unit test mockability

## Workflow Orchestrator (Microsoft Agent Framework Workflows)

An alternative workflow-based orchestrator (`CTLWorkflowOrchestrator`) implemented using [Microsoft Agent Framework Workflows](https://learn.microsoft.com/en-us/agent-framework/workflows/). Uses typed `Executor` classes (`PlanningExecutor`, `InvestigationPhaseExecutor`, `ReflectionExecutor`) connected via `AddEdge` into a single workflow graph, executed with one `InProcessExecution.RunAsync()` call.

### Runtime Flip

Both orchestrators implement `ICTLEvaluationOrchestrator`. Switch at runtime via config:

**Option 1 — appsettings.json:**
```json
{
  "CTLAgent": {
    "UseWorkflowOrchestrator": true
  }
}
```

**Option 2 — Environment variable:**
```bash
set CTL_CTLAgent__UseWorkflowOrchestrator=true
```

Default is `false` (imperative orchestrator).

## License

Proprietary — Cascade 2.0 Platform
