<#
.SYNOPSIS
    Deploys the CTL Agent Solution to Azure Container Apps + ACR.
    Idempotent - safe to re-run.
#>
[CmdletBinding()]
param(
    [string] $ResourceGroup = 'ctl-agent-rg',
    [string] $Location      = 'eastus2',
    [string] $AcrName       = 'ctlagentacr',
    [string] $EnvName       = 'ctl-agent-env',
    [string] $ImageTag      = (Get-Date -Format 'yyyyMMddHHmm'),
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step([string] $msg) {
    Write-Host ""
    Write-Host "==> $msg" -ForegroundColor Cyan
}

function Invoke-Az {
    # Suppress strict mode while running az: many az subcommands print warnings
    # (e.g. "Packing source code into tar...") to stderr, which an outer
    # $ErrorActionPreference='Stop' would otherwise treat as a terminating error.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # --only-show-errors suppresses extension/preview warnings on stdout
        # that would otherwise contaminate captured values like FQDNs.
        $output = & az @args --only-show-errors 2>&1
        if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed: $output" }
        # Filter out any residual WARNING lines (some commands ignore the flag).
        return ($output | Where-Object { $_ -notmatch '^\s*WARNING:' })
    }
    finally { $ErrorActionPreference = $prev }
}

# --- Validate session --------------------------------------------------------
Write-Step "Validating Azure CLI session"
$ctx = Invoke-Az account show -o json | ConvertFrom-Json
Write-Host "  Subscription: $($ctx.name) ($($ctx.id))"
Write-Host "  User:         $($ctx.user.name)"

# --- Read source config ------------------------------------------------------
Write-Step "Reading source config from $repoRoot\config\appsettings.json"
$cfgPath = Join-Path $repoRoot 'config\appsettings.json'
if (-not (Test-Path $cfgPath)) { throw "Missing $cfgPath" }
$cfgRaw = Get-Content $cfgPath -Raw
# Strip JSONC // comments. Only match // that is NOT preceded by a colon (avoid http:// , https:// , api://).
$cfgRaw = [regex]::Replace($cfgRaw, '(?m)(?<!:)//[^\r\n]*', '')
$cfg = $cfgRaw | ConvertFrom-Json

$secrets = [ordered]@{
    'aoai-key'         = $cfg.CTLAgent.AzureAIFoundry.ApiKey
    'judge-key'        = $cfg.CTLAgent.JudgeModel.ApiKey
    'mcp-key'          = $cfg.CTLAgent.McpServer.ApiKey
    'assetservice-key' = $cfg.AssetDomainService.ApiKey
    'search-admin-key' = $cfg.CTLAgent.RAG.AzureSearch.AdminKey
    'search-aoai-key'  = $cfg.CTLAgent.RAG.AzureSearch.AzureOpenAIApiKey
    'appinsights-conn' = $cfg.ApplicationInsights.ConnectionString
}
foreach ($k in $secrets.Keys) {
    if ([string]::IsNullOrWhiteSpace($secrets[$k])) {
        throw "Secret '$k' is empty in appsettings.json - cannot deploy."
    }
}

# Generate a fresh public API key
$apiKey = [Convert]::ToBase64String([Guid]::NewGuid().ToByteArray() + [Guid]::NewGuid().ToByteArray()).TrimEnd('=')
$secrets['agent-api-key'] = $apiKey

# --- Resource Group ----------------------------------------------------------
Write-Step "Resource Group: $ResourceGroup"
Invoke-Az group create --name $ResourceGroup --location $Location | Out-Null

# --- ACR ---------------------------------------------------------------------
Write-Step "Azure Container Registry: $AcrName"
$prevEAP = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
& cmd /c "az acr show --name $AcrName --resource-group $ResourceGroup -o json > nul 2>&1"
$ErrorActionPreference = $prevEAP
if ($LASTEXITCODE -ne 0) {
    Invoke-Az acr create --name $AcrName --resource-group $ResourceGroup --sku Basic --admin-enabled true --location $Location | Out-Null
}
$acrLoginServer = (Invoke-Az acr show --name $AcrName --resource-group $ResourceGroup --query loginServer -o tsv)
Write-Host "  Login server: $acrLoginServer"

# --- ACR Build (cloud) -------------------------------------------------------
if (-not $SkipBuild) {
    Write-Step "Building images via ACR (cloud build, no local Docker required)"
    $images = @(
        @{ name='ctl-agent-api';    dockerfile='src/Cascade.CTL.Agent.Api/Dockerfile' }
        @{ name='ctl-mcpserver';    dockerfile='src/Cascade.CTL.Agent.McpServer/Dockerfile' }
        @{ name='ctl-assetservice'; dockerfile='src/Cascade.CTL.AssetService/Dockerfile' }
        @{ name='ctl-rag-indexer';  dockerfile='src/Cascade.CTL.RAG.Indexer/Dockerfile' }
    )
    foreach ($img in $images) {
        Write-Host ""
        Write-Host "  Building $($img.name):$ImageTag" -ForegroundColor Yellow
        Invoke-Az acr build `
            --registry $AcrName `
            --resource-group $ResourceGroup `
            --image "$($img.name):$ImageTag" `
            --image "$($img.name):latest" `
            --file $img.dockerfile `
            $repoRoot | Out-Null
    }
} else {
    Write-Host "  (Skipping image build - using tag '$ImageTag')"
}

$acrUser = (Invoke-Az acr credential show -n $AcrName --query username -o tsv)
$acrPwd  = (Invoke-Az acr credential show -n $AcrName --query 'passwords[0].value' -o tsv)

# --- Dedicated Log Analytics workspace for the Container Apps env -----------
# (App Insights still receives telemetry via the connection string env var
#  injected into each container; the ACA env needs its own LAW because the
#  App Insights workspace lives in a managed RG with a deny assignment.)
$lawName = 'ctl-agent-law'
Write-Step "Log Analytics workspace: $lawName"
$prevEAP = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
& cmd /c "az monitor log-analytics workspace show -g $ResourceGroup -n $lawName -o json > nul 2>&1"
$ErrorActionPreference = $prevEAP
if ($LASTEXITCODE -ne 0) {
    Invoke-Az monitor log-analytics workspace create -g $ResourceGroup -n $lawName --location $Location | Out-Null
}
$lawCustomerId = (Invoke-Az monitor log-analytics workspace show -g $ResourceGroup -n $lawName --query customerId -o tsv)
$lawSharedKey  = (Invoke-Az monitor log-analytics workspace get-shared-keys -g $ResourceGroup -n $lawName --query primarySharedKey -o tsv)
Write-Host "  Customer ID: $lawCustomerId"

# --- Container Apps Environment ---------------------------------------------
Write-Step "Container Apps Environment: $EnvName"
$prevEAP = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
& cmd /c "az containerapp env show --name $EnvName --resource-group $ResourceGroup -o json > nul 2>&1"
$ErrorActionPreference = $prevEAP
if ($LASTEXITCODE -ne 0) {
    Invoke-Az containerapp env create `
        --name $EnvName `
        --resource-group $ResourceGroup `
        --location $Location `
        --logs-destination log-analytics `
        --logs-workspace-id $lawCustomerId `
        --logs-workspace-key $lawSharedKey | Out-Null
}
Write-Host "  Ready."

# --- Helper: create or update an app ----------------------------------------
function Set-OrCreateContainerApp {
    param(
        [string] $Name,
        [string] $Image,
        [string] $Ingress,
        [int]    $TargetPort = 8080,
        [hashtable] $AppSecrets,
        [hashtable] $EnvVars,
        [int]    $MinReplicas = 1,
        [int]    $MaxReplicas = 3,
        [string] $Cpu = '0.5',
        [string] $Memory = '1.0Gi'
    )

    $secretArgs = @()
    foreach ($k in $AppSecrets.Keys) { $secretArgs += "$k=$($AppSecrets[$k])" }
    $envArgs = @()
    foreach ($k in $EnvVars.Keys) { $envArgs += "$k=$($EnvVars[$k])" }

    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & cmd /c "az containerapp show --name $Name --resource-group $ResourceGroup -o json > nul 2>&1"
    $ErrorActionPreference = $prevEAP
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Creating $Name ..." -ForegroundColor Yellow
        $createArgs = @(
            'containerapp','create',
            '--name',$Name,
            '--resource-group',$ResourceGroup,
            '--environment',$EnvName,
            '--image',$Image,
            '--registry-server',$acrLoginServer,
            '--registry-username',$acrUser,
            '--registry-password',$acrPwd,
            '--target-port',$TargetPort,
            '--ingress',$Ingress,
            '--min-replicas',$MinReplicas,
            '--max-replicas',$MaxReplicas,
            '--cpu',$Cpu,'--memory',$Memory
        )
        if ($secretArgs.Count -gt 0) { $createArgs += @('--secrets'); $createArgs += $secretArgs }
        if ($envArgs.Count -gt 0)    { $createArgs += @('--env-vars'); $createArgs += $envArgs }
        Invoke-Az @createArgs | Out-Null
    } else {
        Write-Host "  Updating $Name ..." -ForegroundColor Yellow
        if ($secretArgs.Count -gt 0) {
            Invoke-Az containerapp secret set --name $Name --resource-group $ResourceGroup --secrets $secretArgs | Out-Null
        }
        $updateArgs = @(
            'containerapp','update',
            '--name',$Name,
            '--resource-group',$ResourceGroup,
            '--image',$Image
        )
        if ($envArgs.Count -gt 0) { $updateArgs += @('--set-env-vars'); $updateArgs += $envArgs }
        Invoke-Az @updateArgs | Out-Null
    }
}

# --- ctl-assetservice (internal) --------------------------------------------
Write-Step "Container App: ctl-assetservice (internal)"
Set-OrCreateContainerApp `
    -Name 'ctl-assetservice' `
    -Image "$acrLoginServer/ctl-assetservice:$ImageTag" `
    -Ingress 'internal' `
    -AppSecrets @{ 'assetservice-key' = $secrets['assetservice-key'] } `
    -EnvVars @{
        'ASPNETCORE_ENVIRONMENT' = 'Production'
        'ASSETDOMAIN_API_KEY'    = 'secretref:assetservice-key'
    }
$assetFqdn = (Invoke-Az containerapp show -n ctl-assetservice -g $ResourceGroup --query properties.configuration.ingress.fqdn -o tsv)
Write-Host "  Internal FQDN: $assetFqdn"

# --- ctl-mcpserver (internal) -----------------------------------------------
Write-Step "Container App: ctl-mcpserver (internal)"
$mcpEnv = @{
    'ASPNETCORE_ENVIRONMENT'                = 'Production'
    'McpServer__ApiKey'                     = 'secretref:mcp-key'
    'CTLAgent__McpServer__ApiKey'           = 'secretref:mcp-key'
    'AssetDomainService__BaseUrl'           = "https://$assetFqdn"
    'AssetDomainService__ApiKey'            = 'secretref:assetservice-key'
    'ApplicationInsights__ConnectionString' = 'secretref:appinsights-conn'
}
Set-OrCreateContainerApp `
    -Name 'ctl-mcpserver' `
    -Image "$acrLoginServer/ctl-mcpserver:$ImageTag" `
    -Ingress 'internal' `
    -AppSecrets @{
        'mcp-key'          = $secrets['mcp-key']
        'assetservice-key' = $secrets['assetservice-key']
        'appinsights-conn' = $secrets['appinsights-conn']
    } `
    -EnvVars $mcpEnv
$mcpFqdn = (Invoke-Az containerapp show -n ctl-mcpserver -g $ResourceGroup --query properties.configuration.ingress.fqdn -o tsv)
Write-Host "  Internal FQDN: $mcpFqdn"

# --- ctl-agent-api (external) - the public endpoint -------------------------
Write-Step "Container App: ctl-agent-api (external) - public endpoint"
$apiEnv = [ordered]@{
    'ASPNETCORE_ENVIRONMENT'                        = 'Production'
    'CTLAgent__Api__ApiKey'                         = 'secretref:agent-api-key'
    'CTLAgent__AzureAIFoundry__ApiKey'              = 'secretref:aoai-key'
    'CTLAgent__JudgeModel__ApiKey'                  = 'secretref:judge-key'
    'CTLAgent__McpServer__Endpoint'                 = "https://$mcpFqdn"
    'CTLAgent__McpServer__ApiKey'                   = 'secretref:mcp-key'
    'AssetDomainService__BaseUrl'                   = "https://$assetFqdn"
    'AssetDomainService__ApiKey'                    = 'secretref:assetservice-key'
    'CTLAgent__RAG__AzureSearch__AdminKey'          = 'secretref:search-admin-key'
    'CTLAgent__RAG__AzureSearch__AzureOpenAIApiKey' = 'secretref:search-aoai-key'
    'ApplicationInsights__ConnectionString'         = 'secretref:appinsights-conn'
}
Set-OrCreateContainerApp `
    -Name 'ctl-agent-api' `
    -Image "$acrLoginServer/ctl-agent-api:$ImageTag" `
    -Ingress 'external' `
    -AppSecrets @{
        'agent-api-key'    = $secrets['agent-api-key']
        'aoai-key'         = $secrets['aoai-key']
        'judge-key'        = $secrets['judge-key']
        'mcp-key'          = $secrets['mcp-key']
        'assetservice-key' = $secrets['assetservice-key']
        'search-admin-key' = $secrets['search-admin-key']
        'search-aoai-key'  = $secrets['search-aoai-key']
        'appinsights-conn' = $secrets['appinsights-conn']
    } `
    -EnvVars $apiEnv `
    -Cpu '1.0' -Memory '2.0Gi' -MinReplicas 1 -MaxReplicas 3
$apiFqdn = (Invoke-Az containerapp show -n ctl-agent-api -g $ResourceGroup --query properties.configuration.ingress.fqdn -o tsv)

# --- ctl-rag-indexer (job) --------------------------------------------------
Write-Step "Container Apps Job: ctl-rag-indexer (manual trigger)"
$indexerSecrets = @(
    "search-admin-key=$($secrets['search-admin-key'])",
    "search-aoai-key=$($secrets['search-aoai-key'])"
)
$indexerEnv = @(
    'CTLRAG__CTLAgent__RAG__AzureSearch__AdminKey=secretref:search-admin-key',
    'CTLRAG__CTLAgent__RAG__AzureSearch__AzureOpenAIApiKey=secretref:search-aoai-key'
)
$prevEAP = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
& cmd /c "az containerapp job show --name ctl-rag-indexer --resource-group $ResourceGroup -o json > nul 2>&1"
$ErrorActionPreference = $prevEAP
if ($LASTEXITCODE -ne 0) {
    Invoke-Az containerapp job create `
        --name 'ctl-rag-indexer' `
        --resource-group $ResourceGroup `
        --environment $EnvName `
        --trigger-type Manual `
        --replica-timeout 1800 `
        --replica-retry-limit 1 `
        --image "$acrLoginServer/ctl-rag-indexer:$ImageTag" `
        --registry-server $acrLoginServer `
        --registry-username $acrUser `
        --registry-password $acrPwd `
        --cpu 0.5 --memory 1.0Gi `
        --secrets $indexerSecrets `
        --env-vars $indexerEnv | Out-Null
} else {
    Invoke-Az containerapp job secret set --name ctl-rag-indexer --resource-group $ResourceGroup --secrets $indexerSecrets | Out-Null
    Invoke-Az containerapp job update `
        --name ctl-rag-indexer `
        --resource-group $ResourceGroup `
        --image "$acrLoginServer/ctl-rag-indexer:$ImageTag" `
        --set-env-vars $indexerEnv | Out-Null
}

# --- Summary ----------------------------------------------------------------
Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host " DEPLOYMENT COMPLETE" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""
Write-Host " Public Agent.Api endpoint:" -ForegroundColor Cyan
Write-Host "   https://$apiFqdn"
Write-Host ""
Write-Host " X-Api-Key (save this):" -ForegroundColor Yellow
Write-Host "   $($secrets['agent-api-key'])"
Write-Host ""
Write-Host " Smoke test:" -ForegroundColor Cyan
Write-Host "   curl -X POST https://$apiFqdn/evaluate -H 'Content-Type: application/json' -H 'X-Api-Key: $($secrets['agent-api-key'])' -d '{\""assetId\"":\""ASSET-NY-004\""}'"
Write-Host ""
Write-Host " Trigger RAG indexer:" -ForegroundColor Cyan
Write-Host "   az containerapp job start --name ctl-rag-indexer --resource-group $ResourceGroup"
Write-Host ""
Write-Host " Audit query (Log Analytics):" -ForegroundColor Cyan
Write-Host "   customEvents | where name startswith 'CTL.' | order by timestamp desc | take 50"
Write-Host ""
