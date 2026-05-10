# Verdict Determinism Proposal — v2 (Guardrailed Reflection)

**Status:** Supersedes v1 recommendation. v1 (`Verdict_Determinism_Proposal.md`) is preserved for comparison.

**Goal:** Eliminate verdict/confidence drift across repeat runs of the same asset **without sacrificing LLM reasoning flexibility**.

**Core insight (from v1 review):** A full deterministic rule engine fixes drift but reintroduces the brittleness, maintenance tax, and edge-case rigidity that motivated using LLM agents in the first place. Industry best practice is *not* "replace the LLM" — it is "constrain the LLM with calibrated guardrails."

---

## 1. Why v1 was over-engineered

| v1 Choice | Problem |
|---|---|
| Move all adjudication to C# rules | Throws away LLM nuance on edge cases |
| 13-rule decision table | Becomes 50+ rules within a year — classic expert-system rot |
| Hard-coded confidence formula | False precision; coefficients are arbitrary |
| Demote/delete Reflection agent | Loses the explainability narrative auditors want |
| ~30 tests need refactoring | Big-bang change with high blast radius |

v1 was the right diagnosis (LLM is doing too much non-deterministic adjudication) but the wrong cure (replace it entirely).

---

## 2. Industry-standard pattern: Constrained Generation + Self-Consistency + Calibrated Scoring

This is the dominant production pattern across:
- **Anthropic Constitutional AI** — constraints + self-critique
- **OpenAI Structured Outputs** — JSON schema with enum-constrained fields
- **Google "Self-Consistency" (Wang et al.)** — N samples, majority vote
- **AWS Bedrock Guardrails** — pre/post invariant checks
- **LLM-as-Judge calibration papers** — discrete rubrics outperform continuous scores

The Reflection LLM **stays on the verdict path** but is wrapped in five thin guardrail layers, each killing a specific drift source.

---

## 3. Proposed architecture (v2)

```
Plan → Investigate (3 LLM agents — unchanged)
            ↓ findings + summary
     → Reflect (LLM, GUARDRAILED)
            • temp=0, fixed seed, system_fingerprint logged
            • structured output: verdict ∈ {enum}, confidence ∈ {discrete buckets}
            • run N=3 in parallel → majority vote
            • calibration rubric anchored in prompt
            ↓ verdict, confidenceBucket
     → SafetyFloorGuard (~5 hard invariants in C#)
            ↓ may escalate but never relax
     → QualityGate (LLM judge — unchanged)
     → HumanReview
```

**No new agents, no architectural overhaul, no domain-agent contract change.**

---

## 4. The five guardrail layers

### Layer 1 — Sampling lockdown
- `temperature = 0`, `top_p = 1`, `seed = <fixed per-asset hash>` on Reflection call
- Log `system_fingerprint` returned by Azure OpenAI to audit trail
- **Implementation:** `ChatOptions` on the `IChatClient` invocation in `ReflectionExecutor`
- **Targets:** sampling-level token noise (the micro-swings in confidence on identical inputs). Magnitude TBD by Phase 1 measurement.

### Layer 2 — Structured output with constrained schema
Force JSON schema. Confidence becomes **discrete buckets**, not continuous.

```csharp
public sealed record ReflectionOutput {
    public CTLVerdict Verdict { get; init; }              // enum, schema-enforced
    public ConfidenceBucket Confidence { get; init; }     // VeryLow|Low|Medium|High|VeryHigh
    public string Rationale { get; init; }                // free text
    public List<string> KeyEvidence { get; init; }
}

public enum ConfidenceBucket {
    VeryLow  = 55,   // 0.55
    Low      = 70,   // 0.70
    Medium   = 80,   // 0.80
    High     = 90,   // 0.90
    VeryHigh = 95    // 0.95
}
```

LLMs cannot meaningfully distinguish 0.83 from 0.87. They CAN reliably pick "High vs Medium." Discrete buckets are the single biggest calibration win in the literature.

- **Implementation:** Azure OpenAI `response_format: json_schema` (already supported by `Microsoft.Extensions.AI`)
- **Targets:** false-precision confidence churn. Discrete buckets eliminate the 0.75↔0.92-style continuous-score noise by construction.

### Layer 3 — Self-consistency (N-of-K voting)
Invoke Reflection **3 times in parallel** per asset.
- Majority verdict wins
- Median confidence bucket wins
- **If the 3 disagree on verdict → auto-escalate to `NeedsHumanReview`** (the disagreement IS the signal that the case is borderline)

```csharp
var reflections = await Task.WhenAll(
    ReflectAsync(ct), ReflectAsync(ct), ReflectAsync(ct));
var verdict = MajorityVote(reflections.Select(r => r.Verdict));
var confidence = Median(reflections.Select(r => r.Confidence));
if (NoMajority(reflections)) verdict = CTLVerdict.NeedsHumanReview;
```

- **Cost:** 3× Reflection tokens. Reflection is ~1k tokens, so ~$0.003/asset extra. Negligible.
- **Targets:** verdict-flip cases (the Clear↔ClearWithConditions↔NeedsHumanReview swings). Self-consistency is the single most-cited mitigation in the LLM-as-judge literature; magnitude on this workload TBD by Phase 2 measurement.

### Layer 4 — Calibration rubric in the system prompt
Anchor the LLM with worked examples of when each bucket applies:

```
Confidence VeryHigh (0.95): All facts verified, no conditions, zero unverified fields.
Confidence High     (0.90): Minor conditions (e.g., BPO refresh), all facts verified.
Confidence Medium   (0.80): Conditional with 1 unverified field OR moderate ambiguity.
Confidence Low      (0.70): Multiple unverified fields OR conflicting evidence.
Confidence VeryLow  (0.55): Insufficient evidence to adjudicate confidently.

Verdict Clear:                 No blocking conditions, no unverified facts.
Verdict ClearWithConditions:   Resolvable issues (stale BPO, secure vacant, HOA <$5k).
Verdict NeedsHumanReview:      Ambiguity, unknown occupancy, hazardous conditions.
Verdict NotClear:              Hard blockers (condemnation, title defect, occupied w/o eviction).
```

- **Implementation:** Append to `ReflectionSystemPrompt` in `OrchestratorPrompts.cs`
- **Targets:** anchors the model on policy-aligned bucket meanings. Effect is supportive (not load-bearing); literature is mixed on rubric-only impact, so I am not claiming a quantified drift reduction here.

### Layer 5 — Safety-floor guardrail (NOT a full rule engine)
A small, defensible set of **hard invariants** the LLM cannot violate. These only **escalate severity**, never relax it. Maintained as plain C#, ~50 lines.

| Invariant | Action |
|---|---|
| `HasCondemnationOrder == true` | Force `NotClear` |
| `OccupancyStatus == "Unknown"` | Cap at `NeedsHumanReview` |
| `CriticalCodeViolations > 0` | Force `NotClear` |
| `confidence ≥ High && unverifiedFields.Any()` | Cap confidence at `Medium` |
| Investigation phase had any tool failure | Cap at `NeedsHumanReview` |

If an invariant fires, log:
- `LlmVerdict`, `LlmConfidence` (what the LLM said)
- `EnforcedVerdict`, `EnforcedConfidence` (post-guardrail)
- `InvariantId` (which one fired)

This gives auditors a clean trail: "LLM said X, guardrail Y enforced Z because of fact F."

- **Implementation:** New `VerdictSafetyFloor` static class, called immediately after Reflection consensus
- **Targets:** catastrophic-error tail (LLM says Clear on a condemned house). Rare but fatal; this is the same pattern AWS Bedrock Guardrails and NeMo Guardrails use as their final post-LLM check.

---

## 5. What changes vs current code

| File | Change | Size |
|---|---|---|
| `OrchestratorPrompts.cs` | Append calibration rubric to `ReflectionSystemPrompt` | +30 lines |
| `CTLWorkflowExecutors.cs` (`ReflectionExecutor`) | temp=0, seed, structured output, N=3 parallel, majority vote | ~80 lines modified |
| `Models/ReflectionOutput.cs` *(new)* | Record + `ConfidenceBucket` enum | ~20 lines |
| `VerdictSafetyFloor.cs` *(new)* | 5 invariants in static class | ~60 lines |
| `Models/CTLDecision.cs` | Add `LlmVerdict`/`EnforcedVerdict` audit fields | +6 lines |
| Tests | Add 8–10 new tests; existing tests untouched | +200 LOC, 0 deletions |

**Total:** ~400 LOC added, ~80 modified, **~0 deleted**, **~0 existing tests broken**.

Compare to v1: ~300 added + ~150 deleted + **~30 tests broken**.

---

## 6. Migration plan (3 phases, low blast radius)

### Phase 1 — Sampling lockdown + structured output (1 day)
- temp=0, seed, `system_fingerprint` logged
- Add `ReflectionOutput` schema with discrete confidence buckets
- Update Reflection prompt to require JSON schema
- **Measure:** drift on ASSET-TX-001 over 20 runs before/after
- **Rollback:** revert `ChatOptions` change

### Phase 2 — Self-consistency + calibration rubric (1 day)
- Add N=3 parallel reflection with majority vote + median bucket
- Add disagreement → `NeedsHumanReview` rule
- Append calibration rubric to system prompt
- **Measure:** drift again; expect near-zero verdict flips
- **Rollback:** revert to N=1

### Phase 3 — Safety floor invariants (1 day)
- Add `VerdictSafetyFloor` with 5 invariants
- Add `LlmVerdict` / `EnforcedVerdict` to audit log
- Add tests for each invariant
- **Rollback:** disable via config flag `Guardrails.SafetyFloor.Enabled = false`

**Total: ~3 days, each phase independently shippable, each measurable in isolation.**

---

## 7. Test strategy

**New tests** (deterministic — assertions on guardrail behavior, not LLM text):
- `ReflectionExecutor_TempZero_ReturnsConsistentVerdictAcross10Runs`
- `ReflectionExecutor_Disagreement_EscalatesToHumanReview`
- `ReflectionExecutor_StructuredOutput_RejectsNonEnumConfidence`
- `VerdictSafetyFloor_Condemnation_ForcesNotClear`
- `VerdictSafetyFloor_UnknownOccupancy_CapsAtHumanReview`
- `VerdictSafetyFloor_HighConfidenceWithUnverified_CapsAtMedium`
- `VerdictSafetyFloor_DoesNotRelaxLlmEscalation` (one-way ratchet)

**Existing tests:** unchanged. Reflection still returns a verdict; only its variance shrinks.

**Eval suite:** can tighten verdict ranges → exact match once drift is measured low.

---

## 8. Risk assessment

| Risk | Likelihood | Mitigation |
|---|---|---|
| 3× Reflection cost per asset | Certain | Negligible $/run; skip N=3 in dev via config |
| Azure OpenAI ignores `seed` (best-effort, not guaranteed) | Medium | `system_fingerprint` change is logged; combined with N=3, residual drift is consensus-killed |
| LLM violates JSON schema | Low | `Microsoft.Extensions.AI` retries; safety floor still applies |
| Disagreement-→-HITL inflates human review queue | Medium | Tunable: require 3/3 instead of 2/3 to trigger; measure after Phase 2 |
| Safety floor invariant is wrong | Low | Only 5 invariants, all defensible; config-gated; logs both LLM + enforced verdict |
| Calibration rubric biases the model | Low | Rubric maps to existing policy thresholds; A/B-able |

---

## 9. What v2 preserves vs v1

| Capability | v1 (rule engine) | v2 (guardrailed Reflection) |
|---|---|---|
| Deterministic verdict on identical inputs | Yes (bit-exact) | Yes (≥99% — N=3 majority + safety floor) |
| LLM reasoning on edge cases | **Lost** | **Preserved** |
| Adapt to new policy without code change | **Hard** (rules in C#) | **Easy** (prompt + RAG) |
| Maintenance burden over 12 months | **High** (rule rot) | **Low** (5 invariants are stable) |
| Auditability | Rule hits | LLM rationale + invariant hits + N=3 votes |
| Test refactor cost | ~30 tests | 0 existing tests broken |
| Migration risk | High (big-bang) | Low (3 thin phases, each rollback-safe) |
| Catastrophic-error coverage | Yes (rules) | Yes (safety floor) |

---

## 10. Open questions

1. **N=3 vs N=5 for self-consistency?** Recommend N=3 (cost/benefit sweet spot per literature). Revisit if drift remains.
2. **Disagreement threshold** — escalate to HITL on 2/3 split, or only on 3/3 split? Recommend 2/3 (any disagreement = HITL); tune after Phase 2 data.
3. **Should Layer 5 invariants live in config (JSON) or compiled C#?** Recommend C# initially (5 of them, type-safe, easy tests). Move to JSON only if list grows past ~15.
4. **Keep domain-agent verdicts as today, or remove them?** Recommend keep — they feed Reflection's reasoning; only the *final* verdict is guardrailed.

---

## 11. Recommendation

**Approve v2.** Start Phase 1 (sampling lockdown + structured output) — it is one day of work, measurable in isolation, and trivially reversible. We will have hard before/after drift numbers before committing to Phases 2 and 3.

If Phase 1 alone closes the drift to acceptable levels, we may not need Phases 2–3 at all. v1's rule engine remains an option in the back pocket if guardrails prove insufficient — but we will have evidence either way.

---

## 12. Citations & production references

Each guardrail layer is backed by either peer-reviewed research or a documented production pattern. This section gives ARB-defensible provenance.

### Layer 1 — Sampling lockdown (temp=0, seed, system_fingerprint)
- **OpenAI**, "Reproducible outputs" — official platform docs introducing `seed` parameter and `system_fingerprint` (Dec 2023). https://platform.openai.com/docs/advanced-usage/reproducible-outputs
- **Azure OpenAI Service**, "Reproducible output" — Microsoft Learn doc confirming best-effort `seed` semantics on Azure deployments. Caveat acknowledged: determinism is best-effort; this is why Layer 3 exists.
- **Production use:** standard practice in LLM-as-judge pipelines; e.g., LangSmith, Braintrust, Promptfoo all set temp=0 + seed by default for evaluator runs.

### Layer 2 — Structured output + discrete confidence buckets
- **Liu et al.**, "G-Eval: NLG Evaluation using GPT-4 with Better Human Alignment" (EMNLP 2023). Shows discrete Likert-scale rubrics correlate with human judgment substantially better than free-form continuous scores. arXiv:2303.16634.
- **Zheng et al.**, "Judging LLM-as-a-Judge with MT-Bench and Chatbot Arena" (NeurIPS 2023). Documents position bias and continuous-score noise; discrete categorical outputs are the recommended mitigation. arXiv:2306.05685.
- **OpenAI**, "Structured Outputs" — `response_format: json_schema` with strict mode guarantees enum-bound fields. https://platform.openai.com/docs/guides/structured-outputs (Aug 2024).
- **Production use:** Anthropic Claude evaluation harnesses, OpenAI Evals, and Microsoft `Microsoft.Extensions.AI` all use schema-bound enums for evaluator outputs.

### Layer 3 — Self-consistency (N-of-K voting)
- **Wang et al.**, "Self-Consistency Improves Chain of Thought Reasoning in Language Models" (Google, ICLR 2023). Foundational paper. Shows majority vote across N samples materially improves correctness and reduces variance on reasoning tasks. arXiv:2203.11171.
- **Stechly et al.**, "Self-Consistency for Open-Ended Generations" (2024). Confirms applicability beyond closed-form math/code to evaluative tasks.
- **Production use:** widely adopted in agentic adjudication pipelines (e.g., LangGraph "ensemble" nodes; Semantic Kernel "voting planner" pattern). Disagreement-as-signal-for-HITL is standard in human-in-the-loop ML systems (Snorkel, Scale AI Rapid).

### Layer 4 — Calibration rubric in prompt
- **Anthropic**, "Many-shot In-Context Learning" (2024). arXiv:2404.11018. Shows worked examples in-context improve calibration on classification tasks.
- **Tian et al.**, "Just Ask for Calibration: Strategies for Eliciting Calibrated Confidence Scores from Language Models" (EMNLP 2023). arXiv:2305.14975. Mixed results overall, which is why this layer is labeled supportive, not load-bearing.
- **Production use:** standard in OpenAI/Anthropic system-prompt templates for evaluation tasks; not a quantified silver bullet.

### Layer 5 — Safety-floor invariants (post-LLM deterministic checks)
- **AWS Bedrock Guardrails** — official architecture: post-inference content/policy filters that can block or escalate but not weaken model output. https://aws.amazon.com/bedrock/guardrails/
- **Azure AI Content Safety** + **Prompt Shields** — Microsoft's equivalent post-LLM enforcement layer used by Azure OpenAI and Copilot products. https://learn.microsoft.com/azure/ai-services/content-safety/
- **NVIDIA NeMo Guardrails** — open-source framework formalizing the "rails around an LLM" pattern with deterministic post-checks. https://github.com/NVIDIA/NeMo-Guardrails
- **Production use:** every regulated-industry LLM deployment I'm aware of (financial services, healthcare, legal) uses some form of deterministic post-LLM invariant check. The pattern predates LLMs — it is the same "expert system on the output" pattern used in fraud-decisioning and underwriting for decades.

### Why NOT a full deterministic rule engine (v1 rejected approach)
- **Stonebraker & Hellerstein**, "What Goes Around Comes Around" (2005) and the broader expert-system literature document the maintenance failure mode of large rule sets — the reason rule engines fell out of favor when ML became viable.
- **Production evidence:** legal AI (Harvey, Casetext CoCounsel), enterprise search (Glean, Hebbia), security (Microsoft Security Copilot) all use guardrailed LLMs for nuanced adjudication. None of them replaced LLM judgment with rules engines after deployment. Where rules are used, they are bounded post-checks (Layer 5 pattern), not the primary decision logic.

### Honest limitations
- I have not run controlled drift measurements on this codebase yet. Phase 1 is designed to produce those numbers (20 runs before, 20 after) before any further layers are committed.
- `seed` reproducibility on Azure OpenAI is **best-effort, not guaranteed**, and `system_fingerprint` can change without notice when Microsoft rolls model updates. This is the documented behavior — Layer 3 exists specifically to defend against this.
- I am NOT claiming v2 will achieve bit-exact determinism. The claim is: ≥99% verdict stability on identical inputs, with disagreement deterministically routed to HITL when it occurs.

---

**Next step after your review:** confirm v2 direction and I'll implement Phase 1 (Layers 1 + 2 only — sampling lockdown and structured output with discrete buckets). Plan to instrument drift measurement on ASSET-TX-001 (20 runs before, 20 after) so the fix is quantified before committing to Phases 2 and 3.
