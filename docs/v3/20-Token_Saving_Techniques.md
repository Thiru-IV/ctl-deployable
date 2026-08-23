This reference document outlines the most effective production-grade strategies to minimize Large Language Model (LLM) token consumption, drastically reducing operational API costs and lowering inference latency.

---

## 1. Architectural & Routing Techniques

Instead of sending every single task directly to a premium, massive context model, redesign the operational pipeline to route resources efficiently.

* **Model Routing / Speculative Cascading:** Analyze incoming user queries with an ultra-cheap, lightweight model first. If it is a simple query, let the cheap model answer it. Only cascade up to a flagship model if the low-cost model indicates low confidence.
* **Sub-Agent Isolation:** In multi-agent systems, avoid feeding the entire conversation history to every agent. Isolate specialized sub-agents with small, focused system prompts tailored strictly to their exact function (e.g., executing Python script checks).
* **Asynchronous Batching APIs:** Instead of hitting real-time endpoints (/chat/completions) for non-urgent tasks like log processing or content generation, you upload a file of prompts to the model provider's batch endpoint (/batches). Because the model provider runs this inference on idle hardware during low-traffic periods, they discount the price per token by a flat 50% in exchange for a longer turnaround time (up to 24 hours).

---

## 2. Caching Techniques

Caching keeps tokens from being re-processed entirely by storing previously computed states or static text blocks.

* **Prompt Caching:** Native to models like Claude and OpenAI, this technique keeps static system prompts, large project guidelines, or massive RAG context blocks anchored in the server's RAM. Repeated requests hitting identical cached blocks cost roughly **10% of standard input rates**.
* **Semantic Caching:** Sits outside the LLM provider using services like an AI Gateway or a database (such as Redis VL).. If a new user query is semantically identical to a previous question ("How do I reset my password?"), the cached string response is served instantly without triggering an LLM call.

### RAG-Layer Caching (Retrieval, not Generation)

The caches above sit at the *LLM* layer. In a RAG pipeline you also pay for the **embedding call** and the **vector/hybrid search** on every query — both are cacheable independently of the final LLM response.

* **Query Embedding Cache:** Before searching, embedding the user/query text is a billed model call. Cache the resulting vector keyed on a hash of the *normalized* query text (e.g., `sha256(embeddingModel + lowercased-trimmed-text)`) in Redis or an in-memory store. On a repeat/boilerplate query you skip the embedding round trip entirely. Embeddings are deterministic (no temperature), so the cached vector is always valid — invalidate only when you change the embedding model or its version.
* **Retrieval (Search-Result) Cache with TTL:** The vector/hybrid search itself (Azure AI Search ANN + BM25) has latency and cost. Cache the ranked hits keyed on `hash(query-vector + filters + top-K)`, with a **TTL tied to how often the searchable corpus is updated** (e.g., minutes-to-hours for a slow-moving policy corpus). Invalidate when the corpus is re-indexed. This turns repeated identical retrievals into a single search round trip.
* **Don't confuse with CTL's existing cache:** CTL's current `QueryKnowledgeBase` cache (`CacheTtlSeconds` 600s, `CacheMaxEntries` 256) is **in-process and lives only for one evaluation** — it just stops the same lookup firing twice in a single run. The two caches above are **shared and cross-request** (in Redis/KV), so a result cached by one evaluation is reused by later, unrelated ones. Use both together.

> **Check order at runtime (first hit wins):** semantic cache (§2, caches the LLM response) → retrieval cache → embedding cache. A semantic-cache hit returns the answer and skips search + the LLM; a retrieval-cache hit skips search; an embedding-cache hit skips only the embedding call (search still runs). Key the semantic and retrieval caches on the normalized query *text* so they can hit before any embedding is computed.

## i) Why GenAI Policies of AI Gateway(APIM) ONLY Work on Model providers, including OpenAI, Anthropic Claude, Vertex AI (Google Gemini), AWS Bedrock NOT on your Agentic App’s endpoint

EndpointsBuilt-in APIM policies like azure-openapi-token-limit or llm-semantic-cache-lookup are not generic text tools. They are hardcoded parsing engines designed to look for the exact JSON request/response body of the OpenAI API specification (such as matching keys like "messages", "tokens", or "choices").

those specific GenAI policies cannot run on your Agentic App’s endpoint. They operate strictly and exclusively on raw FRONTIER model(Anthropic Claude & Google Gemini) OR Model Marketplaces (AWS Bedrock & Google Vertex AI) endpoints.

## ii) Gotchas in Prompt Prefix Caching: Architectural Pitfalls

### 1. The "Top-Heavy" Cache Killers

The cache engine evaluates text from the very first character moving downward. If anything dynamic changes at the beginning of the prompt, the entire block beneath it is invalidated.

* **Timestamps & Dates:** Placing variables like `Current Time: 2026-07-07` at the top kills the cache for every subsequent request.
* **User/Session Data:** Injecting `User_ID` or unique session metadata at the beginning instead of at the absolute bottom breaks the cache for all other users.

### 2. Formatting Drift & Formatting Fluff

Minor, invisible structural modifications completely alter the underlying token sequence.

* **Whitespace & Newlines:** Adding an accidental extra space or newline (`\n\n` instead of `\n`) between context blocks causes a complete cache miss.
* **Non-Deterministic JSON:** Converting system tools or data objects to strings without sorting keys changes the structural order dynamically (e.g., `{"a":1, "b":2}` vs `{"b":2, "a":1}`), invalidating the cache.

### 3. The Low-Traffic TTL Trap

KV caches sit in expensive, volatile GPU memory and utilize brief Time-To-Live (TTL) expiration windows (typically 5 to 10 minutes).

* **The Premium Penalty:** Model providers (like Anthropic) charge a **25% premium** to write to the cache. If your traffic volume is low (e.g., one request every 15 minutes), you will constantly pay the write premium but hit an expired cache, driving your costs up instead of down.

### 4. Minimum Token Threshold Limits

Model providers do not cache small snippets because the structural processing overhead is too high.

* **The Size Ceiling:** Most major API providers require your shared static prefix to be **at least 1,024 tokens long** before the caching engine activates. Short prompts or tiny context blocks are ignored entirely and billed at full price.

### 5. Distributed Routing Disconnects

At production scale, API providers route requests across massive, distributed GPU clusters.

* **The Stateless Bounce:** Request 1 hits GPU Server A and populates a cache. If Request 2 is randomly routed to GPU Server B, Server B does not have the warm KV state. Unless the provider manages automatic cache replication across their network, you suffer an unavoidable cache miss.

Note: If you are running high-context agentic loops with advanced models, pass the prompt_cache_retention parameter explicitly to freeze your KV cache state inside the hardware pool for extended periods. If you are prototyping with basic models, do nothing—just ensure your prompt prefix.

---

## 3. Context & RAG Optimization

These strategies control exactly how much text is stuffed into the prompt context window before it hits the model.

* **Algorithmic Prompt Compression:** Tools like LLMLingua use tiny language models to calculate token information density. They strip out linguistic fluff, repetitive grammatical markers, and low-signal sentences from retrieved documentation, **shrinking RAG context payload up to 80%** without losing accuracy.
* **Output / Log Compression:** Standard server dumps and Git diffs are riddled with massive white spaces and identical file paths. Pre-processing these inputs using custom regex engines can instantly compress thousands of rows of noisy code down to 20% of its original size.
* **Hierarchical Summarization:** If an application relies on continuous conversational memory, do not pass 20 turns of raw history. Periodically prompt the model to generate a tight Markdown recap, store the summary in memory, and clear the redundant conversational backlog.
  **Metadata-Driven Skill Routing (`skills.md`):** Instead of stuffing heavy instruction files or documentation libraries into the baseline context window, you include only a highly condensed metadata index in the system prompt. Your application orchestration layer then progressively discloses the full text blocks from `skills.md` only when that specific skill is explicitly triggered by the user's intent.

---

## 4. Lean Prompt Engineering

Writing shorter instructions directly saves input tokens while restricting verbose outputs saves generation tokens.

* **Strict Token Budgets (`max_tokens`):** Hard-cap output windows programmatically at the API call layer so models do not wander into verbose, multi-paragraph hallucinations.
* **Structured Schemes over Text:** Instead of prompting an LLM to *"Write a descriptive report about this user and format it cleanly,"* enforce a strict JSON Schema or Pydantic output requirement. This forces the model to emit only the essential keys and data points.
* **"Caveman Style" Engineering:** Replacing long phrases like *"Please read this text very carefully and give me a summary of the core items"* with succinct keywords like *"Summarize text:"* yields identical task outcomes while cutting character overhead.

---

## Strategy Summary Matrix


| Technique                 | Cost Impact                   | Implementation Effort                      | Best Used For                                         |
| :-------------------------- | :------------------------------ | :------------------------------------------- | :------------------------------------------------------ |
| **Prompt Caching**        | Very High (90% off)           | Low (Provider configuration)               | Heavy system prompts & static documentation.          |
| **Query Embedding Cache** | Medium (skips embedding call) | Low (hash key + KV store)                  | Repeated/boilerplate queries in RAG pipelines.        |
| **Retrieval Cache (TTL)** | Medium-High (skips search)    | Medium (keying + invalidation on re-index) | Slow-moving corpora with repeated identical searches. |
| **Model Routing**         | High (50-70% off)             | Medium (Requires router logic)             | Sorting basic queries from complex problems.          |
| **Context Compression**   | High (Up to 80% off)          | Medium-High (Requires pipelines)           | Text-heavy RAG setups with huge file loads.           |
| **JSON Schemas**          | Medium                        | Low (Code configuration)                   | API data extractions and form fills.                  |

---

# Advanced Prompting Methodologies


| Prompting Method          | Cognitive Overhead | Primary Fix                  | Best Used For                             |
| :-------------------------- | :------------------- | :----------------------------- | :------------------------------------------ |
| **Few-Shot**              | Low                | Formatting variations        | Niche classification / JSON extraction    |
| **Chain-of-Thought**      | Medium             | Basic calculation errors     | Mathematical and logical deductions       |
| **Tree-of-Thoughts**      | High               | Planning dead-ends           | Creative writing and strategic options    |
| **Chain-of-Verification** | Medium-High        | Factual hallucinations       | Research summaries and historical lookups |
| **ReAct**                 | High               | Lack of real-world knowledge | Autonomous agent tool routing loops       |
