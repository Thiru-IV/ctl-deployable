# Verdict Drift — Fixes Explained (Traditional Software View)

> Why the agent sometimes returned different verdicts for the same property, and what we shipped.

## The symptom
Same input asset (`ASSET-TX-001`) → sometimes `Clear / 0.95`, sometimes `NeedsHumanReview / 0.70` or `0.55`. Unacceptable for an audit-grade decisioning system.

## Mental model
Think of the workflow as a **manufacturing assembly line**:

| Line stage | What it does |
|---|---|
| Plan | Foreman writes the work order |
| Investigate (×3 parallel) | 3 specialist inspectors examine the part |
| Reflect | QA lead reviews all 3 reports and stamps a verdict |
| Parse | Stamping machine reads the verdict tag |
| Quality Gate / Human Review | Final shipping decision |

Drift = the same part rolling off the line with different stamps each time. Below: each problem we found, the root cause, the fix, and the analogy.

---

## Fix A — Reproducible random seed
**Problem.** The QA lead's coin flip used a different random number every shift, even when handed an identical part.
**Cause.** Seed = hash(AssetId | SessionId), but SessionId was a fresh GUID per run.
**Fix.** Seed = hash(AssetId) only. Same part → same seed → same stamp.
**Files.** `CTLAgentOptions.cs`, `ReflectionDeterminismFactory.cs`.
**Analogy.** A barcode scanner that re-keys itself between scans, vs. one that always returns the same code for the same barcode.

---

## Fix B — Robust JSON extraction
**Problem.** The stamping machine sometimes read a barcode from an unrelated sticker on the box and recorded garbage.
**Cause.** Parser used `IndexOf('{') … LastIndexOf('}')` — would happily latch onto an embedded *citations* array if the model wrapped its verdict in markdown.
**Fix.** Parser now scans for the **first balanced JSON object that actually contains a `"verdict"` key**. If none found → fail loudly to `NeedsHumanReview / 0.0` instead of fabricating.
**Files.** `VerdictParser.cs` (new `TryExtractVerdictJson`).
**Analogy.** Validating the barcode is on the *product label*, not on the shipping carton.

---

## Fix C — Strict output contract
**Problem.** The QA lead occasionally hand-wrote a memo instead of filling out the form. Stamping machine had nothing to read.
**Cause.** No protocol-level requirement that the LLM emit JSON. Free-form markdown was legal output.
**Fix.** Reflection now uses Azure OpenAI's strict `response_format: json_schema`. The API itself rejects non-conforming output.
**Files.** `ReflectionDeterminismFactory.cs` (`ChatResponseFormat.ForJsonSchema`).
**Analogy.** Replacing a blank notepad with a pre-printed form that has required fields and validation.

---

## Phase-1 base controls (already shipped earlier)
- **Temperature locked to 0** — same dice, no jitter.
- **Discrete confidence buckets** {0.55, 0.70, 0.80, 0.90, 0.95} — confidence scores snap to a fixed Likert scale instead of free-floating decimals.
- **Audit log of raw vs. snapped values** — full traceability.

---

## Drift root-cause probe (diagnostic, not a feature)
After A+B+C, residual drift remained on ASSET-TX-001 (4× Clear/0.95 vs 4× NHR). Added a hash log of the evidence going into Reflect on every run.

**Finding.** The full prompt hash differed every single run. The cause was **HTTP 429 rate-limit failures** on Azure OpenAI: one of the 3 parallel inspectors lost all 3 retries about half the time, leaving Reflect with a 198-byte stub instead of a real domain report. The QA lead correctly flagged "evidence missing → human review."

**Conclusion.** The drift was **infrastructure variance**, not LLM noise. The model was doing its job.

---

## Fix D — Audit clarity for 429s
**Problem.** When 429s happen, audit log said `ClientResultException` — meaningless to a non-engineer reviewer.
**Fix.** New `ClassifyAgentFailure` helper labels failures with HTTP status and plain-English cause:
> `HTTP 429 (Azure OpenAI rate limit — TPM/RPM quota exhausted, all retries throttled)`

The audit entry also explicitly states the workflow degraded safely to human review per resilience policy.
**Analogy.** Replacing `ERROR-7F23` on the assembly-line console with `Conveyor belt pressure low — auto-stopped per safety interlock`.

---

## Outstanding (not shipped pre-demo)
| Item | Why not yet |
|---|---|
| Per-domain dedicated Azure OpenAI deployments | Code-freeze before demo; queued for next sprint |
| Evidence-frame caching per asset | Same |
| Self-consistency / judge tiebreaker | Evidence shows it would not solve the bimodal drift we measured |

## Test posture
**434 / 434 passing**, 0 warnings. Net new: 13 determinism + 8 parser + 8 failure-classification tests.
