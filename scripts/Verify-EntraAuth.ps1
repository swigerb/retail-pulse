#Requires -Version 7.0
<#
.SYNOPSIS
    Read-only verification of the Retail Pulse single-tenant Entra app registration.

.DESCRIPTION
    Confirms — WITHOUT making any changes — that the app registration and service
    principal produced by Setup-EntraAuth.ps1 satisfy the Retail Pulse auth
    contract:
      * Single tenant (signInAudience = AzureADMyOrg).
      * No client secret / password credential (PKCE public client only).
      * identifierUris contains api://{clientId} (the API audience).
      * Delegated scope (default access_as_user) is present and enabled.
      * App role (default RetailPulse.User) is present and enabled.
      * At least one SPA redirect URI is registered.
      * Service principal exists with appRoleAssignmentRequired = true.

    Exits non-zero if any check fails, so it can gate a security review or CI step.
    Uses `az rest` with the caller's delegated token; prints only safe identifiers
    and check results — never secrets, tokens, or PII beyond the app config.

.PARAMETER TenantId
    Entra tenant (directory) GUID. REQUIRED.

.PARAMETER ClientId
    Application (client) ID to verify. REQUIRED.

.PARAMETER ApiScopeName
    Expected delegated scope name. Default: access_as_user.

.PARAMETER AppRoleValue
    Expected app role value. Default: RetailPulse.User.

.EXAMPLE
    ./scripts/Verify-EntraAuth.ps1 -TenantId <guid> -ClientId <appId>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TenantId,
    [Parameter(Mandatory = $true)][string]$ClientId,
    [string]$ApiScopeName = 'access_as_user',
    [string]$AppRoleValue = 'RetailPulse.User'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$graph = 'https://graph.microsoft.com/v1.0'
$failures = 0

function Test-Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) {
        Write-Host "  [PASS] $name" -ForegroundColor Green
    }
    else {
        Write-Host "  [FAIL] $name $detail" -ForegroundColor Red
        $script:failures++
    }
}

function Invoke-GraphGet([string]$Url) {
    $out = az rest --method get --url $Url 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Graph GET $Url failed: $out" }
    if ([string]::IsNullOrWhiteSpace($out)) { return $null }
    return ($out | ConvertFrom-Json)
}

Write-Host "=== Verifying Entra app registration for client $ClientId ===" -ForegroundColor Cyan

# Context / tenant check.
$account = az account show --output json 2>$null | ConvertFrom-Json
if (-not $account) { throw 'Not logged in. Run: az login --tenant <tenantId>' }
Test-Check 'Signed-in tenant matches -TenantId' ($account.tenantId -eq $TenantId) "(got $($account.tenantId))"

# Application.
$appResp = Invoke-GraphGet "$graph/applications?`$filter=appId eq '$ClientId'&`$select=id,appId,displayName,signInAudience,identifierUris,api,appRoles,spa,web,passwordCredentials"
if (-not $appResp -or $appResp.value.Count -eq 0) {
    Test-Check 'Application exists' $false "(no application with appId $ClientId)"
    Write-Host "`nVerification FAILED: application not found." -ForegroundColor Red
    exit 1
}
$app = $appResp.value[0]
Test-Check 'Application exists' $true

Test-Check 'Single tenant (signInAudience = AzureADMyOrg)' ($app.signInAudience -eq 'AzureADMyOrg') "(got $($app.signInAudience))"

$hasSecret = $app.passwordCredentials -and (@($app.passwordCredentials).Count -gt 0)
Test-Check 'No client secret / password credential (PKCE public client)' (-not $hasSecret)

$audience = "api://$ClientId"
$hasAud = $app.identifierUris -and ($app.identifierUris -contains $audience)
Test-Check "identifierUris contains $audience" $hasAud "(got: $($app.identifierUris -join ', '))"

$scope = $null
if ($app.api -and $app.api.oauth2PermissionScopes) {
    $scope = @($app.api.oauth2PermissionScopes) | Where-Object { $_.value -eq $ApiScopeName } | Select-Object -First 1
}
Test-Check "Delegated scope '$ApiScopeName' present and enabled" ($scope -and $scope.isEnabled)

$role = $null
if ($app.appRoles) {
    $role = @($app.appRoles) | Where-Object { $_.value -eq $AppRoleValue } | Select-Object -First 1
}
Test-Check "App role '$AppRoleValue' present and enabled" ($role -and $role.isEnabled)

$spaCount = 0
if ($app.spa -and $app.spa.redirectUris) { $spaCount = @($app.spa.redirectUris).Count }
Test-Check 'At least one SPA redirect URI registered' ($spaCount -gt 0)

# Service principal.
$spResp = Invoke-GraphGet "$graph/servicePrincipals?`$filter=appId eq '$ClientId'&`$select=id,appId,appRoleAssignmentRequired"
$sp = if ($spResp -and $spResp.value.Count -gt 0) { $spResp.value[0] } else { $null }
Test-Check 'Service principal exists' ($null -ne $sp)
if ($sp) {
    Test-Check 'Assignment required (appRoleAssignmentRequired = true)' ([bool]$sp.appRoleAssignmentRequired)
}

Write-Host ''
if ($failures -eq 0) {
    Write-Host "All checks PASSED. Safe config:" -ForegroundColor Green
    [PSCustomObject]@{
        TenantId = $TenantId
        ClientId = $ClientId
        Audience = $audience
        ApiScope = $ApiScopeName
        AppRole  = $AppRoleValue
    } | Format-List | Out-String | Write-Host
    exit 0
}
else {
    Write-Host "$failures check(s) FAILED. Re-run Setup-EntraAuth.ps1 -Apply to reconcile." -ForegroundColor Red
    exit 1
}
