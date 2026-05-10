# Microsoft AI Middleware — Complete Reference & CTL Solution Mapping

**Prepared:** March 31, 2026  
**Context:** Cascade 2.0 — CTL Agent Solution  
**Purpose:** Demystify what middleware exists in the Microsoft AI ecosystem, what each does internally, and which ones this solution uses, skips, or could adopt.

---

## Part 1: This Solution's Actual Middleware Pipeline

The CTL Agent builds its pipeline in [ServiceRegistration.cs](../src/Cascade.CTL.Agent.Host/ServiceRegistration.cs). The exact code:

```csharp
var pipeline = new ChatClientBuilder(innerClient)
    .UseOpenTelemetry(sourceName: "Cascade.CTL.Agent", configure: c => c.EnableSensitiveData = false)
    .UseFunctionInvocation()
    .Build();

return new GuardrailsMiddleware(pipeline, contentSafety, tokenBudget, piiFilter, logger);
```

### Execution Order (Outside → Inside)

When the Orchestrator calls `chatClient.GetResponseAsync()`, the request flows through middleware in this order:

```
OUTBOUND (request heading to LLM):
  ① GuardrailsMiddleware     [CUSTOM]   — Token budget check + content safety screening
  ② FunctionInvocation        [BUILT-IN] — Passes through on first call (no tool_call yet)
  ③ OpenTelemetry             [BUILT-IN] — Creates trace span, records request metadata
  ④ Azure OpenAI Client       [INNER]    — HTTP call to GPT-4o

INBOUND (response returning from LLM):
  ④ Azure OpenAI Client       [INNER]    — Returns ChatResponse
  ③ OpenTelemetry             [BUILT-IN] — Closes span, records token usage + latency
  ② FunctionInvocation        [BUILT-IN] — Checks: is response a tool_call? If yes → execute tool → re-send to LLM → repeat
  ① GuardrailsMiddleware     [CUSTOM]   — Records token consumption in budget
```

**Critical nuance:** `ChatClientBuilder` uses a **Russian-doll nesting** model. The *last* `.Use*()` call wraps closest to the inner client. The *first* registered is the outermost. But `GuardrailsMiddleware` wraps the entire built pipeline manually, making it the absolute outermost layer.

### Complete Middleware Inventory (This Solution)

| # | Middleware | Source | Custom? | What It Does (Hidden Behavior) |
|---|-----------|--------|---------|-------------------------------|
| 1 | **GuardrailsMiddleware** | `Cascade.CTL.Agent.Guardrails` | ✅ Custom | Pre-request: checks token budget, screens input for injection + content safety. Post-response: records token consumption. Can short-circuit the entire pipeline by returning a synthetic "blocked" response. |
| 2 | **FunctionInvocationChatClient** | `Microsoft.Extensions.AI` | ❌ Built-in | The tool-calling loop. Intercepts `tool_call` responses, executes the matching `AITool`, appends the result as a `Tool` message, re-sends to LLM. Repeats until LLM returns a text response (no more tool calls). This is where **all MCP tool execution actually happens**. |
| 3 | **OpenTelemetryChatClient** | `Microsoft.Extensions.AI` | ❌ Built-in | Creates OpenTelemetry `Activity` spans for every LLM call. Records: model name, token usage (prompt/completion/total), finish reason, latency. If `EnableSensitiveData = true`, also records prompt/response content (disabled in this solution for PII safety). |

**That's it — 3 middleware total.** No others.

---

## Part 2: All Middleware Available in Current Microsoft AI Frameworks

Microsoft provides AI middleware across multiple packages. Here is every middleware class available as of March 2026, across all current (non-obsolete) frameworks.

---

### Framework 1: Microsoft.Extensions.AI (v10.4.x)

The primary AI abstraction layer. All middleware extends `DelegatingChatClient` and plugs into `ChatClientBuilder`.

#### 2.1 UseOpenTelemetry() → `OpenTelemetryChatClient`

**Package:** `Microsoft.Extensions.AI`  
**Registration:** `builder.UseOpenTelemetry(sourceName, configure)`  
**Used in this solution:** ✅ Yes

**What it does internally:**
- Creates an `ActivitySource` with your service name
- On every `GetResponseAsync` / `GetStreamingResponseAsync` call:
  - Starts a new `Activity` (OpenTelemetry span)
  - Tags it with: `gen_ai.system`, `gen_ai.request.model`, `gen_ai.operation.name`
  - After response: tags with `gen_ai.response.finish_reasons`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`
  - If `EnableSensitiveData = true`: records full prompt messages and response content as span events (⚠️ PII risk)
  - Records exceptions as span events with stack traces
- Follows the [OpenTelemetry Semantic Conventions for GenAI](https://opentelemetry.io/docs/specs/semconv/gen-ai/)

**Hidden behavior you wouldn't know:**
- Even with `EnableSensitiveData = false`, it still records token counts, model name, and latency — enough to monitor cost without exposing content
- It traces **every** LLM call individually — if FunctionInvocation triggers 5 tool-call loops, you get 5 separate spans, not 1
- Streaming responses (`GetStreamingResponseAsync`) get a single span that stays open until the stream completes

---

#### 2.2 UseFunctionInvocation() → `FunctionInvocationChatClient`

**Package:** `Microsoft.Extensions.AI`  
**Registration:** `builder.UseFunctionInvocation()`  
**Used in this solution:** ✅ Yes

**What it does internally:**
- After each LLM response, inspects `response.Messages` for any `FunctionCallContent`
- For each `tool_call` found:
  1. Looks up the matching `AITool` from `ChatOptions.Tools`
  2. Calls `tool.InvokeAsync(arguments)` — this is where McpClientTool executes the MCP HTTP call
  3. Wraps the result in a `FunctionResultContent` message with role `Tool`
  4. Appends it to the conversation history
  5. Re-sends the entire conversation to the LLM
- Repeats until the LLM returns a response with **no** `FunctionCallContent` (final text answer)
- Supports **parallel tool calls** — if the LLM returns multiple `tool_call`s in one response, it executes them concurrently via `Task.WhenAll`

**Hidden behavior you wouldn't know:**
- There is a **maximum iteration limit** (default: 128 round-trips). If the LLM keeps emitting tool calls beyond this, it throws to prevent infinite loops
- It does NOT validate tool arguments — it passes whatever the LLM generated directly to the tool. Validation is the tool's responsibility
- If a tool throws an exception, the exception message is sent back to the LLM as the tool result (so the LLM can reason about the failure)
- It modifies the `ChatOptions` to remove tools on the final iteration to signal to the LLM "stop calling tools"
- The conversation history grows with every loop iteration — each tool call + result adds 2 messages. This is why token budgets matter

---

#### 2.3 UseDistributedCache() → `DistributedCachingChatClient`

**Package:** `Microsoft.Extensions.AI`  
**Registration:** `builder.UseDistributedCache()`  
**Used in this solution:** ❌ No

**What it does internally:**
- Computes a cache key from: system prompt + all messages + model + temperature + all ChatOptions
- Before calling the inner LLM: checks `IDistributedCache` for a cached response
- If cache hit: returns the cached response immediately (no LLM call, no tokens consumed)
- If cache miss: calls the inner pipeline, caches the response, returns it
- Cache entry lifetime is configurable via `DistributedCachingChatClientOptions`

**Hidden behavior you wouldn't know:**
- The cache key is a **hash** of the entire conversation — changing even one character in any message produces a different key (no fuzzy matching)
- Streaming responses (`GetStreamingResponseAsync`) are fully materialized before caching — the cache stores the complete response, not a stream
- Tool calls are included in the cache key — the same prompt with different tools produces different cache entries
- ⚠️ **Dangerous for non-deterministic use cases**: if you cache a verdict for Asset A, and the underlying data changes (new lien filed), the cache returns the stale verdict

**Why this solution doesn't use it:** CTL evaluations must always reflect current data. Caching would return stale verdicts for assets whose underlying data has changed. Every evaluation must be independently justifiable.

---

#### 2.4 UseLogging() → `LoggingChatClient`

**Package:** `Microsoft.Extensions.AI`  
**Registration:** `builder.UseLogging(loggerFactory)` or `builder.UseLogging()`  
**Used in this solution:** ❌ No

**What it does internally:**
- Logs every `GetResponseAsync` and `GetStreamingResponseAsync` call using `ILogger`
- **Before request:** Logs at `Debug` level: method name, number of messages, model requested
- **After response:** Logs at `Debug` level: finish reason, token usage, number of response messages
- If `LogSensitiveData = true`: logs full prompt content and response content at `Trace` level
- Logs exceptions at `Error` level with full context

**Hidden behavior you wouldn't know:**
- It uses structured logging — all fields are tagged as `{MethodName}`, `{MessageCount}`, `{TokenUsage}` for filtering in log aggregators
- Streaming: logs each chunk individually if sensitive data is enabled (very verbose)
- It's a **separate concern from OpenTelemetry** — OpenTelemetry creates traces/spans, this creates ILogger entries. You might want both (traces for distributed tracing, logs for text-based debugging)

**Why this solution doesn't use it:** OpenTelemetry already captures all LLM call metadata. Adding UseLogging would duplicate — useful for local debugging but not needed in production pipeline.

---

#### 2.5 UseRateLimiting() → `RateLimitingChatClient` (Proposed / Preview)

**Package:** `Microsoft.Extensions.AI` (available in newer versions)  
**Registration:** `builder.UseRateLimiting(rateLimiter)`  
**Used in this solution:** ❌ No

**What it does internally:**
- Wraps the pipeline with a `RateLimiter` (from `System.Threading.RateLimiting`)
- Before each LLM call: acquires a lease from the rate limiter
- If rate limit exceeded: throws `RateLimiterRejectedException` or waits (depending on configuration)
- Supports: fixed window, sliding window, token bucket, concurrency limiter

**Hidden behavior you wouldn't know:**
- Rate limiting applies to **outbound LLM calls**, not inbound requests — it throttles how fast the application calls Azure OpenAI
- In a FunctionInvocation loop, each re-send to the LLM counts as a separate lease acquisition — a 5-tool-call evaluation acquires 6 leases (1 initial + 5 re-sends)
- This is client-side rate limiting — it supplements (not replaces) Azure OpenAI's server-side TPM/RPM limits

**Why this solution doesn't use it:** Azure OpenAI PTU deployment provides reserved capacity. `TokenBudgetGuard` handles per-evaluation limits. Server-side rate limiting via Azure API Management handles cross-instance throttling.

---

#### 2.6 Custom DelegatingChatClient (Build Your Own)

**Package:** `Microsoft.Extensions.AI.Abstractions`  
**Registration:** `builder.Use(sp => new MyMiddleware(inner, ...))` or `builder.Use<MyMiddleware>()`  
**Used in this solution:** ✅ Yes (`GuardrailsMiddleware`)

**What it provides:**
- Abstract base class `DelegatingChatClient` with a constructor that takes `IChatClient innerClient`
- Override `GetResponseAsync` and/or `GetStreamingResponseAsync`
- Call `base.GetResponseAsync()` to forward to the next middleware in the chain
- Return a synthetic response instead of calling base to **short-circuit** the pipeline

**This is how GuardrailsMiddleware works:**
```csharp
public class GuardrailsMiddleware : DelegatingChatClient
{
    public override async Task<ChatResponse> GetResponseAsync(...)
    {
        // PRE: check budget, screen input
        if (budget exceeded) return syntheticBlockedResponse;  // short-circuit
        if (injection detected) return syntheticBlockedResponse;  // short-circuit
        
        var response = await base.GetResponseAsync(...);  // forward to FunctionInvocation → OpenTelemetry → LLM
        
        // POST: record token usage
        tokenBudget.TryConsumeTokens(response.Usage.TotalTokenCount);
        return response;
    }
}
```

---

### Framework 2: Microsoft.Extensions.AI.Evaluation (v10.4.x)

This is the **Evals framework** — not middleware for the runtime pipeline, but middleware-like components for offline testing.

#### 2.7 ChatConversationEvaluator

**Package:** `Microsoft.Extensions.AI.Evaluation`  
**Purpose:** Runs a set of evaluators (metrics) against a recorded conversation

**What it provides:**
- `RelevanceEvaluator` — Does the response use the provided context?
- `CoherenceEvaluator` — Is the response internally consistent?
- `FluencyEvaluator` — Is the language natural and well-formed?
- `GroundednessEvaluator` — Is the response grounded in provided facts (not hallucinated)?
- `CompletenessEvaluator` — Does the response address all parts of the query?
- `EquivalenceEvaluator` — Does the response match a reference answer?

**Relevance to this solution:** The Evals project (`Cascade.CTL.Agent.Evals`) could use these to measure verdict quality. `GroundednessEvaluator` is particularly relevant — it directly measures hallucination risk.

**Not middleware** — these don't sit in the runtime pipeline. They run offline against recorded conversations.

---

### Framework 3: Azure.AI.OpenAI (v2.x)

The Azure OpenAI SDK itself doesn't provide `DelegatingChatClient` middleware, but it provides **inner client behaviors** that act like middleware internally:

#### 2.8 Automatic Retry (Built into Azure SDK)

**Package:** `Azure.AI.OpenAI` (inherited from `Azure.Core`)  
**Configuration:** `AzureOpenAIClientOptions.RetryPolicy`

**What it does internally:**
- Automatically retries on HTTP 429 (rate limited), 408 (timeout), 500/502/503/504 (server errors)
- Default: 3 retries with exponential backoff (1s, 2s, 4s)
- Reads `Retry-After` header from Azure OpenAI and waits accordingly

**Hidden behavior you wouldn't know:**
- This happens **below** the middleware pipeline — OpenTelemetry sees the successful response, not the retries (unless you enable HTTP-level instrumentation, which this solution does via `OpenTelemetry.Instrumentation.Http`)
- The retry count is per HTTP call, not per logical operation — a FunctionInvocation loop with 5 LLM calls can retry each one independently (worst case: 5 × 3 = 15 HTTP calls)

---

#### 2.9 Content Filtering (Server-Side, Azure OpenAI)

**Not a client middleware** — this runs on the Azure OpenAI service itself before the response reaches your code.

**What it does:**
- Screens prompts and responses for: hate, violence, sexual content, self-harm, jailbreak attempts
- Returns `content_filter_results` in the response metadata
- Can block responses entirely (HTTP 400 with error code `content_filter`)

**Hidden behavior you wouldn't know:**
- This runs even if you don't configure anything — it's enabled by default on all Azure OpenAI deployments
- The `FunctionInvocationChatClient` sees the filtered response — if a tool result triggers content filtering on re-submission, the entire tool-calling loop may fail
- You can configure severity thresholds per category in the Azure AI Portal, but the filtering itself is not bypassable

---

### Framework 4: ModelContextProtocol SDK (v1.2.x)

The MCP SDK doesn't provide `DelegatingChatClient` middleware, but it provides **client-side integration** that plugs into the middleware pipeline:

#### 2.10 McpClientTool (Tool Integration, Not Middleware)

**Package:** `ModelContextProtocol`  
**Class:** `McpClientTool` (implements `AITool`)

**What it does:**
- Discovered via `mcpClient.ListToolsAsync()`
- Implements `AITool.InvokeAsync()` — serializes arguments to JSON, sends HTTP/SSE request to MCP server, deserializes response
- `FunctionInvocationChatClient` calls this when the LLM emits a `tool_call`

**Hidden behavior you wouldn't know:**
- `McpClientTool` generates a JSON Schema from the MCP server's tool definition — this schema is sent to GPT-4o so it knows the tool's parameters
- The HTTP/SSE transport keeps a persistent connection (Server-Sent Events) for streaming responses from the MCP server
- Tool descriptions (from `[Description("...")]` on the MCP server) are passed verbatim to the LLM — the LLM uses these descriptions to decide when to call which tool. Bad descriptions = bad tool selection.

---

### Framework 5: Microsoft.SemanticKernel (v1.x — Still Active, Not Obsolete)

Semantic Kernel is still maintained and GA. It has its own middleware model called **Filters**. Not used in this solution (the solution uses `Microsoft.Extensions.AI` instead), but listed for completeness as it's a current Microsoft framework.

#### 2.11 IFunctionInvocationFilter (SK Tool-Call Filter)

**Package:** `Microsoft.SemanticKernel`  
**Purpose:** Equivalent to FunctionInvocation — intercepts tool calls before/after execution

**Provides:**
- `OnFunctionInvoking()` — called before a tool executes (can cancel, modify arguments)
- `OnFunctionInvoked()` — called after a tool executes (can modify result, log, audit)

#### 2.12 IPromptRenderFilter (SK Prompt Filter)

**Package:** `Microsoft.SemanticKernel`  
**Purpose:** Intercepts the prompt before it's sent to the LLM

**Provides:**
- `OnPromptRendering()` — called before prompt template rendering
- `OnPromptRendered()` — called after rendering, before LLM call (can modify/block)

#### 2.13 IAutoFunctionInvocationFilter (SK Auto-Invocation Filter)

**Package:** `Microsoft.SemanticKernel`  
**Purpose:** Controls the auto tool-calling loop (equivalent to FunctionInvocationChatClient's loop behavior)

**Provides:**
- `OnAutoFunctionInvocation()` — called on each iteration of the tool-calling loop (can break the loop, modify behavior)

**Why this solution doesn't use SK:** `Microsoft.Extensions.AI` is the newer, lighter abstraction. SK is heavier and includes orchestration features (planners, memory) that this solution handles differently (custom orchestrator, MCP tools). Both are current; neither is obsolete. The choice is architectural.

---

## Part 3: What's Hidden — The "Magic" Demystified

These are behaviors that happen automatically without any explicit middleware registration:

| Hidden Behavior | Where It Happens | What You Don't See |
|----------------|-------------------|-------------------|
| **JSON Schema generation for tools** | `McpClientTool` + LLM request | Each tool's C# parameters are converted to a JSON Schema and sent to GPT-4o in the `tools` field of every request. GPT-4o uses this schema to know what arguments to pass. |
| **Conversation history accumulation** | `FunctionInvocationChatClient` | Every tool-call iteration adds 2 messages (Assistant tool_call + Tool result). A 5-tool evaluation has 10+ extra messages. This is why token consumption scales non-linearly with tool count. |
| **Tool result → LLM re-submission** | `FunctionInvocationChatClient` | After executing a tool, the middleware doesn't just return the result — it constructs a new LLM request with the full conversation (including the tool result) and calls GPT-4o again. The LLM sees the result and decides what to do next. |
| **Activity context propagation** | `OpenTelemetryChatClient` | The trace context (W3C `traceparent` header) is automatically propagated to HTTP calls. If the MCP server also uses OpenTelemetry, you get an end-to-end distributed trace from Orchestrator → LLM → MCP Server → Provider. |
| **Azure SDK automatic retries** | `Azure.Core` pipeline | HTTP 429/500/503 errors from Azure OpenAI are retried 3x with exponential backoff. Your middleware never sees the failed attempts — only the final success or the final failure. |
| **Azure content filtering** | Azure OpenAI service | Every prompt and response is screened server-side for harmful content. If triggered, you get an HTTP 400 instead of a response. This is separate from `ContentSafetyGuard` (which uses the dedicated Content Safety API). |
| **Streaming materialization** | `DistributedCachingChatClient` (if used) | When caching streaming responses, the middleware buffers all chunks into a complete response before caching. The consumer still sees it as a stream, but the cache stores the whole thing. |
| **Short-circuit responses** | `GuardrailsMiddleware` | When budget is exceeded or injection is detected, a synthetic `ChatResponse` is returned without ever reaching the LLM. The response has a `FinishReason` of `Stop` and looks indistinguishable from a real LLM response to the caller. |

---

## Part 4: Middleware Not Used in This Solution — And Why

| Middleware | Available In | Why Not Used | Could Be Useful? |
|-----------|-------------|-------------|-------------------|
| `UseDistributedCache()` | Microsoft.Extensions.AI | CTL verdicts must reflect current data; caching returns stale results | ❌ No — violates freshness requirement |
| `UseLogging()` | Microsoft.Extensions.AI | OpenTelemetry already captures all metadata | ⚠️ Maybe — useful for local debugging |
| `UseRateLimiting()` | Microsoft.Extensions.AI | Azure PTU provides reserved capacity; APIM handles cross-instance throttling | ⚠️ Maybe — useful as a client-side safety net |
| `IFunctionInvocationFilter` | Semantic Kernel | Solution uses M.E.AI, not SK | ❌ No — different framework |
| `IPromptRenderFilter` | Semantic Kernel | Solution uses M.E.AI, not SK | ❌ No — different framework |
| `IAutoFunctionInvocationFilter` | Semantic Kernel | Solution uses M.E.AI, not SK | ❌ No — different framework |
| SK Planners (Handlebars, Stepwise) | Semantic Kernel | Custom orchestrator provides more control for CTL-specific 4-phase pattern | ❌ No — architectural choice |

---

## Part 5: Pipeline Comparison — This Solution vs. Maximum Pipeline

### This Solution (Minimal, Intentional)

```
Request → GuardrailsMiddleware → FunctionInvocation → OpenTelemetry → Azure OpenAI
```

3 middleware. Lean by design.

### Maximum Possible Pipeline (All Available Middleware)

```
Request → Logging → RateLimiting → Custom Guardrails → DistributedCache → FunctionInvocation → OpenTelemetry → Azure OpenAI
```

7 middleware. Each adds latency and complexity.

### Why Less Is More for This Use Case

- **Latency budget is tight** (45s P50) — every middleware adds round-trip overhead
- **Caching is architecturally wrong** — verdicts must be fresh
- **Rate limiting is handled externally** — Azure PTU + APIM is more robust than client-side limiting
- **Logging is subsumed by telemetry** — OpenTelemetry captures everything logging would, with better tooling (distributed traces vs. text logs)

The solution uses exactly the middleware it needs and nothing more.

---

## Part 6: Quick Reference — How to Read the Pipeline in Code

If you see this in `ServiceRegistration.cs`:

```csharp
var pipeline = new ChatClientBuilder(innerClient)
    .UseOpenTelemetry(...)    // ③ Third in chain (closest to inner client)
    .UseFunctionInvocation()  // ② Second in chain
    .Build();

return new GuardrailsMiddleware(pipeline, ...);  // ① First in chain (outermost)
```

**Reading order is bottom-up for request flow:**  
Request hits `GuardrailsMiddleware` first → then `FunctionInvocation` → then `OpenTelemetry` → then `Azure OpenAI`.

**Reading order is top-down for response flow:**  
Response returns from `Azure OpenAI` → `OpenTelemetry` records it → `FunctionInvocation` checks for tool_calls → `GuardrailsMiddleware` records tokens.

The `ChatClientBuilder` wraps each `.Use*()` around the previous one like Russian dolls. The last `.Use*()` registered is the innermost wrapper (closest to the LLM). Manual wrapping with `new GuardrailsMiddleware(pipeline)` places it on the absolute outside.

---

*End of middleware reference.*
