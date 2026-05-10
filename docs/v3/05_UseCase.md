# Use Case

**Subject:** What problem does this solution actually solve, and is the use case real?

---

## 1. The Use Case in One Sentence

> Before any distressed property can be **listed for sale**, somebody has to declare it **Clear-To-List (CTL)** — *legally clean, properly valued, and physically ready*. This solution produces that declaration automatically — with evidence and policy citations — instead of leaving it to an analyst reading across vendor outputs by hand, or to brittle hardcoded rules that can only check whether a field is populated and recent.

---
## 2. Where CTL Sits — Asset Disposition Path

```mermaid
flowchart LR
    A[Borrower Default<br/>Workout Options Closed] --> B{Onboarding Path}
    B --> C1[Foreclosure Asset]
    B --> C2[REO Asset]
    C1 --> D[Property<br/>Preservation<br/>and Securing]
    C2 --> D
    D --> E[Title Clean-up<br/>Liens / HOA / Code]
    E --> F[Property<br/>Valuation]
    F --> G{Clear-To-List?}
    G -->|Yes| H[Publish to<br/>Disposition Channel]
    G -->|Yes, with conditions| I[Resolve Conditions<br/>then Publish]
    G -->|No| J[Hold for Rework]
    G -->|Needs human review| K[Analyst Reviews Case]
    H --> L[Offer / Closing]

    classDef gate fill:#fff3e0,stroke:#e65100,stroke-width:2px,color:#000
    classDef done fill:#e8f5e9,stroke:#2e7d32,color:#000
    classDef start fill:#e3f2fd,stroke:#1565c0,color:#000
    class G gate
    class L done
    class A start
```

Everything to the left of the diamond is operational prep that Cascade 2.0 already does; everything to the right is sales execution. **CTL is the gating decision that releases an asset from inventory carrying-cost into market exposure.**

---

## 3. What CTL Replaces — and What It Does Not

### Layer 1 — Data capture (upstream of CTL): vendor-driven, *unchanged by this solution*

The *facts themselves* are produced by external operators (or proprietary in-house systems, e.g., Xome's XVM for valuation) and flow into Cascade 2.0 through established integrations. CTL **does not replace any of this**. *(Vendor names below are industry examples for context, not statements about specific Cascade integrations.)*

| Step                                            | Typical capture pattern (industry examples)                                                                                                                                     | Inherently manual element (at the vendor)                          |
| ----------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------ |
| **Property Preservation & Eviction**            | Field-service vendors *(e.g., Safeguard, MCS, Cyprexx, Five Brothers)* dispatch contractors; photos + condition reports typically flow back via VOM feeds. Eviction milestones posted by attorney networks. | Contractor on-site inspection; attorney free-text status notes.    |
| **Title Curative / HOA / Liens / Code Violations** | Title vendors *(e.g., ServiceLink, Stewart, FNF, Old Republic)* deliver title commitments + Schedule B PDFs. HOA estoppels often via HomeWiseDocs / Sperlonga. Code violations sourced from BuildFax / ATTOM / municipal portals. | Title examiner drafting Schedule B; mgmt company issuing estoppel. |
| **Valuation (BPO + AVM)**                       | Brokers submit BPOs through valuation platforms; AVM signals come from automated models — either third-party AVMs *(e.g., CoreLogic, HouseCanary)* or proprietary in-house models *(e.g., Xome's XVM)*.                                | Broker drive-by/interior inspection and comp selection.            |

The "manual" piece here is the **human in the field producing the input** (broker, inspector, examiner). Their output flows into the servicing platform as structured data + attached documents. **That stays exactly where it is.**

### Layer 2 — Validation & verdict (the CTL gate): *this is what the agent replaces*

Once vendor data has landed, somebody has to (1) read across all of it, (2) validate it is sufficient/fresh/internally consistent, (3) interpret unstructured pieces (Schedule B legal prose, inspector remarks, BPO-vs-AVM variance), (4) apply the right investor + state + program policy, and (5) declare a verdict with citations.

Today this is handled by a thin mix of:

- **(a) Hardcoded rules** (in workflow gateways, microservices, or both) — can only check *presence* and *recency* ("is BPO < 90 days old? is field populated?"). Cannot interpret prose, cannot reconcile vendor disagreements, cannot keep up with quarterly policy churn across investors × states × programs.
- **(b) Manual analyst review** — the bottleneck. Analyst reads across Cascade screens (where vendors have already submitted their data) plus investor + state policy bulletins, weighs the evidence, and types the verdict.

```mermaid
flowchart LR
    subgraph CAPTURE["LAYER 1 — Data Capture (UNCHANGED)"]
        direction TB
        V1[Field Services Vendors<br/>Property Pres. + Eviction]
        V2[Title / HOA / Code<br/>Sources]
        V3[Valuation Inputs<br/>BPO + AVM / XVM]
    end

    subgraph TODAY["LAYER 2 TODAY — Validation & Verdict"]
        direction TB
        R[Brittle hardcoded rules<br/>field-presence + recency only]
        H[Manual analyst<br/>reads + interprets + decides]
    end

    subgraph CTL["LAYER 2 WITH CTL — Validation & Verdict"]
        direction TB
        AGENT[Multi-agent investigation<br/>+ RAG policy lookup<br/>+ AI judge<br/>+ deterministic policy enforcer<br/>+ HITL on low confidence]
    end

    CAPTURE --> TODAY
    CAPTURE --> CTL
    TODAY -.replaced by.-> CTL

    style CAPTURE fill:#eceff1,stroke:#455a64
    style TODAY  fill:#ffebee,stroke:#c62828
    style CTL    fill:#e8f5e9,stroke:#2e7d32
```

> **In one line:** vendors keep capturing the data the same way; CTL takes over the **validation and verdict on top of that data** — the piece that today is either an analyst reading Schedule B by eye or a hardcoded rule that can only ask *"is this field populated?"* It does **not** replace the vendor capture above it, and it does **not** replace the deterministic governance (policy enforcer, HITL routing) below it.

---

## 4. Business-Friendly View — Who Wins, and How

```mermaid
flowchart TB
    subgraph TODAY["BEFORE — Manual CTL"]
        T1[Vendors push data into Cascade<br/>via portals / APIs — Title /<br/>HOA / Code / BPO / AVM /<br/>Occupancy / Field Services]
        T2[Analyst reads across<br/>Cascade screens + investor<br/>and state policy PDFs]
        T3[Types verdict<br/>into Camunda task]
        T1 --> T2 --> T3
    end

    subgraph AFTER["AFTER — Agentic CTL"]
        A1[Asset arrives<br/>at CTL gate]
        A2[3 specialist agents<br/>investigate in parallel]
        A3[Reflection agent<br/>synthesizes findings<br/>into structured verdict]
        A4[Independent AI judge<br/>verifies grounding]
        A5[Verdict + evidence<br/>+ citations published]
        A1 --> A2 --> A3 --> A4 --> A5
    end

    subgraph WINS["Business Outcomes"]
        W1[⏱ Faster<br/>time-to-list]
        W2[💰 Lower<br/>carrying cost]
        W3[📑 Defensible<br/>audit trail]
        W4[👥 Analysts focus<br/>on hard cases]
        W5[🔄 Policy changes =<br/>content update]
    end

    TODAY -.replaced by.-> AFTER
    AFTER --> WINS

    style TODAY fill:#ffebee,stroke:#c62828
    style AFTER fill:#e8f5e9,stroke:#2e7d32
    style WINS fill:#e3f2fd,stroke:#1565c0
```
