# Building & Designing MCP Servers From Scratch

**Audience:** Engineers, architects, and reviewers who need to understand how Model Context Protocol (MCP) servers are constructed in general — and specifically how the CTL solution implements one.

**Scope:** Protocol fundamentals → design patterns → concrete walkthrough of [src/Cascade.CTL.Agent.McpServer](../../src/Cascade.CTL.Agent.McpServer/) → best practices → recommended target architecture.

---

## 1. What MCP Is (and Is Not)

The **Model Context Protocol** is an open, JSON-RPC 2.0–based protocol that standardises how an LLM/agent host (the **client**) discovers and invokes capabilities exposed by an external process (the **server**). It is to LLM tool-use what LSP is to editors: one protocol, many implementations, pluggable on either side.

An MCP server can expose three kinds of primitives:

| Primitive    | Purpose                                                                 | Analogy             |
| ------------ | ----------------------------------------------------------------------- | ------------------- |
| **Tools**    | Callable functions with typed JSON-schema inputs/outputs (side effects allowed) | REST POST / RPC     |
| **Resources** | Read-only addressable content the model can pull into context          | GET / file://       |
| **Prompts**  | Reusable, parameterised prompt templates the host can offer to the user | Stored procedures   |

It also defines **sampling** (server-initiated LLM calls back through the client) and **roots/elicitation** for richer interactive sessions.

**What MCP is NOT:** it is not a runtime, not an agent framework, not a vector store. It is purely the wire contract + capability negotiation layer. Your business logic still lives behind the tool handlers.

---

## 2. Anatomy of an MCP Server (General)

A from-scratch MCP server, regardless of language, has six concerns:

1. **Transport** — how bytes move between client and server.
   - `stdio` — child process over stdin/stdout (best for local dev, IDE plugins, desktop hosts like Claude Desktop).
   - `Streamable HTTP` (current spec, supersedes the older HTTP+SSE pairing) — single HTTP endpoint that upgrades to SSE for server→client streaming. Required for remote / multi-tenant deployments.
2. **Framing** — JSON-RPC 2.0 messages: `initialize`, `tools/list`, `tools/call`, `resources/list`, `resources/read`, `prompts/list`, `prompts/get`, plus notifications (`notifications/initialized`, progress, log).
3. **Capability negotiation** — during `initialize`, server advertises which primitive categories it supports and its protocol version. Clients must gracefully degrade.
4. **Schema** — every tool declares a JSON Schema for its input. Output is either unstructured `content[]` (text/image/resource blobs) or, increasingly, a typed `structuredContent` payload backed by an output schema.
5. **Handlers** — the actual code that executes when `tools/call` arrives. Must be idempotent-friendly, side-effect-aware, and return well-defined errors.
6. **Lifecycle & cancellation** — long-running calls should honour client cancellation (`notifications/cancelled`) and emit `notifications/progress` when useful.

### 2.1 Minimal hand-rolled flow

```
client ──▶ initialize(protocolVersion, capabilities)
server ──▶ initialize result(serverInfo, capabilities)
client ──▶ notifications/initialized
client ──▶ tools/list
server ──▶ { tools: [ {name, description, inputSchema}, ... ] }
client ──▶ tools/call { name, arguments }
server ──▶ { content: [...], isError?: bool, structuredContent?: {...} }
```

You can implement this from scratch in any language. In practice, **use an SDK** — there are first-party SDKs for TypeScript, Python, C#/.NET, Java, Go, Rust, Kotlin, Swift. They give you transports, schema generation, decorators/attributes, and capability negotiation for free.

---

## 3. Designing an MCP Server: Decisions Before Code

Before writing a single handler, lock down:

| Decision               | Options                                                                 | Trade-off                                                                 |
| ---------------------- | ----------------------------------------------------------------------- | ------------------------------------------------------------------------- |
| **Granularity**        | One server with many tools vs. many servers with focused toolsets       | Monolith = simpler ops; federated = blast-radius isolation + independent scaling |
| **Transport**          | stdio vs. Streamable HTTP                                               | stdio = trust boundary = process; HTTP = trust boundary = auth header     |
| **Statefulness**       | Stateless (each call independent) vs. session-bound                     | Stateless scales horizontally; session enables sampling/elicitation       |
| **Tool naming**        | Verb-first (`searchTitle`) vs. domain-prefixed (`legal.search_title`)    | Prefixes prevent collisions when an agent mounts multiple servers         |
| **Output shape**       | Plain text vs. JSON string vs. `structuredContent` w/ schema            | Structured output is reliable for downstream code; text is cheaper for LLM context |
| **Auth model**         | None (stdio only) / API key / OAuth 2.1 PKCE / mTLS                     | OAuth is the spec-blessed remote model; API key is pragmatic for internal |
| **Idempotency**        | Natural keys, dedupe tokens, or accept duplicates                       | Critical for tools with side effects (writes, payments, ticket creation)  |
| **Failure semantics**  | `isError:true` with content vs. JSON-RPC error vs. retryable flag       | Soft errors (`isError`) let the LLM recover; hard errors halt the chain   |

---

## 4. How the CTL Solution Built Its MCP Server

The CTL agent platform implements a **single .NET Streamable-HTTP MCP server** that fronts every external/back-office capability the agents need: asset lookup, title search, HOA/code-violation checks, occupancy, valuation, and a RAG knowledge base over policy documents.

### 4.1 Project layout

[src/Cascade.CTL.Agent.McpServer/](../../src/Cascade.CTL.Agent.McpServer/)
- [Program.cs](../../src/Cascade.CTL.Agent.McpServer/Program.cs#L1) — ASP.NET Core minimal host, locates the shared `config/appsettings.json` at the repo root so the MCP server uses the **same configuration** as the API/Host.
- [McpServerRegistration.cs](../../src/Cascade.CTL.Agent.McpServer/McpServerRegistration.cs#L1) — DI wiring + auth middleware + endpoint mapping.
- [Tools/](../../src/Cascade.CTL.Agent.McpServer/Tools/) — five tool classes, each a thin facade over a domain provider:
  - [AssetProfileTools.cs](../../src/Cascade.CTL.Agent.McpServer/Tools/AssetProfileTools.cs)
  - [LegalTools.cs](../../src/Cascade.CTL.Agent.McpServer/Tools/LegalTools.cs#L1)
  - [OccupancyTools.cs](../../src/Cascade.CTL.Agent.McpServer/Tools/OccupancyTools.cs)
  - [ValuationTools.cs](../../src/Cascade.CTL.Agent.McpServer/Tools/ValuationTools.cs)
  - [RAGTools.cs](../../src/Cascade.CTL.Agent.McpServer/Tools/RAGTools.cs#L1)
- [Dockerfile](../../src/Cascade.CTL.Agent.McpServer/Dockerfile#L1) — multi-stage build, non-root runtime user, `/app/audit-logs` writable, health check on port 8080.
- [Cascade.CTL.Agent.McpServer.csproj](../../src/Cascade.CTL.Agent.McpServer/Cascade.CTL.Agent.McpServer.csproj#L1) — single dependency on `ModelContextProtocol.AspNetCore` plus references to Domain and Infrastructure projects.

### 4.2 The four-line registration

The C# SDK collapses the entire server bootstrap into a few calls (see [McpServerRegistration.cs](../../src/Cascade.CTL.Agent.McpServer/McpServerRegistration.cs#L16)):

```csharp
services.AddCTLInfrastructure(useMockProviders: true, configuration: configuration);

services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();
```

What each line does:

- `AddCTLInfrastructure(...)` registers the **real business providers** (`ITitleSearchProvider`, `IHOAProvider`, `IRAGQueryService`, etc.) that the tool classes depend on. The MCP server is **not** where business logic lives — it is a transport-shaped wrapper.
- `AddMcpServer()` registers the MCP runtime (lifecycle, JSON-RPC dispatcher, capability negotiation).
- `WithHttpTransport()` selects the Streamable-HTTP transport — required because the agent host runs in a separate container/process.
- `WithToolsFromAssembly()` reflects over the current assembly and auto-discovers every type annotated with `[McpServerToolType]` and every method annotated with `[McpServerTool]`. **Zero registration boilerplate.** Adding a new tool = adding a new attributed method.

### 4.3 Authentication

The CTL MCP server protects every endpoint behind an **API-key middleware** (see [McpServerRegistration.cs](../../src/Cascade.CTL.Agent.McpServer/McpServerRegistration.cs#L28)):

```csharp
var expectedApiKey = app.Configuration["McpServer:ApiKey"]
    ?? throw new InvalidOperationException("McpServer:ApiKey must be configured...");

app.Use(async (context, next) =>
{
    var apiKey = context.Request.Headers["X-Api-Key"].ToString();
    if (!string.Equals(apiKey, expectedApiKey, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ...
    }
    await next();
});

app.MapMcp();
```

Notes:
- The key is **mandatory at startup** — the server fails fast if it isn't configured, eliminating the "accidentally deployed without auth" failure mode.
- The same header (`X-Api-Key`) and config key (`McpServer:ApiKey`) are used by the API gateway in [Program.cs](../../src/Cascade.CTL.Agent.Api/Program.cs#L22) — one auth pattern, two surfaces.
- This is deliberately **pragmatic, not spec-perfect**. The MCP spec recommends OAuth 2.1 for remote servers; an API key sitting behind an internal mesh (or AKS network policy) is an acceptable starting point and is straightforward to swap out — the only code that needs to change is the middleware.

### 4.4 How a tool is authored

A CTL MCP tool is a normal C# method on a normal C# class. The SDK does schema generation, parameter binding, and JSON-RPC plumbing automatically. Example from [LegalTools.cs](../../src/Cascade.CTL.Agent.McpServer/Tools/LegalTools.cs#L32):

```csharp
[McpServerToolType]
public sealed class LegalTools
{
    private readonly ITitleSearchProvider _titleProvider;
    // ... constructor injection ...

    [McpServerTool, Description("Search for title defects, open liens, and encumbrances ...")]
    public async Task<string> SearchTitle(
        [Description("County recorder parcel identifier (e.g., TX-DAL-123456)")] string parcelId,
        [Description("Two-letter US state code (e.g., TX, CA, FL)")] string stateCode)
    {
        if (string.IsNullOrWhiteSpace(parcelId))         return """{"error": "parcelId is required"}""";
        if (parcelId.Length > 50)                        return """{"error": "parcelId exceeds maximum length..."}""";
        if (string.IsNullOrWhiteSpace(stateCode) || stateCode.Length != 2)
            return """{"error": "stateCode must be a valid 2-letter US state code"}""";

        try
        {
            var result = await _titleProvider.SearchAsync(parcelId, stateCode.ToUpperInvariant());
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new {
                error = "Title search failed",
                transient = IsTransient(ex),
                detail = ex.GetType().Name
            }, JsonOptions);
        }
    }
}
```

Five design choices worth calling out:

1. **`[Description]` on every parameter** — these strings become the JSON-schema `description` fields the LLM reads at planning time. **Bad descriptions = bad tool selection.** Treat them as production prompt-engineering surface area.
2. **Input validation lives in the tool** — null/empty/length/format checks return a structured `{"error": "..."}` payload instead of throwing. The model sees the error and can correct itself on the next turn.
3. **No raw exceptions leak** — the `catch` block returns a serialised error with a `transient` boolean derived from `IsTransient(ex)` (network/IO/timeout exceptions). The orchestrator's retry policy can act on that flag.
4. **Type names are never exposed** — the `detail` field returns `ex.GetType().Name`, not `ex.Message`, to avoid leaking internal stack details / connection strings / customer data.
5. **Tools are stateless** — providers are injected per-request, no static caches, safe for concurrent calls.

### 4.5 Wire-level: what `SearchTitle` actually looks like on the network

The C# attributes above generate the JSON-RPC 2.0 messages below. This is the **actual contract** between the agent host and the MCP server — useful for debugging, contract tests, or implementing a non-.NET client.

**1. `tools/list` request** (sent once after `initialize`, results cached by the client):

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/list"
}
```

**2. `tools/list` response** (only the `SearchTitle` entry shown; the real response contains every `[McpServerTool]` in the assembly):

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "tools": [
      {
        "name": "SearchTitle",
        "description": "Search for title defects, open liens, and encumbrances for a property by parcel ID and state code. Returns title clearance status, list of open liens, encumbrances, and title defects.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "parcelId": {
              "type": "string",
              "description": "County recorder parcel identifier (e.g., TX-DAL-123456)"
            },
            "stateCode": {
              "type": "string",
              "description": "Two-letter US state code (e.g., TX, CA, FL)"
            }
          },
          "required": ["parcelId", "stateCode"]
        }
      }
    ]
  }
}
```

**3. `tools/call` request** (the planner decided to invoke `SearchTitle`):

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "SearchTitle",
    "arguments": {
      "parcelId": "TX-DAL-123456",
      "stateCode": "TX"
    }
  }
}
```

**4. `tools/call` response** (the `text` payload is the JSON string the C# method returned via `JsonSerializer.Serialize(result, JsonOptions)`):

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "content": [
      {
        "type": "text",
        "text": "{\n  \"parcelId\": \"TX-DAL-123456\",\n  \"stateCode\": \"TX\",\n  \"isClear\": false,\n  \"openLiens\": [\n    { \"type\": \"Tax\", \"amount\": 4820.55, \"holder\": \"Dallas County\" }\n  ],\n  \"encumbrances\": [],\n  \"defects\": [\"Missing reconveyance on 2019 deed of trust\"]\n}"
      }
    ],
    "isError": false
  }
}
```

**Error variant** — when the validation guard in the handler fires (e.g., empty `parcelId`), the same envelope is used; only the `text` content changes:

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "content": [
      { "type": "text", "text": "{\"error\": \"parcelId is required\"}" }
    ],
    "isError": false
  }
}
```

Note that CTL returns validation failures as **normal results with an `error` field in the payload** (so the LLM can read and recover), not as JSON-RPC faults. JSON-RPC `error` objects are reserved for genuine protocol problems (auth failure, malformed request, server crash).

### 4.6 RAG as just another tool

The same pattern wraps Azure AI Search vector retrieval ([RAGTools.cs](../../src/Cascade.CTL.Agent.McpServer/Tools/RAGTools.cs#L25)):

```csharp
[McpServerTool, Description("Query the CTL policy knowledge base via RAG ... Filters by state, county, and asset type. Use this to ground decisions in documented policies rather than general knowledge.")]
public async Task<string> QueryPolicyKnowledgeBaseViaRAG(
    [Description("Natural language search query ...")] string query,
    [Description("Two-letter US state code ... (optional)")] string? stateCode = null,
    [Description("County name ... (optional)")] string? county = null,
    [Description("Asset type ... (optional: Foreclosure, REO, NonForeclosure, ShortSale)")] string? assetType = null)
```

The description tells the model **when** to use it ("Use this to ground decisions in documented policies rather than general knowledge"). That single sentence is how an LLM learns to prefer grounded retrieval over hallucinated policy text.

### 4.7 The client side

The agent host consumes the server through [McpToolProvider.cs](../../src/Cascade.CTL.Agent.Application/Orchestration/McpToolProvider.cs#L1):

```csharp
var transportOptions = new HttpClientTransportOptions
{
    Endpoint = new Uri(endpoint),
    TransportMode = HttpTransportMode.StreamableHttp,
    Name = $"CTLAgent-{string.Join("-", serverNames)}"
};

if (!string.IsNullOrEmpty(_apiKey))
{
    transportOptions.AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = _apiKey };
}

var transport = new HttpClientTransport(transportOptions);
return await McpClient.CreateAsync(transport, cancellationToken: ct);
```

Then `client.ListToolsAsync()` returns `McpClientTool` objects which implement `Microsoft.Extensions.AI.AITool` — meaning they plug directly into `IChatClient` invocations with no glue code. The agent kernel sees them as ordinary function-calling tools.

Important architectural touches:

- **Endpoint deduplication.** The config maps multiple *logical* server names (`Legal`, `Valuation`, `RAG`, ...) to URLs. If two logical servers share an endpoint (today, all of them share the monolithic server), the provider opens **one** connection and aggregates tools. This means the code is already shaped for the day each tool family is split into its own deployable.
- **Resilience around init.** Connection is wrapped in a Polly pipeline (`ResiliencePipelineFactory.CreateMcpInitPipeline`) with a configurable timeout — a misbehaving MCP server cannot wedge agent startup forever.
- **API key flows through the transport headers**, mirroring the server-side middleware.
- **Asset profile is deliberately *not* exposed as a callable tool** to the planner (see comment in [McpToolProvider.cs](../../src/Cascade.CTL.Agent.Application/Orchestration/McpToolProvider.cs#L116)) — the orchestrator pre-fetches it and injects it into the prompt. The lesson: just because you *can* expose something as an MCP tool doesn't mean you *should*. Pre-fetched, always-needed context belongs in the system prompt, not in a tool-call round-trip.

### 4.8 Packaging & deployment

[Dockerfile](../../src/Cascade.CTL.Agent.McpServer/Dockerfile#L1) follows the boring-and-correct pattern:

- Multi-stage SDK→ASP.NET runtime image.
- Layer-cached restore (csproj copy first, then sources).
- Runs as `$APP_UID` (non-root).
- Listens on `:8080` only, no exposed admin port.
- `HEALTHCHECK` hits `/` so Kubernetes/Docker Compose can readiness-probe it.

Net result: the MCP server is a self-contained, horizontally scalable HTTP service with one writable mount (`/app/audit-logs`) and one open port.

---

## 5. Best Practices

### 5.1 Protocol & schema
- **Be specific in descriptions.** Both the tool description and each parameter description are *prompt surface*. Include when-to-use guidance, units, formats, examples.
- **Constrain inputs in the schema** (enums, max length, regex patterns). The model will respect them; bad-inputs become impossible rather than runtime errors.
- **Prefer `structuredContent` for machine-consumed outputs** (and supply an output schema). Plain text is fine when the model is the only consumer, but downstream code wants typed JSON.
- **Stable tool names.** Renaming a tool is a breaking change for every cached client and every saved trajectory in your evals.

### 5.2 Tool design
- **One verb, one tool.** Don't build `manageThing(action: "create"|"update"|"delete")` — build three tools. Smaller schemas → fewer LLM mistakes.
- **Idempotency keys** on any tool that writes. The model *will* call twice during retry storms.
- **Pagination** on any tool that returns lists. Hard-cap the response size; return a continuation token.
- **Return errors as data**, not as JSON-RPC faults, when the model can recover (validation failures, "not found"). Reserve faults for genuine protocol problems.
- **Deterministic JSON ordering** (use a single `JsonSerializerOptions` instance — as the CTL tools do) so evals can diff outputs reliably.

### 5.3 Security (OWASP-aligned)
- **Authenticate every transport.** stdio inherits process trust; HTTP needs API key minimum, OAuth 2.1 PKCE preferred for multi-tenant.
- **Authorise per-tool when scopes differ.** The MCP server is a privileged surface — a single compromised key shouldn't grant write access to everything. Map keys/tokens to allowed-tool lists.
- **Validate at the boundary.** Even though the schema constrains inputs, hostile clients exist; check lengths/formats inside the handler (CTL does this on every tool).
- **Never echo secrets or stack traces.** Return error categories and the exception *type*, not the message (CTL pattern).
- **Treat tool outputs as untrusted** on the client side — they will be fed to an LLM that may follow injected instructions. Strip/escape or fence outputs in the prompt template.
- **Rate-limit per client/key.** A planner stuck in a loop can DoS your downstream APIs.
- **Audit every call.** Persist request, latency, outcome, and (redacted) arguments. Required for SOX/regulatory work like CTL.
- **Network-restrict outbound calls from the server.** If `SearchTitle` only needs the county recorder, egress policy should reflect that.

### 5.4 Reliability
- **Timeout every downstream call** with a budget smaller than the agent's per-step budget.
- **Distinguish transient vs. permanent errors** (CTL's `IsTransient` helper) so the client can retry intelligently.
- **Honour cancellation tokens** end-to-end; a cancelled agent should not leave an HTTP request running for 60 seconds.
- **Emit progress notifications** for tools that take >2-3s; let the host show a spinner / cancel button.
- **Health endpoint** distinct from MCP endpoint (so the orchestrator can probe without holding a JSON-RPC session).

### 5.5 Observability
- **Structured logs** with correlation id, tool name, arg hash, duration, outcome.
- **Metrics**: tool-call count / latency histogram / error rate per tool name. A single high-error tool is your worst LLM behaviour.
- **Traces**: propagate `traceparent` from the client through the server into downstream HTTP calls — end-to-end span for one agent step.
- **Eval hooks**: log inputs/outputs in a deterministic, replay-friendly format. CTL does this in [tests/Cascade.CTL.Agent.Evals/](../../tests/Cascade.CTL.Agent.Evals/).

### 5.6 Versioning & evolution
- **Semver the *tool surface***, not the binary. Adding a tool or an optional parameter = minor; removing a tool / changing a parameter type = major.
- **Soft-deprecate.** Keep an old tool alive with a description that says "deprecated, prefer X" for at least one release cycle.
- **Pin protocol versions** during `initialize` and gracefully degrade if the client speaks an older spec.

### 5.7 Testing
- **Unit-test each tool method directly** (it's just a C# method).
- **Contract-test the server** with an MCP client SDK in CI — `initialize`, `tools/list`, sample `tools/call` round-trips.
- **Eval-test at the agent level** — run the full planner over canned scenarios and assert verdict determinism. CTL's eval harness does exactly this.

---

## 6. Recommended Target Architecture

For a system the size of CTL (and most enterprise agent platforms), the proven shape is:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                            Agent Host (orchestrator)                         │
│   ┌────────────────────┐    ┌────────────────────┐                          │
│   │  Planner / ReAct   │◀──▶│  IChatClient (LLM) │                          │
│   └─────────┬──────────┘    └────────────────────┘                          │
│             │ AITool[]                                                       │
│   ┌─────────▼──────────┐                                                     │
│   │  McpToolProvider   │  (multi-endpoint, dedup, resilience, X-Api-Key)    │
│   └─────────┬──────────┘                                                     │
└─────────────┼───────────────────────────────────────────────────────────────┘
              │  Streamable HTTP + JSON-RPC 2.0
              ▼
┌─────────────────────────────────┐  ┌─────────────────────────────────┐
│  MCP Server: Legal & Title      │  │  MCP Server: Valuation          │   ◀── federated, per-domain
│  - SearchTitle                  │  │  - GetAVM                       │       (single binary OK at first)
│  - CheckHOADelinquency          │  │  - ComparableSales              │
│  - LookupCodeViolations         │  │                                 │
└──────────┬──────────────────────┘  └──────────┬──────────────────────┘
           │ thin adapters                       │
           ▼                                      ▼
┌──────────────────────┐               ┌──────────────────────┐
│  Domain providers    │               │  Domain providers    │   ◀── business logic
│  (Infrastructure)    │               │  (Infrastructure)    │
└──────────┬───────────┘               └──────────┬───────────┘
           │                                       │
           ▼                                       ▼
   External APIs / Azure AI Search / Databases / Queues
```

### 6.1 Layering rules (the ones CTL follows)

1. **MCP tools are adapters, never logic.** A tool method is: validate → call a provider → serialise. If it grows past ~30 lines, the logic belongs in a domain service.
2. **Domain interfaces live in `Domain`**, implementations in `Infrastructure`. The MCP server depends on Domain contracts only; swapping the real `ITitleSearchProvider` for a mock is one DI flag (`useMockProviders: true`). This is exactly what enables CTL's eval harness and air-gapped demos.
3. **Configuration is shared** across host / API / MCP server (single `config/appsettings.json` discovered by walking parent directories) — guarantees one source of truth for endpoints, keys, and feature flags.
4. **Authentication is symmetric.** The same `X-Api-Key` pattern that protects the MCP server also protects the agent API. One mental model.
5. **Resilience belongs on the client side.** Polly pipelines around `McpClient.CreateAsync` and `ListToolsAsync` mean a flaky server can't cascade into a startup deadlock. The server itself stays simple.
6. **Don't expose data you already have.** Asset profile is pre-fetched and injected; only *new* information that the planner might or might not need becomes a tool.

### 6.2 When to split a monolith MCP server into federated servers

CTL currently runs a single MCP process exposing all tool families because (a) operational simplicity wins at the current scale and (b) the client-side `McpToolProvider` already supports the federated topology. Split when you hit at least two of:

- One tool family has materially different scaling needs (RAG: GPU/memory; Title: bursty IO).
- One tool family has a stricter data-classification boundary (PII, payment data).
- One tool family has a different release cadence and can't tolerate the others' deploys.
- Different teams own different tool families and need separate on-call.

The split itself is mechanical: extract the `Tools/` subfolder + the providers it touches into a new csproj, deploy under a new URL, and add an entry to the `McpServer:Endpoints` config. Zero changes to the orchestrator.

### 6.3 Target hardening checklist

| Area              | Current CTL                          | Production-grade target                                       |
| ----------------- | ------------------------------------ | ------------------------------------------------------------- |
| Auth              | Shared API key                       | OAuth 2.1 PKCE w/ per-client scopes (key acceptable internally) |
| Transport         | Streamable HTTP, cleartext within mesh | TLS terminated at ingress + mTLS within mesh                  |
| Authorisation     | All tools available to all callers   | Scope claims → allow-list per tool                            |
| Rate limiting     | None at MCP layer                    | Per-key token bucket + global circuit breaker                 |
| Audit             | Audit store in API tier              | Per-tool-call audit row, immutable, retained per regulation   |
| Schema validation | Manual `if` checks in handlers       | Generated JSON Schema with `MinLength`/`MaxLength`/`Pattern`  |
| Secrets           | App config                           | Azure Key Vault / Managed Identity                            |
| Outbound egress   | Unrestricted                         | NetworkPolicy / NSG per tool family                           |
| Observability     | Logs                                 | Logs + OTel metrics + distributed traces + eval replay logs   |
| Deployment        | Docker Compose + AKS                 | Same, plus blue-green tool-surface deploys                    |

---

## 7. Step-by-Step: Building One From Zero in .NET

If you were starting today, this is the shortest correct path — the one [src/Cascade.CTL.Agent.McpServer](../../src/Cascade.CTL.Agent.McpServer/) effectively followed:

1. `dotnet new web -n MyDomain.Mcp` — start from ASP.NET Core Web.
2. Add the package: `dotnet add package ModelContextProtocol.AspNetCore`.
3. In `Program.cs`:
   ```csharp
   var builder = WebApplication.CreateBuilder(args);
   builder.Services.AddMyDomainServices(builder.Configuration); // your providers
   builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();
   var app = builder.Build();
   app.Use(/* API key or OAuth middleware */);
   app.MapMcp();
   app.Run();
   ```
4. Create `Tools/MyTools.cs`:
   ```csharp
   [McpServerToolType]
   public sealed class MyTools(IMyProvider provider)
   {
       [McpServerTool, Description("...")]
       public async Task<string> DoThing([Description("...")] string arg) { ... }
   }
   ```
5. Dockerise (multi-stage SDK→aspnet, non-root, single port, healthcheck).
6. From the agent side, point an `McpClient` with `HttpClientTransport(StreamableHttp)` at it and pass the API key as a header.
7. Wire up logs, metrics, traces. Add evals. Ship.

That's it. Everything else in this document is what separates a demo from a system you can put behind a regulated workflow.

---

## 8. Further Reading

- MCP specification — <https://modelcontextprotocol.io/specification>
- C# SDK — <https://github.com/modelcontextprotocol/csharp-sdk>
- CTL solution architecture — [02_Solution_Architecture.md](02_Solution_Architecture.md)
- CTL Phase-2 enterprise plan (includes federated MCP roadmap) — [04_Phase2_Enterprise_Grade_Plan.md](04_Phase2_Enterprise_Grade_Plan.md)

---

## 9. Q&A — Clarifications

**Q1. `server ──▶ { content, isError?, structuredContent? }` — is the recommendation to return both `structuredContent` and `content`?**
Yes. Best practice (and the spec's intent) is to return **both**: `structuredContent` for programmatic consumers and a human/LLM-readable `content[]` text mirror of the same data. Hosts that don't yet understand `structuredContent` still get a usable response; hosts that do can validate against your output schema. Returning only `structuredContent` will silently degrade on older clients.

**Q2. "Constrain inputs in the schema (enums, max length, regex patterns)" — is that done in the `[Description]`?**
No. The description is free text the LLM reads. The constraints are **JSON Schema keywords** on the parameter itself: `enum`, `maxLength`, `minLength`, `pattern`, `minimum`, `maximum`, `format`. In the .NET SDK you express them with attributes like `[StringLength(50)]`, `[RegularExpression("^[A-Z]{2}$")]`, `[Range(0, 100)]`, or by using strongly-typed enums for parameter types — those flow into the generated input schema. Description tells the model *intent*; schema tells it (and validates) *shape*.

**Q3. "Structured output is reliable for downstream code; text is cheaper for LLM context" — downstream code? Isn't the MCP response primarily for the LLM?**
Both consume it. The LLM reads the `content[]` text in the next turn, but the **agent host code** also handles the response: parsing for tool-result caching, audit logging, eval comparison, deterministic post-processing (e.g., extracting `verdict.confidence` from a structured field rather than regex'ing text), conditional branching, and downstream non-LLM workflows (Camunda steps, DB writes). `structuredContent` is for those code paths; `content` text is what the LLM sees.

**Q4. With mTLS, can we leverage Istio's cert management? Does that assume the MCP server is in the same AKS cluster?**
Correct on both. If the MCP server runs inside the same service mesh (Istio, Linkerd, OSM), enable **STRICT mTLS** in a `PeerAuthentication` policy and the sidecar handles cert issuance, rotation, and validation transparently — your code stays the API-key version. For **external clients** (other clusters, partner orgs, on-prem callers) you have two options: (a) expose through an Istio ingress gateway that terminates mTLS at the edge using SPIFFE/SDS-issued certs and re-establishes mTLS to the workload, or (b) front the server with APIM / Azure Front Door doing mTLS termination + OAuth 2.1 to clients, mTLS to the workload. Same MCP binary, different perimeter.

**Q5. What does idempotency have to do with MCP — isn't it the wrapped service's job?**
Both. The wrapped service should be idempotent if it can be (natural keys, upsert semantics). When it can't, the **MCP tool wrapper** must add it, because the LLM/orchestrator *will* retry. Concise example: a `CreateInspectionOrder` tool. The downstream API has no dedupe. So the tool accepts an `idempotencyKey` parameter (or hashes `assetId + orderType + dayBucket`), maintains a short-lived cache (Redis/DB), and on a duplicate call returns the prior result instead of placing a second order. Without this, one stuck planner loop = N duplicate vendor invoices.

**Q6. `tools/list` returns name/description/inputSchema — no output schema?**
The original spec only required `inputSchema`, but the current spec adds an optional **`outputSchema`** alongside it (and `structuredContent` in the response is validated against it when present). Modern SDKs emit it when you declare a typed return. So: include `outputSchema` whenever you return `structuredContent`; older clients ignore the unknown field.

**Q7. In production, can MCP servers sit behind a gateway like APIM? Is it recommended?**
Yes, and **recommended** for any externally reachable MCP server. APIM (or Kong, Envoy, Front Door) gives you: TLS/mTLS termination, OAuth 2.1 token validation, per-subscription rate limiting, IP allow-listing, request/response logging, regional failover, and a stable public hostname decoupled from pod IPs. Two caveats specific to MCP: (a) the gateway must support **Streamable HTTP / SSE long-lived responses** — disable response buffering and set generous idle timeouts; (b) don't rewrite or strip the JSON-RPC body. APIM and Envoy both do this fine; some legacy WAFs do not.

**Q8. `SearchTitle` returns text to the LLM, not `structuredContent` (since it's optional) — correct?**
Correct, that is what the current code does. It serialises a JSON string into `content[].text`. It is **valid** but **suboptimal**: today the orchestrator has to JSON-parse the text to act on the result. Adding `structuredContent` (with a matching `outputSchema`) is the recommended evolution — keep the text mirror for the LLM, add the typed payload for the orchestrator and evals.

**Q9. Are `tools/call` and `tools/list` handled by the MCP SDKs?**
Yes — entirely. The .NET SDK (`ModelContextProtocol.AspNetCore` on the server, `ModelContextProtocol.Client` on the client) implements the JSON-RPC dispatcher, method routing, schema generation, parameter binding, capability negotiation, and lifecycle. You write attributed methods; the SDK exposes them as `tools/list` entries and routes `tools/call` to them. You never write a JSON-RPC handler by hand.

**Q10. Is `"id": 1` for matching request to response on the client side?**
Yes. It is the standard JSON-RPC 2.0 correlation id — the client generates it, the server echoes it in the response, the client uses it to match the response to the pending request (essential because notifications and out-of-order responses share the same connection). The SDK manages this; your code never sees it.

**Q11. Is `isError` in the response part of the protocol spec?**
Yes. `isError: true` on a `tools/call` result is the spec-defined way to signal a **tool-level failure** (the call reached the server but the tool itself failed) while still returning content the model can read. It is distinct from a JSON-RPC `error` object, which signals **protocol-level failure** (auth, malformed request, method not found). Rule of thumb: use `isError:true` for "the tool ran and failed gracefully"; use JSON-RPC `error` for "the request never reached the tool".

**Q12. Wrapping a REST API in MCP adds a hop — is that architecturally valid?**
Yes. The extra hop is a deliberate trade for: (a) protocol-level uniformity so any MCP-aware client can use it without a bespoke SDK, (b) a place to inject auth/audit/rate-limit/idempotency without touching the upstream API, (c) schema and description curation tuned for LLM consumption (LLM-friendly names ≠ REST resource names), and (d) tool-level egress and observability boundaries. The latency cost is typically <5–10 ms in-cluster — negligible compared to the LLM call itself. Don't wrap if the consumer is purely deterministic code; do wrap whenever an LLM or another MCP host is the consumer.

**Q13. Is granular per-endpoint egress tightening actually possible?**
Yes — at multiple layers. **Kubernetes `NetworkPolicy`** restricts pod egress to specific CIDRs/ports. **Istio `Sidecar` + `ServiceEntry`** restricts which external hostnames a workload may reach. **Azure NSGs / Application Security Groups / Firewall FQDN rules** restrict at the subnet or VNet edge. The cleanest pattern for MCP: split tools that talk to *different external systems* into separate pods/Deployments, each with its own egress policy (e.g., a `legal-mcp` pod allowed only to `recorder.dallascounty.org:443`; a `valuation-mcp` pod allowed only to the AVM vendor). This is one of the strongest reasons to federate MCP servers along data-source boundaries rather than keep one monolith.
