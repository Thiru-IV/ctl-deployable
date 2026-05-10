<#
.SYNOPSIS
    Provisions all Azure services required by the CTL Agent solution.

.DESCRIPTION
    Creates Resource Group, Azure AI Foundry Hub + Project, deploys a serverless
    model endpoint, provisions Azure AI Content Safety, Application Insights,
    assigns RBAC roles, and updates appsettings.json. Handles failures gracefully
    and reports provisioning status for each service.

.PARAMETER ResourceGroupName
    Name of the Azure resource group. Default: rg-ctlagent

.PARAMETER Location
    Azure region. Default: eastus2

.PARAMETER AIHubName
    Name for the Azure AI Foundry Hub. Default: ctlagent-hub

.PARAMETER AIProjectName
    Name for the Azure AI Foundry Project. Default: ctlagent-project

.PARAMETER ContentSafetyResourceName
    Name for the Azure AI Content Safety resource. Default: ctlagent-contentsafety

.PARAMETER ModelName
    Model to deploy as a serverless endpoint. Default: gpt-4o

.PARAMETER ModelPublisher
    Model registry publisher. Default: azure-openai
    Use 'azureml-meta' for Llama, 'azureml-mistral' for Mistral, 'azureml' for Phi.

.PARAMETER ModelVersion
    Model version to deploy. Default: 2024-08-06

.PARAMETER SkipContentSafety
    Skip provisioning the optional Content Safety resource.

.PARAMETER SkipPiiFilter
    Skip provisioning the optional Azure AI Language resource (PII detection).

.PARAMETER SkipAppInsights
    Skip provisioning the Application Insights resource.

.PARAMETER AppInsightsName
    Name for the Application Insights resource. Default: ctlagent-appinsights

.PARAMETER SkipAzureSearch
    Skip provisioning the Azure AI Search resource used by the RAG pipeline.

.PARAMETER AzureSearchServiceName
    Name for the Azure AI Search service. Default: ctlagent-search

.PARAMETER AzureSearchSku
    SKU for Azure AI Search. 'free' supports 50 MB / 3 indexes / 10 K docs (sufficient for this solution).
    Options: free | basic | standard.

.PARAMETER SkipRoleAssignment
    Skip RBAC role assignments (useful if running without AAD permissions).

.PARAMETER UpdateConfig
    Automatically update config/appsettings.json with provisioned endpoints.

.EXAMPLE
    .\Provision-AzureServices.ps1 -UpdateConfig
    .\Provision-AzureServices.ps1 -ModelName "Phi-4" -ModelPublisher "azureml" -ModelVersion "4" -UpdateConfig
    .\Provision-AzureServices.ps1 -SkipContentSafety -SkipPiiFilter -SkipRoleAssignment -UpdateConfig
    .\Provision-AzureServices.ps1 -SkipAzureSearch -UpdateConfig   # skip RAG search provisioning
    .\Provision-AzureServices.ps1 -AzureSearchSku "basic" -UpdateConfig
    .\Provision-AzureServices.ps1 -SkipAppInsights -UpdateConfig          # skip Application Insights
#>

[CmdletBinding()]
param(
    [string]$ResourceGroupName = "rg-ctlagent",
    [string]$Location = "eastus2",
    [string]$AIHubName = "ctlagent-hub",
    [string]$AIProjectName = "ctlagent-project",
    [string]$ContentSafetyResourceName = "ctlagent-contentsafety",
    [string]$LanguageResourceName = "ctlagent-language",
    [string]$AzureSearchServiceName = "ctlagent-search",
    [ValidateSet("free","basic","standard")][string]$AzureSearchSku = "free",
    [string]$ModelName = "gpt-4o",
    [string]$ModelPublisher = "azure-openai",
    [string]$ModelVersion = "2024-08-06",
    [switch]$SkipContentSafety,
    [switch]$SkipPiiFilter,
    [switch]$SkipAppInsights,
    [string]$AppInsightsName = "ctlagent-appinsights",
    [switch]$SkipAzureSearch,
    [switch]$SkipRoleAssignment,
    [switch]$UpdateConfig
)

Set-StrictMode -Version Latest
# Use "Continue" instead of "Stop" because Azure CLI writes warnings and
# expected error messages (e.g. ResourceNotFound on 'show' checks) to stderr.
# With "Stop", PowerShell treats every stderr line as a terminating error,
# killing the script before it can check $LASTEXITCODE and proceed to 'create'.
$ErrorActionPreference = "Continue"

# ──────────────────────────────────────────────────────────────────
# Provisioning status tracker
# ──────────────────────────────────────────────────────────────────

$provisioningResults = [System.Collections.Generic.List[PSCustomObject]]::new()

function Add-Result {
    param(
        [string]$Service,
        [string]$Status,   # Provisioned | Failed | Skipped
        [string]$Detail
    )
    $provisioningResults.Add([PSCustomObject]@{
        Service = $Service
        Status  = $Status
        Detail  = $Detail
    })
}

function Write-Step {
    param([string]$Message)
    Write-Host "`n── $Message ──" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "  [OK] $Message" -ForegroundColor Green
}

function Write-Fail {
    param([string]$Message)
    Write-Host "  [FAIL] $Message" -ForegroundColor Red
}

function Write-Skip {
    param([string]$Message)
    Write-Host "  [SKIP] $Message" -ForegroundColor Yellow
}

# ──────────────────────────────────────────────────────────────────
# Pre-flight checks
# ──────────────────────────────────────────────────────────────────

Write-Step "Pre-flight checks"

# Verify az CLI is installed
try {
    $azVersion = az version 2>&1 | ConvertFrom-Json
    Write-Success "Azure CLI $($azVersion.'azure-cli') found"
}
catch {
    Write-Fail "Azure CLI not found. Install from https://aka.ms/installazurecli"
    exit 1
}

# Verify logged in
try {
    $account = az account show 2>&1 | ConvertFrom-Json
    Write-Success "Logged in as $($account.user.name) (Subscription: $($account.name))"
}
catch {
    Write-Fail "Not logged in. Run 'az login' first."
    exit 1
}

# Verify/install az ml extension
try {
    az extension add --name ml --upgrade --yes 2>$null | Out-Null
    # Verify extension is present regardless of upgrade exit code
    $mlCheck = az extension list --query "[?name=='ml'].name" -o tsv 2>$null
    if (-not $mlCheck) { throw "ML extension not found after install attempt" }
    Write-Success "Azure ML CLI extension installed/updated"
}
catch {
    Write-Fail "Failed to install 'ml' extension. Run: az extension add --name ml"
    exit 1
}

$principalId = $null
if (-not $SkipRoleAssignment) {
    try {
        $principalId = az ad signed-in-user show --query id -o tsv 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Failed to get principal ID" }
        Write-Success "Principal ID: $principalId"
    }
    catch {
        Write-Fail "Could not retrieve signed-in user principal. Use -SkipRoleAssignment if using a service principal."
        exit 1
    }
}

# ──────────────────────────────────────────────────────────────────
# 1. Resource Group
# ──────────────────────────────────────────────────────────────────

Write-Step "1/10 Resource Group: $ResourceGroupName"

try {
    $rgExists = az group exists --name $ResourceGroupName 2>$null
    if ($rgExists -eq "true") {
        Write-Success "Resource group '$ResourceGroupName' already exists"
        Add-Result "Resource Group" "Provisioned" "Already existed"
    }
    else {
        az group create --name $ResourceGroupName --location $Location --output none 2>$null
        if ($LASTEXITCODE -ne 0) { throw "az group create failed" }
        Write-Success "Created resource group '$ResourceGroupName' in $Location"
        Add-Result "Resource Group" "Provisioned" "Created in $Location"
    }
}
catch {
    Write-Fail "Resource Group: $_"
    Add-Result "Resource Group" "Failed" "$_"
    Write-Host "`nCannot continue without a resource group." -ForegroundColor Red
    $provisioningResults | Format-Table -AutoSize
    exit 1
}

# ──────────────────────────────────────────────────────────────────
# 2. Azure AI Foundry Hub
# ──────────────────────────────────────────────────────────────────

Write-Step "2/10 AI Foundry Hub: $AIHubName"

$hubArmId = $null

try {
    # Check if hub already exists
    $existingJson = az ml workspace show `
        --name $AIHubName `
        --resource-group $ResourceGroupName 2>$null
    $existing = $null
    if ($LASTEXITCODE -eq 0 -and $existingJson) {
        $existing = $existingJson | ConvertFrom-Json -ErrorAction SilentlyContinue
    }

    if ($existing -and $existing.id) {
        $hubArmId = $existing.id
        Write-Success "Hub already exists: $AIHubName"
        Add-Result "AI Foundry Hub" "Provisioned" "Already existed"
    }
    else {
        az ml workspace create `
            --name $AIHubName `
            --resource-group $ResourceGroupName `
            --kind hub `
            --location $Location `
            --output none 2>$null
        if ($LASTEXITCODE -ne 0) { throw "az ml workspace create --kind hub failed" }

        $hubArmId = az ml workspace show `
            --name $AIHubName `
            --resource-group $ResourceGroupName `
            --query id -o tsv 2>$null
        if ($LASTEXITCODE -ne 0) { throw "Failed to retrieve hub ARM ID" }

        Write-Success "Created AI Foundry Hub: $AIHubName"
        Add-Result "AI Foundry Hub" "Provisioned" "Created (auto-provisions Storage Account + Key Vault)"
    }
}
catch {
    Write-Fail "AI Foundry Hub: $_"
    Add-Result "AI Foundry Hub" "Failed" "Fix: Ensure Microsoft.MachineLearningServices provider is registered. Run: az provider register -n Microsoft.MachineLearningServices"
}

# ──────────────────────────────────────────────────────────────────
# 3. Azure AI Foundry Project
# ──────────────────────────────────────────────────────────────────

Write-Step "3/10 AI Foundry Project: $AIProjectName"

$projectCreated = $false

if (-not $hubArmId) {
    Write-Skip "Skipped — AI Foundry Hub not provisioned"
    Add-Result "AI Foundry Project" "Skipped" "Depends on AI Foundry Hub"
}
else {
    try {
        $existingJson = az ml workspace show `
            --name $AIProjectName `
            --resource-group $ResourceGroupName 2>$null
        $existing = $null
        if ($LASTEXITCODE -eq 0 -and $existingJson) {
            $existing = $existingJson | ConvertFrom-Json -ErrorAction SilentlyContinue
        }

        if ($existing -and $existing.id) {
            $projectCreated = $true
            Write-Success "Project already exists: $AIProjectName"
            Add-Result "AI Foundry Project" "Provisioned" "Already existed"
        }
        else {
            az ml workspace create `
                --name $AIProjectName `
                --resource-group $ResourceGroupName `
                --kind project `
                --hub-id $hubArmId `
                --output none 2>$null
            if ($LASTEXITCODE -ne 0) { throw "az ml workspace create --kind project failed" }

            $projectCreated = $true
            Write-Success "Created AI Foundry Project: $AIProjectName"
            Add-Result "AI Foundry Project" "Provisioned" "Created under hub $AIHubName"
        }
    }
    catch {
        Write-Fail "AI Foundry Project: $_"
        Add-Result "AI Foundry Project" "Failed" "Fix: Verify hub '$AIHubName' exists and you have Contributor access"
    }
}

# ──────────────────────────────────────────────────────────────────
# 4. Model Deployment (Serverless Endpoint)
# ──────────────────────────────────────────────────────────────────

$modelId = "azureml://registries/$ModelPublisher/models/$ModelName/versions/$ModelVersion"
Write-Step "4/10 Model Deployment: $ModelName (serverless)"
Write-Host "  Model ID: $modelId" -ForegroundColor DarkGray

$modelEndpoint = $null
$modelApiKey = $null

if (-not $projectCreated) {
    Write-Skip "Skipped — AI Foundry Project not provisioned"
    Add-Result "Model Deployment ($ModelName)" "Skipped" "Depends on AI Foundry Project"
}
else {
    try {
        # Sanitize endpoint name (lowercase, no hyphens — serverless endpoints reject them)
        $endpointName = $ModelName.ToLower() -replace '[^a-z0-9]', ''

        # Check if serverless endpoint already exists
        $existingJson = az ml serverless-endpoint show `
            --name $endpointName `
            --resource-group $ResourceGroupName `
            --workspace-name $AIProjectName 2>$null
        $existing = $null
        if ($LASTEXITCODE -eq 0 -and $existingJson) {
            $existing = $existingJson | ConvertFrom-Json -ErrorAction SilentlyContinue
        }

        if ($existing -and $existing.inference_uri) {
            $modelEndpoint = $existing.inference_uri -replace '/v1/chat/completions$', '/'
            Write-Success "Serverless endpoint already exists: $modelEndpoint"
            Add-Result "Model Deployment ($ModelName)" "Provisioned" "Already existed"
        }
        else {
            # Use YAML file (az ml serverless-endpoint create requires --file, not --model-id)
            $yamlPath = Join-Path $env:TEMP "ctl-serverless-endpoint.yaml"
            @"
name: $endpointName
model_id: azureml://registries/$ModelPublisher/models/$ModelName
"@ | Set-Content $yamlPath -Encoding UTF8

            az ml serverless-endpoint create `
                --file $yamlPath `
                --resource-group $ResourceGroupName `
                --workspace-name $AIProjectName `
                --output none 2>$null
            if ($LASTEXITCODE -ne 0) { throw "az ml serverless-endpoint create failed" }

            $inferenceUri = az ml serverless-endpoint show `
                --name $endpointName `
                --resource-group $ResourceGroupName `
                --workspace-name $AIProjectName `
                --query inference_uri -o tsv 2>$null
            if ($LASTEXITCODE -ne 0) { throw "Failed to retrieve inference URI" }

            $modelEndpoint = $inferenceUri -replace '/v1/chat/completions$', '/'
            Write-Success "Deployed serverless endpoint: $modelEndpoint"
            Add-Result "Model Deployment ($ModelName)" "Provisioned" $modelEndpoint
        }

        # Retrieve API key for the serverless endpoint
        try {
            $modelApiKey = az ml serverless-endpoint get-credentials `
                --name $endpointName `
                --resource-group $ResourceGroupName `
                --workspace-name $AIProjectName `
                --query primary_key -o tsv 2>$null
            if ($LASTEXITCODE -ne 0) { throw "Failed to retrieve API key" }
            Write-Success "Retrieved endpoint API key"
        }
        catch {
            Write-Fail "Could not retrieve API key: $_"
            Write-Host "  You can retrieve it manually from the AI Foundry portal." -ForegroundColor DarkGray
        }
    }
    catch {
        Write-Fail "Model Deployment: $_"
        $tip = if ($ModelPublisher -eq "azure-openai") {
            "Azure OpenAI models require separate access approval at https://aka.ms/oai/access. Try open-source: -ModelName Phi-4 -ModelPublisher azureml -ModelVersion 4"
        } else {
            "Verify model availability: az ml model list --registry-name $ModelPublisher --query `"[?name=='$ModelName']`""
        }
        Add-Result "Model Deployment ($ModelName)" "Failed" "Fix: $tip"
    }
}

# ──────────────────────────────────────────────────────────────────
# 5. Azure AI Content Safety
# ──────────────────────────────────────────────────────────────────

Write-Step "5/10 Azure AI Content Safety: $ContentSafetyResourceName"

$contentSafetyEndpoint = $null

if ($SkipContentSafety) {
    Write-Skip "Skipped by -SkipContentSafety flag"
    Add-Result "Azure AI Content Safety" "Skipped" "User opted out via -SkipContentSafety"
}
else {
    try {
        $existingJson = az cognitiveservices account show `
            --name $ContentSafetyResourceName `
            --resource-group $ResourceGroupName 2>$null
        $existing = $null
        if ($LASTEXITCODE -eq 0 -and $existingJson) {
            $existing = $existingJson | ConvertFrom-Json -ErrorAction SilentlyContinue
        }

        if ($existing -and $existing.properties.endpoint) {
            $contentSafetyEndpoint = $existing.properties.endpoint
            Write-Success "Resource already exists at $contentSafetyEndpoint"
            Add-Result "Azure AI Content Safety" "Provisioned" "Already existed at $contentSafetyEndpoint"
        }
        else {
            az cognitiveservices account create `
                --name $ContentSafetyResourceName `
                --resource-group $ResourceGroupName `
                --kind ContentSafety `
                --sku S0 `
                --location $Location `
                --output none 2>$null
            if ($LASTEXITCODE -ne 0) { throw "az cognitiveservices account create failed" }

            $contentSafetyEndpoint = az cognitiveservices account show `
                --name $ContentSafetyResourceName `
                --resource-group $ResourceGroupName `
                --query properties.endpoint -o tsv 2>$null
            if ($LASTEXITCODE -ne 0) { throw "Failed to retrieve endpoint" }

            Write-Success "Created Azure AI Content Safety at $contentSafetyEndpoint"
            Add-Result "Azure AI Content Safety" "Provisioned" $contentSafetyEndpoint
        }
    }
    catch {
        Write-Fail "Content Safety: $_"
        Add-Result "Azure AI Content Safety" "Failed" "Fix: Verify 'ContentSafety' kind is available in '$Location'. Not all regions support it."
    }
}

# ──────────────────────────────────────────────────────────────────
# 6. Azure AI Language (PII Detection)
# ──────────────────────────────────────────────────────────────────

Write-Step "6/10 Azure AI Language: $LanguageResourceName"

$languageEndpoint = $null

if ($SkipPiiFilter) {
    Write-Skip "Skipped by -SkipPiiFilter flag"
    Add-Result "Azure AI Language (PII)" "Skipped" "User opted out via -SkipPiiFilter"
}
else {
    try {
        $existingJson = az cognitiveservices account show `
            --name $LanguageResourceName `
            --resource-group $ResourceGroupName 2>$null
        $existing = $null
        if ($LASTEXITCODE -eq 0 -and $existingJson) {
            $existing = $existingJson | ConvertFrom-Json -ErrorAction SilentlyContinue
        }

        if ($existing -and $existing.properties.endpoint) {
            $languageEndpoint = $existing.properties.endpoint
            Write-Success "Resource already exists at $languageEndpoint"
            Add-Result "Azure AI Language (PII)" "Provisioned" "Already existed at $languageEndpoint"
        }
        else {
            az cognitiveservices account create `
                --name $LanguageResourceName `
                --resource-group $ResourceGroupName `
                --kind TextAnalytics `
                --sku S `
                --location $Location `
                --output none 2>$null
            if ($LASTEXITCODE -ne 0) { throw "az cognitiveservices account create failed" }

            $languageEndpoint = az cognitiveservices account show `
                --name $LanguageResourceName `
                --resource-group $ResourceGroupName `
                --query properties.endpoint -o tsv 2>$null
            if ($LASTEXITCODE -ne 0) { throw "Failed to retrieve endpoint" }

            Write-Success "Created Azure AI Language at $languageEndpoint"
            Add-Result "Azure AI Language (PII)" "Provisioned" $languageEndpoint
        }
    }
    catch {
        Write-Fail "Azure AI Language: $_"
        Add-Result "Azure AI Language (PII)" "Failed" "Fix: Verify 'TextAnalytics' kind is available in '$Location'. Run: az cognitiveservices account list-kinds -l $Location"
    }
}

# ──────────────────────────────────────────────────────────────────
# 7. Azure AI Search (RAG index)
# ──────────────────────────────────────────────────────────────────

Write-Step "7/10 Azure AI Search: $AzureSearchServiceName ($AzureSearchSku tier)"

$azureSearchEndpoint = $null
$azureSearchAdminKey = $null
$azureSearchQueryKey = $null

if ($SkipAzureSearch) {
    Write-Skip "Skipped by -SkipAzureSearch flag"
    Add-Result "Azure AI Search" "Skipped" "User opted out via -SkipAzureSearch"
}
else {
    try {
        $existingSearchJson = az search service show --name $AzureSearchServiceName --resource-group $ResourceGroupName 2>$null
        $existingSearch = $null
        if ($LASTEXITCODE -eq 0) {
            $existingSearch = $existingSearchJson | ConvertFrom-Json -ErrorAction SilentlyContinue
        }

        if ($existingSearch -and $existingSearch.name) {
            $azureSearchEndpoint = "https://$($existingSearch.name).search.windows.net"
            Write-Success "Search service already exists at $azureSearchEndpoint"
            Add-Result "Azure AI Search" "Provisioned" "Already existed at $azureSearchEndpoint"
        }
        else {
            az search service create --name $AzureSearchServiceName --resource-group $ResourceGroupName --sku $AzureSearchSku --location $Location --partition-count 1 --replica-count 1 --output none
            if ($LASTEXITCODE -ne 0) {
                throw "az search service create failed (only one free-tier search service is allowed per subscription)"
            }
            $azureSearchEndpoint = "https://$AzureSearchServiceName.search.windows.net"
            Write-Success "Created Azure AI Search at $azureSearchEndpoint"
            Add-Result "Azure AI Search" "Provisioned" $azureSearchEndpoint
        }

        $adminKeyJson = az search admin-key show --service-name $AzureSearchServiceName --resource-group $ResourceGroupName 2>$null
        if ($LASTEXITCODE -eq 0) {
            $adminKeys = $adminKeyJson | ConvertFrom-Json -ErrorAction SilentlyContinue
            if ($adminKeys -and $adminKeys.primaryKey) { $azureSearchAdminKey = $adminKeys.primaryKey }
        }

        $queryKeyJson = az search query-key list --service-name $AzureSearchServiceName --resource-group $ResourceGroupName 2>$null
        if ($LASTEXITCODE -eq 0) {
            $queryKeys = @($queryKeyJson | ConvertFrom-Json -ErrorAction SilentlyContinue)
            if ($queryKeys.Count -gt 0 -and $queryKeys[0].key) { $azureSearchQueryKey = $queryKeys[0].key }
        }
    }
    catch {
        Write-Fail "Azure AI Search: $_"
        Add-Result "Azure AI Search" "Failed" "Fix: Only one free-tier search service is allowed per subscription; use -AzureSearchSku basic or delete the existing free service."
    }
}

# ──────────────────────────────────────────────────────────────────
# 8. Application Insights
# ──────────────────────────────────────────────────────────────────

Write-Step "8/10 Application Insights: $AppInsightsName"

$appInsightsConnectionString = $null

if ($SkipAppInsights) {
    Write-Skip "Skipped by -SkipAppInsights flag"
    Add-Result "Application Insights" "Skipped" "User opted out via -SkipAppInsights"
}
else {
    try {
        # Ensure the application-insights extension is available
        az extension add --name application-insights --upgrade --yes 2>$null | Out-Null

        $existingJson = az monitor app-insights component show `
            --app $AppInsightsName `
            --resource-group $ResourceGroupName 2>$null
        $existing = $null
        if ($LASTEXITCODE -eq 0) {
            $existing = $existingJson | ConvertFrom-Json -ErrorAction SilentlyContinue
        }

        if ($existing -and $existing.connectionString) {
            $appInsightsConnectionString = $existing.connectionString
            Write-Success "Application Insights already exists: $AppInsightsName"
            Add-Result "Application Insights" "Provisioned" "Already existed"
        }
        else {
            az monitor app-insights component create `
                --app $AppInsightsName `
                --resource-group $ResourceGroupName `
                --location $Location `
                --kind web `
                --application-type web `
                --output none 2>$null
            if ($LASTEXITCODE -ne 0) { throw "az monitor app-insights component create failed" }

            $appInsightsConnectionString = az monitor app-insights component show `
                --app $AppInsightsName `
                --resource-group $ResourceGroupName `
                --query connectionString -o tsv 2>$null
            if ($LASTEXITCODE -ne 0) { throw "Failed to retrieve connection string" }

            Write-Success "Created Application Insights: $AppInsightsName"
            Add-Result "Application Insights" "Provisioned" "Created in $Location"
        }
    }
    catch {
        Write-Fail "Application Insights: $_"
        Add-Result "Application Insights" "Failed" "Fix: Verify Microsoft.Insights provider is registered. Run: az provider register -n Microsoft.Insights"
    }
}

# ──────────────────────────────────────────────────────────────────
# 9. RBAC Role Assignments
# ──────────────────────────────────────────────────────────────────

Write-Step "9/10 RBAC Role Assignments"

if ($SkipRoleAssignment) {
    Write-Skip "Skipped by -SkipRoleAssignment flag"
    Add-Result "RBAC: AI Developer" "Skipped" "User opted out"
    Add-Result "RBAC: Content Safety User" "Skipped" "User opted out"
    Add-Result "RBAC: Language User" "Skipped" "User opted out"
    Add-Result "RBAC: Search Index Data Contributor" "Skipped" "User opted out"
    Add-Result "RBAC: Monitoring Metrics Publisher" "Skipped" "User opted out"
}
else {
    # AI Foundry Project — Azure AI Developer role
    if ($projectCreated) {
        try {
            $scope = az ml workspace show `
                --name $AIProjectName `
                --resource-group $ResourceGroupName `
                --query id -o tsv 2>$null

            az role assignment create `
                --assignee $principalId `
                --role "Azure AI Developer" `
                --scope $scope `
                --output none 2>$null
            Write-Success "Assigned 'Azure AI Developer' on project $AIProjectName"
            Add-Result "RBAC: AI Developer" "Provisioned" "Assigned to $principalId on project"
        }
        catch {
            Write-Fail "AI Developer role assignment: $_"
            Add-Result "RBAC: AI Developer" "Failed" "Fix: Ensure you have Owner/User Access Administrator on the resource group"
        }
    }
    else {
        Write-Skip "AI Developer role — project not provisioned"
        Add-Result "RBAC: AI Developer" "Skipped" "Depends on AI Foundry Project"
    }

    # Content Safety — Cognitive Services User role
    if ($contentSafetyEndpoint) {
        try {
            $scope = az cognitiveservices account show `
                --name $ContentSafetyResourceName `
                --resource-group $ResourceGroupName `
                --query id -o tsv 2>$null

            az role assignment create `
                --assignee $principalId `
                --role "Cognitive Services User" `
                --scope $scope `
                --output none 2>$null
            Write-Success "Assigned 'Cognitive Services User' role"
            Add-Result "RBAC: Content Safety User" "Provisioned" "Assigned to $principalId"
        }
        catch {
            Write-Fail "Content Safety role assignment: $_"
            Add-Result "RBAC: Content Safety User" "Failed" "Fix: Ensure you have Owner/User Access Administrator on the resource group"
        }
    }
    else {
        Write-Skip "Content Safety role — resource not provisioned"
        Add-Result "RBAC: Content Safety User" "Skipped" "Depends on Content Safety resource"
    }

    # Azure AI Language — Cognitive Services Language Reader role
    if ($languageEndpoint) {
        try {
            $scope = az cognitiveservices account show `
                --name $LanguageResourceName `
                --resource-group $ResourceGroupName `
                --query id -o tsv 2>$null

            az role assignment create `
                --assignee $principalId `
                --role "Cognitive Services Language Reader" `
                --scope $scope `
                --output none 2>$null
            Write-Success "Assigned 'Cognitive Services Language Reader' role"
            Add-Result "RBAC: Language User" "Provisioned" "Assigned to $principalId"
        }
        catch {
            Write-Fail "Language role assignment: $_"
            Add-Result "RBAC: Language User" "Failed" "Fix: Ensure you have Owner/User Access Administrator on the resource group"
        }
    }
    else {
        Write-Skip "Language role — resource not provisioned"
        Add-Result "RBAC: Language User" "Skipped" "Depends on Language resource"
    }

    # Azure AI Search - Search Index Data Contributor (indexer writes + reads)
    if ($azureSearchEndpoint) {
        try {
            $searchScopeJson = az search service show --name $AzureSearchServiceName --resource-group $ResourceGroupName --query id --output tsv 2>$null
            if ($LASTEXITCODE -eq 0 -and $searchScopeJson) {
                az role assignment create --assignee $principalId --role "Search Index Data Contributor" --scope $searchScopeJson --output none 2>$null | Out-Null
                Write-Success "Assigned 'Search Index Data Contributor' role"
                Add-Result "RBAC: Search Index Data Contributor" "Provisioned" "Assigned to $principalId"
            }
            else {
                throw "Could not resolve Search service resource id"
            }
        }
        catch {
            Write-Fail "Search role assignment: $_"
            Add-Result "RBAC: Search Index Data Contributor" "Failed" "Fix: Ensure you have Owner/User Access Administrator on the resource group"
        }
    }
    else {
        Write-Skip "Search role skipped - resource not provisioned"
        Add-Result "RBAC: Search Index Data Contributor" "Skipped" "Depends on Azure AI Search resource"
    }

    # Application Insights — Monitoring Metrics Publisher role
    if ($appInsightsConnectionString) {
        try {
            $scope = az monitor app-insights component show `
                --app $AppInsightsName `
                --resource-group $ResourceGroupName `
                --query id -o tsv 2>$null

            az role assignment create `
                --assignee $principalId `
                --role "Monitoring Metrics Publisher" `
                --scope $scope `
                --output none 2>$null
            Write-Success "Assigned 'Monitoring Metrics Publisher' role"
            Add-Result "RBAC: Monitoring Metrics Publisher" "Provisioned" "Assigned to $principalId"
        }
        catch {
            Write-Fail "App Insights role assignment: $_"
            Add-Result "RBAC: Monitoring Metrics Publisher" "Failed" "Fix: Ensure you have Owner/User Access Administrator on the resource group"
        }
    }
    else {
        Write-Skip "App Insights role — resource not provisioned"
        Add-Result "RBAC: Monitoring Metrics Publisher" "Skipped" "Depends on Application Insights resource"
    }
}

# ──────────────────────────────────────────────────────────────────
# 9. Update appsettings.json
# ──────────────────────────────────────────────────────────────────

Write-Step "10/10 Update appsettings.json"

if (-not $UpdateConfig) {
    Write-Skip "Skipped — use -UpdateConfig flag to auto-update"
    Add-Result "Config Update" "Skipped" "Use -UpdateConfig to enable"
}
else {
    $configPath = Join-Path (Join-Path (Join-Path $PSScriptRoot "..") "config") "appsettings.json"
    $configPath = [System.IO.Path]::GetFullPath($configPath)

    if (-not (Test-Path $configPath)) {
        Write-Fail "Config file not found: $configPath"
        Add-Result "Config Update" "Failed" "File not found: $configPath"
    }
    else {
        try {
            $config = Get-Content $configPath -Raw | ConvertFrom-Json

            if ($modelEndpoint) {
                $config.CTLAgent.AzureAIFoundry.Endpoint = $modelEndpoint
                $config.CTLAgent.AzureAIFoundry.ModelId = $ModelName

                if ($modelApiKey) {
                    # Serverless endpoints provide their own API key
                    $config.CTLAgent.AzureAIFoundry.UseAzureIdentity = $false
                    $config.CTLAgent.AzureAIFoundry.ApiKey = $modelApiKey
                }
                else {
                    $config.CTLAgent.AzureAIFoundry.UseAzureIdentity = $true
                    $config.CTLAgent.AzureAIFoundry.ApiKey = ""
                }
            }

            if ($contentSafetyEndpoint) {
                $config.ContentSafety.Endpoint = $contentSafetyEndpoint
                $config.ContentSafety.Enabled = $true
                $config.ContentSafety.PromptShieldsEnabled = $true
            }

            if ($languageEndpoint) {
                $config.PiiFilter.AzurePiiEnabled = $true
                $config.PiiFilter.Endpoint = $languageEndpoint
            }

            if ($azureSearchEndpoint) {
                $searchObj = [ordered]@{
                    Enabled              = $true
                    Endpoint             = $azureSearchEndpoint
                    IndexName            = 'ctl-policy-knowledge'
                    UseAzureIdentity     = $true
                    TopK                 = 5
                    EmbeddingDeployment  = 'text-embedding-3-small'
                    EmbeddingDimensions  = 1536
                    AzureOpenAIEndpoint  = ''
                }
                if ($azureSearchAdminKey) { $searchObj['AdminKey'] = $azureSearchAdminKey }
                if ($azureSearchQueryKey) { $searchObj['QueryKey'] = $azureSearchQueryKey }

                if (-not $config.CTLAgent.PSObject.Properties['RAG']) {
                    $config.CTLAgent | Add-Member -NotePropertyName 'RAG' -NotePropertyValue ([pscustomobject]@{}) -Force
                }
                $config.CTLAgent.RAG | Add-Member -NotePropertyName 'AzureSearch' -NotePropertyValue ([pscustomobject]$searchObj) -Force
            }

            if ($appInsightsConnectionString) {
                if (-not $config.PSObject.Properties['ApplicationInsights']) {
                    $config | Add-Member -NotePropertyName 'ApplicationInsights' -NotePropertyValue ([pscustomobject]@{}) -Force
                }
                $config.ApplicationInsights | Add-Member -NotePropertyName 'ConnectionString' -NotePropertyValue $appInsightsConnectionString -Force
            }

            $config | ConvertTo-Json -Depth 10 | Set-Content $configPath -Encoding UTF8
            Write-Success "Updated $configPath"
            Add-Result "Config Update" "Provisioned" "Endpoints written to appsettings.json"
        }
        catch {
            Write-Fail "Config update: $_"
            Add-Result "Config Update" "Failed" "$_"
        }
    }
}

# ──────────────────────────────────────────────────────────────────
# Summary
# ──────────────────────────────────────────────────────────────────

Write-Host "`n" -NoNewline
Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor White
Write-Host "║              PROVISIONING SUMMARY                          ║" -ForegroundColor White
Write-Host "╠══════════════════════════════════════════════════════════════╣" -ForegroundColor White

$provisioningResults | ForEach-Object {
    $color = switch ($_.Status) {
        "Provisioned" { "Green" }
        "Failed"      { "Red" }
        "Skipped"     { "Yellow" }
    }
    $icon = switch ($_.Status) {
        "Provisioned" { "[OK]  " }
        "Failed"      { "[FAIL]" }
        "Skipped"     { "[SKIP]" }
    }
    $line = "  $icon $($_.Service)"
    Write-Host $line -ForegroundColor $color -NoNewline
    Write-Host " — $($_.Detail)" -ForegroundColor DarkGray
}

Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor White

# Exit with failure if any service failed
$failedCount = ($provisioningResults | Where-Object { $_.Status -eq "Failed" }).Count
if ($failedCount -gt 0) {
    Write-Host "`n$failedCount service(s) failed to provision. See details above." -ForegroundColor Red
    exit 1
}

Write-Host "`nAll services provisioned successfully." -ForegroundColor Green

# Print next steps
if (-not $UpdateConfig -and ($modelEndpoint -or $contentSafetyEndpoint -or $languageEndpoint -or $appInsightsConnectionString)) {
    Write-Host "`nNext steps — update config/appsettings.json:" -ForegroundColor Yellow
    if ($modelEndpoint) {
        Write-Host "  CTLAgent:AzureAIFoundry:Endpoint = $modelEndpoint" -ForegroundColor DarkYellow
        Write-Host "  CTLAgent:AzureAIFoundry:ModelId   = $ModelName" -ForegroundColor DarkYellow
        if ($modelApiKey) {
            Write-Host "  CTLAgent:AzureAIFoundry:ApiKey    = (retrieved - use -UpdateConfig to auto-set)" -ForegroundColor DarkYellow
        }
    }
    if ($contentSafetyEndpoint) {
        Write-Host "  ContentSafety:Endpoint = $contentSafetyEndpoint" -ForegroundColor DarkYellow
        Write-Host "  ContentSafety:Enabled  = true" -ForegroundColor DarkYellow
    }
    if ($languageEndpoint) {
        Write-Host "  PiiFilter:AzurePiiEnabled = true" -ForegroundColor DarkYellow
        Write-Host "  PiiFilter:Endpoint        = $languageEndpoint" -ForegroundColor DarkYellow
    }
    if ($appInsightsConnectionString) {
        Write-Host "  ApplicationInsights:ConnectionString = (retrieved - use -UpdateConfig to auto-set)" -ForegroundColor DarkYellow
    }
    Write-Host "  Or re-run with -UpdateConfig to auto-update." -ForegroundColor Yellow
}
