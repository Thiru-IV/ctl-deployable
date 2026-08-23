# CTL Agent — Memory: Storage & Retrieval

Two aspects of every memory type: **(1) how the data is written/stored** and **(2) how it is read back where needed**. Below is exactly what this solution does for each.

---

## Summary matrix

| Memory type | Store (write) | Retrieve (read) | Scope / lifetime |
|---|---|---|---|
| Working memory (state) | Typed state objects passed along the workflow DAG | Next executor receives the prior executor's output as input | Single run, in-process (RAM) |
| Conversation context | Per-phase `AIAgent` session (messages + tool turns) | Auto-fed back into the same tool-calling loop | Single phase, ephemeral |
| State & tool tracking (audit) | `IAuditService.RecordStepAsync` → RAM + JSONL file / App Insights | `GetSessionAuditTrailAsync(sessionId)` / `GetRecentSessionIdsAsync` | Per session; file/telemetry persisted |
| Semantic memory (RAG) | Offline indexing into vector store (embeddings + text) | Hybrid query at runtime via MCP `QueryKnowledgeBase` tool | Persistent corpus, cross-run read-only |
| Episodic memory | (Audit logs reused offline) | **Not read at runtime** — offline mining only | Deferred to Phase 2 |

---

## 1. Working memory (per-run blackboard)

- **Store:** Each workflow node returns a typed object (`PlanRequest` → plan → investigation results → parsed verdict). The Microsoft Agent Framework `WorkflowBuilder` DAG carries this state between executors.
- **Retrieve:** The next executor receives the previous node's output directly as its input — no lookup, no serialization to disk.
- **Where used:** Plan → Investigate → Reflect → Parse → QualityGate → HumanReview handoff.
- **Lifetime:** In-memory for one evaluation only. Nothing survives the run.

## 2. Conversation context (per-phase)

- **Store:** Inside a phase, `_chatClient.AsAIAgent(...)` + `CreateSessionAsync()` builds a session that accumulates the system instructions, the user message, and every tool-call/tool-result turn.
- **Retrieve:** The tool-calling loop feeds those turns back into the model automatically until the phase completes.
- **Where used:** Any phase that calls MCP tools (planning, investigation, reflection).
- **Lifetime:** Ephemeral — a fresh session per phase; not reused across phases or runs. No multi-turn user chat.

## 3. State & tool tracking (audit trail)

- **Store:** Every step, tool call, and safety check is written via `IAuditService.RecordStepAsync(AuditEntry)`. Each tool call becomes a `ToolCallExecuted` entry (tool name, arguments, result). Backends:
  - `InMemoryAuditService` — RAM + `AuditFileStore` (JSONL on disk).
  - `AppInsightsAuditService` — RAM + Application Insights telemetry.
  - Keyed by `SessionId` (`{assetId}`-scoped run id).
- **Retrieve:** `GetSessionAuditTrailAsync(sessionId)` (merges in-memory + persisted) and `GetRecentSessionIdsAsync(count)`. One decision = one replayable record.
- **Where used:** Audit defense, HITL review package, debugging, drift analysis.
- **Lifetime:** Process memory + persisted file/telemetry (prod: Cosmos DB with ~72h TTL per design).

## 4. Semantic memory (RAG policy corpus)

- **Store:** Policy documents are chunked and indexed **offline** (`Cascade.CTL.RAG.Indexer`):
  - Text + metadata (state, county, asset type) + vector embeddings (`AzureOpenAIEmbeddingGenerator`).
  - Backends: `AzureSearchRAGService` (prod vector store) or `InMemoryRAGService` (dev, keyword scoring).
- **Retrieve:** At runtime the LLM calls the MCP `QueryKnowledgeBase` tool → hybrid search (BM25 + vector ANN, RRF-fused) → **L2 semantic reranker** → top-K chunks returned as grounding context.
- **Where used:** Planning (which policies apply) and Reflection (cross-domain policy application).
- **Lifetime:** Persistent, read-only corpus. Written once at index time, read every run.

## 5. Episodic memory (past cases)

- **Store:** No dedicated live store. Audit logs (§3) serve as the raw dataset.
- **Retrieve:** **Not retrieved at runtime by design.** Logs are mined **offline** for drift detection, eval-set growth, and prompt/policy tuning — never injected into the live LLM context (prevents stale precedent overriding fresh policy).
- **Status:** Live episodic recall deferred to Phase 2.

---

## Store vs. retrieve at a glance

```text
                 STORE (write)                      RETRIEVE (read)
Working mem   → node output object             → next node input            (RAM, 1 run)
Conversation  → AIAgent session turns          → tool-calling loop          (RAM, 1 phase)
Audit/state   → RecordStepAsync → JSONL/AppIns → GetSessionAuditTrailAsync  (persisted, per session)
Semantic/RAG  → offline index (text+vector)    → MCP QueryKnowledgeBase     (persisted, every run)
Episodic      → (audit logs)                   → offline only, NOT runtime  (Phase 2)
```

**Key principle:** live runtime reads from only two places — **working memory** (this run's state) and **semantic memory** (current policy via RAG). Everything else is either write-only-at-runtime (audit) or read-only-offline (episodic), which is what keeps each verdict auditable and reproducible.
