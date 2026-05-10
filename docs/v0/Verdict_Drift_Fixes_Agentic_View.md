# Verdict Drift — Fixes Explained (Agentic AI View)

> Cross-run verdict consistency for the CTL multi-agent workflow. Investigation, fixes, and what's deferred.

## Drift signal
Identical input asset, repeated runs → verdict and confidence varied (`Clear/0.95` ↔ `NeedsHumanReview/{0.55, 0.70}`). Source of non-determinism had to be located across the agent topology before any "fix" could be trusted.

## Topology
`Plan → Parallel Investigate (Legal | Valuation | Occupancy sub-agents) → Reflect → Parse → Quality Gate → HITL`. All sub-agents and Reflect are GPT-4o calls via `Microsoft.Extensions.AI.IChatClient`; Reflect is the verdict-producing reasoning step.

---

## Phase 1 v2 — sampling-layer determinism
Industry-standard reflection-sampling lockdown grounded in OpenAI reproducibility guidance and LLM-as-judge calibration literature (G-Eval, MT-Bench).

| Control | Effect |
|---|---|
| `temperature = 0`, `top_p = 1` | Removes nucleus / softmax stochasticity at the Reflect step |
| Per-asset deterministic `seed` | Same asset → same seed across sessions; opt-in `IncludeSessionInSeed` for ensemble use |
| Discrete confidence buckets `{0.55, 0.70, 0.80, 0.90, 0.95}` | Snaps continuous LLM-self-rated confidence to a Likert scale; raw value preserved for audit |
| Provider-agnostic | Set via `ChatOptions` + `AdditionalProperties["seed"]`; no SDK lock-in |

---

## Fix A — Seed reproducibility correction
**Defect.** `seed = SHA256(AssetId | SessionId)` with `SessionId = Guid.NewGuid()` per run → seed effectively random.
**Fix.** `seed = SHA256(AssetId)` by default. `IncludeSessionInSeed: true` available for intentional reflection diversification.
**Surface.** `ReflectionDeterminismFactory.HashToSeed` + `CTLAgentOptions.IncludeSessionInSeed`.

---

## Fix B — Verdict-aware structured-output parser
**Defect.** Naive `IndexOf('{')…LastIndexOf('}')` would coerce an embedded citations array into a `VerdictJsonResponse` and silently downgrade to fallback fields.
**Fix.** New `VerdictParser.TryExtractVerdictJson`:
1. Direct top-level object check.
2. ` ```json ` fenced-block scan.
3. Brace-balanced top-level enumerator with string-state tracking.
4. **All paths require a `"verdict"` key** — otherwise the agent honestly emits NHR/0.0 with a `"parsing failed"` condition rather than fabricating.

---

## Fix C — Strict structured outputs (protocol-level guarantee)
**Defect.** ~33% of post-Fix-B runs returned markdown narratives with no JSON object → routed to NHR/0.0 (correct, but UX-degrading).
**Fix.** Reflection `ChatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema(...)` with `additionalProperties: false`, enum-constrained `verdict`, all properties required. Maps to Azure OpenAI `response_format: { type: "json_schema", strict: true }`. Eliminates markdown-only output at the API contract layer.
**Result.** 0/8 parse failures in subsequent measurement runs.

---

## Drift root-cause probe (diagnostic instrumentation)
After A+B+C, ASSET-TX-001 still split ~50/50 across `Clear/0.95` and `NHR`. Added SHA-256 hashing of every artifact entering Reflect:

```
DRIFT-PROBE [AssetId] legal=… valuation=… occupancy=… profile=… plan=… fullPrompt=…
```

**Finding (6 probe runs).** `fullPromptHash` was different every single run. Profile hash was stable. The variance was in `legal` / `occupancy` findings — and the run logs showed `Polly` retry exhaustion on **HTTP 429** for one sub-agent ~67% of the time.

**Conclusion.** The drift was **upstream evidence-frame variance caused by Azure OpenAI TPM/RPM quota contention** between three parallel sub-agents sharing one deployment, not Reflect-side sampling noise. With one sub-agent failing, Reflect received a 198-byte canned stub for that domain and correctly inferred "evidence missing → escalate."

This **falsifies** the case for self-consistency / majority voting (Wang et al., ICLR 2023) as the next fix — the assumption that drift = reasoning noise is wrong. The model is grounded; the inputs are flapping.

---

## Fix D — Failure-classification audit trail
**Defect.** Sub-agent `AgentExhaustedRetries` audit entries logged opaque `ClientResultException`.
**Fix.** New `ClassifyAgentFailure` helper:
- `HttpRequestException.StatusCode` direct read.
- `System.ClientModel.ClientResultException` `Status` property via reflection (no SDK reference leak into Application).
- Inner-exception walk (Polly wrapping).
- Last-resort message scan (`HTTP 429`, `too_many_requests`, `rate limit`).
- Cancellation/timeout dedicated label.

Audit description now reads:
> `HTTP 429 (Azure OpenAI rate limit — TPM/RPM quota exhausted, all retries throttled). Workflow safely degraded the {domain} sub-agent to NeedsHumanReview/0.00 per the resilience policy.`

Failure label is also propagated into the synthetic `findings` JSON consumed by Reflect, preserving the causal chain in the verdict's `evidenceTrail`.

---

## Deferred (post-demo) — addresses the *real* root cause
| Work | Hypothesis | Evidence basis |
|---|---|---|
| Per-domain dedicated Azure OpenAI deployments | Removes shared-quota contention on parallel fan-out | Microsoft Foundry "isolate concurrent workloads" guidance |
| Evidence pinning per `(assetId, content-version)` | Eliminates residual non-429 RAG/ordering variance; rerun-equivalence guarantee | Anthropic high-stakes pattern; SR 11-7 model-risk reproducibility |
| Self-consistency / judge tiebreaker | Conditional — only justified if drift persists post-isolation | Wang et al. ICLR 2023 (closed-form-answer assumption must hold) |

---

## Determinism guardrails currently active
- Reflection sampling lockdown (Phase 1 v2)
- Per-asset deterministic seed (Fix A)
- Verdict-key-aware extraction (Fix B)
- Strict JSON-schema response format (Fix C)
- Discrete confidence buckets
- Confidence/verdict consistency remap (LLM verdict + low confidence → NHR)
- Drift-probe evidence hashing (every run)
- HTTP-status-classified failure auditing (Fix D)

## Test posture
**434/434** xUnit, 0 warnings.
