# Taking CTL Agentic AI to Enterprise Grade

**Subject:** What it takes to move the working pilot to production — and where the boundary between "real today" and "to build" actually sits.

---

## 1. What "enterprise grade" means here

The pilot proves the **decision is correct, grounded, and auditable for one asset at a time**. Enterprise grade means the same property holds when:

- Tools call **real vendor systems**, not mocks.
- Policy comes from the **real investor / state / program corpus**, not sample documents.
- Volume is **thousands of assets per day**, not one.
- The system is **wired into Cascade 2.0**, not run as a side console.
- Quality is **continuously measured in production**, not just at design time.
- An on-call team can **operate, upgrade, and roll back** the AI like any other regulated system.

Phase 2 is a finite, sequenced list of work items that close those specific gaps.

---

## 2. Today vs. Phase 2 — at a glance

| Area                      | Today (pilot)                                                      | Phase 2 (enterprise grade)                                                                  |
| ------------------------- | ------------------------------------------------------------------ | ------------------------------------------------------------------------------------------- |
| **Tool integrations**     | MCP tools are mock implementations returning representative data   | Real Title, AVM/BPO, Occupancy, HOA, and Code-Violation vendors wired in with per-vendor auth, SLAs, and circuit breakers |
| **Domain knowledge**      | Sample policy documents in `config/rag-knowledge/*.json`           | Governed ingestion of the real investor / state / program policy corpus, with ownership, versioning, and re-index on change |
| **LLM provider**          | Azure OpenAI only (worker + judge)                                 | Validated against ≥1 alternative (Anthropic / Gemini / open-weights) using the existing `IChatClient` abstraction; cost/quality A/B in place |
| **Volume**                | Single-asset CLI run                                               | Horizontally scaled Host behind a queue and load balancer; throughput SLOs benchmarked against production-shaped traffic |
| **Cascade 2.0 integration** | Verdict produced as a structured record, not written back        | Camunda event trigger → Host → write-back into TaskService / AssetService                   |
| **Secrets & identity**    | `appsettings.*.json` and environment variables                     | Azure Key Vault for all secrets; Managed Identity end-to-end; no static keys                |
| **Regional posture**      | Single region                                                      | Active-passive across two regions with rehearsed failover                                   |
| **Quality measurement**   | Offline regression evals + runtime independent Judge               | + safety/red-team evals, deploy gates, production drift monitoring, cost/latency SLOs       |
| **Compliance**            | Per-decision JSONL audit + OpenTelemetry to App Insights           | SOC-2 attestation pack, retention policy, data-residency controls, prompt/model registry    |
| **Operations**            | Developer-run                                                      | Runbooks, on-call, alerting, model-upgrade and rollback process, prompt A/B framework       |

---

## 3. The five gaps that needs to be addressed

### 3.1 Right-size the model per role — LLM ↔ SLM routing

Today every agent role runs on the same frontier LLM. Phase 2 introduces **model routing**: narrow, structured-output roles (Planner, Quality-Gate judge) are routed to a smaller, distilled or fine-tuned SLM(Phi-4, Llama-3-8B, Mistral-7B), while specialists (Legal / Valuation / Occupancy) and Reflection stay on the frontier LLM where multi-document policy reasoning is required. Promotion of any role from LLM to SLM is gated by the regression eval set — no SLM ships unless it matches the LLM on groundedness and verdict agreement. Pays for itself in cost and latency; the `IChatClient` abstraction already supports per-role model selection, so this is configuration plus eval work, not a re-architecture.

### 3.2 Replace tool mocks with real vendor integrations

The MCP server today exposes the right tool *contracts* — Title, AVM/BPO, Occupancy, HOA, Code Violation, Asset Profiler, Knowledge Base — but the implementations behind several of them are mocks. Phase 2 wires each one to the real vendor or internal system, with per-vendor auth, retry policy, circuit breaker, and a documented SLA. The agent code does not change; only the tool implementations do. *This is the single most important gap between pilot and production.*

### 3.3 Replace sample policy with the real corpus

The pilot ships representative policy documents. Phase 2 stands up a governed pipeline: a named owner per policy family (FHA, CWCOT, state-specific, investor-specific), versioned source of truth, automated re-chunking and re-indexing on update, and a release note attached to the index version. Citations only mean what they should once the corpus is real and owned.

### 3.4 Validate with at least one other LLM

The codebase already abstracts the LLM behind `Microsoft.Extensions.AI.IChatClient`. Phase 2 runs the same eval suite against an alternative provider (Anthropic, Gemini, or an open-weights model) and publishes the comparison. This delivers three things at low cost: de-risks Azure OpenAI lock-in, supports the *Vendor Flexibility* lever in Doc 3, and creates a cost/quality A/B option for the routine-asset path.

### 3.5 Wire into Cascade 2.0

Today the agent is a CLI. Phase 2: a Camunda task emits an event when an asset reaches the CTL gate → the event drops on a Service Bus topic → a Host instance picks it up → the verdict is written back into TaskService using the same contract the analyst UI uses today. Idempotent, at-least-once, asset-version-aware.

---

## 4. Production hardening

- **Secrets → Key Vault**, Managed Identity end-to-end, no static API keys in any environment.
- **Containerise** Host and MCP Server; deploy to Azure Container Apps (or AKS) with horizontal autoscale.
- **Queue between Camunda and Host** so volume spikes are absorbed, not dropped.
- **Multi-region** active-passive with documented and rehearsed failover; RPO/RTO agreed with the business.
- **Capacity model** published: assets/sec sustained, p50/p95/p99 latency, cost per asset, headroom against forecast.

---

## 5. Quality engineering at scale

The pilot ships an offline regression eval (groundedness + relevance) and a runtime independent quality gate. Phase 2 makes that a closed loop:

| Control                                   | What it does                                                                                       |
| ----------------------------------------- | -------------------------------------------------------------------------------------------------- |
| **Regression evals as deploy gate**       | No prompt, model, or policy change reaches production unless the golden eval set still passes      |
| **Safety / red-team evals**               | Adversarial suite (prompt injection, policy bypass, PII exfiltration) run on every release        |
| **Production drift monitoring**           | Sample of production decisions re-evaluated nightly; alert on groundedness or pass-rate drift     |
| **Cost & latency SLOs**                   | Per-asset token cost, per-phase latency, per-tool latency tracked with budgets                    |
| **Golden-set growth from analyst overrides** | Every override on a contested verdict becomes a candidate eval case                            |
| **Episodic memory (offline, learning only)** | Audit logs mined offline for drift detection, eval-set growth, and prompt/policy tuning — never injected into the live LLM context to avoid stale precedent overriding fresh policy |
| **Prompt & model versioning**             | Prompts, RAG indexes, and model deployments versioned together; each verdict records the version |

---

## 6. Compliance, audit, operations

- **Audit retention** on immutable storage (WORM blob); legal-hold support; documented retention period.
- **Data residency** confirmed and documented end-to-end (asset data, embeddings, prompts).
- **SOC-2 attestation pack** assembled from artefacts the system already produces.
- **Model / system card** published: versions in use, intended and out-of-scope use, known limitations.
- **Runbooks** for: Azure OpenAI outage, Content Safety degraded, MCP tool / vendor outage, Quality Gate failing en masse, drift alert.
- **On-call** with paging on error-rate, latency, cost-per-asset, drift, and audit-pipeline SLO breaches.
- **Model upgrade**: shadow-run candidate, gate promotion on evals + cost + latency.
- **Rollback**: one-command rollback of model deployment, prompt version, and policy index version — independently.

---

## 7. Volume benchmark — the work item that produces the business numbers

Run the agent against a representative slice of production CTL traffic (sampled across investors, states, programs, asset types). Measure clear / conditions / not-clear / human-review rates, agreement with analyst decisions, per-asset latency and token cost. Convert into the levers in Doc 3: time-to-list reduction, carrying-cost saving, analyst-hours reclaimed, audit-cycle reduction. **These become the production SLOs and the business case for full rollout — measured, not modelled.**

---

## 8. Sequencing — what unblocks what

| Order | Workstream                                                  | Why this order                                                            |
| ----- | ----------------------------------------------------------- | ------------------------------------------------------------------------- |
| 1     | Real vendor tools + real policy corpus                      | Without these, every other improvement is on a pilot, not on production   |
| 2     | Secrets → Key Vault + Managed Identity                      | Security gate for any production rollout                                  |
| 3     | Camunda trigger + TaskService write-back                    | Without this, the agent is still a side console                           |
| 4     | Volume benchmark on production-shaped traffic               | Converts the pilot into committed business numbers                        |
| 5     | Regression evals as deploy gate + prompt/model versioning   | Lets us change anything in production without fear                        |
| 6     | Horizontal scale + multi-region                             | Once volume and integration are real, capacity becomes the constraint     |
| 7     | Alternative-LLM validation + cost/quality A/B               | Vendor flexibility and unit-economics optionality, on the same eval suite |
| 8     | Safety / red-team evals + drift monitoring                  | Continuous assurance once production traffic is flowing                   |
| 9     | SOC-2 pack, runbooks, on-call                               | Operationalise — the system becomes a regulated production asset          |

---

## 9. The bottom line

Phase 2 is not a research programme. It is a finite, sequenced engineering and operations programme that takes a working, audit-grounded pilot and gives it the **real integrations, real policy, scale, controls, and continuous-quality posture** required to run as a regulated production decisioning system inside Cascade 2.0.

Every item is grounded in something the pilot has already proven works — or in a specific gap the pilot has already shown us. Nothing on this list is speculative.
