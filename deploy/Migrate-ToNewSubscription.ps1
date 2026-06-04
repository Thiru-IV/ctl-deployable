<#
.SYNOPSIS
    One-shot migration of the entire CTL Agent solution to a NEW Azure
    subscription. Fully self-contained: requires only an authenticated az CLI
    session and the values in deploy/migration.config.psd1.

.DESCRIPTION
    Stages, all idempotent and re-runnable:
      0. Verify az login + target subscription, auto-discover TenantId.
      1. Provision primary Foundry + Content Safety + Language + App Insights + Search
         (delegates to scripts/Provision-AzureServices.ps1 -UpdateConfig).
      2. Create judge-model deployment on the SAME AOAI account, patch JudgeModel
         block in config/appsettings.json.
      3. Patch TenantId in ContentSafety + PiiFilter blocks with the new tenant.
      4. Run RAG indexer to populate the new Azure AI Search index.
      5. Build + deploy containers via deploy/Deploy-CTL-Containers.ps1
         (uses 'az acr build' — no local Docker required).
      6. (Optional) Register the deployed API as a Foundry agent.
      7. Smoke test POST {ApiFqdn}/evaluate with payload.json.
      8. Emit a final report of every provisioned resource + where its key landed.

.PARAMETER ConfigFile
    Path to migration.config.psd1. Default: deploy/migration.config.psd1.

.PARAMETER WhatIfPlan
    Print the plan and exit without making changes.

.PARAMETER SkipSmokeTest
    Skip the final HTTP smoke test (e.g. if the agent is private / behind a VNet).

.EXAMPLE
    # Standard end-to-end run after 'az login' to the new subscription:
    .\deploy\Migrate-ToNewSubscription.ps1

.EXAMPLE
    # Dry-run plan:
    .\deploy\Migrate-ToNewSubscription.ps1 -WhatIfPlan
#>
[CmdletBinding()]
param(
    [string] $ConfigFile    = (Join-Path $PSScriptRoot 'migration.config.psd1'),
    [switch] $WhatIfPlan,
    [switch] $SkipSmokeTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ── Helpers ─────────────────────────────────────────────────────────────────
function Write-Phase([string]$msg) {
    Write-Host ""
    Write-Host "════════════════════════════════════════════════════════════" -ForegroundColor DarkCyan
    Write-Host " $msg" -ForegroundColor Cyan
    Write-Host "════════════════════════════════════════════════════════════" -ForegroundColor DarkCyan
}
function Write-Info([string]$msg) { Write-Host "  $msg" -ForegroundColor Gray }
function Write-Ok([string]$msg)   { Write-Host "  [OK]   $msg" -ForegroundColor Green }
function Write-Warn2([string]$msg){ Write-Host "  [WARN] $msg" -ForegroundColor Yellow }
function Write-Err2([string]$msg) { Write-Host "  [FAIL] $msg" -ForegroundColor Red }

function Invoke-AzJson {
    param([Parameter(ValueFromRemainingArguments=$true)] [string[]]$AzArgs)
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $out = & az @AzArgs --only-show-errors 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw ("az " + ($AzArgs -join ' ') + " failed: " + ($out -join "`n"))
        }
        return ($out | Where-Object { $_ -notmatch '^\s*WARNING:' }) -join "`n"
    } finally { $ErrorActionPreference = $prev }
}

# ── Locate paths ────────────────────────────────────────────────────────────
$repoRoot   = Split-Path -Parent $PSScriptRoot
$scriptsDir = Join-Path $repoRoot 'scripts'
$configPath = Join-Path $repoRoot 'config/appsettings.json'
$apiConfigPath = Join-Path $repoRoot 'src/Cascade.CTL.Agent.Api/appsettings.json'

if (-not (Test-Path $ConfigFile)) { throw "Config file not found: $ConfigFile" }
$cfg = Import-PowerShellDataFile -Path $ConfigFile

# Resource-tracking ledger (printed at the end)
$ledger = [System.Collections.Generic.List[PSCustomObject]]::new()
function Add-Ledger([string]$service, [string]$name, [string]$detail) {
    $ledger.Add([PSCustomObject]@{ Service = $service; Name = $name; Detail = $detail })
}

# ── 0. Pre-flight ───────────────────────────────────────────────────────────
Write-Phase '0/8  Pre-flight: az CLI + target subscription'

try { az version --only-show-errors 2>$null | Out-Null }
catch { Write-Err2 "Azure CLI not found. Install: https://aka.ms/installazurecli"; throw }

$ctxRaw = az account show --only-show-errors 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Err2 "Not logged in to az. Run 'az login' against the NEW subscription, then re-run."
    throw "az login required"
}
$ctx = $ctxRaw | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($cfg.SubscriptionId)) {
    Write-Info "No SubscriptionId in config — using current az context."
} elseif ($ctx.id -ne $cfg.SubscriptionId) {
    Write-Info "Switching subscription: $($ctx.id) -> $($cfg.SubscriptionId)"
    az account set --subscription $cfg.SubscriptionId | Out-Null
    $ctx = az account show --only-show-errors | ConvertFrom-Json
}

$tenantId = if ([string]::IsNullOrWhiteSpace($cfg.TenantId)) { $ctx.tenantId } else { $cfg.TenantId }

Write-Ok "Subscription: $($ctx.name) ($($ctx.id))"
Write-Ok "Tenant      : $tenantId"
Write-Ok "Identity    : $($ctx.user.name)"
Write-Ok "Region      : $($cfg.Location)"

if ($WhatIfPlan) {
    Write-Phase 'WhatIf — execution plan'
    Write-Host @"
  Phase 1 : Provision-AzureServices.ps1 -UpdateConfig
              RG=$($cfg.PrimaryResourceGroup) Loc=$($cfg.Location)
              Hub=$($cfg.AIHubName) Project=$($cfg.AIProjectName)
              Model=$($cfg.PrimaryModel.Name) v=$($cfg.PrimaryModel.Version)
              ContentSafety=$($cfg.ContentSafetyName)
              Language     =$($cfg.LanguageName)
              AppInsights  =$($cfg.AppInsightsName)
              Search       =$($cfg.SearchServiceName) (sku=$($cfg.SearchSku))
  Phase 2 : Create AOAI deployment '$($cfg.JudgeModel.DeploymentName)' on primary account
              + patch JudgeModel block in config/appsettings.json
  Phase 3 : Patch TenantId=$tenantId into ContentSafety + PiiFilter blocks
  Phase 4 : Run Cascade.CTL.RAG.Indexer to populate index '$($cfg.SearchIndexName)'
  Phase 5 : Deploy-CTL-Containers.ps1
              RG=$($cfg.DeployResourceGroup) ACR=$($cfg.AcrName) Env=$($cfg.ContainerEnvName)
  Phase 6 : Register Foundry agent? $($cfg.RegisterFoundryAgent)
  Phase 7 : Smoke test POST {fqdn}$($cfg.SmokeTestPath) (skip=$SkipSmokeTest)
  Phase 8 : Print resource ledger
"@
    return
}

# ── 1. Primary provisioning ─────────────────────────────────────────────────
Write-Phase '1/8  Provision primary Foundry + companion services'

$provArgs = @(
    '-ResourceGroupName',          $cfg.PrimaryResourceGroup,
    '-Location',                   $cfg.Location,
    '-AIHubName',                  $cfg.AIHubName,
    '-AIProjectName',              $cfg.AIProjectName,
    '-ContentSafetyResourceName',  $cfg.ContentSafetyName,
    '-LanguageResourceName',       $cfg.LanguageName,
    '-AppInsightsName',            $cfg.AppInsightsName,
    '-AzureSearchServiceName',     $cfg.SearchServiceName,
    '-AzureSearchSku',             $cfg.SearchSku,
    '-ModelName',                  $cfg.PrimaryModel.Name,
    '-ModelPublisher',             $cfg.PrimaryModel.Publisher,
    '-ModelVersion',               $cfg.PrimaryModel.Version,
    '-UpdateConfig'
)
& (Join-Path $scriptsDir 'Provision-AzureServices.ps1') @provArgs
if ($LASTEXITCODE -ne 0) { throw "Provision-AzureServices.ps1 exited with code $LASTEXITCODE" }

Add-Ledger 'Resource Group (primary)' $cfg.PrimaryResourceGroup $cfg.Location
Add-Ledger 'Foundry Hub'    $cfg.AIHubName    "project=$($cfg.AIProjectName)"
Add-Ledger 'Content Safety' $cfg.ContentSafetyName ''
Add-Ledger 'AI Language'    $cfg.LanguageName      'PII detection'
Add-Ledger 'App Insights'   $cfg.AppInsightsName   ''
Add-Ledger 'AI Search'      $cfg.SearchServiceName "sku=$($cfg.SearchSku) index=$($cfg.SearchIndexName)"
Add-Ledger 'AOAI deployment (primary)' $cfg.PrimaryModel.Name "v=$($cfg.PrimaryModel.Version)"

# ── 2. Judge model deployment + JudgeModel patch ───────────────────────────
Write-Phase '2/8  Judge model deployment + appsettings patch'

# Discover the AOAI/Cognitive Services account name that Provision created.
$aoaiAccountsJson = Invoke-AzJson cognitiveservices account list `
    --resource-group $cfg.PrimaryResourceGroup -o json
$aoaiAccounts = $aoaiAccountsJson | ConvertFrom-Json
$aoai = $aoaiAccounts | Where-Object { $_.kind -in @('AIServices','OpenAI') } | Select-Object -First 1
if (-not $aoai) { throw "Could not find AIServices/OpenAI account in $($cfg.PrimaryResourceGroup)" }
Write-Ok "AOAI account: $($aoai.name)  endpoint=$($aoai.properties.endpoint)"

# Create the judge deployment (idempotent — 'create' is a no-op if same spec exists).
try {
    Invoke-AzJson cognitiveservices account deployment create `
        --name $aoai.name `
        --resource-group $cfg.PrimaryResourceGroup `
        --deployment-name $cfg.JudgeModel.DeploymentName `
        --model-name $cfg.JudgeModel.ModelName `
        --model-version $cfg.JudgeModel.ModelVersion `
        --model-format 'OpenAI' `
        --sku-name $cfg.JudgeModel.SkuName `
        --sku-capacity $cfg.JudgeModel.Capacity `
        -o json | Out-Null
    Write-Ok "Judge deployment created: $($cfg.JudgeModel.DeploymentName)"
} catch {
    Write-Warn2 "Judge deployment create returned an error (may already exist): $_"
}
Add-Ledger 'AOAI deployment (judge)' $cfg.JudgeModel.DeploymentName "on $($aoai.name)"

# Retrieve the AOAI key + endpoint, patch JudgeModel block.
$aoaiKey = (Invoke-AzJson cognitiveservices account keys list `
    --name $aoai.name --resource-group $cfg.PrimaryResourceGroup --query key1 -o tsv).Trim()
$aoaiEndpoint = $aoai.properties.endpoint

if (-not (Test-Path $configPath)) { throw "Expected config file missing: $configPath" }
$configJson = Get-Content $configPath -Raw | ConvertFrom-Json
$configJson.CTLAgent.JudgeModel.Endpoint         = $aoaiEndpoint
$configJson.CTLAgent.JudgeModel.ModelId          = $cfg.JudgeModel.DeploymentName
$configJson.CTLAgent.JudgeModel.UseAzureIdentity = $false
$configJson.CTLAgent.JudgeModel.ApiKey           = $aoaiKey

# ── 3. Patch TenantId ───────────────────────────────────────────────────────
Write-Phase '3/8  Patch TenantId into ContentSafety + PiiFilter'

if ($configJson.PSObject.Properties.Name -contains 'ContentSafety') {
    $configJson.ContentSafety.TenantId = $tenantId
    Write-Ok "ContentSafety.TenantId = $tenantId"
}
if ($configJson.PSObject.Properties.Name -contains 'PiiFilter') {
    $configJson.PiiFilter.TenantId = $tenantId
    Write-Ok "PiiFilter.TenantId     = $tenantId"
}
# Persist
$configJson | ConvertTo-Json -Depth 32 | Set-Content -Path $configPath -Encoding UTF8
Write-Ok "Wrote $configPath"

# Mirror critical secrets into src/Cascade.CTL.Agent.Api/appsettings.json so
# `dotnet run` works locally against the new subscription without re-copying.
if (Test-Path $apiConfigPath) {
    Copy-Item $configPath $apiConfigPath -Force
    Write-Ok "Mirrored config → $apiConfigPath"
}

# ── 4. RAG indexer ──────────────────────────────────────────────────────────
Write-Phase '4/8  RAG indexer (populate new Azure AI Search)'

$indexerProj = Join-Path $repoRoot 'src/Cascade.CTL.RAG.Indexer'
$searchEndpoint = "https://$($cfg.SearchServiceName).search.windows.net"
$adminKey = $configJson.CTLAgent.RAG.AzureSearch.AdminKey
$aoaiEndpointForIdx = $configJson.CTLAgent.RAG.AzureSearch.AzureOpenAIEndpoint

Push-Location $indexerProj
try {
    dotnet run --project $indexerProj -- `
        --knowledge-path (Join-Path $repoRoot $cfg.RagKnowledgePath) `
        --endpoint $searchEndpoint `
        --index-name $cfg.SearchIndexName `
        --aoai-endpoint $aoaiEndpointForIdx `
        --embedding-model $cfg.EmbeddingModel `
        --use-key-auth --admin-key $adminKey `
        --recreate-index
    if ($LASTEXITCODE -ne 0) { throw "RAG indexer exited with code $LASTEXITCODE" }
    Write-Ok "Indexed $($cfg.SearchIndexName)"
} finally { Pop-Location }
Add-Ledger 'Search index' $cfg.SearchIndexName "endpoint=$searchEndpoint"

# ── 5. Containers ───────────────────────────────────────────────────────────
Write-Phase '5/8  Build + deploy containers (ACR + Container Apps)'

$deployArgs = @(
    '-ResourceGroup', $cfg.DeployResourceGroup,
    '-Location',      $cfg.Location,
    '-AcrName',       $cfg.AcrName,
    '-EnvName',       $cfg.ContainerEnvName
)
& (Join-Path $PSScriptRoot 'Deploy-CTL-Containers.ps1') @deployArgs
if ($LASTEXITCODE -ne 0) { throw "Deploy-CTL-Containers.ps1 exited with code $LASTEXITCODE" }
Add-Ledger 'Resource Group (deploy)' $cfg.DeployResourceGroup ''
Add-Ledger 'ACR'                     $cfg.AcrName              ''
Add-Ledger 'Container Apps Env'      $cfg.ContainerEnvName     ''

# Discover the API FQDN that Deploy-CTL-Containers just created.
$apiFqdn = (Invoke-AzJson containerapp show `
    --name 'ctl-agent-api' --resource-group $cfg.DeployResourceGroup `
    --query 'properties.configuration.ingress.fqdn' -o tsv).Trim()
if ([string]::IsNullOrWhiteSpace($apiFqdn)) {
    Write-Warn2 "Could not resolve API FQDN — skipping smoke test."
} else {
    Write-Ok "API FQDN: $apiFqdn"
    Add-Ledger 'Container App: ctl-agent-api' $apiFqdn 'public ingress'
}

# ── 6. Foundry agent registration (optional) ───────────────────────────────
Write-Phase '6/8  Foundry agent registration'
if (-not $cfg.RegisterFoundryAgent) {
    Write-Warn2 "Skipped (RegisterFoundryAgent=`$false in config). Run deploy/Register-FoundryAgent.ps1 manually after verifying Foundry project shape."
} elseif (-not $apiFqdn) {
    Write-Warn2 "No API FQDN — skipping."
} else {
    # Read the API key Deploy-CTL-Containers placed into Key Vault → containerapp env.
    $apiKey = (Invoke-AzJson containerapp show `
        --name 'ctl-agent-api' --resource-group $cfg.DeployResourceGroup `
        --query "properties.template.containers[0].env[?name=='Auth__ApiKey'].value | [0]" -o tsv).Trim()
    if (-not $apiKey) {
        Write-Warn2 "Could not extract Auth__ApiKey from container app. Skipping Foundry agent."
    } else {
        & (Join-Path $PSScriptRoot 'Register-FoundryAgent.ps1') `
            -ApiFqdn $apiFqdn `
            -ApiKey $apiKey `
            -ModelDeployment $cfg.PrimaryModel.Name `
            -AgentName $cfg.FoundryAgentName `
            -ProjectAccount $cfg.AIHubName `
            -ProjectName $cfg.AIProjectName `
            -ResourceGroup $cfg.PrimaryResourceGroup `
            -SubscriptionId $ctx.id
        if ($LASTEXITCODE -eq 0) {
            Write-Ok "Foundry agent '$($cfg.FoundryAgentName)' registered"
            Add-Ledger 'Foundry Agent' $cfg.FoundryAgentName "calls https://$apiFqdn"
        } else {
            Write-Warn2 "Foundry agent registration failed (exit $LASTEXITCODE) — manual follow-up needed."
        }
    }
}

# ── 7. Smoke test ───────────────────────────────────────────────────────────
Write-Phase '7/8  Smoke test'
$smokePayloadPath = Join-Path $repoRoot $cfg.SmokeTestPayload
if ($SkipSmokeTest) {
    Write-Warn2 'Skipped (-SkipSmokeTest).'
} elseif (-not $apiFqdn) {
    Write-Warn2 'No API FQDN — skipping.'
} elseif (-not (Test-Path $smokePayloadPath)) {
    Write-Warn2 "Payload not found at $smokePayloadPath — skipping."
} else {
    $apiKey = (Invoke-AzJson containerapp show `
        --name 'ctl-agent-api' --resource-group $cfg.DeployResourceGroup `
        --query "properties.template.containers[0].env[?name=='Auth__ApiKey'].value | [0]" -o tsv).Trim()
    $uri = "https://$apiFqdn$($cfg.SmokeTestPath)"
    try {
        $resp = Invoke-RestMethod -Method POST -Uri $uri `
            -Headers @{ 'X-Api-Key' = $apiKey; 'Content-Type' = 'application/json' } `
            -Body (Get-Content $smokePayloadPath -Raw) `
            -TimeoutSec 120
        Write-Ok "Smoke test 200 OK"
        $resp | ConvertTo-Json -Depth 6 | Out-Host
    } catch {
        Write-Err2 "Smoke test FAILED against $uri : $_"
        throw
    }
}

# ── 8. Ledger ───────────────────────────────────────────────────────────────
Write-Phase '8/8  Migration ledger (resources in NEW subscription)'
$ledger | Format-Table -AutoSize | Out-String | Write-Host

Write-Host ""
Write-Host "Migration complete. Subscription: $($ctx.name) ($($ctx.id))" -ForegroundColor Green
Write-Host "Tenant: $tenantId" -ForegroundColor Green
Write-Host ""
Write-Host "Remember:" -ForegroundColor Yellow
Write-Host "  • Old-subscription resources can now be deleted." -ForegroundColor Yellow
Write-Host "  • Rotate keys out of config/appsettings.json into Key Vault / user-secrets before commit." -ForegroundColor Yellow
