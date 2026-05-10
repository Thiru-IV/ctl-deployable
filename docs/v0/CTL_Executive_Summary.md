# Cascade 2.0 — CTL(Clear-To-List) Agent

### Agentic AI for Asset Clear-To-List Determination

**April 2026 | Cascade 2.0 Platform | Prepared for Xome**

---

## The Problem

Every foreclosed or REO asset must pass a **Clear-To-List (CTL) gate** before it can be listed on Xome.com. Today this is a **manual, analyst-driven process** — an asset manager queries title systems, valuation providers, field services, and municipal records individually, stitches findings together, and makes a judgment call.

**This process does not scale.** It takes 2–4 hours per asset, varies by analyst, and produces inconsistent documentation. Every day an asset sits unlisted is lost revenue for Sellers.

---

## The Solution

The **CTL Agent** is a multi-agent AI system that automates the entire CTL investigation — from data gathering across vendor systems to a structured, auditable listing-readiness verdict.

**Given an asset ID, the system produces a CTL verdict in ~5 seconds.**

```
                  ┌───────────────────────────────────┐
                  │         CTL Orchestrator          │
                  │         Phase 1: PLAN             │
                  └────────────────┬──────────────────┘
                                   │
            ┌──────────────────────┼──────────────────────┐
            │                      │                      │
            ▼                      ▼                      ▼
   ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
   │    Legal &      │    │   Valuation     │    │   Occupancy     │
   │    Title Agent  │    │   Agent         │    │   Agent         │
   └────────┬────────┘    └────────┬────────┘    └────────┬────────┘
            │                      │                      │
            │       Phase 2: INVESTIGATE (parallel)       │
            └──────────────────────┼──────────────────────┘
                                   │
                                   ▼
                  ┌───────────────────────────────────┐
                  │         CTL Orchestrator          │
                  │    Phase 3: REFLECT & VERDICT     │
                  └────────────────┬──────────────────┘
                                   │
                                   ▼
                          Verdict + Evidence
                        + Confidence + Audit
```

Each verdict includes a **confidence score**, a list of **conditions** (if any), a full **evidence trail**, and an **audit-ready reflection log** explaining how the decision was reached.

---

## Business Impact

| Metric | Today (Manual) | With CTL Agent |
|--------|---------------|----------------|
| **Time per decision** | 2–4 hours | ~5 seconds |
| **Daily throughput** | 15–25 assets / analyst | Thousands / day |
| **Consistency** | Analyst-dependent | Policy-driven, standardized |
| **Evidence trail** | Partial, unstructured | Full, structured, audit-ready |
| **Time-to-listing** | Days to weeks | Same day |

**Strategic impact:**
- **Revenue acceleration** — Faster listings on Xome.com = faster sales cycle for Sellers
- **Scales without headcount** — Absorb volume spikes (portfolio acquisitions, seasonal waves) without proportional hiring
- **Reduces risk** — AI catches title defects, stale valuations, and occupancy conflicts that analysts miss under time pressure
- **Audit-ready from day one** — Full evidence trail and reasoning log on every decision

---

## What the Agent Evaluates

| Domain | What It Checks | Outcome |
|--------|---------------|---------|
| **Legal & Title** | Liens, encumbrances, title defects, HOA delinquency, code violations, state-specific rules | Blockers or conditions identified |
| **Valuation** | BPO freshness & quality, AVM variance, state-specific thresholds | Stale or missing valuations flagged |
| **Occupancy** | Vacancy status, eviction timeline, property condition | Occupancy barriers surfaced |

The orchestrator **reflects** on combined findings — detecting contradictions (e.g., clear title but delinquent HOA) and applying confidence adjustments before issuing one of four verdicts:

| Verdict | Meaning |
|---------|---------|
| **Clear** | Ready to list on Xome.com |
| **ClearWithConditions** | Ready to list if specific conditions are resolved |
| **NotClear** | Critical blockers found — remediation required |
| **NeedsHumanReview** | Insufficient data or contradictions — escalated to analyst |

---

## Fit Within Cascade 2.0

The CTL Agent is a **bounded capability** within the Cascade 2.0 platform — not a standalone system.

```
┌────────────────────────────────────────────────────────────────────┐
│                       Cascade 2.0 Platform                         │
│                                                                    │
│   ┌───────────────┐                    ┌───────────────────────┐   │
│   │               │  CTLEvaluation     │                       │   │
│   │   Camunda 8   │  Requested Event   │  CTL Agent Service    │   │
│   │  (Workflow)   │───────────────────►│                       │   │
│   │               │                    │  Orchestrator         │   │
│   │               │◄───────────────────│  + 3 AI Agents        │   │
│   │               │  Verdict           │  + Guardrails         │   │
│   └───────────────┘                    └───────────┬───────────┘   │
│                                                    │               │
│   ┌───────────────┐                    ┌───────────▼───────────┐   │
│   │ AssetService  │                    │  Azure AI Foundry     │   │
│   │ DocumentSvc   │                    │  (GPT-4o)             │   │
│   │ CamundaGW     │                    └───────────────────────┘   │
│   └───────────────┘                                                │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘
```

- **Triggered** by Azure Service Bus event from Camunda workflow
- **Returns** structured verdict to Camunda — the workflow gate decides final action
- **Reads** from existing Cascade 2.0 services (AssetService, DocumentService)
- **Zero autonomous action** — the agent is purely advisory; Camunda retains workflow control

---

## Safeguards & Risk Posture

| Concern | How It's Addressed |
|---------|--------------------|
| **AI making wrong decisions** | Advisory only — Camunda retains gate control; uncertain cases escalate via NeedsHumanReview |
| **Hallucination** | Grounded in policy knowledge base (not internet); reflection phase catches contradictions |
| **Data privacy** | PII masked on every LLM call; private endpoints; no data leaves Azure tenant |
| **Cost control** | Token budget caps; plan-driven routing skips unnecessary agent calls |
| **Vendor lock-in** | Model-agnostic abstractions — swap GPT-4o for any model without code changes |
| **System failures** | Automatic retry, circuit breakers, graceful degradation to human review |
| **Compliance** | Full audit trail on every decision — every tool call, retry, and verdict logged |

---

## Technology Alignment

Fully aligned with Cascade 2.0 platform standards:

- **Microsoft Agent Framework SDK** — .NET-native AI agent framework
- **Azure AI Foundry** — Managed AI platform (supports GPT-4o, Llama, Mistral)
- **MCP (Model Context Protocol)** — Industry-standard AI tool integration
- **.NET 8 / Azure / OpenTelemetry** — Cascade 2.0 stack

No Python. No third-party AI frameworks. 100% Microsoft Azure stack.

---

## Demo Scenarios (Target: Next Week)

Three scenarios are designed to demonstrate the system's decision-making across the verdict spectrum.

| Scenario | State | Verdict | Story |
|----------|-------|---------|-------|
| **Clean Path** | TX | **Clear** | Foreclosure, Tier 1 seller, vacant. Clean title, current BPO, no violations. |
| **Contradictions** | CA | **ClearWithConditions** | REO, Tier 2 seller. HOA delinquency, stale BPO, occupied with eviction in progress. |
| **Insufficient Data** | FL | **NeedsHumanReview** | Title defect, missing BPO, unknown occupancy. Escalated for analyst. |

Runnable with mock data once Azure AI Foundry is provisioned.

---

## Recommended Next Steps

1. **Walkthrough** of the approach with Xome operations stakeholders
2. **Validate** CTL business rules and verdict thresholds with Xome team
3. **Assess use case fitment** — determine if CTL is the right first candidate or if another use case benefits more
4. **Provision Azure AI Foundry** — required for live testing and demo
5. **Identify vendor integration priorities** — title search likely delivers the highest immediate value

---

*The CTL Agent demonstrates how agentic AI can reduce a multi-hour manual process to a seconds-level, auditable assessment — accelerating time-to-listing on Xome.com while keeping humans in control of final decisions.*
