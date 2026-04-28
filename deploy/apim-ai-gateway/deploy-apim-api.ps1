<#
.SYNOPSIS
    Deploys the Patron Pulse APIM AI Gateway infrastructure.

.DESCRIPTION
    Deploys Bicep template that creates an inference API on an existing APIM instance
    backed by Azure AI Foundry. Optionally sets the subscription key as a .NET user secret.

.PARAMETER ResourceGroup
    Target resource group for the APIM instance. Default: rg-bstest-dev-northcentralus-001

.PARAMETER SetUserSecrets
    If specified, stores the APIM subscription key as a .NET user secret for RetailPulse.Api.
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = 'rg-bstest-dev-northcentralus-001',
    [switch]$SetUserSecrets
)

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot

# ── Prerequisites ──────────────────────────────────────────────────────────────
Write-Host '🔍 Checking Azure CLI...' -ForegroundColor Cyan
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Error 'Azure CLI (az) is not installed. Install from https://aka.ms/installazurecli'
    exit 1
}

$account = az account show 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error 'Not logged in to Azure CLI. Run "az login" first.'
    exit 1
}
$accountInfo = $account | ConvertFrom-Json
Write-Host "  Subscription: $($accountInfo.name) ($($accountInfo.id))" -ForegroundColor Gray

# ── Deploy ─────────────────────────────────────────────────────────────────────
Write-Host "`n🚀 Deploying APIM AI Gateway to $ResourceGroup..." -ForegroundColor Cyan

$deploymentName = "retail-pulse-ai-gateway-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

$result = az deployment group create `
    --resource-group $ResourceGroup `
    --name $deploymentName `
    --template-file "$scriptDir\main.bicep" `
    --parameters "$scriptDir\params.json" `
    --output json

if ($LASTEXITCODE -ne 0) {
    Write-Error "Deployment failed."
    exit 1
}

$deployment = $result | Where-Object { $_ -match '^\s*[\{\[]' -or $_ -match '^\s*"' -or $_ -match '^\s*\}' } | Out-String | ConvertFrom-Json
$outputs = $deployment.properties.outputs

$gatewayUrl       = $outputs.apimGatewayUrl.value
$subscriptionKey  = $outputs.subscriptionKey.value
$inferenceEndpoint = $outputs.inferenceEndpoint.value

# ── User secrets ───────────────────────────────────────────────────────────────
if ($SetUserSecrets) {
    Write-Host "`n🔑 Setting .NET user secret 'OpenAI:ApiKey'..." -ForegroundColor Cyan
    $apiProject = Join-Path $scriptDir '..\..\src\RetailPulse.Api'
    dotnet user-secrets set 'OpenAI:ApiKey' $subscriptionKey --project $apiProject
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'Failed to set user secret. Set it manually if needed.'
    } else {
        Write-Host '  User secret set successfully.' -ForegroundColor Green
    }
}

# ── Summary ────────────────────────────────────────────────────────────────────
Write-Host "`n✅ Deployment complete!" -ForegroundColor Green
Write-Host '─────────────────────────────────────────────────────────' -ForegroundColor DarkGray
Write-Host "  Gateway URL:         $gatewayUrl"
Write-Host "  Inference Endpoint:  $inferenceEndpoint"
Write-Host "  Subscription Key:    $($subscriptionKey.Substring(0,8))****"
Write-Host '─────────────────────────────────────────────────────────' -ForegroundColor DarkGray
Write-Host ''
Write-Host '📋 Next steps:' -ForegroundColor Yellow
Write-Host "  1. Test the endpoint:"
Write-Host "     curl $inferenceEndpoint/deployments/gpt-5.4-mini/chat/completions?api-version=2025-03-01-preview \"
Write-Host "       -H 'api-key: <subscription-key>' \"
Write-Host "       -H 'Content-Type: application/json' \"
Write-Host "       -d '{""messages"":[{""role"":""user"",""content"":""Hello""}]}'"
Write-Host ''
Write-Host "  2. Or set user secrets and run the API:"
Write-Host "     .\deploy-apim-api.ps1 -SetUserSecrets"
Write-Host "     dotnet run --project ..\..\src\RetailPulse.Api"
