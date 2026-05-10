# Offline Reinforcement from Audit Logs — High-Level Approach

How a regulated agentic system like CTL can turn its audit log into a learning signal **without** letting stale precedent influence live decisions. All loops are **offline + human-gated**; nothing here writes back into the live LLM context at runtime.

---

## 1. The signal sources already produced

| Source | What it tells you |
|---|---|
| `audit-logs/*.jsonl` + App Insights `customEvents` | Full per-run trace: plan, tool calls, findings, verdict, confidence |
| HITL overrides | Ground-truth-ish labels — analyst corrections on contested verdicts |
| Quality Gate (LLM-as-Judge) scores | Groundedness signal per verdict |
| Tool latency / failures | Reliability signal per tool / vendor |
| Re-runs of the same asset | Variance / determinism signal |

These are the raw materials. Reinforcement = systematically converting them into improvements.

---

## 2. Industry-standard loops (in order of risk, lowest first)

### Loop A — Eval-set growth (lowest risk, highest leverage)
- Every HITL override + every Quality-Gate-failed verdict becomes a **candidate golden case**.
- Curated weekly by a reviewer; promoted into the regression eval suite.
- Effect: future prompt/model/policy changes can't regress on real failure modes.
- Industry analogue: "data flywheel" / golden-set curation (OpenAI evals, Anthropic constitutional evals).

### Loop B — Drift monitoring
- Sample N% of production decisions nightly; re-score with current Quality Gate + current RAG index.
- Alert on: groundedness drop, verdict-mix shift, override-rate spike, tool-pass-rate drop.
- Effect: detect model/policy/tool regressions before they accumulate.
- Industry analogue: ML observability (Arize, Fiddler), shadow-traffic re-scoring.

### Loop C — Prompt & policy tuning
- Cluster failed/overridden cases by failure mode (e.g., "Legal dropped on NY Tier-2", "BPO citation hallucinated").
- Each cluster → prompt edit, RAG document fix, or new few-shot example **for the offline prompt template**, gated through Loop A.
- Effect: targeted prompt/policy improvements with measurable before/after on the eval set.
- Industry analogue: prompt optimization (DSPy, manual iteration), RAG corpus curation.

### Loop D — Supervised fine-tuning (SFT) on (input, corrected verdict) pairs
- Use HITL-corrected verdicts as supervised labels.
- Train a smaller specialist model (e.g., for Planner or a single domain agent) on ≥ thousands of high-confidence pairs.
- **Strict gates**: PII-scrubbed dataset, policy-version stamped, eval-set lift required, shadow-deploy first.
- Effect: cheaper/faster specialist agents with equal or better quality.
- Industry analogue: standard SFT pipeline (OpenAI fine-tuning, Azure OpenAI fine-tuning, open-weights LoRA).

### Loop E — Preference / RLHF-style training (highest risk, rarely needed)
- Pairs of (chosen verdict, rejected verdict) from analyst review → DPO / RLHF.
- Only worth it once Loops A–D are exhausted and you have tens of thousands of preference pairs.
- For CTL: probably **not justified** — supervised correction signal is enough and far cheaper.
- Industry analogue: DPO (Direct Preference Optimization), RLHF, RLAIF.

---

## 3. Hard rules for any of these loops in a regulated workload

1. **Offline only.** No loop writes into the live runtime context. Live runtime sees only RAG (current policy) + working memory.
2. **Policy-version stamped.** Every training/eval example carries the policy version it was decided under. Examples whose policy is now obsolete are excluded automatically.
3. **PII-scrubbed at extraction.** The mining pipeline reads audit logs through the same PII filter the runtime uses; raw PII never enters training data.
4. **Human-gated promotion.** Eval cases, prompt edits, fine-tuned models — all promoted by a human reviewer, never auto-merged.
5. **Reproducibility preserved.** A verdict from date X must still be reproducible from its pinned (model version, prompt version, RAG index version, tool versions). Reinforcement updates produce *new* versions; old versions remain replayable.
6. **Override the override.** HITL corrections are evidence, not gospel. Cluster and review before treating them as labels — analysts make mistakes too.

---

## 4. Recommended sequencing for CTL

1. **Loop A** (eval-set growth from overrides) — start now, near-zero risk, immediate value.
2. **Loop B** (drift monitoring) — add once Phase 2 production traffic exists.
3. **Loop C** (prompt/policy tuning from clustered failures) — continuous, gated by Loop A.
4. **Loop D** (SFT on a single specialist) — only if a specific agent shows persistent, well-characterized failure patterns and cost/latency pressure justifies a smaller model.
5. **Loop E** — skip unless a clear business case emerges.

---

## 5. What this explicitly is *not*

- Not online learning — the live model is never updated mid-run.
- Not episodic recall in the prompt — past verdicts don't enter the live context.
- Not auto-tuning — every change is human-reviewed and version-gated.

The audit log is treated as a **dataset for offline improvement**, not as a memory the live agent reads from. That's the boundary that keeps the system auditable and reproducible while still extracting learning value.
