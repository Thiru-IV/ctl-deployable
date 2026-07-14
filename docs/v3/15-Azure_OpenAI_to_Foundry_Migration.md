# Migrating a Disparate Azure OpenAI Agentic Solution to Microsoft Foundry

> Concise migration playbook. Current as of Jul 2026 (Foundry resource model / new portal `ai.azure.com`).
> Scope: a CTL-like solution using standalone Azure OpenAI + separately-provisioned Azure services (Search, Content Safety, Storage, Key Vault, App Insights) → consolidated under the **Microsoft Foundry resource** model.

---

## 1. Why migrate (the business case)

| Driver | "Disparate" today | Foundry target |
|---|---|---|
| **Deduplication** | Each app/team spins its own AOAI, Search, storage, KV | One Foundry resource; projects reuse shared model deployments + connections |
| **Security** | Keys scattered in appsettings; per-service RBAC | Managed identity + Entra RBAC at resource *and* project scope; BYO Key Vault; CMK |
| **Governance** | Policy applied per resource, inconsistently | Central control-plane; existing AOAI Azure Policy + RBAC **carry over** (same `Microsoft.CognitiveServices` provider) |
| **Cost** | Fragmented, hard to attribute | Unified cost analysis per project; PTU/batch/global deployment choices |
| **Control** | Multiple SDKs, connection strings | One Foundry API + SDK; connections managed centrally |
| **Observability** | App Insights bolted on per service | Azure Monitor scoped resource + project; agent/eval/token metrics built in |

Key fact: **Foundry (`Microsoft.CognitiveServices/accounts`, kind `AIServices`)** is a superset of Azure OpenAI. Projects are **child resources** (`accounts/projects`). IT governs at the resource level; devs self-serve projects as "folders."

---

## 2. Study / discovery (before touching anything)

1. **Inventory** every AI-touching resource: AOAI accounts + deployments, model versions, embedding models, Search indexes, Content Safety, Storage, Key Vault, App Insights, any hub/hub-project.
2. **Map consumers**: which apps/agents call which endpoint; connection strings vs. managed identity today.
3. **Classify data residency**: which prompts/data must stay in region/zone → drives deployment type (Global vs Data Zone vs Regional).
4. **Quota audit**: TPM/PTU per model per region — **quota does NOT migrate**, request on target first.
5. **Region/feature check**: confirm target region supports Agents, Evaluations, required models ([region-support]).
6. **Networking baseline**: public vs private endpoint, VNet requirements for agents.
7. **Identify hub dependency**: some features still need a hub (fine-tuning ML stack, some tools) — check the support matrix before decommissioning.

---

## 3. Resource hierarchy (ARM view): before vs after

There are **two possible "before" states** — decide which one you're migrating from.

**Before (A) — disparate standalone Azure OpenAI (no hub; each resource stands alone):** *(Azure OpenAI Service GA: January 2023)*

```
Subscription
└─ Resource Group
   ├─ Microsoft.CognitiveServices/accounts (kind=OpenAI)
   │  └─ deployments (gpt-4o, embeddings, judge)
   ├─ Microsoft.Search/searchServices
   ├─ Microsoft.CognitiveServices/accounts (kind=ContentSafety)
   ├─ Microsoft.Storage/storageAccounts
   ├─ Microsoft.KeyVault/vaults
   └─ Microsoft.Insights/components (App Insights)
   (no shared governance; each wired app-by-app)
```

**Before (B) — hub-based project model (the "AI Hub" resource type):** *(hub model shipped with Azure AI Studio GA May 2024)*

> **Portals ≠ resources — that's the disconnect.** A *portal* is just a UI; a *resource* is what's deployed in ARM. 

The **AI Hub** is a **resource** (`MachineLearningServices/workspaces, kind=hub`) — it is not "owned by" any portal. 

**Two separate portals can open the same hub:** Foundry (classic) at `ai.azure.com` and **ML Studio** at `ml.azure.com`. 

So ML Studio isn't *part of* Microsoft Foundry — it's a different UI that can open the same hub resource. 

(Brand history of the Foundry UI: Azure AI Studio, preview Nov 2023 → GA May 2024 → renamed Azure AI Foundry Nov 2024 → now Microsoft Foundry.)
>
> | | What it is | Examples |
> |---|---|---|
> | **Portal (UI)** | A tool you log into | Microsoft Foundry (`ai.azure.com`) · ML Studio (`ml.azure.com`) |
> | **Resource (ARM)** | What's actually deployed | AI Hub · Foundry account · Azure OpenAI · Storage |
>
> The diagram below is the **AI Hub resource model**, not any portal.


```
Subscription
└─ Resource Group
   ├─ Microsoft.MachineLearningServices/workspaces (kind=hub)   ← AI Hub (governance/parent)
   │  ├─ .../workspaces (kind=project)      ← hub-based project(s)
   │  │  └─ assets: (preview) agents · indexes · flows · files
   │  └─ connections (hub-level, shared by hub projects)
   │        │  references ↓
   ├─ Microsoft.CognitiveServices/accounts (kind=AIServices | OpenAI)  ← model access (connected)
   ├─ Microsoft.Storage/storageAccounts     ← REQUIRED by hub
   ├─ Microsoft.KeyVault/vaults             ← REQUIRED by hub
   ├─ Microsoft.Search/searchServices
   └─ Microsoft.Insights/components (App Insights)
   (hub is built on the Azure Machine Learning stack; needs its own Storage + Key Vault)
```

**After — Microsoft Foundry (hub-less: unified account + child projects):** *(Foundry resource + Foundry projects + Agent Service GA: May 2025, Build 2025)*

```
Subscription
└─ Resource Group
   ├─ Microsoft.CognitiveServices/accounts (kind=AIServices)   ← Foundry resource ("account", NO hub)
   │  ├─ .../deployments            (gpt-4o, embeddings, judge)   ← shared by all projects
   │  ├─ .../connections            (references → external resources, account-scoped)
   │  └─ .../projects               ← child resources (replace the old hub-based projects)
   │     ├─ project-A
   │     │  ├─ .../connections       (project-scoped, optional)
   │     │  └─ assets: agents · evaluations · files · threads
   │     └─ project-B ...
   │
   └─ Connected resources (independent, referenced via connections):
      ├─ Microsoft.Search/searchServices
      ├─ Microsoft.Storage/storageAccounts
      ├─ Microsoft.KeyVault/vaults
      └─ Microsoft.Insights/components (App Insights)
```

> **Hub vs Foundry account — don't conflate them.** The classic **AI Hub** = `Microsoft.MachineLearningServices/workspaces (kind=hub)` on the Azure ML stack, with hub-based **projects** as its children and mandatory Storage + Key Vault. The new **Foundry "account"** = `Microsoft.CognitiveServices/accounts (kind=AIServices)` — **no hub**, projects are direct children, and Storage/KV are optional references (not required to exist). Migration path B collapses the hub layer into the Foundry account.


---

## 4. Recommended target architecture

```
Foundry resource (governance boundary)
├─ Model deployments (gpt-4o, judge, embeddings)  ← reused by all projects
├─ Security: managed identity, RBAC, CMK, BYO Key Vault
├─ Connections (references only, NOT child resources) ──┐
└─ Projects (dev boundaries)                            │
   ├─ Project A (agents, evals, files, threads)         │
   └─ Project B ...                                     │
                                                        ▼
Connected resources (independent, separate governance boundaries):
   Azure AI Search · Storage · Key Vault · App Insights
```

- **Basic agent setup** = Microsoft-managed storage for threads/messages/files.
- **Standard agent setup** = bring-your-own Storage + Search + Cosmos → data isolated per project in *your* accounts (recommended for enterprise/regulated).

> **Important — what "under Foundry" really means.** Storage, Key Vault, AI Search, and App Insights are **independent Azure resources with their own governance boundaries**. They are **not** created as child resources of the Foundry account. Foundry only *references* them through a **connection** (a pointer + auth: managed identity, or a secret stored in Key Vault). The only true child resources of the Foundry account are **projects, model deployments, agents, and evaluations**. (Foundry can additionally auto-provide Microsoft-managed storage for the basic agent setup; standard setup overrides this with your own resources via connections.)

---

## 5. Migration steps

1. **Provision Foundry resource** (IaC — Bicep/Terraform; `kind=AIServices`). Reuse existing custom Azure Policy/RBAC.
2. **Connect existing AOAI** (or migrate deployments) so current model access continues uninterrupted.
3. **Create project(s)** — one per use case/team; inherit resource-level security/networking.
4. **Recreate connections** at *account level* — here "account" = the **Foundry resource** (`Microsoft.CognitiveServices/accounts`), **not** a hub. A connection is only a **reference** (pointer + auth) to an independent Azure resource; it does **not** move Search/Storage/KV inside Foundry. Two scopes: `accounts/connections` = **account/resource-level, shared by all projects** (use this for shared Search/Storage/KV); `accounts/projects/connections` = project-scoped only.
5. **Wire identity**: assign **Foundry User** to each dev principal + each project managed identity at resource scope; drop API keys from config.
6. **Migrate code**: swap connection strings for the **Foundry project endpoint**; use `AIProjectClient` + `DefaultAzureCredential` (Python) / `Azure.AI.Projects` (.NET). Upgrade to stable Agent SDK (class-structure changes vs preview).
7. **Rebuild agents**: preview agent state (threads/messages/files) does **not** transfer — recreate via code/IaC.
8. **Move guardrails**: reconfigure Content Safety as inline Foundry guardrails per deployment (input/output/tool-call scanning).
9. **Observability**: enable diagnostic settings → Log Analytics; adopt resource + project metrics (tokens, latency, eval outcomes, agent invocations).
10. **Validate** (see §7), then **clean up** redundant AOAI/hub resources (keep Foundry resource — it holds deployments/fine-tunes).

---

## 6. What transfers vs. what doesn't

| Transfers | Does NOT transfer |
|---|---|
| Model deployments, fine-tuned models | Preview Agent state (threads/messages/files) → recreate in code |
| Data files, vector stores | Open-source model deployments (unsupported on Foundry projects) |
| Assistants | Hub-project cross-access to new projects |
| Existing custom Azure Policy + RBAC actions | Auto cross-region failover (must design at app layer) |

---

## 7. Impact analysis & risks

| Area | Impact | Mitigation |
|---|---|---|
| **Code** | SDK/client + auth model change; endpoint replaces conn string | Feature-flag; run old + new side-by-side during cutover |
| **Agent state loss** | Preview threads/files not migrated | Re-provision via IaC; export any needed history first |
| **Quota** | Not migrated; first deploy can fail | Request TPM/PTU on target **before** provisioning |
| **Data residency** | Global deployments may route cross-region | Use Data Zone / Regional deployments where required |
| **Networking** | Private-endpoint / VNet-isolated agents need SDK/CLI (not portal) | Plan container-injection subnet delegated to `Microsoft.App/environments` |
| **Secrets** | Moving off key-based auth | BYO Key Vault connection; managed identity; CMK if required |
| **Feature parity** | Some capabilities still hub-only | Keep hub alongside Foundry until parity confirmed |
| **Cost model shift** | PTU vs pay-per-token vs batch | Model cost per deployment type before cutover |
| **Downtime** | Endpoint cutover | Blue/green: keep AOAI live until new path validated |

**Pre-rollout validation checklist:** models/features available in region · RBAC scoped correctly (resource + project) · private link / network isolation paths · CMK + Key Vault · quotas/limits.

---

## 8. Sequencing (low-risk order)

`Discovery → Provision Foundry (IaC) → Connect AOAI (no downtime) → Project + connections + identity → Migrate one non-critical app → Validate → Roll remaining apps → Cutover endpoints → Decommission duplicates`

---

## References
- Foundry architecture: `learn.microsoft.com/azure/ai-foundry/concepts/architecture`
- Migrate to Foundry projects: `learn.microsoft.com/azure/ai-foundry/how-to/migrate-project`
- Foundry rollout planning: `.../ai-foundry/concepts/planning`
- Sample Bicep/Terraform: `github.com/microsoft-foundry/foundry-samples/tree/main/infrastructure`
- SDK migration guide: `github.com/Azure/azure-sdk-for-python/.../AGENTS_MIGRATION_GUIDE.md`
