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
.PARAMETER McpApiName     APIM/API Center API id of the MCP server (default retail-pulse-mcp-server).
.PARAMETER McpEndpoint    Expected APIM remote MCP endpoint. Defaults to $env:AZURE_APIM_MCP_ENDPOINT.
.PARAMETER SelfTest       Offline: exercises the MCP registration checks against synthetic data; no Azure calls.

    MCP registration (issue #336) additionally verifies, read-only, that the API Center API is
    kind 'mcp', has a version and an active deployment linked to an environment whose runtimeUri
    is exactly the APIM endpoint (https, no credentials), that the discovery metadata lists exactly
    the four approved tools, and that APIM itself serves exactly those four tools.
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = $env:AZURE_RESOURCE_GROUP,
    [string]$ApimName = $env:AZURE_APIM_NAME,
    [string]$ApiCenterName = $env:AZURE_API_CENTER_NAME,
    [string]$McpApiName = 'retail-pulse-mcp-server',
    [string]$McpEndpoint = $env:AZURE_APIM_MCP_ENDPOINT,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$script:ApprovedTools = @('get_demand_forecast', 'get_inventory', 'get_stockout_risk', 'get_supply_disruptions')

function Get-McpRegistrationFailures {
    param($Api, $Versions, $Deployments, $Environment, $ApimTools, [string]$ExpectedEndpoint)
    $f = [System.Collections.Generic.List[string]]::new()
    if ($null -eq $Api) { $f.Add('MCP API is not registered in API Center'); return , $f.ToArray() }
    if ($Api.properties.kind -ne 'mcp') { $f.Add("API Center API kind is '$($Api.properties.kind)', expected 'mcp'") }
    $meta = $Api.properties.customProperties.retailPulseMcp
    if ($null -eq $meta) { $f.Add("API Center API is missing the 'retailPulseMcp' discovery metadata") }
    else {
        $registered = @($meta.approvedTools | Sort-Object)
        if (($registered -join ',') -ne ($script:ApprovedTools -join ',')) { $f.Add("discovery metadata tools [$($registered -join ', ')] != approved [$($script:ApprovedTools -join ', ')]") }
        if ($meta.readOnly -ne $true) { $f.Add('discovery metadata is not marked readOnly') }
        $schemaNames = @($meta.toolSchemas.PSObject.Properties.Name | Sort-Object)
        if (($schemaNames -join ',') -ne ($script:ApprovedTools -join ',')) { $f.Add("tool schemas [$($schemaNames -join ', ')] != approved tools") }
    }
    if (@($Versions).Count -lt 1) { $f.Add('API has no registered version') }
    elseif (-not (@($Versions) | Where-Object { $_.properties.lifecycleStage })) { $f.Add('API version has no lifecycle stage') }
    $active = @($Deployments | Where-Object { $_.properties.state -eq 'active' })
    if ($active.Count -lt 1) { $f.Add('API has no active deployment') }
    foreach ($d in $active) {
        $uris = @($d.properties.server.runtimeUri)
        if ($uris.Count -ne 1 -or $uris[0] -ne $ExpectedEndpoint) { $f.Add("deployment '$($d.name)' runtimeUri [$($uris -join ', ')] != APIM endpoint '$ExpectedEndpoint'") }
        foreach ($u in $uris) { if ($u -notmatch '^https://' -or $u -match '[@?]|subscription-key') { $f.Add("deployment '$($d.name)' runtimeUri must be https without credentials") } }
        if ($null -eq $Environment -or -not ($d.properties.environmentId -like "*/environments/$($Environment.name)")) { $f.Add("deployment '$($d.name)' is not linked to an existing API Center environment") }
    }
    $apimNames = @($ApimTools | ForEach-Object { $_.name } | Sort-Object)
    if (($apimNames -join ',') -ne ($script:ApprovedTools -join ',')) { $f.Add("APIM serves tools [$($apimNames -join ', ')] != approved [$($script:ApprovedTools -join ', ')]") }
    return , $f.ToArray()
}

if ($SelfTest) {
    $ep = 'https://apim.example.net/retail-pulse-mcp/mcp'
    $tools = @($script:ApprovedTools | ForEach-Object { [pscustomobject]@{ name = $_ } })
    $schemas = [pscustomobject]@{}
    foreach ($t in $script:ApprovedTools) { $schemas | Add-Member -NotePropertyName $t -NotePropertyValue ([pscustomobject]@{ readOnly = $true }) }
    $goodApi = [pscustomobject]@{ properties = [pscustomobject]@{ kind = 'mcp'; customProperties = [pscustomobject]@{ retailPulseMcp = [pscustomobject]@{ approvedTools = $script:ApprovedTools; readOnly = $true; toolSchemas = $schemas } } } }
    $ver = @([pscustomobject]@{ properties = [pscustomobject]@{ lifecycleStage = 'production' } })
    $env1 = [pscustomobject]@{ name = 'apim-gateway' }
    $dep = @([pscustomobject]@{ name = 'd'; properties = [pscustomobject]@{ state = 'active'; environmentId = '/workspaces/default/environments/apim-gateway'; server = [pscustomobject]@{ runtimeUri = @($ep) } } })
    $bad = 0
    function Expect([string]$Name, [int]$Min, $r) {
        $n = @($r).Count
        if (($Min -eq 0 -and $n -ne 0) -or ($Min -gt 0 -and $n -lt $Min)) { Write-Host "[FAIL] self-test: $Name ($n failures)"; $script:bad++ } else { Write-Host "[PASS] self-test: $Name" }
    }
    Expect 'valid registration passes' 0 (Get-McpRegistrationFailures $goodApi $ver $dep $env1 $tools $ep)
    Expect 'missing API fails' 1 (Get-McpRegistrationFailures $null $ver $dep $env1 $tools $ep)
    Expect 'extra APIM tool fails' 1 (Get-McpRegistrationFailures $goodApi $ver $dep $env1 ($tools + [pscustomobject]@{ name = 'write_inventory' }) $ep)
    Expect 'missing APIM tool fails' 1 (Get-McpRegistrationFailures $goodApi $ver $dep $env1 $tools[0..2] $ep)
    Expect 'wrong endpoint fails' 1 (Get-McpRegistrationFailures $goodApi $ver $dep $env1 $tools 'https://other.example.net/mcp')
    Expect 'unlinked environment fails' 1 (Get-McpRegistrationFailures $goodApi $ver $dep $null $tools $ep)
    Expect 'no version fails' 1 (Get-McpRegistrationFailures $goodApi @() $dep $env1 $tools $ep)
    Expect 'no deployment fails' 1 (Get-McpRegistrationFailures $goodApi $ver @() $env1 $tools $ep)
    if ($bad -gt 0) { exit 1 }
    Write-Host 'Self-test passed.'
    exit 0
}

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

$mcp = Invoke-ArmGet "$base/workspaces/default/apis/$McpApiName" '2024-03-01'
$mcpVersions = Invoke-ArmGet "$base/workspaces/default/apis/$McpApiName/versions" '2024-03-01'
$mcpDeployments = Invoke-ArmGet "$base/workspaces/default/apis/$McpApiName/deployments" '2024-03-01'
$mcpEnvironments = Invoke-ArmGet "$base/workspaces/default/environments" '2024-03-01'
$apimTools = Invoke-ArmGet "/resourceGroups/$ResourceGroup/providers/Microsoft.ApiManagement/service/$ApimName/apis/$McpApiName/tools" '2025-09-01-preview'
if ([string]::IsNullOrWhiteSpace($McpEndpoint)) { Assert-Check $false 'McpEndpoint / AZURE_APIM_MCP_ENDPOINT is required to verify the remote endpoint' }
$envName = ''
$firstDeployment = @($mcpDeployments.value) | Select-Object -First 1
if ($firstDeployment -and $firstDeployment.properties.environmentId) { $envName = ($firstDeployment.properties.environmentId -split '/')[-1] }
$mcpEnvironment = @($mcpEnvironments.value) | Where-Object { $_.name -eq $envName } | Select-Object -First 1
foreach ($m in (Get-McpRegistrationFailures $mcp @($mcpVersions.value) @($mcpDeployments.value) $mcpEnvironment @($apimTools.value) $McpEndpoint)) {
    Assert-Check $false $m
}
if ($failures.Count -eq 0) { Write-Host '[PASS] MCP registration, linkage, endpoint and four-tool set verified' }
Write-Host 'Note: API Center synchronization is one-way and eventually consistent; re-run if the API was just provisioned.'

if ($failures.Count -gt 0) {
    Write-Host "`n$($failures.Count) check(s) failed."
    exit 1
}
Write-Host "`nAll API Center checks passed."
exit 0
