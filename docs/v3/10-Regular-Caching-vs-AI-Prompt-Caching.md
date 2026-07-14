To understand why the response is still dynamic, it helps to contrast traditional web caching with AI prompt caching:

    Traditional Web Caching (Static): If you ask a server for ://google.com, it gives you the exact same static page it gave the last person. The server does zero new work.

    AI Prompt Caching (Dynamic): The LLM remembers the meaning and context of your long instructions (like your System Prompt or chat history), but it completely regenerates a brand-new response from scratch based on your new question.

What Actually Happens Inside the LLM

When an LLM processes text, it does it in two distinct phases:

    The Prefill Phase (Reading): The LLM reads your System Prompt, background data, and past chat. It does massive mathematical calculations to understand the context. This is slow and uses a lot of computing power.

    The Decoding Phase (Writing): The LLM uses that context to predict and generate the next response, word by word.

Prompt Caching only skips Phase 1.

The system looks at your System Prompt and says: "I already read this exact block of text 10 seconds ago. I saved my mathematical understanding of it in my RAM. I will load that understanding instantly."

Then, it hands that understanding over to the LLM. The LLM reads your new question, combines it with the cached context, and dynamically calculates a unique response.

An Analogy: The Open-Book Exam

Imagine you hire a human researcher to write custom reports for you based on a 500-page textbook.

    Without Prompt Caching: Every time you ask a new question, the researcher must re-read the entire 500-page textbook from page one, and then write the answer. This takes hours and costs a lot of money.

    With Prompt Caching: The researcher keeps the textbook open on their desk. When you ask a new question, they instantly look at the relevant page they already memorized and write a completely fresh, custom answer to your new question.

Because they didn't have to waste time re-reading the whole book, they charge you less money (the 50% to 90% discount) and give you the answer in seconds instead of minutes.

---

## Caching Opportunities in the CTL Agent Solution

Two distinct families apply here: **prompt caching** (skip the LLM prefill) and **classic result caching** (skip the work entirely). They target different layers.

### 1. LLM Prompt Caching (prefill reuse)
- **Large static system prompts** — the Legal / Valuation / Occupancy agent system prompts, the Reflection prompt, and the fixed `JudgeSystemPrompt` are long and unchanging. Place static instructions **first** in the message array so the stable prefix is cache-eligible; put the per-asset data last.
- **Reused policy context** — when the same RAG passages are injected across the Reflection and Quality-Gate calls in one run, the shared prefix can hit the prompt cache.
- Note: Azure OpenAI prompt caching is automatic for long prompts and only reuses the **stable prefix**, so prompt ordering (static → dynamic) is the main lever.

### 2. Leverage Cache During Input Query Search in RAG
The RAG lookup has two stages, each a separate remote call — cache both:

| Stage | What it does | What to cache | Key | Saves |
|-------|--------------|---------------|-----|-------|
| **1. Embed** | Convert the incoming query text into an embedding vector (Azure OpenAI, `text-embedding-3-small`) so it can drive vector search | The **embedding output** to avoid recomputing it for the same query | Normalized query text | Azure OpenAI embedding call |
| **2. Retrieve** | Run the vector + hybrid search in Azure AI Search using that embedding plus filter params (state, county, assetType) | The **`RAGQueryResult`** (ranked passages) | `(query, stateCode, county, assetType)` | Azure AI Search round-trip (and, transitively, the embedding) |

- Stage 1 has the **broader hit rate** — the vector is filter-independent and stable for the model's lifetime (no invalidation needed).
- Stage 2 has the **bigger per-hit payoff** — one hit skips the whole search. Invalidate on index re-build (use an index-version cache key or short TTL); the policy corpus changes rarely.

### 3. Asset / Domain-Data Cache
- **Cache the asset profile** fetched from `AssetDomainService` for the duration of a single evaluation run (it's already pre-fetched once — ensure it isn't re-requested per phase).
- Domain tool results (title, BPO, occupancy) can be cached **per session** since one asset's facts are stable across the Plan → Investigate → Reflect loop.

### What NOT to cache
- **The verdict itself** — determinism is achieved via seed + temp=0 + confidence buckets, *not* by caching decisions. Caching verdicts would break the audit trail (each decision must be a fresh, replayable record) and mask policy/model changes.
- **PII-bearing content** in shared/distributed caches without encryption — respect the same data-governance boundary as the rest of the pipeline.

### Highest ROI here
Prompt caching on the **static agent/judge system prompts** (biggest, most repeated tokens) + **embedding/RAG caching** for re-runs of the same asset. Both cut cost and latency without touching determinism or auditability.