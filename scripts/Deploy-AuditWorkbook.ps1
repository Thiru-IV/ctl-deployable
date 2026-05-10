param(
  [string]$ResourceGroup = 'CTL-Agent-RG',
  [string]$AppInsightsName = 'ctlagent-appinsights',
  [string]$Location = 'eastus2',
  [string]$DisplayName = 'CTL Agent - Verdict Evidence and Audit Trail',
  [string]$WorkbookJsonPath = "$PSScriptRoot/workbook-ctl-audit.json"
)

$ErrorActionPreference = 'Stop'

Write-Host "Resolving Application Insights resource id..." -ForegroundColor Cyan
$ai = az resource show -g $ResourceGroup -n $AppInsightsName --resource-type 'microsoft.insights/components' --query 'id' -o tsv
if (-not $ai) { throw "Application Insights '$AppInsightsName' not found in '$ResourceGroup'." }

$sub = az account show --query id -o tsv
$wbId = [guid]::NewGuid().ToString()

Write-Host "Building body..." -ForegroundColor Cyan
$serialized = Get-Content $WorkbookJsonPath -Raw
$bodyObj = [ordered]@{
  location = $Location
  kind = 'shared'
  properties = [ordered]@{
    displayName    = $DisplayName
    category       = 'workbook'
    serializedData = $serialized
    version        = '1.0'
    sourceId       = $ai
  }
}
$bodyJson = $bodyObj | ConvertTo-Json -Depth 20 -Compress
$bodyPath = Join-Path $PSScriptRoot 'workbook-body.json'
Set-Content -Path $bodyPath -Value $bodyJson -Encoding utf8
Write-Host ("Body file: {0} ({1} bytes)" -f $bodyPath, (Get-Item $bodyPath).Length) -ForegroundColor Cyan

$url = "https://management.azure.com/subscriptions/$sub/resourceGroups/$ResourceGroup/providers/Microsoft.Insights/workbooks/$wbId" + "?api-version=2022-04-01"
Write-Host "PUT $url" -ForegroundColor Cyan

$result = az rest --method put --url $url --body "@$bodyPath" --headers 'Content-Type=application/json' -o json
if ($LASTEXITCODE -ne 0) { throw "az rest failed with exit code $LASTEXITCODE" }

$obj = $result | ConvertFrom-Json
Write-Host ""
Write-Host "Workbook created." -ForegroundColor Green
Write-Host ("  Name (resource id):  {0}" -f $obj.name)
Write-Host ("  Display name:        {0}" -f $obj.properties.displayName)
Write-Host ""
$portalPath = "/resource$($obj.id)/workbook"
$portalUrl = "https://portal.azure.com/#@/resource$($obj.id)"
Write-Host "Open in portal:" -ForegroundColor Yellow
Write-Host "  $portalUrl"
