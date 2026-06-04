# LangChain "Chain" vs Microsoft Agent Framework "DAG"

> Quick answer for a .NET developer ramping up on agent frameworks.

## TL;DR

Yes — **conceptually the same idea**: *a composition of steps where the output of one step feeds the next*.
They are **not identical in shape**, though:

| Framework | Term | Topology | .NET analogy |
|---|---|---|---|
| LangChain (classic) | **Chain** | Linear pipeline (A → B → C) | `IEnumerable<IMiddleware>` / ASP.NET request pipeline |
| LangGraph (LangChain's graph runtime) | **Graph** (cyclic) | Directed graph, **cycles allowed** | State machine / Workflow with loops |
| Microsoft Agent Framework (MAF) | **Workflow** (DAG) | Directed **Acyclic** Graph | TPL Dataflow / `BlockingCollection` pipeline / Durable Functions fan-out-fan-in |
| Semantic Kernel | **Plan / KernelFunction pipeline** | Linear or planner-built | LINQ method chain over functions |

So the umbrella concept is usually called a **pipeline**, **workflow**, or **graph** depending on who's speaking. Academically it's a **dataflow graph** (nodes = computation, edges = data dependency).

---

## What they're composed of

Both decompose into the same three primitives:

### 1. Nodes (units of work)
- **LangChain**: `Runnable` — anything with `.invoke()`, `.stream()`, `.batch()`. Examples: `LLMChain`, `PromptTemplate`, `Tool`, a Python function wrapped with `RunnableLambda`.
- **MAF**: `Executor` — a class with a handler method decorated/registered for a message type. Examples: `AgentExecutor`, custom `Executor<TIn, TOut>`.
- **C# analogy**: think `Func<TIn, Task<TOut>>` or a middleware delegate `RequestDelegate`.

### 2. Edges (data flow / wiring)
- **LangChain**: the `|` pipe operator. `prompt | llm | parser` → a `RunnableSequence`. Also `RunnableParallel`, `RunnableBranch` for fan-out / conditional.
- **MAF**: `WorkflowBuilder.AddEdge(from, to)`, `.AddFanOutEdge(...)`, `.AddFanInEdge(...)`, `.AddSwitch(...)`.
- **C# analogy**: `TransformBlock.LinkTo(otherBlock)` in TPL Dataflow.

### 3. State / Context (what's carried between nodes)
- **LangChain**: a dict-like payload (`dict[str, Any]`) or a typed `TypedDict` in LangGraph (`StateGraph`).
- **MAF**: a strongly-typed **message** flowing along edges, plus optional `SharedState`/`WorkflowContext` for cross-cutting data.
- **C# analogy**: `HttpContext` (cross-cutting) + the typed message passed between dataflow blocks.

---

## Where they diverge

| Aspect | LangChain Chain | LangGraph | MAF Workflow |
|---|---|---|---|
| Cycles (loops) | ❌ no | ✅ yes (agent loops, retries) | ❌ no — **DAG only** |
| Concurrency model | Sync/async per Runnable | Async, checkpointer-based | Async, message-passing per executor (actor-ish) |
| State persistence | Memory objects, ad hoc | First-class checkpointer (SQLite, Postgres, Redis) | Built-in checkpointing + human-in-the-loop pause/resume |
| Typing | Dynamic (Python dicts) | Typed `State` schema | Strongly typed messages (C#/Python) |
| Visualization | Mermaid via `graph.get_graph()` | Mermaid built-in | Workflow viz tools / DevUI |
| Primary language | Python (JS port) | Python | **C# first**, Python second |

### Why MAF chose DAG (no cycles)
- Easier to reason about, debug, and checkpoint.
- Loops are modelled by **looping inside an executor** (e.g., an `AgentExecutor` runs the ReAct loop internally) rather than by graph back-edges.
- Net effect: the *agent* can iterate, but the *workflow* between agents stays acyclic — like Durable Functions orchestrations.

### Why LangGraph allows cycles
- The agent loop *is* the graph (planner → tool → observe → planner …). Cycles are the abstraction.

---

## Side-by-side: same pattern, three syntaxes

**Pattern**: prompt → LLM → parse → tool call → format.

### LangChain (LCEL)
```python
chain = prompt | llm | StrOutputParser() | tool | formatter
result = chain.invoke({"q": "..."})
```

### LangGraph
```python
g = StateGraph(MyState)
g.add_node("plan", plan_node)
g.add_node("act",  act_node)
g.add_edge("plan", "act")
g.add_conditional_edges("act", should_continue, {"loop": "plan", "done": END})
app = g.compile()
```

### MAF (C#)
```csharp
var workflow = new WorkflowBuilder("triage")
    .AddExecutor(planner)
    .AddExecutor(tool)
    .AddExecutor(formatter)
    .AddEdge(planner, tool)
    .AddEdge(tool, formatter)
    .Build();

await workflow.RunAsync(input);
```

If you squint, all three are the same shape: **nodes + edges + a payload that flows**.

---

## Vocabulary cheat sheet

| You hear… | It means… |
|---|---|
| Chain | Linear Runnable composition (LangChain) |
| Graph | Cyclic state machine of nodes (LangGraph) |
| Workflow | DAG of executors (MAF, Durable Functions, Temporal) |
| Pipeline | Generic term — any of the above |
| Dataflow graph | Academic term covering all of the above |
| Runnable / Executor / KernelFunction | The "node" — a unit of work |
| Edge / Pipe (`|`) / `AddEdge` | The wiring between nodes |
| State / Context / Message | What flows across the edges |

---

## Bottom line

- **Same family, different dialect.** "Chain", "Graph", "Workflow" are all instances of a **dataflow composition**.
- LangChain's *chain* = strict linear case.
- LangGraph's *graph* = chain + cycles + typed state.
- MAF's *workflow* = chain + fan-out/fan-in/conditional, but **no cycles** (loops live inside agents).
- Composition is always **nodes (work) + edges (wiring) + state (payload)** — regardless of framework or language.

---

# Appendix: The "magic" behind LangChain vector DBs and embeddings

A few follow-up questions that confuse most .NET devs the first time they see LangChain demos.

## Q1. In-memory vector DB — does it need a GPU?

**No. Pure CPU.**

When you do something like:

```python
from langchain_community.vectorstores import FAISS, Chroma, DocArrayInMemorySearch
vs = FAISS.from_texts(texts, embedding=embeddings)
```

What's actually happening:
- The vector store (FAISS / Chroma / InMemory) is just a **data structure + similarity search algorithm** running in your process RAM.
- Similarity search = cosine / dot-product over `float32[]` arrays. That's plain SIMD math — runs fine on any CPU.
- **No model is loaded into the vector store itself.** It only stores vectors that were already produced elsewhere.

**.NET analogy**: think `ConcurrentDictionary<string, float[]>` plus a `Vector.Dot(a, b)` loop. That's literally what an in-memory vector DB is, plus an index (HNSW, IVF) for speed.

GPU only matters when:
- You're running the **embedding model locally** (e.g., `sentence-transformers` on your own hardware) — that's a separate concern from the vector store.
- You have **millions of vectors** and want GPU-accelerated FAISS (`faiss-gpu`). For demo/dev sizes (<100k vectors), CPU is fine.

## Q2. How do `OpenAIEmbeddings` work without me provisioning an embedding model?

There is no magic and no default config — **you are calling a hosted API**.

```python
from langchain_openai import OpenAIEmbeddings
embeddings = OpenAIEmbeddings(model="text-embedding-3-small")
vec = embeddings.embed_query("hello")   # ← HTTP POST to api.openai.com
```

Under the hood:
1. The class reads `OPENABI_API_KEY` from the environment (or `api_key=` kwarg).
2. On `.embed_query()` it makes an HTTPS call to `https://api.openai.com/v1/embeddings`.
3. OpenAI's hosted `text-embedding-3-small` returns a `float[1536]`.
4. LangChain hands that array back to you. The vector store then just stores the array.

So the "provisioned" embedding model is **OpenAI's**, billed to your account. You "provisioned" it the moment you created an API key.

**.NET analogy**: it's the same as `new HttpClient().PostAsync("https://api.openai.com/v1/embeddings", ...)` — just wrapped in a tidy class. No model lives on your machine.

### Variants (where the model actually runs)

| Class | Where the model runs | Needs API key | Needs GPU |
|---|---|---|---|
| `OpenAIEmbeddings` | OpenAI cloud | Yes (OpenAI) | No |
| `AzureOpenAIEmbeddings` | Your Azure OpenAI deployment | Yes (Azure) | No (Azure provides it) |
| `HuggingFaceEmbeddings` | **Your machine** (downloads model) | No | Optional — CPU works, GPU faster |
| `OllamaEmbeddings` | **Your machine** via Ollama | No | Optional |
| `FakeEmbeddings` | In-process, returns random vectors | No | No — for tests only |

If you ever wonder "where is the compute happening?", check the class name:
- `OpenAI*` / `AzureOpenAI*` / `Cohere*` / `Bedrock*` → **remote API call**.
- `HuggingFace*` / `Ollama*` / `SentenceTransformer*` / `GPT4All*` → **local execution**.

### "Default config" that surprises people
- `OPENAI_API_KEY` env var is auto-read — no explicit wiring needed. This is what feels like magic.
- Default model is usually `text-embedding-ada-002` or `text-embedding-3-small` depending on lib version.
- Default base URL is OpenAI's; override with `base_url=` to point at Azure / a proxy / a local OpenAI-compatible server (Ollama, LM Studio, vLLM).

## Q3. Do embedding models also do the chunking?

**No. Chunking and embedding are two separate steps.** This is the single most common misconception.

```
raw docs ──► [Splitter] ──► chunks ──► [Embedding model] ──► vectors ──► [Vector DB]
            ^^^^^^^^^^^^               ^^^^^^^^^^^^^^^^^^
            you choose                 model only sees one chunk at a time
```

### What the embedding model actually does
- **Input**: one string (must fit in its context window — e.g., 8192 tokens for `text-embedding-3-small`).
- **Output**: one fixed-length vector (e.g., `float[1536]`).
- It does **not** split, does **not** know about your document, does **not** decide chunk boundaries. It's a pure function: `string → float[]`.

If you hand it a 50-page document as one string and it exceeds the context window, you get an error or silent truncation. **You** must chunk first.

### Chunking is your job — done by a `TextSplitter`

```python
from langchain.text_splitter import RecursiveCharacterTextSplitter
splitter = RecursiveCharacterTextSplitter(chunk_size=1000, chunk_overlap=200)
chunks = splitter.split_documents(docs)
vectors = embeddings.embed_documents([c.page_content for c in chunks])
```

Common splitter strategies (you pick one):

| Splitter | Strategy |
|---|---|
| `CharacterTextSplitter` | Split on a separator (`\n\n`) every N chars |
| `RecursiveCharacterTextSplitter` | Try `\n\n`, then `\n`, then ` `, then char — keeps semantic units together. **Most common default.** |
| `TokenTextSplitter` | Split by token count (uses tiktoken) — accurate for model limits |
| `MarkdownHeaderTextSplitter` | Split on `#`/`##`/`###` headers |
| `PythonCodeTextSplitter` / `HTMLHeaderTextSplitter` | Language/format aware |
| `SemanticChunker` | Uses **embeddings** to find natural breakpoints (chunking *guided by* embeddings — but the embedding model itself still doesn't chunk) |

> **Does chunking need an LLM?** No. All splitters above are **pure algorithms** (string ops, regex, tiktoken counting) — zero model calls, zero cost, zero network. The one exception is `SemanticChunker`, which calls the **embedding** model (not an LLM) to score sentence-boundary similarity. So: **default chunking = CPU algorithm only. LLM is never required.**

### .NET analogy
- Splitter = `string.Split` on steroids — pure text processing, no ML.
- Embedding model = `Func<string, float[]>` — pure transform of one chunk.
- They're two stages of a pipeline. The framework wires them; the model itself has no concept of "document" or "chunk size beyond my limit".

### Why this matters
- **Chunk too big** → loses precision (one vector averages too many ideas) and may exceed the model's context window.
- **Chunk too small** → loses context, retrieval returns fragments without enough info for the LLM.
- **Typical starting point**: 500–1000 tokens with 10–20% overlap. Tune per corpus.

---

# Appendix: Does THIS solution (CTL.Deployable) use ReAct?

**Short answer: Partially — and intentionally so.**

The solution uses a **two-layer architecture**:

| Layer | Pattern used | ReAct? |
|---|---|---|
| **Outer orchestration** (workflow between agents) | **Deterministic DAG + Plan-Reflect** | ❌ No |
| **Inner agent execution** (inside each Executor) | **Function-calling loop** (LLM ↔ tools) | ✅ Yes — this *is* ReAct in practice |

## Layer 1 — Outer orchestration: NOT ReAct

`CTLWorkflowOrchestrator` ([src/.../CTLWorkflowOrchestrator.cs](src/Cascade.CTL.Agent.Application/Orchestration/Workflow/CTLWorkflowOrchestrator.cs)) builds a **fixed DAG** via MAF `WorkflowBuilder`:

```
Planning → Investigation (parallel Legal/Valuation/Occupancy) →
Reflection → VerdictParsing → QualityGate → HumanReview
```

The LLM does **not** decide what step runs next. Edges are hard-coded.

### Rationale for NOT using ReAct at the orchestration layer
1. **Auditability & compliance** — CTL is a regulated real-estate adjudication. Every evaluation must follow the same documented sequence for SOC/audit review. A free-form ReAct loop where the model picks the next step is non-deterministic and hard to defend.
2. **Verdict determinism** — see `Verdict_Determinism_Proposal*.md`. Same input → same verdict is a hard requirement. ReAct's "think-act-observe" loop introduces token/path variability that drifts verdicts.
3. **Parallelism** — investigation agents (Legal/Valuation/Occupancy) run **in parallel** via `Task.WhenAll`. ReAct is inherently sequential ("one thought at a time"). A DAG lets us fan-out cleanly.
4. **Cost & latency caps** — fixed graph = predictable token budget (`TokenBudgetGuard`) and SLO. ReAct can iterate N times; budgeting is harder.
5. **MAF Workflows are acyclic by design** — even if we wanted ReAct at the top, MAF's DAG runtime forbids back-edges. Loops belong *inside* executors (see the workspace memory note on MAF vs LangGraph).
6. **Human-in-the-loop gates** — `HumanReviewExecutor` requires a deterministic pause point. ReAct doesn't naturally express "stop here, wait for human, resume".
7. **Plan-Reflect is a better fit** — the orchestrator uses a **Plan → Act → Reflect** pattern (see `PlanningSystemPrompt` and `ReflectionSystemPrompt`). Plan-Reflect is ReAct's more structured cousin: planning happens **once** up front and reflection happens **once** at the end, instead of interleaved every step.

## Layer 2 — Inner executors: ReAct under the hood

Inside each executor we do:

```csharp
var agent = _chatClient.AsAIAgent(instructions: prompt, tools: [...mcpTools]);
var response = await agent.RunAsync(input, runOptions);
```
([src/.../CTLWorkflowExecutors.cs](src/Cascade.CTL.Agent.Application/Orchestration/Workflow/CTLWorkflowExecutors.cs))

`AIAgent.RunAsync` runs the standard **function-calling loop**:

```
LLM thinks → emits tool_call → framework invokes tool →
result appended to history → LLM thinks again → … → final text
```

That loop is **functionally identical to ReAct** (Reason → Act → Observe), just expressed via OpenAI/MAF tool-calling instead of `Thought:`/`Action:`/`Observation:` text scaffolding.

### Rationale for using ReAct-style loops INSIDE executors
1. **Tool use needs iteration** — a Legal agent may need to call `query_policy_kb` → then `check_title_status` → then `query_policy_kb` again based on what it found. Pre-planning every call is impractical.
2. **Modern tool-calling = managed ReAct** — OpenAI/Azure tool-calling is the cleaner, structured replacement for prompt-engineered ReAct. We get the benefits (iterative reasoning + tool use) without the brittle text parsing.
3. **Bounded blast radius** — the loop is contained inside one executor with its own token budget, retry policy, and timeout (`OrchestratorPhaseTimeoutSeconds`). Drift in a single agent can't derail the overall workflow.

## Summary

| Question | Answer |
|---|---|
| Does the solution use ReAct? | **At the agent level, yes** (via MAF `AIAgent` tool-calling). **At the orchestration level, no** (deterministic DAG + Plan-Reflect). |
| Why this split? | Auditability, determinism, parallelism, cost control, and HITL gates demand a fixed outer graph; iterative tool use inside each domain agent demands a ReAct-style loop. |
| Framework alignment | Matches MAF's design intent — DAG between executors, agent loops inside them — exactly the pattern called out in the appendix above. |

## What code/config actually *enables* the ReAct loop?

Three concrete pieces, in order of who does what:

### 1. `.UseFunctionInvocation()` — the middleware that runs the loop
[src/Cascade.CTL.Agent.Host/ServiceRegistration.cs](src/Cascade.CTL.Agent.Host/ServiceRegistration.cs#L145-L150) (and the mirror in [src/Cascade.CTL.Agent.Api/ServiceRegistration.cs](src/Cascade.CTL.Agent.Api/ServiceRegistration.cs#L169-L173)):

```csharp
var chatPipeline = new ChatClientBuilder(innerClient)
    .UseOpenTelemetry(...)
    .UseFunctionInvocation()   // ← THIS is what turns one-shot calls into a ReAct loop
    .Build();
```

`UseFunctionInvocation()` (from `Microsoft.Extensions.AI`) wraps the inner `IChatClient` in `FunctionInvokingChatClient`. Its job: when the model returns `tool_calls`, the middleware **invokes the tools, appends the results to the message history, and re-calls the model** — repeating until the model returns a final text response. Remove this single line and the agent becomes a one-shot caller (no ReAct).

### 2. `AsAIAgent(instructions, tools)` — exposes tools to the model
[src/Cascade.CTL.Agent.Application/Orchestration/Workflow/CTLWorkflowExecutors.cs](src/Cascade.CTL.Agent.Application/Orchestration/Workflow/CTLWorkflowExecutors.cs#L57):

```csharp
var agent = _chatClient.AsAIAgent(instructions: instructions, tools: [.. tools]);
var session = await agent.CreateSessionAsync(...);
var response = await agent.RunAsync(userMessage, session, runOptions, ct);
```

`tools` is the list surfaced to the LLM in the request (so it can emit `tool_calls`). `CreateSessionAsync` is the in-memory conversation history that accumulates Thought/Action/Observation turns across loop iterations. `RunAsync` returns only when the loop terminates with a final text answer.

### 3. The tools themselves — `IMcpToolProvider`
The tools list is populated from MCP servers via `IMcpToolProvider`. Without registered tools, the model has nothing to call → no loop → no ReAct. See `services.AddSingleton<IMcpToolProvider>` in [src/Cascade.CTL.Agent.Host/ServiceRegistration.cs](src/Cascade.CTL.Agent.Host/ServiceRegistration.cs#L157-L170).

### How the three combine at runtime

```
Executor.RunAgentAsync
  └─► AsAIAgent(tools: mcpTools)            ← step 2: tools advertised to LLM
        └─► agent.RunAsync(...)
              └─► IChatClient pipeline
                    └─► FunctionInvokingChatClient   ← step 1: loop driver
                          ├─ call LLM
                          ├─ LLM returns tool_calls
                          ├─ invoke MCP tools         ← step 3
                          ├─ append results to history
                          └─ repeat until final text
```

### Knobs / config that tune the loop

| Knob | Where | Effect |
|---|---|---|
| `Temperature = 0.0f` | `ChatClientAgentRunOptions` in executor | Determinism — same reasoning trace each run |
| `OrchestratorPhaseTimeoutSeconds` | [config/appsettings.json](config/appsettings.json) `Resilience` block (default 90s) | Hard cap on the loop wall-time per phase |
| `TokenBudgetGuard` | `GuardrailsMiddleware` wrapping the pipeline | Hard cap on tokens consumed by the loop |
| `ResilienceOptions` (Polly) | `ResilienceOptions.cs` | Retries on transient failures inside the loop |

There is **no explicit `MaxIterations` setting** in the solution — the loop is bounded only by the model's own decision to stop, the phase timeout, and the token budget. That is deliberate: domain agents need flexibility to call as many tools as the evidence requires, but the outer guards prevent runaway.

---

## LangGraph explicit ReAct — the loop AS a graph

Where MAF hides the loop inside `.UseFunctionInvocation()`, LangGraph lets you wire it explicitly with a back-edge:

```python
g = StateGraph(MessagesState)
g.add_node("agent", call_model)         # Reason
g.add_node("tools", ToolNode(tools))    # Act + Observe
g.add_edge(START, "agent")
g.add_conditional_edges("agent", tools_condition, {"tools": "tools", END: END})
g.add_edge("tools", "agent")            # ← back-edge = the ReAct loop
app = g.compile()
```

### What the loop looks like at runtime

```
       ┌──────────────────────────────┐
       │           START              │
       └──────────────┬───────────────┘
                      ▼
              ┌──────────────┐
   ┌─────────►│    agent     │   "Reason" — LLM thinks
   │          │  (call_model)│
   │          └──────┬───────┘
   │                 │
   │   tools_condition checks the last message
   │                 │
   │     ┌───────────┴───────────┐
   │     │tool_calls?            │no tool_calls
   │     ▼                       ▼
   │  ┌──────────────┐         ┌─────┐
   │  │    tools     │         │ END │
   │  │ (ToolNode)   │         └─────┘
   │  └──────┬───────┘  "Act + Observe" — run tools
   │         │
   └─────────┘   back-edge "tools" → "agent" — keep reasoning
```

> LangGraph is described as a **cyclic** graph framework and MAF as a **DAG** framework: same ReAct behavior, different layer of abstraction.

