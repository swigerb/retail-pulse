#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Live, read-only verification of the optional Azure API Center and its APIM link.

.DESCRIPTION
    Verifies, using only ARM GET calls, that when AZURE_API_CENTER_ENABLED is true:
        * the Microsoft.ApiCenter/services resource exists and is Succeeded;
        * it has a system-assigned identity;
        * the default workspace has an apiSources entry whose
          azureApiManagementSource.resourceId is the Retail Pulse APIM service.
    Synchronization is one-way (APIM -> API Center) and eventually consistent;
    this script checks the declarative link, not that every API has appeared yet.

    Exit codes: 0 = all checks pass (or feature disabled), 1 = failures,
    2 = skipped (not signed in / missing inputs).

.PARAMETER ResourceGroup  Defaults to $env:AZURE_RESOURCE_GROUP.
.PARAMETER ApimName       Defaults to $env:AZURE_APIM_NAME.
.PARAMETER ApiCenterName  Defaults to $env:AZURE_API_CENTER_NAME.
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = $env:AZURE_RESOURCE_GROUP,
    [string]$ApimName = $env:AZURE_APIM_NAME,
    [string]$ApiCenterName = $env:AZURE_API_CENTER_NAME
)

$ErrorActionPreference = 'Stop'

if ($env:AZURE_API_CENTER_ENABLED -and $env:AZURE_API_CENTER_ENABLED.ToLowerInvariant() -ne 'true') {
    Write-Host '[SKIP] AZURE_API_CENTER_ENABLED is not true; API Center is not provisioned.'
    exit 0
}
if ([string]::IsNullOrWhiteSpace($ResourceGroup) -or [string]::IsNullOrWhiteSpace($ApimName) -or [string]::IsNullOrWhiteSpace($ApiCenterName)) {
    Write-Host '[SKIP] ResourceGroup, ApimName and ApiCenterName are required (azd env outputs).'
    exit 2
}

$sub = (az account show --query id -o tsv 2>$null | Out-String).Trim()
if (-not $sub) { Write-Host '[SKIP] az is not signed in.'; exit 2 }
$token = (az account get-access-token --resource https://management.azure.com --query accessToken -o tsv 2>$null | Out-String).Trim()

function Invoke-ArmGet([string]$Path, [string]$ApiVersion) {
    $url = "https://management.azure.com/subscriptions/$sub$Path?api-version=$ApiVersion"
    try { return Invoke-RestMethod -Uri $url -Headers @{ Authorization = "Bearer $token" } -Method Get }
    catch {
        $status = $_.Exception.Response.StatusCode
        if ($status -in @('NotFound', 'Forbidden')) { return $null }
        throw
    }
}

$failures = [System.Collections.Generic.List[string]]::new()
function Assert-Check([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "[PASS] $Message" } else { Write-Host "[FAIL] $Message"; $failures.Add($Message) }
}

$base = "/resourceGroups/$ResourceGroup/providers/Microsoft.ApiCenter/services/$ApiCenterName"
$center = Invoke-ArmGet $base '2024-03-01'
Assert-Check ($null -ne $center) "API Center '$ApiCenterName' exists"
if ($center) {
    Assert-Check ($center.properties.provisioningState -eq 'Succeeded') 'API Center provisioningState is Succeeded'
    Assert-Check ($center.identity.type -match 'SystemAssigned') 'API Center has a system-assigned identity'
}

$sources = Invoke-ArmGet "$base/workspaces/default/apiSources" '2024-06-01-preview'
$apimId = "/subscriptions/$sub/resourceGroups/$ResourceGroup/providers/Microsoft.ApiManagement/service/$ApimName"
$linked = @()
if ($sources) {
    $linked = @($sources.value | Where-Object { $_.properties.azureApiManagementSource.resourceId -ieq $apimId })
}
Assert-Check ($linked.Count -ge 1) "default workspace has an apiSource linked to APIM '$ApimName' (one-way sync)"

if ($failures.Count -gt 0) {
    Write-Host "`n$($failures.Count) check(s) failed."
    exit 1
}
Write-Host "`nAll API Center checks passed."
exit 0
