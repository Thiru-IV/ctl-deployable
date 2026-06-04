<#
    migration.config.psd1 — single source of truth for migrating the CTL Agent
    solution to a NEW Azure subscription. NO reference to the old subscription
    is retained anywhere in the codebase: this file + Migrate-ToNewSubscription.ps1
    are fully self-contained.

    Edit values here if you need to override defaults. Leave SubscriptionId/TenantId
    empty to use whatever 'az account show' returns after 'az login'.
#>
@{
    # ── Target subscription / tenant ───────────────────────────────────────
    # Leave empty to use the currently-logged-in az context.
    SubscriptionId = ''
    TenantId       = ''     # auto-discovered post-login; written into appsettings.json

    # ── Region ─────────────────────────────────────────────────────────────
    Location = 'eastus2'

    # ── Primary resource group + Foundry ───────────────────────────────────
    PrimaryResourceGroup = 'rg-ctlagent'
    AIHubName            = 'ctlagent-hub'
    AIProjectName        = 'ctlagent-project'

    # ── Primary model deployment ───────────────────────────────────────────
    PrimaryModel = @{
        Name      = 'gpt-4o'
        Publisher = 'azure-openai'
        Version   = '2024-08-06'
    }

    # ── Judge model (same Foundry account, separate deployment) ───────────
    # Per migration decision: keep judge in same region as primary (eastus2).
    # We create a SECOND deployment on the primary AOAI account rather than a
    # separate hub. The deployment NAME is what 'CTLAgent.JudgeModel.ModelId'
    # in appsettings.json points to.
    JudgeModel = @{
        DeploymentName = 'gpt-4o-judge'
        ModelName      = 'gpt-4o'
        ModelVersion   = '2024-08-06'
        SkuName        = 'Standard'
        Capacity       = 10
    }

    # ── Companion services ─────────────────────────────────────────────────
    ContentSafetyName = 'ctlagent-contentsafety'
    LanguageName      = 'ctlagent-language'        # Azure AI Language (PII)
    AppInsightsName   = 'ctlagent-appinsights'

    # ── Azure AI Search (RAG) ──────────────────────────────────────────────
    SearchServiceName  = 'ctlagent-search'
    SearchSku          = 'free'                    # free | basic | standard
    SearchIndexName    = 'ctl-policy-knowledge'
    EmbeddingModel     = 'text-embedding-3-small'
    EmbeddingDims      = 1536

    # ── Container deployment (ACR + Container Apps) ───────────────────────
    DeployResourceGroup = 'ctl-agent-rg'           # Deploy-CTL-Containers default
    AcrName             = 'ctlagentacr'            # must be globally unique; override if taken
    ContainerEnvName    = 'ctl-agent-env'

    # ── Foundry agent registration (optional) ─────────────────────────────
    # When $true, the wrapper will attempt to register the deployed ACA API
    # as a Foundry agent. Off by default — Foundry project naming is brittle
    # post-migration; enable only after verifying the project exists.
    RegisterFoundryAgent = $false
    FoundryAgentName     = 'ctl-asset-evaluator'

    # ── RAG content ────────────────────────────────────────────────────────
    RagKnowledgePath = 'config/rag-knowledge'      # relative to repo root

    # ── Smoke test ─────────────────────────────────────────────────────────
    SmokeTestPayload = 'payload.json'              # relative to repo root
    SmokeTestPath    = '/evaluate'
}
