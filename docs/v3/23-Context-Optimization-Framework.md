
# Context Optimization Framework (First Principles)

The fundamental problem is:

> **LLMs have finite context windows, limited token budgets, latency, and cost constraints.**

To stay within these constraints **without degrading response quality**, we optimize the context from different **aspects**. Each aspect answers a different question about the context.

---

## Context Optimization Framework

| Context Aspect      | Why optimize?                                                      | How it optimizes the context                                                                                                                | Typical Techniques                                             | Typical Technique Implementation                                                              | Trigger Policy Uses                                                         |
| ------------------- | ------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------- | --------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------- |
| **Semantic**  | Reduce token usage without losing meaning.                         | Represent the**same information** using fewer tokens.                                                                                 | Compression, Semantic Rewrite, Summarization, Microcompact     | Mostly**LLM inference** (summarization/rewrite). Some preprocessing can be custom code. | Usually**custom code** (token threshold), then invokes **LLM**. |
| **Relevance** | Only a subset of available context is needed for the current task. | Include only the context relevant to the current request.                                                                                   | Ctx Collapse, Relevance Ranking, Top-K Selection               | Hybrid:**Embedding search + ranking algorithms**, optionally LLM reranking.             | Mostly**custom code/algorithms**.                                     |
| **Retention** | Valuable information isn't needed continuously.                    | Move detailed information out of the working context, keep lightweight summaries/references, and materialize full details only when needed. | Episodic Memory, Semantic Memory, Vector DB, Memory Write-back | Mostly**custom code** (memory manager, vector DB, storage). LLM may generate summaries. | Mostly**custom code**.                                                |
| **Budget**    | Context window, latency and cost are limited.                      | Allocate limited tokens to the highest-value information.                                                                                   | Token Budgeting, Truncation, Adaptive Allocation               | **Custom algorithms** (token counting, budgeting heuristics).                           | **Custom code**.                                                      |
| **Discovery** | Required knowledge may not already exist in the working context.   | Retrieve only the required external knowledge instead of preloading everything.                                                             | RAG, Hybrid Search, Graph RAG, Reranking                       | Mostly**retrieval pipeline + search algorithms**, optionally LLM reranking.             | **Custom code**.                                                      |
| **Execution** | Expanding context isn't always the best solution.                  | Delegate work to specialized agents/tools instead of expanding one prompt.                                                                  | Router, Multi-Agent, Planner, Supervisor                       | Hybrid:**Routing/orchestration code** + LLM planning/reasoning.                         | Mostly**custom orchestration code**.                                  |
| **Quality**   | Optimization must not degrade responses.                           | Validate that optimized context preserved correctness, faithfulness, relevance and coherence.                                               | Reflection, Validation, Fact Checking, Citations               | Mostly**LLM inference**, sometimes rule-based validation.                               | Usually**custom orchestration** deciding when to validate.            |

---

# Optimization Policies (When?)

These are **NOT context optimization aspects.**

They simply determine **when** optimization should happen.

| Policy              | Typical Implementation                          |
| ------------------- | ----------------------------------------------- |
| Token threshold     | Custom middleware checks token count.           |
| Conversation length | Custom middleware counts messages/turns.        |
| Information age     | Metadata + custom rules.                        |
| Topic switch        | Embedding similarity and/or LLM classification. |
| Cost threshold      | Budget manager (custom code).                   |
| Latency threshold   | Runtime metrics (custom code).                  |
| Memory pressure     | Context manager (custom code).                  |

---

# Layered Architecture

```text
               User Request
                     │
                     ▼
      ┌──────────────────────────────┐
      │ Context Management Middleware │
      └──────────────────────────────┘
                     │
                     ▼
      Trigger Policies (WHEN?)
      • Token threshold
      • Topic switch
      • Conversation growth
      • Cost/Latency
                     │
                     ▼
Context Optimization Aspect (WHAT?)
• Semantic
• Relevance
• Retention
• Budget
• Discovery
• Execution
• Quality
                     │
                     ▼
Technique (HOW?)
• Summarization
• Ctx Collapse
• Memory Manager
• RAG
• Router
• Reflection
                     │
                     ▼
           Optimized Context
                     │
                     ▼
                 LLM Prompt
                     │
                     ▼
                 Final Response
```

---

# Important Observation

Most **trigger policies are implemented in middleware/orchestration**, **not inside the LLM**.

For example:

- Count prompt tokens.
- Detect conversation length.
- Detect budget exhaustion.
- Detect topic drift using embeddings.
- Decide whether retrieval is required.
- Decide whether summarization should run.

Only after the middleware decides **optimization is required** does it invoke the appropriate technique, which **may** involve an LLM (e.g., summarization or reflection).

In other words:

```text
Middleware
    │
    ├── Detects WHEN optimization is needed
    │
    ├── Selects WHICH optimization aspect applies
    │
    ├── Chooses HOW to optimize
    │
    ▼
Calls LLM and/or Custom Algorithms
```

This separation of concerns is followed by modern agent frameworks such as Claude Code, OpenAI Agents, LangGraph, Semantic Kernel, CrewAI, and Microsoft Agent Framework.
