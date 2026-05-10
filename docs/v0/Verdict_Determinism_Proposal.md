# Verdict Determinism Proposal

**Goal:** Eliminate verdict/confidence drift across repeat runs of the same asset by separating LLM-driven *evidence extraction* from deterministic *verdict assignment*.

**Problem observed:** Same asset, run twice → confidence swings 0.75 ↔ 0.92, occasionally verdict flips between `Clear` / `ClearWithConditions` / `NeedsHumanReview`. Caused by LLM doing both extraction AND adjudication.

---

## 1. Current architecture (drift source)

```
Plan → Investigate (3 LLM agents)
            ↓ each returns: { domainVerdict, confidence, findings[], summary }   ← LLM decides domain verdict
     → Reflect (1 LLM agent)
            ↓ returns: { verdict, confidenceScore, reflectionLog }                ← LLM decides final verdict
     → Parse → QualityGate (LLM judge) → HumanReview
```

**Three places where the LLM picks numbers/categories non-deterministically:**
1. Each domain sub-agent assigns its own `domainVerdict` + `confidence`
2. Reflection LLM picks final `verdict` + `confidenceScore` (free-form reasoning)
3. Quality Gate judge gives groundedness 1–5 (already isolated, low risk)

---

## 2. Proposed architecture

```
Plan → Investigate (3 LLM agents — extract STRUCTURED FACTS only)
            ↓ each returns: { facts: {...}, evidence: [...] }                     ← no verdict, no confidence
     → VerdictRuleEngine (DETERMINISTIC C# code)
            ↓ returns: { verdict, confidenceScore, ruleHits[] }                   ← same facts → same verdict
     → QualityGate (LLM judge — validates fact extraction was grounded)
     → HumanReview
```

The Reflection LLM phase is **removed from the verdict path**. Reflection becomes optional explanatory narrative for human reviewers (read-only).

---

## 3. New domain agent output contract

Each domain agent returns a typed `DomainFacts` record. Verdict/confidence are removed.

### 3.1 Legal & Title
```csharp
public sealed record LegalFacts {
    public bool TitleClear { get; init; }
    public int OpenLienCount { get; init; }
    public decimal LienTotalAmount { get; init; }
    public bool HasLisPendens { get; init; }
    public int CriticalCodeViolations { get; init; }   // health/safety/structural
    public int NonCriticalCodeViolations { get; init; }
    public bool HasCondemnationOrder { get; init; }
    public decimal HoaDelinquencyAmount { get; init; }
    public List<Citation> Evidence { get; init; }
    public List<string> UnverifiedFields { get; init; }
}
```

### 3.2 Valuation
```csharp
public sealed record ValuationFacts {
    public bool BpoExists { get; init; }
    public int? BpoAgeDays { get; init; }
    public string? BpoQuality { get; init; }            // High / Medium / Low
    public bool AvmExists { get; init; }
    public decimal? AvmToBpoVariancePercent { get; init; }
    public List<Citation> Evidence { get; init; }
    public List<string> UnverifiedFields { get; init; }
}
```

### 3.3 Occupancy & Condition
```csharp
public sealed record OccupancyFacts {
    public string OccupancyStatus { get; init; }        // VacantSecured / VacantUnsecured / Occupied / Unknown
    public int? LastInspectionAgeDays { get; init; }
    public bool HasHazardousConditions { get; init; }
    public bool EvictionInProgress { get; init; }
    public bool CashForKeysAgreement { get; init; }
    public List<Citation> Evidence { get; init; }
    public List<string> UnverifiedFields { get; init; }
}
```

**Agents are told:** "Return only the facts. Do not assign verdicts or scores. The verdict is determined elsewhere."

---

## 4. VerdictRuleEngine (deterministic C#)

A pure function: `(LegalFacts, ValuationFacts, OccupancyFacts, AssetProfile) → VerdictResult`.

```csharp
public sealed record VerdictResult {
    public CTLVerdict Verdict { get; init; }
    public double ConfidenceScore { get; init; }
    public List<RuleHit> RuleHits { get; init; }        // which rules fired and why
    public List<string> BlockingConditions { get; init; }
    public List<string> ConditionalNotes { get; init; }
}

public sealed record RuleHit(string RuleId, string Description, string Outcome);
```

### 4.1 Decision rules (initial set, ordered priority)

| Rule ID | Condition | Outcome |
|---------|-----------|---------|
| `R-LEGAL-01` | `HasCondemnationOrder == true` | **NotClear** |
| `R-LEGAL-02` | `CriticalCodeViolations > 0` | **NotClear** |
| `R-LEGAL-03` | `TitleClear == false` OR `OpenLienCount > 0 && LienTotalAmount > 2500` | **NotClear** |
| `R-OCC-01` | `OccupancyStatus == "Occupied" && !CashForKeysAgreement && !EvictionInProgress` | **NotClear** |
| `R-OCC-02` | `HasHazardousConditions == true` | **NeedsHumanReview** |
| `R-VAL-01` | `BpoExists == false` | **NeedsHumanReview** |
| `R-VAL-02` | `BpoAgeDays > 90` | **ClearWithConditions** (refresh required) |
| `R-VAL-03` | `AvmToBpoVariancePercent > 15` | **ClearWithConditions** |
| `R-OCC-03` | `OccupancyStatus == "Unknown" OR LastInspectionAgeDays > 30` | **NeedsHumanReview** |
| `R-LEGAL-04` | `HoaDelinquencyAmount >= 5000` | **NotClear** |
| `R-LEGAL-05` | `HoaDelinquencyAmount >= 1000 && < 5000` | **ClearWithConditions** |
| `R-OCC-04` | `OccupancyStatus == "VacantUnsecured"` | **ClearWithConditions** (secure within 5 days) |
| `R-TIER-01` | `SellerTier == Tier1 && AnyConditionalRule fired` | Escalate to **NotClear** (Tier 1 disallows conditional) |
| `R-DEFAULT` | All checks pass | **Clear** |

**Verdict precedence:** `NotClear > NeedsHumanReview > ClearWithConditions > Clear` — worst outcome wins.

### 4.2 Confidence scoring (deterministic formula)

```
baseConfidence = 1.0
- 0.05 per UnverifiedField (across all domains)
- 0.10 per ConditionalRule that fired
- 0.20 per NeedsHumanReview rule that fired
+ 0.0 for NotClear (high confidence in blocking decision)

confidence = clamp(baseConfidence, 0.0, 1.0)
```

Same inputs → same confidence. Always.

---

## 5. What stays, what goes

| Component | Status |
|-----------|--------|
| `PlanningExecutor` | **Stays** — already non-critical drift |
| `InvestigationPhaseExecutor` | **Stays** but agents output facts-only |
| Domain agent prompts | **Updated** — "extract facts, no verdicts" |
| `ReflectionExecutor` | **Demoted** — optional narrative, not on verdict path; can be deleted to simplify |
| `VerdictParsingExecutor` | **Replaced** with `VerdictRuleEngineExecutor` |
| `QualityGateExecutor` (LLM judge) | **Stays** — but judges *fact extraction*, not verdict |
| `HumanReviewExecutor` | **Stays** — unchanged |
| `CTLVerdict` enum | **Stays** unchanged |

---

## 6. Risk assessment

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Agent fails to extract a fact correctly → wrong verdict | Medium | Quality Gate now validates fact-evidence grounding; failures escalate to HITL |
| Rules don't cover edge cases | Medium | `R-DEFAULT` is `Clear`; add `UnverifiedFields > N → NeedsHumanReview` safety net |
| Rule engine gets complex over time | Low | Keep it as plain C# `if/else` chain in one file; no DSL |
| Tests break | High | Existing tests assert LLM verdict shape; need refactor (~30 tests) |
| Eval suite (`Cascade.CTL.Agent.Evals`) breaks | Medium | Eval cases assert verdict ranges; tighten now that determinism allows exact match |
| Loss of "LLM judgment" on edge cases | Low-Medium | LLM still extracts facts; rules are policy-driven (auditable). Edge cases route to HITL by design. |

---

## 7. Migration plan (4 phases, each independently shippable)

### Phase 1 — Add the rule engine (no behavior change)
- Create `VerdictRuleEngine` class with rules
- Run it in **shadow mode**: log its verdict alongside the LLM verdict in audit trail
- Compare drift over N runs → quantifies the fix
- **No production behavior change.** Easy rollback.

### Phase 2 — Add facts schema to agents (additive)
- Domain agents return BOTH old `domainVerdict` (for compat) AND new `facts` block
- Rule engine consumes facts, still in shadow mode
- Existing tests untouched

### Phase 3 — Switch verdict source
- `VerdictRuleEngineExecutor` replaces `VerdictParsingExecutor` in workflow graph
- Reflection becomes read-only narrative (or deleted)
- Update tests to assert on rule hits, not LLM reasoning

### Phase 4 — Cleanup
- Remove old `domainVerdict` field from agent contracts
- Remove `ReflectionExecutor` if not needed for explainability
- Update prompts to only ask for facts

---

## 8. Test strategy

**New tests** (deterministic — no flakiness):
- `VerdictRuleEngine_GivenFacts_ProducesExpectedVerdict` — table-driven, covers each rule
- `VerdictRuleEngine_TitleNotClear_ReturnsNotClear`
- `VerdictRuleEngine_NoBpo_ReturnsNeedsHumanReview`
- `VerdictRuleEngine_StaleBpo_ReturnsClearWithConditions`
- `VerdictRuleEngine_Tier1WithConditions_EscalatesToNotClear`
- `VerdictRuleEngine_AllClean_ReturnsClear`
- Drift test: same facts → run engine 100x → assert identical verdict + confidence

**Updated tests:**
- Integration tests assert on `RuleHits[]` not on LLM `reflectionLog` text
- Eval suite tightened from `[Clear, ClearWithConditions]` → exact verdict match where deterministic

---

## 9. Estimated impact

- New code: ~300 lines (`VerdictRuleEngine` + records + tests)
- Modified files: ~6 (executors, prompts, workflow graph, contracts)
- Deleted code: ~150 lines (`ReflectionExecutor` if removed, `VerdictParsingExecutor`)
- Test changes: ~30 existing tests need contract updates
- Rebuild + re-index NOT required (rules are pure C#; policies remain in RAG for explainability)

---

## 10. What this does NOT solve

- **RAG drift**: Different chunks retrieved on different runs can still cause an LLM agent to extract slightly different facts. Mitigation: self-consistency (run agent N=3 times, majority vote per fact) — optional Phase 5.
- **Tool result variance**: Mock providers are deterministic; real providers (title search APIs) may return different data over time. Out of scope.
- **Plan domain selection drift**: Already addressed — Tier 2 exemption is policy-driven.

---

## 11. Open questions for review

1. **Keep or delete `ReflectionExecutor`?** Recommendation: keep as read-only narrative for human reviewers; do not let it influence verdict.
2. **Confidence formula coefficients** (the `-0.05`, `-0.10`, `-0.20` weights) — calibrate via eval suite, or accept as policy decision?
3. **Tier 1 escalation rule (`R-TIER-01`)** — should it escalate ANY conditional finding to `NotClear`, or only specific ones (e.g., HOA but not stale BPO)?
4. **Should rules be data-driven** (loaded from JSON like RAG policies) instead of compiled C#? Trade-off: data-driven is hot-swappable but harder to test/version.

---

**Next step after your review:** confirm the rules table (Section 4.1) and the migration phases (Section 7), then I'll implement Phase 1 (shadow mode) first so you can compare drift numbers before committing.
