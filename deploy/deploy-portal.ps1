#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Deploys the AI Gateway Dev Portal to Azure Static Web Apps
.DESCRIPTION
    Builds and deploys the ai-gateway-dev-portal to Azure Static Web Apps.
    Prerequisites: Node.js 20+, Azure CLI logged in, SWA CLI installed
.PARAMETER ResourceGroup
    Resource group name (default: rg-bstest-dev-northcentralus-001)
.PARAMETER SwaName
    Static Web App name (default: retail-pulse-portal)
.PARAMETER ClientId
    Entra app client ID for MSAL authentication
#>
param(
    [string]$ResourceGroup = "rg-bstest-dev-northcentralus-001",
    [string]$SwaName = "retail-pulse-portal",
    [string]$ClientId = "5bd5b4d1-914a-4c28-ada7-6e466d16a080"
)

$ErrorActionPreference = "Stop"
$portalDir = Join-Path $PSScriptRoot ".." "ai-gateway-dev-portal"

Write-Host "`n=== Deploying AI Gateway Dev Portal ===" -ForegroundColor Cyan

# Get deployment token
Write-Host "Retrieving SWA deployment token..." -ForegroundColor Yellow
$token = az staticwebapp secrets list --name $SwaName --resource-group $ResourceGroup --query "properties.apiKey" -o tsv
if (-not $token) { throw "Failed to retrieve SWA deployment token" }

# Build
Write-Host "Building portal..." -ForegroundColor Yellow
Push-Location $portalDir
"VITE_AZURE_CLIENT_ID=$ClientId" | Set-Content .env
npm run build
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "Build failed" }

# Deploy
Write-Host "Deploying to Azure Static Web Apps..." -ForegroundColor Yellow
swa deploy ./dist --deployment-token $token --env production
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "Deployment failed" }
Pop-Location

# Get URL
$hostname = az staticwebapp show --name $SwaName --resource-group $ResourceGroup --query "defaultHostname" -o tsv
Write-Host "`nPortal deployed: https://$hostname" -ForegroundColor Green
