<#
.SYNOPSIS
    Registers the CTL Agent (already running on Azure Container Apps) as a Foundry
    agent in the Azure AI Foundry project, with an OpenAPI 3.0 tool that calls
    POST /evaluate via X-Api-Key.

.DESCRIPTION
    Implements the "Option A — minimal rewrite" pattern: no application code is
    moved into Foundry. Foundry hosts a gpt-4o agent that, when asked about an
    asset, calls the existing ACA endpoint as a tool. After running this script
    the agent is invokable from:
      - Azure AI Foundry portal Playground
      - Foundry Agents REST API
      - Microsoft Agent Framework / Azure AI Agents SDK
    and its conversation telemetry (token usage, tool calls, latency) appears in
    the Foundry project's Tracing/Monitoring panes, plus the agent.api's own
    CTL.* customEvents continue to flow to App Insights.

.PARAMETER ApiFqdn
    The public hostname of the deployed Agent.Api (no scheme, no trailing slash).
.PARAMETER ApiKey
    The X-Api-Key value emitted by Deploy-CTL-Containers.ps1.
.PARAMETER ModelDeployment
    Foundry/AOAI deployment name to use as the agent's LLM. Defaults to gpt-4o.
.PARAMETER AgentName
    Friendly name for the Foundry agent.
#>
[CmdletBinding()]
param(
    # All resource-identifier defaults intentionally blank after old-subscription
    # teardown. Migrate-ToNewSubscription.ps1 passes the discovered values
    # explicitly. Standalone invocation must supply every parameter.
    [Parameter(Mandatory=$true)] [string]$ApiFqdn,
    [Parameter(Mandatory=$true)] [string]$ApiKey,
    [string]$ModelDeployment = 'gpt-4o',
    [string]$AgentName       = 'ctl-asset-evaluator',
    [Parameter(Mandatory=$true)] [string]$ProjectAccount,
    [Parameter(Mandatory=$true)] [string]$ProjectName,
    [Parameter(Mandatory=$true)] [string]$ResourceGroup,
    [Parameter(Mandatory=$true)] [string]$SubscriptionId,
    [string]$OpenApiPath     = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OpenApiPath)) {
    $OpenApiPath = Join-Path $PSScriptRoot '..\src\Cascade.CTL.Agent.Api\openapi.json'
}

function Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }

Step "Validating Azure CLI context"
$ctx = az account show -o json | ConvertFrom-Json
if ($ctx.id -ne $SubscriptionId) { az account set --subscription $SubscriptionId | Out-Null }
Write-Host "  Subscription: $($ctx.name)"
Write-Host "  Project:      $ProjectAccount/$ProjectName"

# --- Acquire data-plane token for Foundry Agents API -------------------------
Step "Acquiring access token (audience: ai.azure.com)"
$token = az account get-access-token --resource 'https://ai.azure.com' --query accessToken -o tsv
if (-not $token) { throw "Failed to acquire ai.azure.com token. Run 'az login' first." }

# Foundry data-plane base URL for AIServices accounts.
$base = "https://$ProjectAccount.services.ai.azure.com/api/projects/$ProjectName"
$apiVersion = '2025-05-01'    # Foundry Agents GA api-version
$h = @{ Authorization = "Bearer $token"; 'Content-Type' = 'application/json' }

# --- Load + patch OpenAPI spec ----------------------------------------------
Step "Loading OpenAPI spec from $OpenApiPath"
if (-not (Test-Path $OpenApiPath)) { throw "OpenAPI file not found: $OpenApiPath" }
$spec = Get-Content $OpenApiPath -Raw | ConvertFrom-Json
$spec.servers = @(@{ url = "https://$ApiFqdn"; description = 'Azure Container Apps (production)' })
$specJson = $spec | ConvertTo-Json -Depth 30
Write-Host "  Server URL set to https://$ApiFqdn"

# --- Define the agent body (Foundry "agents" resource) ----------------------
$instructions = @"
You are the Clear-To-List (CTL) Asset Evaluation assistant.

When the user asks to evaluate an asset (or mentions an asset id like
ASSET-NY-004), you MUST call the evaluateAsset tool with that assetId and
wait for the response. Do not invent verdicts.

When the tool returns, summarize for the user:
  1. verdict (one of Clear / ClearWithConditions / NotClear / NeedsHumanReview)
  2. confidence as a percentage
  3. evidenceTrail items as a short bullet list
  4. any conditions that must be satisfied
  5. the sessionId so the user can correlate with App Insights

If verdict is NeedsHumanReview, clearly call out which blockers require a
human and what data is missing. Cite specific evidence sentences verbatim
from evidenceTrail rather than paraphrasing.

If the tool returns an error, report the HTTP status and a one-line cause,
and suggest checking App Insights customEvents for the most recent CTL.*
events.
"@

$tool = @{
    type    = 'openapi'
    openapi = @{
        name        = 'ctl_agent_api'
        description = 'Runs the full CTL evaluation pipeline for a given asset id.'
        spec        = ($specJson | ConvertFrom-Json)
        auth        = @{
            type             = 'connection'
            security_scheme  = @{ ref = 'ApiKeyAuth' }
        }
    }
}

# NOTE on auth: the Foundry OpenAPI tool supports three auth modes:
#   - anonymous
#   - connection (managed identity / api-key connection in the project)
#   - managed_identity
# To pass the X-Api-Key automatically, you MUST create a "Custom keys"
# connection in the project first. We attempt that programmatically below;
# if it fails (RBAC / preview API drift) we fall back to anonymous and the
# Operator pastes the key into the Playground manually.

$connectionName = 'ctl-agent-api-key'
Step "Ensuring custom-keys connection '$connectionName' exists"
$connBody = @{
    name       = $connectionName
    properties = @{
        category       = 'CustomKeys'
        authType       = 'CustomKeys'
        target         = "https://$ApiFqdn"
        isSharedToAll  = $true
        credentials    = @{
            keys = @{ 'X-Api-Key' = $ApiKey }
        }
        metadata       = @{ purpose = 'CTL Agent.Api OpenAPI tool auth' }
    }
} | ConvertTo-Json -Depth 10

$connUrl = "https://management.azure.com/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.CognitiveServices/accounts/$ProjectAccount/projects/$ProjectName/connections/$($connectionName)?api-version=2025-06-01"
$mgmtToken = az account get-access-token --resource 'https://management.azure.com' --query accessToken -o tsv
$mgmtH = @{ Authorization = "Bearer $mgmtToken"; 'Content-Type' = 'application/json' }
try {
    Invoke-RestMethod -Method Put -Uri $connUrl -Headers $mgmtH -Body $connBody -TimeoutSec 60 | Out-Null
    Write-Host "  Connection upserted." -ForegroundColor Green
    $tool.openapi.auth = @{
        type             = 'connection'
        security_scheme  = @{
            connection_id = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.CognitiveServices/accounts/$ProjectAccount/projects/$ProjectName/connections/$connectionName"
        }
    }
} catch {
    Write-Host "  Could not create connection automatically: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "  Falling back to anonymous auth - paste X-Api-Key in the Playground when prompted." -ForegroundColor Yellow
    $tool.openapi.auth = @{ type = 'anonymous' }
}

# --- Look up existing agent by name (idempotent) ----------------------------
Step "Looking up existing agent '$AgentName'"
$listUrl = "$base/assistants?api-version=$apiVersion"
$existing = $null
try {
    $resp = Invoke-RestMethod -Method Get -Uri $listUrl -Headers $h -TimeoutSec 60
    $existing = $resp.data | Where-Object { $_.name -eq $AgentName } | Select-Object -First 1
} catch {
    Write-Host "  List failed (may be empty / preview drift): $($_.Exception.Message)" -ForegroundColor Yellow
}

$agentBody = @{
    name         = $AgentName
    model        = $ModelDeployment
    instructions = $instructions
    tools        = @($tool)
    metadata     = @{
        managed_by = 'CTLDeploy/Register-FoundryAgent.ps1'
        api_fqdn   = $ApiFqdn
    }
} | ConvertTo-Json -Depth 30

if ($existing) {
    Step "Updating existing agent $($existing.id)"
    $upd = Invoke-RestMethod -Method Post -Uri "$base/assistants/$($existing.id)?api-version=$apiVersion" -Headers $h -Body $agentBody -TimeoutSec 60
    $agentId = $upd.id
} else {
    Step "Creating new agent"
    $create = Invoke-RestMethod -Method Post -Uri $listUrl -Headers $h -Body $agentBody -TimeoutSec 60
    $agentId = $create.id
}

Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host " FOUNDRY AGENT REGISTERED" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""
Write-Host " Agent id:   $agentId"
Write-Host " Agent name: $AgentName"
Write-Host " Model:      $ModelDeployment"
Write-Host " Project:    $ProjectAccount/$ProjectName"
Write-Host ""
Write-Host " Open in portal:"
Write-Host "   https://ai.azure.com/build/agents/$agentId/playground`?wsid=/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.CognitiveServices/accounts/$ProjectAccount/projects/$ProjectName" -ForegroundColor Cyan
Write-Host ""
Write-Host " Try in the Playground:"
Write-Host "   Evaluate asset ASSET-NY-004 and tell me the verdict."
Write-Host ""
Write-Host " Observability:"
Write-Host "   - Foundry portal -> Tracing       (tool calls, token usage, latency, cost)"
Write-Host "   - Foundry portal -> Monitoring    (model usage charts)"
Write-Host "   - App Insights customEvents       (CTL.* steps for the ACA pipeline)"
Write-Host ""
