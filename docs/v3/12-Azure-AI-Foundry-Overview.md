# Azure AI Foundry Project Overview and Solution Architecture Guide

1. Full stack view

Users / Apps
→ Corporate Network / VNet
→ Azure AI Foundry (AI Control Plane)

Foundry handles:

prompts
agents
evaluations
guardrails
model routing

Backend services:

Foundry
→ Azure OpenAI / Model Endpoints
→ Azure AI Search (RAG / Vector DB)
→ Storage Account (data / logs / artifacts)
→ Key Vault (secrets / keys)


Client VNet
→ Private Endpoint: Azure AI Foundry
→ Private Endpoint: Azure OpenAI
→ Private Endpoint: Azure AI Search
→ Private Endpoint: Storage Account
→ Private Endpoint: Key Vault


## 1. Portal role and mental model

Azure AI Foundry is a consolidated workspace for designing, building, evaluating, securing, and operating AI applications on Azure using both Microsoft and partner frontier models (OpenAI, Anthropic, etc.).[page:1]  
A Foundry *project* (like `xm-ds-claude-opus-foundry`) represents a solution space where you manage models, data, agents, evaluations, and observability under a single resource and governance boundary.[page:1]  
From a solution architect perspective, the portal is the control plane; your runtime applications (APIs, web apps, background jobs, batch pipelines) call the project’s endpoints using keys and identity to invoke models and agents.[page:1]

At a high level, the capabilities group into four main stages of the AI lifecycle:[page:1]

- Define and explore models and use cases.[page:1]  
- Build and customize solutions (agents, apps, and tuned models).[page:1]  
- Observe and optimize behavior and performance.[page:1]  
- Protect and govern usage, risks, and compliance.[page:1]

---

## 2. Key navigation areas and building blocks

### 2.1 Global navigation rail (left side)

The navigation rail in your project surfaces the core building blocks of solutioning.[page:1]

- **Overview**  
  Entry point that summarizes project identity, endpoints, keys, and prescriptive “getting started” steps across define, build, observe, and govern.[page:1]

- **Model catalog**  
  Central directory of available base and fine‑tuned models you can browse, filter, and select for deployment.[page:1]  
  Architecturally this is where you decide model families (GPT‑style, Claude, vision, embedding, etc.) and map them to solution requirements like latency, context length, and cost.[page:1]

- **Playgrounds**  (refer bottom for details)
  Hosted, browser‑based sandboxes for prompt experimentation, few‑shot design, and rapid prototyping without writing code.[page:1]  
  These become living specifications for prompts that you later move into agents, SDK code, or templates.[page:1]

- **Build and customize** (section)

  - Agents – no/low‑code construction of AI agents that orchestrate models, tools, and data.[page:1]  
  - Templates – pre‑built solution accelerators and sample flows for common patterns (chat, copilots, retrieval, etc.).[page:1]  
  - Fine‑tuning – workflows to adapt base models to your domain data for improved relevance and accuracy.[page:1]  
  - Content Understanding – managed pipelines to index, chunk, and understand unstructured content as a foundation for RAG and knowledge experiences.[page:1]

- **Observe and optimize** (section)

  - Tracing (Preview) – request‑level traces across prompts, tool calls, and downstream services to debug and optimize flows.[page:1]  
  - Monitoring – application analytics and telemetry for throughput, latency, success rates, and usage patterns.[page:1]

- **Protect and govern** (section)

  - Evaluation (Preview) – structured quality and safety assessment against test sets and metrics.[page:1]  
  - Guardrails + controls – content filters, safety policies, and rule‑based controls attached to endpoints or agents.[page:1]  
  - Risks + alerts (Preview) – runtime risk detection with alerting and incident triage.[page:1]  
  - Governance (Preview) – organizational policies, approvals, lineage, and centralized oversight of AI assets.[page:1]

- **Azure OpenAI**

  - Stored completions – managed storage and replay of generated outputs, used for analytics and compliance.[page:1]  
  - Batch jobs – large‑scale asynchronous inference workloads (e.g., backfills, document processing).[page:1]

- **My assets**

  - Data + indexes – logical container for data sources, vector indices, and knowledge assets referenced by agents and applications.[page:1]  
  - Models + endpoints – surfacing of deployed models, including base and custom models, with endpoint metadata.[page:1]

- **Management center**  
  Cross‑project administration for users, quota, cost tracking, and resource connections, giving a centralized operational view.[page:1]

### 2.2 Project overview panel

The Overview page for `xm-ds-claude-opus-foundry` exposes several implementation‑critical elements.[page:1]

- Project name and description  
  Provides human context for the solution; not functional but important for governance and discovery.[page:1]

- Endpoints and keys  
  - “View all endpoints” links to all service deployments bound to this project.[page:1]  
  - A project‑level API key and project endpoint are exposed for calling deployed models via a single consolidated URL.[page:1]  
  - Example endpoint format:  
    `https://res-xm-datascience-claude-opus-f.services.ai.azure.com/api/projects/xm-ds-claude-opus-foundry`.[page:1]  
  - This endpoint routes to the various model deployments and agents you configure, acting as a logical “front door” for your AI capabilities.[page:1]

- Project details  
  - Project resource ID: the full ARM path to the project, used for IaC, scripts, and cross‑service linkage.[page:1]  
  - Subscription, resource group, location (`eastus2`) to anchor billing, locality, and compliance.[page:1]  
  - Shortcuts for adding users, viewing quota, connecting resources, and tracking costs via the management center.[page:1]

- Lifecycle guide (“Nail the basics with these steps”)  
  - Define + explore: choose models and experiment in playgrounds.[page:1]  
  - Build + customize: agents, templates, and fine‑tuning.[page:1]  
  - Observe + optimize: tracing and monitoring.[page:1]  
  - Protect + govern: evaluation, guardrails, risks and alerts, governance.[page:1]

- Learning content and templates  
  Links to templates for core scenarios and tutorials such as “Build and deploy a copilot” or “Build a custom chat app in Python,” plus the Microsoft Foundry SDK references.[page:1]

---

## 3. AI solutioning capabilities by lifecycle stage

### 3.1 Define and explore

Capabilities:[page:1]

- Model catalog for scanning available models, their capabilities, and deployment options.[page:1]  
- Playgrounds to quickly test prompts, temperature, system messages, and tool wiring scenarios interactively.[page:1]  
- Content Understanding to explore how documents and unstructured content are ingested and represented before building full RAG pipelines.[page:1]

Architect patterns enabled:

- Model selection and benchmarking across providers without switching tools.  
- Early UX discovery with business stakeholders using playgrounds instead of code.  
- Rapid validation of retrieval strategies and indexing approaches.

### 3.2 Build and customize

Capabilities:[page:1]

- Agents: configure multi‑step, tool‑using AI agents that encapsulate prompts, grounding data, and tool integrations.[page:1]  
- Templates: start from reference implementations for chat, copilots, RAG, classification, document processing, etc.[page:1]  
- Fine‑tuning: run domain adaptation for specific tasks like intent classification, domain‑specific Q&A, or structured extraction.[page:1]  
- Content Understanding: define indexes and semantic structures over data sources to reuse across agents and apps.[page:1]

Architect patterns enabled:

- Encapsulated “agent as an application” surfaces which can be exposed as APIs or embedded in UIs.  
- Reusable templates as canonical architectures for new projects, ensuring consistency across teams.  
- Separation of concerns between base models, tuned variants, and the pipelines that manage them.

Development experience:

- Portal‑first design with code export (via SDK snippets and templates) so you can transition prototypes into production repos.[page:1]  
- Integration with SDKs (e.g., Microsoft Foundry SDK) for CI/CD pipelines and infrastructure as code.[page:1]

### 3.3 Observe and optimize

Capabilities:[page:1]

- Tracing (Preview) with detailed spans for prompts, tool calls, retrieval queries, and downstream dependencies.[page:1]  
- Monitoring dashboards for application analytics (latency, throughput, error rates, token usage).[page:1]

Architect patterns enabled:

- Centralized observability for all AI flows in a project instead of per‑app instrumentation only.  
- Data‑driven prompt and agent optimization based on trace evidence.  
- Operational SLO/SLA tracking for AI workloads comparable to traditional microservices.

### 3.4 Protect and govern

Capabilities:[page:1]

- Evaluation (Preview) for systematic quality and safety assessment using evaluation sets and metrics.[page:1]  
- Guardrails + controls for content filtering, safety policy enforcement, lexical and semantic rules.[page:1]  
- Risks + alerts (Preview) for runtime detection of policy violations or anomalous behavior.[page:1]  
- Governance (Preview) for global policies, lineage tracking, approvals, and organizational controls.[page:1]

Architect patterns enabled:

- Consistent application of safety policies across many endpoints and agents.  
- “Shift‑left” safety: evaluation and guardrail design occurs in the same workspace as build and testing.  
- Governance‑by‑design: asset lineage and policy binding are captured as part of normal project work.

---

## 4. Core AI building blocks

From an AI solution architecture perspective, the Foundry project exposes these primary building blocks you design around.

- Project endpoint and keys  
  - Single logical HTTP front door for all model and agent invocations in the project.[page:1]  
  - Secured via keys and Azure identity, used by applications, pipelines, and tools.[page:1]

- Models and deployments  
  - Base models from Azure OpenAI and other catalogs, plus your fine‑tuned variants.[page:1]  
  - Deployed as named endpoints surfaced under “Models + endpoints”.[page:1]

- Agents  
  - Configurable orchestrators that combine prompts, tools, data sources, and guardrails into higher‑level behaviors.[page:1]  
  - Can be invoked programmatically or wired into UX surfaces (web apps, bots, line‑of‑business systems).[page:1]

- Data + indexes  
  - Data assets and vector indexes used for grounding, retrieval, and content understanding.[page:1]  
  - Shared assets across multiple agents and solutions in the same project.[page:1]

- Observability artifacts  
  - Traces, logs, metrics, and stored completions used for debugging, performance tuning, and compliance.[page:1]

- Safety and governance assets  
  - Guardrail configurations, evaluation suites, risk policies, and governance rules maintained alongside the AI assets.[page:1]

These let you define a layered architecture: models and data at the foundation, agents and endpoints as the service layer, and applications on top consuming these via SDKs and APIs.[page:1]

---

## 5. Non‑functional capabilities and NFR support

While the portal page does not list every non‑functional characteristic explicitly, it surfaces several NFR‑relevant levers for an architect.[page:1]

### 5.1 Security and access control

- API keys  
  The Overview page exposes a project‑scoped API key for calling endpoint(s), implying key‑based authentication for service access.[page:1]

- Azure RBAC and user management  
  “Add users” and the Management center indicate integration with Azure roles and permissions for project governance.[page:1]

- Guardrails + controls  
  Safety policies and content filters contribute to security and misuse prevention at the application layer.[page:1]

### 5.2 Reliability and availability

- Regional deployment  
  The project location (`eastus2`) is visible, indicating data residency and regional redundancy characteristics anchored in the underlying Azure infrastructure.[page:1]

- Monitoring and tracing  
  First‑class observability supports operational reliability by enabling detection and diagnosis of failures and performance issues.[page:1]

- Batch jobs and stored completions  
  Decoupled batch processing and stored outputs support resilient processing and replays for critical workloads.[page:1]

### 5.3 Performance and scalability

- Model selection  
  The Model catalog allows trade‑offs between model sizes and capabilities, implicitly allowing you to tune performance versus quality.[page:1]

- Batch jobs  
  Dedicated interfaces for batch workloads enable horizontally scalable, asynchronous processing instead of synchronous, user‑facing calls.[page:1]

- Monitoring metrics  
  Monitoring provides latency and throughput metrics from which you derive SLOs and scaling policies.[page:1]

### 5.4 Cost management

- “Track costs” and quota views  
  The Overview and Management center expose cost tracking, quota usage, and controls, enabling you to manage spend at the project level.[page:1]

- Batch vs online inference  
  Architectural support for choosing between real‑time and batch processing enables cost‑efficient patterns per workload.[page:1]

### 5.5 Compliance, risk, and auditability

- Governance (Preview)  
  Dedicated governance workspace for policies, lineage, and central oversight of AI use.[page:1]

- Risks + alerts (Preview)  
  Built‑in risk detection and alerts enable compliance monitoring and incident response.[page:1]

- Evaluation  
  Quality and safety evaluations produce artefacts useful for regulatory reporting and internal approvals.[page:1]

- Stored completions  
  Keeping generated outputs can support auditing and investigations for high‑risk domains.[page:1]

---

## 6. Example end‑to‑end solution blueprint

For a concrete mental model, consider building a customer‑facing mortgage servicing copilot using this Foundry project as the backbone.

1. Define and explore  
   - Use the Model catalog to select a suitable GPT‑class model.[page:1]  
   - Use Playgrounds to prototype the customer service prompt, style, and constraints with sample interactions.[page:1]

2. Build and customize  
   - Configure Content Understanding to index knowledge sources like policy PDFs, product guides, and historical FAQs.[page:1]  
   - Create an Agent that uses the chosen model plus the knowledge index and specific tools (e.g., an internal API for account lookup).[page:1]  
   - If needed, fine‑tune a model on historical conversation transcripts to improve domain fluency.[page:1]

3. Expose and integrate  
   - Deploy the agent or model as an endpoint listed under Models + endpoints and confirm its project endpoint URL.[page:1]  
   - Integrate your web or contact‑center application using the Microsoft Foundry SDK and the project endpoint/key from the Overview page.[page:1]

4. Observe and optimize  
   - Enable Tracing to capture detailed spans for user conversations and tool calls.[page:1]  
   - Use Monitoring to track latency, errors, and token usage; tune prompts, index configuration, or model choice based on telemetry.[page:1]

5. Protect and govern  
   - Configure Guardrails + controls to block disallowed topics, redact sensitive fields, and enforce tone.[page:1]  
   - Define Evaluation runs with test conversation sets to track quality over iterations.[page:1]  
   - Use Risks + alerts and Governance to enforce internal AI usage policies and demonstrate compliance.[page:1]

---

## 7. How to read this project page as an architect

When you land on a Foundry project Overview page like `xm-ds-claude-opus-foundry`, you can systematically interpret it as follows.[page:1]

- Identity and boundaries  
  Name, resource ID, subscription, resource group, and location define the blast radius for cost, security, and compliance.[page:1]

- Integration contract  
  The project endpoint URL and keys define how any external system will call into your AI services.[page:1]

- Lifecycle readiness  
  Presence of configured agents, data indexes, monitoring, evaluation, and guardrails indicates maturity of the solution in terms of reliability, safety, and governance.[page:1]

- Operational levers  
  Management center, quota, cost tracking, and user management show where to control scalability, access, and budgets.[page:1]

Treat the page not just as a console, but as the high‑level architecture sheet for your AI solution: it tells you which capabilities you have already operationalized and which ones are still gaps to be addressed.[page:1]

---

## 8. Azure AI Foundry vs Azure OpenAI Service

A frequent source of confusion: these are **not** competing products. Azure OpenAI Service is one of several model providers **inside** Azure AI Foundry. Pick the right mental model for the conversation:

| Dimension | Azure OpenAI Service (AOAI) | Azure AI Foundry |
|-----------|------------------------------|------------------|
| **What it is** | A single Cognitive Services resource that hosts OpenAI models (GPT‑4o, GPT‑4o‑mini, o‑series, embeddings, DALL·E, Whisper, etc.) as deployments. | A platform (hub + projects) for the full AI app lifecycle: model catalog, agents, prompt flow, evaluations, tracing, guardrails, governance — across many model providers (Azure OpenAI, Meta, Mistral, Cohere, NVIDIA, Microsoft, Hugging Face, etc.). |
| **Scope** | Model inference only. | Models **plus** orchestration, RAG, agents, evaluation, observability, safety, governance. |
| **Resource shape (ARM)** | `Microsoft.CognitiveServices/accounts` of kind `OpenAI`. One resource = one set of deployments + one set of keys + one regional endpoint. | `Microsoft.MachineLearningServices/workspaces` of kind `Hub` and child `Project`. A Foundry project can **reference** one or more AOAI resources (and AI Search, Content Safety, Storage, Key Vault, etc.) as Connections. |
| **Endpoint shape** | `https://<acct>.openai.azure.com/openai/deployments/<deployment>/chat/completions?api-version=...` | `https://<acct>.services.ai.azure.com/api/projects/<project>` — single front door that routes to model deployments **and** agents, threads, evaluations, tracing. |
| **Authentication** | API key OR Entra (`Cognitive Services OpenAI User` role). | Entra everywhere (`Azure AI Developer` on the project). Each connection inside the project may still use a key, but the project itself uses Entra. |
| **Models available** | OpenAI models only. | Full **Model Catalog**: Azure OpenAI deployments + Models‑as‑a‑Service (serverless Mistral, Llama, Cohere Command R+, Phi), Models‑as‑a‑Platform (managed compute for OSS / Hugging Face), and Microsoft first‑party (Phi family). |
| **Pricing surface** | Per‑token (or PTU) for the AOAI resource. | Each underlying resource bills separately (AOAI, AI Search, App Insights, etc.). Foundry itself adds no inference cost; orchestration and tracing are built on the connected resources. |
| **Quota** | Per AOAI account, per region, per model. Subscription‑scoped. Does **not** migrate. | Same — quota lives on the underlying AOAI account that the project connects to. Foundry inherits the quota of the connected accounts. |
| **Networking** | Public endpoint by default; Private Endpoint + custom subdomain supported. | Hub‑level managed virtual network with private endpoints to each connected resource — meant for full network isolation including the portal. |
| **SDKs** | `Azure.AI.OpenAI` (C#), `openai` (Python with `azure_endpoint=`), REST. | `Azure.AI.Projects` / `azure-ai-projects` — opens the project, lists connections, drives agents, evaluations, tracing. **Inference itself still uses `Azure.AI.OpenAI` or `Azure.AI.Inference` (or the OpenAI SDK pointed at a model‑inference endpoint).** |
| **Agent runtime** | Assistants API (legacy, narrow) and the new Azure OpenAI Assistants v2. | Azure AI **Agent Service** — multi‑model, tool‑rich (Functions, OpenAPI, Logic Apps, AI Search, Bing, Code Interpreter, File Search), with built‑in tracing into the project. |
| **RAG / knowledge** | "On Your Data" feature: AOAI auto‑retrieves from a single AI Search index per call. Limited control. | First‑class Data + Indexes assets, Content Understanding skillset, agents can attach multiple indexes; full control of chunking, retrievers, reranking. |
| **Evaluations** | Not provided. Roll your own. | Built‑in Evaluations blade (groundedness, relevance, coherence, fluency, safety) plus dataset management and scheduled runs. |
| **Tracing / observability** | Diagnostic settings → Log Analytics; no LLM‑aware UI. | Project Tracing blade with span‑level prompt/response/tool‑call view, bound to a chosen Application Insights resource; OpenTelemetry GenAI semantic conventions. |
| **Safety** | Built‑in content filters per deployment (configurable). | Same content filters **plus** dedicated Azure AI Content Safety connection (Prompt Shields, Groundedness Detection, Protected Material, custom blocklists) and project‑wide Guardrails + Controls. |
| **Governance** | RBAC on the account, diagnostic logs. | RBAC plus the **Governance (Preview)** blade — lineage, approvals, asset registry, policy enforcement across projects in a hub. |
| **Fine‑tuning** | Yes (per model, per region). | Yes — exposed in the Fine‑tuning blade and surfaced as deployments inside the project. |
| **Use case fit** | "I just need GPT‑4o inference from my app and I'll handle orchestration, RAG, eval, tracing myself." | "I'm building a full agentic application with multiple models, RAG, evaluations, and need a portal where PMs/data scientists/security can collaborate." |

### How they relate (the picture)

```
┌─────────────────────────────────────────────────────────────────────┐
│ Azure AI Foundry Hub  (Microsoft.MachineLearningServices/workspaces)│
│ ┌─────────────────────────────────────────────────────────────────┐ │
│ │ Foundry Project  (e.g., xm-ds-claude-opus-foundry)              │ │
│ │                                                                 │ │
│ │  Connections ───────────────────────────────────────────────┐   │ │
│ │   • Azure OpenAI account (gpt-4o, gpt-4o-mini, embeddings)──┼─► │ │  ← AOAI resource lives here, referenced by the project
│ │   • Azure AI Search                                         │   │ │
│ │   • Azure AI Content Safety                                 │   │ │
│ │   • Application Insights                                    │   │ │
│ │   • Storage account / Key Vault (auto-provisioned)          │   │ │
│ │                                                             │   │ │
│ │  Project capabilities                                       │   │ │
│ │   • Agents, Threads, Tools (OpenAPI / Functions / AI Search)│   │ │
│ │   • Data + Indexes                                          │   │ │
│ │   • Evaluations                                             │   │ │
│ │   • Tracing + Monitoring                                    │   │ │
│ │   • Guardrails, Risks + Alerts, Governance                  │   │ │
│ └─────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────┘
```

The Foundry project does not replace the AOAI resource — it **wraps and orchestrates** it alongside other services.

### Which one do I provision?

- **Need only model inference, integrated into existing code?** Provision an Azure OpenAI resource directly. SDK: `Azure.AI.OpenAI`. Simplest, cheapest control plane, fewest moving parts.
- **Need multi‑model choice, agents, evaluations, tracing, or a portal for non‑developers?** Provision a Foundry Hub + Project, then create (or connect) an AOAI resource as one of the project's Connections. SDK: `Azure.AI.Projects` to drive the project; the same `Azure.AI.OpenAI` or `Azure.AI.Inference` for the actual inference calls.
- **Already on AOAI and want to add Foundry capabilities?** Yes — create a Foundry project in the same subscription/region and add your existing AOAI resource as a Connection. No data migration needed; deployments and quota stay where they are.

### Common misconceptions

1. **"Foundry replaces Azure OpenAI."** No — Foundry consumes AOAI (and other model providers) as Connections.
2. **"AOAI and Foundry have different keys / quotas."** No — the AOAI resource owns the keys and TPM/RPM quota. Foundry inherits whatever AOAI provides.
3. **"I have to use Foundry to call GPT‑4o."** No — direct AOAI calls work fine. Use Foundry when you want the surrounding capabilities (agents, eval, tracing, governance), not just inference.
4. **"Foundry's project endpoint is just a renamed AOAI endpoint."** Different shape (`/api/projects/<project>`) and routes to agents, threads, tracing, evaluations — not just model deployments.
5. **"Foundry's Agent Service is the same as AOAI Assistants."** Related lineage, but Foundry Agent Service is multi‑model, multi‑tool (including OpenAPI, AI Search, Bing, Logic Apps), and ships with built‑in tracing into the project. AOAI Assistants is OpenAI‑models‑only.

### Migration / coexistence pattern

- Existing AOAI workloads keep working unchanged.
- Create a Foundry project, add the existing AOAI account as a Connection.
- New capabilities (agents, evaluations, project‑level tracing) light up immediately without touching the AOAI deployments.
- Optional next step: route inference SDK calls through the project endpoint instead of the AOAI endpoint, so RBAC, audit, and tracing converge.

---

# Playgrounds — what can you actually analyze?

**Short answer:** No, playgrounds are not just for "output quality." They're a sandbox for analyzing several dimensions of model + prompt + tooling behavior before you commit to code.

## C# analogy
Think of a playground like **LINQPad for LLMs**: a scratch surface where you test a query (prompt) against a data source (model) with knobs (parameters), inspect the result, the timing, the cost, and the call shape — without spinning up a full project.

## What you can analyze in a playground

### 1. Output quality (the obvious one)
- Relevance, correctness, tone, format adherence
- Hallucination rate across reworded prompts
- Compare same prompt across models (GPT-4o vs GPT-4o-mini vs o-series, etc.)

### 2. Prompt engineering
- System prompt vs user prompt behavior
- Few-shot examples — do they actually help?
- Prompt sensitivity (small wording changes → big output changes = brittle prompt)
- Token usage of the prompt itself (prompt bloat)

### 3. Parameter / sampling behavior
- `temperature`, `top_p`, `frequency_penalty`, `presence_penalty`
- `max_tokens` truncation behavior
- Determinism with `seed` (where supported)
- `response_format` (JSON mode / structured outputs) — does the model actually obey the schema?

### 4. Latency & throughput
- Time-to-first-token (TTFT) vs total completion time
- Streaming vs non-streaming
- Effect of prompt length on latency
- Model-to-model latency comparison

### 5. Cost
- Input tokens vs output tokens per call
- Estimate $ per request before scaling to production
- Compare cost/quality tradeoff between model tiers

### 6. Tool / function calling
- Does the model pick the right tool?
- Argument shape correctness against your JSON schema
- Parallel tool calls behavior
- Multi-turn tool loops

### 7. Structured output / schemas
- JSON schema adherence
- Pydantic / strict-mode validation
- Failure modes when the model can't fit the schema

### 8. Retrieval-Augmented Generation (RAG) — in "Chat with your data" / Foundry playgrounds
- Which chunks the retriever returned (citations)
- Whether the model actually grounded on them vs hallucinated
- Chunk size / overlap / top-K tuning
- Hybrid vs vector vs keyword search comparison

### 9. Safety / content filters
- Which categories trigger (hate, violence, self-harm, sexual, jailbreak, prompt injection)
- Severity thresholds (low / medium / high)
- Protected-material / groundedness detection (Azure AI Content Safety)

### 10. Image / multimodal inputs
- Vision model accuracy on your specific images
- OCR-like extraction quality
- Audio (Whisper / realtime) transcription quality

### 11. Assistants / Agents
- Thread state behavior
- File search / code interpreter tool usage
- Multi-step reasoning traces

### 12. Export to code
- Most playgrounds (OpenAI, Azure AI Foundry, Anthropic Console) emit the **exact HTTP / SDK call** you just ran — useful for copy-pasting into your app once the prompt is tuned.

## When to leave the playground
Playgrounds are for **exploration**, not evaluation at scale. Once you have a candidate prompt/model, move to:
- **Evaluation harnesses** (Azure AI Foundry Evaluations, promptfoo, Ragas, DeepEval) for batch scoring across a dataset
- **Tracing** (Application Insights, LangSmith, Langfuse) for production observability
- **A/B testing** for real-user signal