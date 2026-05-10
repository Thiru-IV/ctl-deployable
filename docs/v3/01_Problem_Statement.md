# Problem Statement: Clear-To-List (CTL)

## 1. The Decision Behind Every Listing

Before any distressed asset (REO, foreclosure, short-sale, non-foreclosure) goes on the market, the business must declare it **Clear-To-List** — *legally clean, properly valued, physically ready*.

| Check         | What it answers                                            |
| ------------- | ---------------------------------------------------------- |
| **Legal**     | Title clear? Liens, HOA dues, code violations resolved?    |
| **Valuation** | BPO fresh and aligned with the AVM?                        |
| **Occupancy** | Vacant? Eviction status? Property condition acceptable?    |

The output is one of four verdicts — **`Clear` · `ClearWithConditions` · `NotClear` · `NeedsHumanReview`** — with the conditions, evidence, and policy citations that justify it.

The decision is hard because policy varies by state, county, asset type, investor program, and seller tier, and may changes quarterly; the inputs come from several vendor systems; and every verdict has to stand up to investor and regulator review.

---

## 2. Why This Is the Bottleneck Today

Cascade 2.0 — the Camunda workflow, AssetService, AccountService, TaskService — already runs the **process** and owns the **data**. That part works.

What still happens by hand is the *decision itself*: an analyst opens the asset, reads across systems, interprets policy, and types the verdict into a task. That single manual step is where the business pain lives:

- **Slow time-to-market.** Every day an asset waits for a CTL verdict is carrying cost, lost listing exposure, and a missed investor SLA.
- **Inconsistent verdicts.** Two analysts on the same asset can reach different conclusions; the same asset reviewed twice can flip.
- **Invisible policy drift.** Investor, regulator, and program rules move quarterly — nothing enforces that yesterday’s decision would still hold under today’s policy.
- **Expensive audit defense.** When investors or regulators ask *“why was this listed?”*, the answer has to be reconstructed from emails, screenshots, and memory.
- **Scarce expert time misallocated.** Senior analysts spend most of their day on routine assets, leaving the genuinely hard cases under-attended.

The existing platform was never meant to close this gap. Workflows route work; they don’t make judgment calls. Rule books can’t keep up with the policy surface or read unstructured vendor narrative. Microservices return facts (lien records, BPO values, occupancy flags) — they don’t turn those facts into *“ClearWithConditions, here’s why, here’s the policy clause.”* And handing the call to a generic AI assistant isn’t the answer either — without access to your real data, your policy, and proper controls, it cannot be trusted, secured, or audited.

> **What’s missing is a judgment layer** — a system that turns the facts the platform already gathers into a defensible verdict, at machine speed, every time, with the evidence attached.

---

## 3. How This Solution Closes It

This solution inserts that **judgment layer** between Cascade’s data and the listing decision. AI is used only where judgment is genuinely needed; everything around it — controls, policy lookup, evidence capture, audit — is deterministic.

**The decision happens in six steps:**

1. **Plan** — Read the asset; using current policy, decide which checks (Legal, Valuation, Occupancy) apply.
2. **Investigate** — Three specialist agents work the checks **in parallel**, each restricted to its own approved data sources.
3. **Reflect** — Synthesize the findings into a structured verdict with conditions, evidence, and citations.
4. **Apply Policy** — Confidence is mapped to a fixed scale; low-confidence outcomes are routed to a human, by rule.
5. **Quality Gate** — A **second, independent AI reviewer** checks that the verdict is actually grounded in the evidence. If not, it’s blocked.
6. **Human Review** — Anything flagged for review reaches an analyst with the full evidence package; the analyst’s decision is captured as part of the record.

**Why the business can rely on it:**

| What the business cares about              | How the solution delivers it                                                                  |
| ------------------------------------------ | --------------------------------------------------------------------------------------------- |
| **Same answer every time** for the same asset | Verdicts are reproducible by design — same inputs, same outcome.                            |
| **Always uses current policy**             | Policy documents are the source of truth and are searched live; updating policy is a content change, not a code change. |
| **Hallucination-resistant**                | Facts come from approved data sources only; an independent AI reviewer verifies the verdict is supported by evidence. |
| **Secure and compliant**                   | Layered safety controls screen for prompt-injection, sensitive data, and runaway cost on every request. |
| **Resilient**                              | Built-in retries, timeouts, and graceful fallbacks when an upstream system is unhealthy.       |
| **Audit-ready**                            | Every step, every data lookup, every safety check is logged with a session id and timestamps. A full decision is one replayable record. |
| **Human in the loop, by design**           | Routine assets clear automatically; only the genuinely ambiguous ones reach an analyst — with full context already assembled. |
| **Future-proof**                           | The platform isn’t locked to one AI vendor; new data sources and new models can be added without rewriting the workflow. |

> **This solution adds the missing judgment layer** — placed inside a controlled, instrumented agent workflow with real data sources, grounded policy, layered safety, an independent reviewer, and a complete audit trail — so CTL becomes *fast, consistent, and defensible*, while Cascade keeps doing what it already does well.
