# Azure AI Foundry Integration — Current State, Best Practices, Improvement Opportunities

MS Foundry Documentation: https://learn.microsoft.com/en-us/azure/foundry/concepts/architecture

**Scope:** how the CTL Agent solution integrates with Azure AI Foundry today, what we already do well, what we should change, where Managed Identity can replace remaining secrets, which additional Foundry resources are worth leveraging, and how to materially improve LLM/agent observability.

**Audience:** engineers and reviewers working on this repo. Sections are technical, not promotional.

---

## 1. What "Azure AI Foundry" Means in This Repo

We use the Foundry surface area in five distinct ways. They are independent — each can be replaced without breaking the others.

| # | Concern | Foundry resource used | Where it's wired |
|---|---------|------------------------|------------------|
| 1 | Primary LLM (Plan/Investigate/Reflect) | Azure OpenAI deployment on a Foundry project (or a serverless Model Inference endpoint) | [ServiceRegistration.cs](src/Cascade.CTL.Agent.Api/ServiceRegistration.cs#L113-L176), [Host/ServiceRegistration.cs](src/Cascade.CTL.Agent.Host/ServiceRegistration.cs) |
| 2 | Judge LLM (groundedness gate) | Optional second AOAI deployment (`gpt-4o-judge`) | [ServiceRegistration.cs](src/Cascade.CTL.Agent.Api/ServiceRegistration.cs#L194-L217), [VerdictGroundednessEvaluator.cs](src/Cascade.CTL.Agent.Application/Orchestration/VerdictGroundednessEvaluator.cs) |
| 3 | Embeddings + Hybrid+Semantic retrieval | Azure OpenAI `text-embedding-3-small` + Azure AI Search | [AzureSearchClientFactory.cs](src/Cascade.CTL.Agent.Infrastructure/RAG/AzureSearchClientFactory.cs), [AzureSearchRAGOptions.cs](src/Cascade.CTL.Agent.Infrastructure/RAG/AzureSearchRAGOptions.cs) |
| 4 | Content safety / prompt-shielding / PII | Azure AI Content Safety + Azure AI Language | [ContentSafetyGuard.cs](src/Cascade.CTL.Agent.Guardrails/ContentSafetyGuard.cs), [PiiFilter.cs](src/Cascade.CTL.Agent.Guardrails/PiiFilter.cs) |
| 5 | Foundry **Agent Service** as an external surface | A Foundry "agent" that calls our `/evaluate` REST endpoint as a tool | [deploy/Register-FoundryAgent.ps1](deploy/Register-FoundryAgent.ps1), [openapi.json](src/Cascade.CTL.Agent.Api/openapi.json) |

**Important distinction:** our orchestrator (Plan → Investigate → Reflect → Quality Gate) is a Microsoft Agent Framework + MCP composition that runs **inside our process**. The Foundry Agent Service is only used as a *front door* (Option A — register the existing API as a tool); our agent does **not** delegate orchestration to Foundry Agents at runtime. See [§7](#7-leveraging-more-of-foundry).

---

## 2. How the Integration Works Today

### 2.1 Chat client construction (primary + judge)

Both `ServiceRegistration.cs` files (Api host and CLI host) do the same dance — they pick the SDK based on endpoint suffix:

```csharp
// src/Cascade.CTL.Agent.Api/ServiceRegistration.cs
var isAzureOpenAI = endpoint.Contains(".openai.azure.com", ...)
                 || endpoint.Contains(".cognitiveservices.azure.com", ...);

if (isAzureOpenAI) {
    var azureClient = options.UseAzureIdentity
        ? new AzureOpenAIClient(endpoint, new DefaultAzureCredential())
        : new AzureOpenAIClient(endpoint, new ApiKeyCredential(options.ApiKey));
    innerClient = azureClient.GetChatClient(options.ModelId).AsIChatClient();
} else {
    // Foundry serverless Model Inference path — wraps Entra token as a static ApiKeyCredential
    var token = new DefaultAzureCredential().GetToken(...);
    innerClient = new OpenAIClient(new ApiKeyCredential(token.Token), new() { Endpoint = endpoint })
        .GetChatClient(options.ModelId).AsIChatClient();
}

var pipeline = new ChatClientBuilder(innerClient)
    .UseOpenTelemetry(sourceName: "Cascade.CTL.Agent", configure: c => c.EnableSensitiveData = true)
    .UseFunctionInvocation()
    .Build();
return new GuardrailsMiddleware(pipeline, ...);
```

The judge model uses the same code shape with `CTLAgent:JudgeModel:*` and falls back to the primary `IChatClient` when no endpoint is configured.

### 2.2 RAG (Foundry embeddings + AI Search)

`AzureSearchClientFactory.CreateEmbeddingClient` / `CreateSearchClient` build the embedding and search clients using either `DefaultAzureCredential` or per-resource keys. The runtime path is `AzureSearchRAGService` → hybrid vector + BM25 → optional L2 semantic reranker (`ctl-semantic-config`). Indexer is a separate console project ([Cascade.CTL.RAG.Indexer](src/Cascade.CTL.RAG.Indexer)) that chunks `config/rag-knowledge/*.json` (1500 chars, 150 overlap) and uploads with `text-embedding-3-small` (1536 dims).

### 2.3 Foundry Agent registration

[`Register-FoundryAgent.ps1`](deploy/Register-FoundryAgent.ps1) acquires a Foundry data-plane token (`https://ai.azure.com/.default`), patches `openapi.json` with the deployed ACA FQDN, creates an X-Api-Key custom-keys connection, and upserts an agent definition whose only tool is `evaluateAsset`. The Foundry runtime then invokes our `/evaluate` endpoint from Playground / SDK / REST.

### 2.4 Resilience

- **Polly v8** retry + timeout for MCP initialization and agent calls ([ResiliencePipelineFactory.cs](src/Cascade.CTL.Agent.Application/Resilience/ResiliencePipelineFactory.cs)).
- Custom circuit breaker around Content Safety (`5 failures / 60s open / regex fallback`).
- `AddStandardResilienceHandler` on the Asset Domain HTTP client.

### 2.5 Observability

[TelemetryConfiguration.cs](src/Cascade.CTL.Agent.Infrastructure/Observability/TelemetryConfiguration.cs) registers an OpenTelemetry tracer + meter, adds the `"Cascade.CTL.Agent"` and `"Microsoft.Extensions.AI"` sources, and exports to Azure Monitor when `ApplicationInsights:ConnectionString` is set. Console exporter is always on. Audit events go through `IAuditService` as custom App Insights events (`CTL.AuditStep`).

---

## 3. What We're Doing Well (Keep)

1. **Single options model with explicit Entra-vs-key switch.** `AzureAIFoundryOptions.UseAzureIdentity` is binary and visible in `appsettings.json` — no silent ambient credential resolution. Same pattern used for AOAI, Search, judge, embeddings.
2. **`DefaultAzureCredential` is the default everywhere it matters.** Content Safety and PII Filter have no key path at all; they require Entra. AOAI and Search default to identity in `Provision-AzureServices.ps1` Phase 9 (role assignments are part of provisioning, not an afterthought).
3. **GenAI semantic conventions are wired in.** `ChatClientBuilder.UseOpenTelemetry(...)` from `Microsoft.Extensions.AI` emits the standard `gen_ai.*` attributes (operation name, request model, response model, token counts, finish reason). We pick those up via the `"Microsoft.Extensions.AI"` source.
4. **Mandatory parameters on provisioning scripts.** `migration.config.psd1` and `Provision-AzureServices.ps1` use `Mandatory=$true` instead of silent stale defaults — a deliberate hardening from the last subscription migration.
5. **Foundry as a *peer*, not the master.** Our orchestrator stays in-process; the Foundry agent is just a thin façade calling our `/evaluate`. This keeps determinism (token budget, reflection lockdown, audit trail) under our control, which is the right call for a regulated decision.
6. **Repo is self-contained** — no live endpoints/keys are checked in; `appsettings.json` ships with empty strings; smoke-test step in provisioning validates before declaring success.
7. **Linked content for RAG knowledge.** After the recent refactor, `config/rag-knowledge/*.json` is the single source of truth, linked into Api, Evals, and Indexer via `LinkBase`. Same JSONs ship to ACA via Dockerfile mirroring.

---

## 4. Scope for Improvement (Code & Wiring)

### 4.1 Duplicated chat-client wiring across hosts

`ServiceRegistration.cs` in `Cascade.CTL.Agent.Api` and `Cascade.CTL.Agent.Host` re-implement the same Foundry chat-client construction. The class comment already calls this out:

> `// NOTE: keep this in sync with Host/ServiceRegistration.cs until both are merged into a shared composition module (tracked as deferred work).`

**Action:** extract to a single `AddCTLChatClient(this IServiceCollection, IConfiguration)` in Infrastructure (next to `InfrastructureRegistration.cs`). Take an `AzureAIFoundryOptions` parameter so the Judge model reuses the exact same code path. Eliminates drift across the three current call sites (primary in Api, primary in Host, judge in both).

### 4.2 The serverless path leaks Entra tokens as static `ApiKeyCredential`

```csharp
var token = azureCredential.GetToken(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]));
credential = new ApiKeyCredential(token.Token);
```

Two problems:
- The token is acquired **once at DI-singleton construction** and never refreshed. After ~1 hour the chat client returns 401.
- It's a synchronous `GetToken` inside a DI factory — not what the SDK expects.

**Action:** for serverless Model Inference, switch to `Azure.AI.Inference.ChatCompletionsClient` (or the newer `Microsoft.Extensions.AI.Azure.AIInference` once GA) and pass `DefaultAzureCredential` directly. Both refresh tokens correctly. If we must stay on `OpenAIClient`, use `BearerTokenAuthenticationPolicy` via the pipeline rather than baking the JWT into an `ApiKeyCredential`.

### 4.3 `GetChatClient(...).AsIChatClient()` returns a non-disposable wrapper

We register `IChatClient` as a singleton, but the inner `AzureOpenAIClient` owns an HTTP pipeline. There is no `Dispose` on the singleton path. For long-lived ACA replicas this is fine, but it means we cannot rotate the underlying credential without a restart. Combined with §4.2 above, an identity-rotation strategy is the cleanest fix.

### 4.4 Endpoint-suffix sniffing is fragile

`endpoint.Contains(".openai.azure.com")` will misclassify private-endpoint FQDNs, custom DNS, or future Foundry hostnames. Express the choice in config (`CTLAgent:AzureAIFoundry:Mode = AzureOpenAI | FoundryInference`) and default it to `AzureOpenAI` when not specified. Removes a class of "works in dev, fails in prod" bugs.

### 4.5 `MicrosoftAppPassword` for Teams is a static secret

The Teams HITL path still uses a `MicrosoftAppPassword`. Bot Framework supports **Managed Identity bots** since Bot Service 4.20 (`MicrosoftAppType = "UserAssignedMSI"`). Provision a UAMI, set `MicrosoftAppId = <uami clientId>` and drop the password from config entirely. Already a config-level branch — only the documentation and `Provision-AzureServices.ps1` need updates.

### 4.6 MCP server auth is a shared static `X-Api-Key`

[McpServerRegistration.cs](src/Cascade.CTL.Agent.McpServer/McpServerRegistration.cs) compares the incoming `X-Api-Key` to a single config value. Two issues: rotation requires both sides redeployed simultaneously, and the key is the same across replicas/tenants. See [§5.4](#54-managed-identity-for-the-mcp-server).

### 4.7 `EnableSensitiveData = true` is hard-coded

Currently:

```csharp
.UseOpenTelemetry(sourceName: ..., configure: c => c.EnableSensitiveData = true)
```

Sensitive data (full prompts/responses, including PII before masking) is shipped to App Insights by default. The PII filter runs upstream in `GuardrailsMiddleware`, but reflection / judge prompts pass through later and can still contain regulated content. Bind this to a config flag and default it to `false` in `Production`, `true` in `Development`.

### 4.8 No request-level cost / token telemetry on the judge model

The judge call (`VerdictGroundednessEvaluator`) goes through a different `IChatClient` registration that **does not** chain `UseOpenTelemetry`. So judge spend is invisible in App Insights. Apply the same pipeline (same `UseOpenTelemetry` + `UseFunctionInvocation`).

### 4.9 Health checks don't probe Foundry

`/health` is unauthenticated and returns 200 without touching AOAI / Search / Content Safety. ACA readiness probes will mark replicas healthy even when the AOAI deployment is throttled or its key has rotated.

**Action:** add `Microsoft.Extensions.Diagnostics.HealthChecks` with custom checks that issue a 1-token completion against AOAI and a cheap `count=1` query against Search; expose at `/health/ready` (vs `/health/live`).

---

## 5. Managed Identity — Where to Push Next

### 5.1 What's already on Entra (good)

- Content Safety, PII Filter — Entra only.
- AOAI primary + judge — Entra default in provisioning; key path exists for dev.
- Azure AI Search — Entra default.
- Application Insights — uses ingestion endpoint with connection string; no key needed.

### 5.2 Switch to **User-Assigned Managed Identity** (UAMI) instead of System-Assigned

ACA today is provisioned with a System-Assigned MI (implicit in `az containerapp up`). The roles in `Provision-AzureServices.ps1` Phase 9 are assigned to **the developer's user principal**, not to the workload identity. That means the deployed app silently relies on whatever System-Assigned MI ACA hands it.

**Action:**
1. Create a UAMI per environment in [Provision-AzureServices.ps1](scripts/Provision-AzureServices.ps1).
2. Assign roles to the UAMI's `principalId` (not `signedInUser`).
3. Attach the UAMI to each Container App in [Deploy-CTL-Containers.ps1](deploy/Deploy-CTL-Containers.ps1) (`--user-assigned <uamiArmId>`).
4. Set `AZURE_CLIENT_ID` env var on the container so `DefaultAzureCredential` selects the right identity when multiple MIs are attached.

Required RBAC (target = UAMI principalId):

| Resource | Role | Why |
|----------|------|-----|
| Azure OpenAI (primary + judge) | `Cognitive Services OpenAI User` | Inference calls |
| Azure AI Search | `Search Index Data Reader` | Runtime queries (Agent.Api) |
| Azure AI Search | `Search Index Data Contributor` | Index writes (RAG Indexer only) |
| Azure AI Content Safety | `Cognitive Services User` | `text:analyze`, `text:shieldPrompt` |
| Azure AI Language | `Cognitive Services Language Reader` | PII detection |
| Application Insights | `Monitoring Metrics Publisher` | OTLP ingestion |
| Foundry Project | `Azure AI Developer` | Agent invocation, if Foundry Agent fronts us |

### 5.3 Drop every remaining API key from `appsettings.json`

Currently still keyed (acceptable in dev, eliminate in prod):

| Key | Replacement |
|-----|-------------|
| `CTLAgent:AzureAIFoundry:ApiKey` | UAMI + `Cognitive Services OpenAI User` |
| `CTLAgent:JudgeModel:ApiKey` | Same UAMI (same project, separate deployment) |
| `CTLAgent:RAG:AzureSearch:AdminKey` / `QueryKey` | UAMI + Search RBAC |
| `CTLAgent:RAG:AzureSearch:AzureOpenAIApiKey` | UAMI + `Cognitive Services OpenAI User` |
| `Teams:MicrosoftAppPassword` | UAMI bot (`MicrosoftAppType = UserAssignedMSI`) |
| `McpServer:ApiKey` | UAMI + Entra token auth on MCP server (see §5.4) |
| `AssetDomainService:ApiKey` | UAMI + Entra-protected API |

Keep API keys gated behind `if (string.IsNullOrEmpty(apiKey) && !UseAzureIdentity) throw` so production cannot accidentally start with a key path.

### 5.4 Managed Identity for the MCP server

Replace the static `X-Api-Key` check in [McpServerRegistration.cs](src/Cascade.CTL.Agent.McpServer/McpServerRegistration.cs) with JWT validation:

```csharp
app.UseAuthentication(); app.UseAuthorization();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
```

Agent side: the Agent.Api's UAMI requests a token for the MCP server's app registration audience and passes it as `Authorization: Bearer <token>`. `McpToolProvider` already supports arbitrary headers via `HttpClientTransportOptions.AdditionalHeaders` — swap the static `X-Api-Key` for a refreshing bearer provider.

### 5.5 Workload Identity Federation for GitHub Actions / Azure DevOps

`Deploy-CTL-Containers.ps1` currently assumes a logged-in az session. Pipelines should use OIDC federation (`azure/login@v2` with `federatedToken`) — no secret in the pipeline at all.

---

## 6. Better Observability for the LLM / Agent

The current setup gets us *traces with prompt/response content* in App Insights. That is the table stakes. The gaps:

### 6.1 Enable the Foundry-native tracing surface

`Microsoft.Extensions.AI` 10.4 emits OpenTelemetry GenAI semantic conventions, but Foundry has a **Tracing** blade per project that expects spans named per the Azure AI Inference convention and exported to the project's bound Application Insights. Two things must align:

1. **Bind the Foundry project's tracing to the same App Insights resource** we already use. In `Provision-AzureServices.ps1` Phase 8, set the project's `applicationInsights` property to our AI resource ARM ID.
2. **Add `Microsoft.Extensions.AI.Telemetry.AzureAIInference`** (or the `AzureAIInferenceTracing` package once it ships) so the source name and attribute set match what the Foundry blade indexes.

The payoff: per-agent / per-thread cost and latency curves rendered in the Foundry portal, not just in App Insights Logs.

### 6.2 Per-step custom span attributes

Today we emit one big LLM span per turn. The orchestrator runs Plan → Investigate (N tools) → Reflect → Quality Gate as distinct activities — they should each be a span with consistent attributes:

```csharp
using var activity = ActivitySource.StartActivity("ctl.step.reflect");
activity?.SetTag("ctl.session_id", session.Id);
activity?.SetTag("ctl.asset_id", asset.AssetId);
activity?.SetTag("ctl.step", "Reflect");
activity?.SetTag("ctl.tokens.prompt", usage.InputTokens);
activity?.SetTag("ctl.tokens.completion", usage.OutputTokens);
activity?.SetTag("ctl.verdict.proposed", verdict.Verdict.ToString());
activity?.SetTag("ctl.confidence", verdict.ConfidenceScore);
activity?.SetTag("ctl.judge.score", groundednessScore);
```

This lets a single KQL query slice cost / latency / verdict-distribution by step. The custom `CTL.AuditStep` events we already emit overlap with this — collapse them into span attributes + events on the same span, instead of a parallel event stream.

### 6.3 Token & cost meters

Standard counters that should be a `Meter`:

| Instrument | Type | Tags |
|------------|------|------|
| `ctl.llm.tokens.input` | Counter | `model`, `step`, `agent` |
| `ctl.llm.tokens.output` | Counter | `model`, `step`, `agent` |
| `ctl.llm.requests` | Counter | `model`, `step`, `outcome` (`ok`/`throttled`/`error`) |
| `ctl.llm.duration` | Histogram (ms) | `model`, `step` |
| `ctl.guardrails.blocks` | Counter | `guard` (`prompt_shield`/`pii`/`content_safety`/`token_budget`) |
| `ctl.judge.groundedness` | Histogram | `model` |
| `ctl.verdict.distribution` | Counter | `verdict`, `confidence_bucket` |

Add `metrics.AddMeter("Cascade.CTL.Agent")` in `TelemetryConfiguration.cs` (already there) and emit through a singleton `Meter` injected into the orchestrator. Wire to Azure Monitor metrics so dashboards / alerts work without log queries.

### 6.4 Use **Azure AI Evaluation** for offline+online quality

The `Microsoft.Extensions.AI.Evaluation` and `Microsoft.Extensions.AI.Evaluation.Quality` packages are already referenced in [Cascade.CTL.Agent.Tests.csproj](tests/Cascade.CTL.Agent.Tests/Cascade.CTL.Agent.Tests.csproj). We use them in tests but not in the Foundry portal. The Foundry **Evaluations** blade can:

- Run our existing eval suite against a Foundry deployment on a schedule (groundedness, relevance, coherence, fluency).
- Compare two deployments / two prompt versions side by side.
- Surface drift over time per evaluator.

**Action:** add a `Cascade.CTL.Agent.Evals.Foundry` lightweight project that uploads our eval datasets to the Foundry project's data assets and registers an evaluation run definition. Run nightly from the pipeline.

### 6.5 Trace the Foundry agent → our API call as one logical span

When the external Foundry agent calls our `/evaluate`, today the inbound HTTP span and the outbound LLM spans are in different App Insights resources (Foundry's vs ours). Pass `traceparent` through the Foundry agent tool definition — Foundry already supports W3C trace context propagation when the OpenAPI tool has the `traceparent` header in `parameters`. Add to `openapi.json`:

```json
"parameters": [
  { "in": "header", "name": "traceparent", "schema": { "type": "string" }, "required": false }
]
```

End result: one end-to-end trace from Foundry Playground → our API → AOAI → judge AOAI → AI Search → MCP tools.

### 6.6 Sampling and PII scrubbing

`EnableSensitiveData = true` ships full prompts. For prod:
- Add a `TraceProcessor` that redacts via `PiiFilter` *before* the Azure Monitor exporter runs.
- Use `ParentBasedSampler(probabilistic 10%)` for high-volume health checks; keep `AlwaysOn` for `/evaluate`.

---

## 7. Leveraging More of Foundry

These are not "do all of them" — they're optionality with clear cost/benefit.

### 7.1 Foundry **Model Catalog** for the judge

The judge does not need `gpt-4o`. A cheap model fine-tuned for groundedness scoring (`gpt-4o-mini`, `Phi-4`, or a Mistral / Llama deployment from the Catalog) is appropriate. `JudgeModel.ModelId` already supports any deployment name; only provisioning + the eval suite need updates.

### 7.2 Foundry **Prompt Flow** for the Plan and Reflect prompts

Today, prompts are C# string literals in `Application/Orchestration/*`. Moving them to a Prompt Flow gives:
- Side-by-side prompt versioning in the Foundry portal.
- A/B routing without redeploy.
- Replays of historical sessions against a new prompt version.

Tradeoff: another deployable surface. Worth doing only if non-engineers are tuning prompts.

### 7.3 Foundry **Agent Service** as the primary orchestrator — **NOT recommended** for this workload

The current solution intentionally keeps orchestration in-process for determinism (token budget guard, reflection lockdown buckets, audit-step granularity, MCP tool surface). Moving to the Foundry Agent runtime would surrender:
- The custom `GuardrailsMiddleware` pipeline.
- The verdict-determinism v2 audit fields (`LlmRawVerdict`, `LlmRawConfidence`, `ModelFingerprint`).
- The MCP server's fine-grained tool surface (Foundry agents prefer OpenAPI tools).

What **is** worth doing is what we already do — register the existing API as a Foundry agent tool so Playground and Foundry-native consumers can reach us.

### 7.4 Foundry **Connections** instead of per-service config sections

A Foundry project carries first-class Connection objects for AOAI, AI Search, Content Safety, etc. Reading those at startup (via `AIProjectClient.GetConnectionsClient()`) collapses ~6 endpoint/key pairs in `appsettings.json` into a single project resource ID. Combined with §5.3, the runtime config becomes:

```json
"CTLAgent": {
  "FoundryProject": { "SubscriptionId": "...", "ResourceGroup": "...", "ProjectName": "..." },
  "Models": { "Primary": "gpt-4o", "Judge": "gpt-4o-mini", "Embedding": "text-embedding-3-small" }
}
```

…and nothing else. All endpoints resolved at startup via the project's Connections.

### 7.5 Foundry **Tracing + Evaluations + Datasets** loop

Foundry's value proposition past raw inference is the *loop*: capture production traces → save as datasets → run evaluations → diff against previous version. We have the inputs (audit events, eval suite) — wiring them to the project's data assets closes the loop without rebuilding tooling.

### 7.6 Use **Azure AI Search Indexer with Knowledge Store + Skillset** instead of our custom indexer

Our `Cascade.CTL.RAG.Indexer` is ~250 lines of chunk/embed/upload code. Azure AI Search's **integrated vectorization** (GA) can pull from Blob Storage, chunk via the `SplitSkill`, embed via `AzureOpenAIEmbeddingSkill`, and write to the index — all declarative. Reduces the indexer to a Skillset JSON + a Blob upload. Worth migrating if RAG content sources expand beyond curated JSON.

### 7.7 **Azure AI Speech / Document Intelligence** for non-JSON evidence

Title commitments, FHA notices, HOA estoppels arrive as PDFs/scans in real workloads. Document Intelligence (prebuilt-document, prebuilt-layout) feeds structured text into the same RAG index, and the LegalAnalyzer MCP tool can quote page-level citations. Currently out of scope (we use mock providers), but the *integration shape* should be designed now so it's a drop-in for production.

### 7.8 **On Your Data** is *not* a fit

AOAI "On Your Data" is appealing (auto-retrieval, no orchestration), but it bypasses the guardrails middleware and the judge. Our regulated decisions need the explicit Plan → Investigate → Reflect → Quality Gate pipeline. Skip.

---

## 8. Other Foundry-Adjacent Items Worth Tracking

| Item | Status | Action |
|------|--------|--------|
| Foundry **quota** request for `gpt-4o` and embeddings TPM | Per-subscription, does NOT migrate | Run quota request in `Provision-AzureServices.ps1` preflight; fail fast if denied. |
| **Regional pinning** | Currently `eastus2` only | Document data-residency assumption; add a `Location` parameter check against the model's supported regions. |
| **Private endpoints** for AOAI, Search, Content Safety, AI Language | Not wired | Required for any production tenant; add a `-PrivateEndpoints` switch to provisioning. |
| **Customer-managed keys (CMK)** on Foundry project storage | Not wired | Compliance ask for some Cascade customers; provision via Key Vault + project encryption settings. |
| **Foundry Hub vs Project boundary** | One project per env today | Confirm hub/project topology with platform team — typically one hub per business unit, project per workload. |
| **Token budget alignment with Foundry quotas** | `TokenBudgetGuard` uses a hard 50k per request | Cross-reference with the deployed model's TPM/RPM; add a `Meter` counter so we see saturation before throttling. |
| **Content Safety blocklists** | Built-in categories only | Add a custom blocklist (PII patterns, internal project codenames) via `BlocklistClient`; reference by name in `analyze` calls. |
| **Bring Your Own Storage** for App Insights / Foundry tracing | Default Microsoft-managed | Required for some regulated tenants. |

---

## 9. Summary Roadmap (Sequenced)

| Order | Change | Why first |
|-------|--------|-----------|
| 1 | Consolidate chat-client construction into Infrastructure | Eliminates drift before any other refactor lands |
| 2 | Fix serverless Entra-token-as-API-key bug (§4.2) | Real production bug — 401 after ~1 hour |
| 3 | UAMI for ACA + Bot Framework, drop remaining keys (§5.2, §5.3, §5.5) | Removes the largest class of secrets |
| 4 | Health checks that touch AOAI + Search (§4.9) | Stop routing traffic to broken replicas |
| 5 | Apply `UseOpenTelemetry` to judge client (§4.8) | Make judge spend visible before optimizing it |
| 6 | Per-step spans + meters (§6.2, §6.3) | Foundation for cost dashboards & SLOs |
| 7 | Foundry Tracing binding + traceparent propagation (§6.1, §6.5) | End-to-end traces across Foundry / Api / AOAI |
| 8 | Mode flag instead of endpoint suffix sniffing (§4.4) | Removes a recurring class of misconfig bugs |
| 9 | Foundry Connections + project resolver (§7.4) | Collapses appsettings.json into 1 project ref |
| 10 | Foundry Evaluations on a schedule (§6.4) | Closes the production-quality feedback loop |

Items in §7.2 (Prompt Flow), §7.6 (Integrated vectorization), §7.7 (Document Intelligence) and §8 (CMK / Private Endpoints) are tenant-specific — defer until a tenant asks.

---

## Appendix A — File Map

| File | Role |
|------|------|
| [src/Cascade.CTL.Agent.Api/ServiceRegistration.cs](src/Cascade.CTL.Agent.Api/ServiceRegistration.cs) | Primary + judge chat client construction, Teams wiring, telemetry root |
| [src/Cascade.CTL.Agent.Host/ServiceRegistration.cs](src/Cascade.CTL.Agent.Host/ServiceRegistration.cs) | CLI host duplicate of the above |
| [src/Cascade.CTL.Agent.Infrastructure/InfrastructureRegistration.cs](src/Cascade.CTL.Agent.Infrastructure/InfrastructureRegistration.cs) | RAG service factory, audit/telemetry registration |
| [src/Cascade.CTL.Agent.Infrastructure/RAG/AzureSearchClientFactory.cs](src/Cascade.CTL.Agent.Infrastructure/RAG/AzureSearchClientFactory.cs) | Embedding + Search client construction |
| [src/Cascade.CTL.Agent.Infrastructure/Observability/TelemetryConfiguration.cs](src/Cascade.CTL.Agent.Infrastructure/Observability/TelemetryConfiguration.cs) | OTel pipeline + App Insights exporter |
| [src/Cascade.CTL.Agent.Guardrails/ContentSafetyGuard.cs](src/Cascade.CTL.Agent.Guardrails/ContentSafetyGuard.cs) | Content Safety + Prompt Shields client |
| [src/Cascade.CTL.Agent.Guardrails/PiiFilter.cs](src/Cascade.CTL.Agent.Guardrails/PiiFilter.cs) | Azure AI Language PII detection |
| [src/Cascade.CTL.Agent.McpServer/McpServerRegistration.cs](src/Cascade.CTL.Agent.McpServer/McpServerRegistration.cs) | MCP server + X-Api-Key auth |
| [src/Cascade.CTL.Agent.Application/Orchestration/McpToolProvider.cs](src/Cascade.CTL.Agent.Application/Orchestration/McpToolProvider.cs) | MCP client + resilience |
| [src/Cascade.CTL.Agent.Application/Orchestration/VerdictGroundednessEvaluator.cs](src/Cascade.CTL.Agent.Application/Orchestration/VerdictGroundednessEvaluator.cs) | Judge LLM call |
| [src/Cascade.CTL.Application/Resilience/ResiliencePipelineFactory.cs](src/Cascade.CTL.Agent.Application/Resilience/ResiliencePipelineFactory.cs) | Polly v8 pipelines |
| [deploy/Register-FoundryAgent.ps1](deploy/Register-FoundryAgent.ps1) | Foundry Agent registration |
| [scripts/Provision-AzureServices.ps1](scripts/Provision-AzureServices.ps1) | 10-phase resource + RBAC provisioning |
| [src/Cascade.CTL.Agent.Api/openapi.json](src/Cascade.CTL.Agent.Api/openapi.json) | OpenAPI spec consumed by Foundry agent tool |
| [config/appsettings.json](config/appsettings.json) | Single config source; sensitive keys empty in repo |
