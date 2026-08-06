#Requires -Version 7.0
<#
.SYNOPSIS
    Idempotently provisions the single-tenant Entra app registration that fronts
    the Retail Pulse SPA + API with MSAL PKCE auth.

.DESCRIPTION
    Creates and configures ONE single-tenant Entra application + service principal:
      * Public SPA client (PKCE, no client secret / no password credential).
      * Delegated API scope (default: access_as_user) exposed as api://{clientId}.
      * App role (default: RetailPulse.User) required for API/hub authorization.
      * Service principal with appRoleAssignmentRequired = true (assignment required).
      * SPA + Web redirect URIs derived from -FrontendOrigin (and -RedirectUri).
      * An app-role assignment for the operator (or -AssignUserUpn) so they can sign in.

    The script is IDEMPOTENT: re-running reconciles the existing app in place and
    never creates duplicates, extra scopes, extra roles, or secrets.

    It talks to Microsoft Graph exclusively via `az rest` using the CALLER's
    delegated Azure CLI token — it never writes secrets, never prints tokens, and
    never reads .env files. Output is limited to SAFE, PUBLIC identifiers
    (tenant ID, client/app ID, audience, scope name, app-role value) plus the
    `azd env set` commands the operator runs next.

    SAFETY: preview-by-default. No write call is made unless you pass -Apply.
    Without -Apply the script prints exactly what it WOULD create or change.

.PARAMETER TenantId
    The Entra tenant (directory) GUID to provision in. REQUIRED. The script fails
    fast if the signed-in `az` context is a different tenant.

.PARAMETER DisplayName
    Display name of the app registration. Default: 'Retail Pulse'. Used to locate
    an existing registration for idempotent reconciliation.

.PARAMETER FrontendOrigin
    Origin(s) of the deployed SPA (e.g. https://white-sea-123.azurestaticapps.net).
    Redirect URIs are the bare origin. May be passed multiple times.

.PARAMETER RedirectUri
    Extra explicit redirect URIs to register (e.g. http://localhost:5173 for local
    dev). Combined with the -FrontendOrigin values.

.PARAMETER ApiScopeName
    Delegated scope name exposed by the API. Default: access_as_user.

.PARAMETER AppRoleValue
    App role value required for protected API/hub access. Default: RetailPulse.User.

.PARAMETER AssignUserUpn
    UPN/email of the user to grant the app role. Default: the signed-in `az` user.

.PARAMETER Apply
    Perform the writes. Omit for a read-only preview (the default).

.EXAMPLE
    ./scripts/Setup-EntraAuth.ps1 -TenantId <guid> -FrontendOrigin https://app.example.net
    # Preview only — shows the plan, changes nothing.

.EXAMPLE
    ./scripts/Setup-EntraAuth.ps1 -TenantId <guid> -FrontendOrigin https://app.example.net -RedirectUri http://localhost:5173 -Apply
    # Provisions / reconciles the registration and grants the caller the app role.

.NOTES
    Requires: Azure CLI (az) logged in as a user who can create app registrations
    and app-role assignments in the target tenant. No secrets are created.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TenantId,

    [string]$DisplayName = 'Retail Pulse',

    [string[]]$FrontendOrigin = @(),

    [string[]]$RedirectUri = @(),

    [string]$ApiScopeName = 'access_as_user',

    [string]$AppRoleValue = 'RetailPulse.User',

    [string]$AssignUserUpn,

    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$graph = 'https://graph.microsoft.com/v1.0'

function Write-Section([string]$text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Plan([string]$text) { Write-Host "  [plan] $text" -ForegroundColor Yellow }
function Write-Done([string]$text) { Write-Host "  [done] $text" -ForegroundColor Green }
function Write-Skip([string]$text) { Write-Host "  [ok]   $text" -ForegroundColor DarkGray }

# --- Graph helpers (delegated az token; never prints the token) ---------------
function Invoke-Graph {
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PATCH', 'DELETE')][string]$Method,
        [Parameter(Mandatory)][string]$Url,
        [object]$Body
    )
    $restArgs = @('rest', '--method', $Method.ToLower(), '--url', $Url,
        '--headers', 'Content-Type=application/json')
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 20 -Compress
        # Write body to a temp file to avoid shell-quoting issues on all platforms.
        $tmp = New-TemporaryFile
        try {
            Set-Content -Path $tmp -Value $json -Encoding utf8
            $restArgs += @('--body', "@$tmp")
            $out = az @restArgs 2>&1
        }
        finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
    }
    else {
        $out = az @restArgs 2>&1
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Graph $Method $Url failed: $out"
    }
    if ([string]::IsNullOrWhiteSpace($out)) { return $null }
    return ($out | ConvertFrom-Json)
}

# --- 1. Validate az context + tenant -----------------------------------------
Write-Section 'Validating Azure CLI context'
$account = az account show --output json 2>$null | ConvertFrom-Json
if (-not $account) { throw 'Not logged in. Run: az login --tenant <tenantId>' }
if ($account.tenantId -ne $TenantId) {
    throw "Signed-in tenant '$($account.tenantId)' != requested -TenantId '$TenantId'. Run: az login --tenant $TenantId"
}
Write-Done "Tenant $TenantId confirmed"

if (-not $AssignUserUpn) {
    $AssignUserUpn = $account.user.name
}
Write-Skip "App-role assignee: $AssignUserUpn"
if (-not $Apply) {
    Write-Host "`nPREVIEW MODE — no changes will be made. Re-run with -Apply to provision." -ForegroundColor Magenta
}

# Assemble redirect URIs (trim trailing slashes; de-dupe).
$redirects = @()
foreach ($u in @($FrontendOrigin + $RedirectUri)) {
    if (-not [string]::IsNullOrWhiteSpace($u)) { $redirects += $u.TrimEnd('/') }
}
$redirects = $redirects | Select-Object -Unique
if ($redirects.Count -eq 0) {
    Write-Warning 'No -FrontendOrigin / -RedirectUri supplied. SPA redirect URIs will be left unset.'
}

# --- 2. Find or create the application ----------------------------------------
Write-Section "Application registration '$DisplayName'"
$escaped = $DisplayName.Replace("'", "''")
$existing = Invoke-Graph -Method GET -Url "$graph/applications?`$filter=displayName eq '$escaped'&`$select=id,appId,displayName,signInAudience,identifierUris"
$app = $null
if ($existing.value.Count -gt 0) { $app = $existing.value[0] }

if (-not $app) {
    Write-Plan "Create single-tenant application '$DisplayName' (signInAudience=AzureADMyOrg, SPA public client, no secret)"
    if ($Apply) {
        $spaRedirects = @($redirects)
        $body = @{
            displayName    = $DisplayName
            signInAudience = 'AzureADMyOrg'
            spa            = @{ redirectUris = $spaRedirects }
            web            = @{ redirectUris = $spaRedirects }
        }
        $app = Invoke-Graph -Method POST -Url "$graph/applications" -Body $body
        Write-Done "Created application appId=$($app.appId) objectId=$($app.id)"
    }
}
else {
    Write-Skip "Application exists: appId=$($app.appId) objectId=$($app.id)"
    if ($app.signInAudience -ne 'AzureADMyOrg') {
        Write-Plan "Set signInAudience to AzureADMyOrg (single tenant) — currently '$($app.signInAudience)'"
        if ($Apply) {
            Invoke-Graph -Method PATCH -Url "$graph/applications/$($app.id)" -Body @{ signInAudience = 'AzureADMyOrg' } | Out-Null
            Write-Done 'signInAudience set to AzureADMyOrg'
        }
    }
}

# In preview with no pre-existing app we synthesize a placeholder so the plan reads clearly.
$appObjectId = if ($app) { $app.id } else { '<new-app-object-id>' }
$clientId = if ($app -and $app.appId) { $app.appId } else { '<new-client-id>' }
$audience = "api://$clientId"

# --- 3. identifierUris (Application ID URI = api://{clientId}) -----------------
Write-Section 'Application ID URI (audience)'
$hasAudience = $app -and $app.identifierUris -and ($app.identifierUris -contains $audience)
if ($hasAudience) {
    Write-Skip "identifierUris already contains $audience"
}
else {
    Write-Plan "Set identifierUris = [ $audience ]"
    if ($Apply -and $app) {
        Invoke-Graph -Method PATCH -Url "$graph/applications/$($app.id)" -Body @{ identifierUris = @($audience) } | Out-Null
        Write-Done "identifierUris set to $audience"
    }
}

# --- 4. Delegated API scope (oauth2PermissionScopes) --------------------------
Write-Section "Delegated API scope '$ApiScopeName'"
$appApi = if ($app) { Invoke-Graph -Method GET -Url "$graph/applications/$($app.id)?`$select=api" } else { $null }
$scopes = @()
if ($appApi -and $appApi.api -and $appApi.api.oauth2PermissionScopes) {
    $scopes = @($appApi.api.oauth2PermissionScopes)
}
$scope = $scopes | Where-Object { $_.value -eq $ApiScopeName } | Select-Object -First 1
if ($scope) {
    Write-Skip "Scope '$ApiScopeName' exists (id=$($scope.id))"
}
else {
    $scopeId = [guid]::NewGuid().ToString()
    Write-Plan "Add delegated scope '$ApiScopeName' (id=$scopeId, adminConsent, enabled)"
    if ($Apply -and $app) {
        $newScope = @{
            id                      = $scopeId
            value                   = $ApiScopeName
            type                    = 'User'
            isEnabled               = $true
            adminConsentDisplayName = "Access Retail Pulse as the signed-in user"
            adminConsentDescription = "Allow the app to access the Retail Pulse API on behalf of the signed-in user."
            userConsentDisplayName  = "Access Retail Pulse"
            userConsentDescription  = "Allow the app to access the Retail Pulse API on your behalf."
        }
        $api = if ($appApi.api) { $appApi.api } else { @{} }
        $api = $api | Select-Object * -ExcludeProperty oauth2PermissionScopes
        $mergedScopes = @($scopes + $newScope)
        Invoke-Graph -Method PATCH -Url "$graph/applications/$($app.id)" -Body @{ api = @{ oauth2PermissionScopes = $mergedScopes } } | Out-Null
        Write-Done "Scope '$ApiScopeName' added"
    }
}

# --- 5. App role (RetailPulse.User) -------------------------------------------
Write-Section "App role '$AppRoleValue'"
$appRoles = if ($app) {
    $r = Invoke-Graph -Method GET -Url "$graph/applications/$($app.id)?`$select=appRoles"
    @($r.appRoles)
}
else { @() }
$role = $appRoles | Where-Object { $_.value -eq $AppRoleValue } | Select-Object -First 1
if ($role) {
    Write-Skip "App role '$AppRoleValue' exists (id=$($role.id))"
}
else {
    $roleId = [guid]::NewGuid().ToString()
    Write-Plan "Add app role '$AppRoleValue' (id=$roleId, allowedMemberTypes=User, enabled)"
    if ($Apply -and $app) {
        $newRole = @{
            id                 = $roleId
            value              = $AppRoleValue
            displayName        = 'Retail Pulse User'
            description        = 'Users who may access the Retail Pulse application.'
            allowedMemberTypes = @('User')
            isEnabled          = $true
        }
        $merged = @($appRoles + $newRole)
        Invoke-Graph -Method PATCH -Url "$graph/applications/$($app.id)" -Body @{ appRoles = $merged } | Out-Null
        Write-Done "App role '$AppRoleValue' added"
    }
}

# --- 6. Redirect URIs (reconcile SPA + Web) -----------------------------------
Write-Section 'Redirect URIs'
if ($redirects.Count -gt 0 -and $app) {
    $current = Invoke-Graph -Method GET -Url "$graph/applications/$($app.id)?`$select=spa,web"
    $curSpa = @()
    if ($current.spa -and $current.spa.redirectUris) { $curSpa = @($current.spa.redirectUris) }
    $missing = $redirects | Where-Object { $curSpa -notcontains $_ }
    if ($missing.Count -eq 0) {
        Write-Skip "SPA redirect URIs already present: $($redirects -join ', ')"
    }
    else {
        Write-Plan "Set SPA + Web redirect URIs: $($redirects -join ', ')"
        if ($Apply) {
            Invoke-Graph -Method PATCH -Url "$graph/applications/$($app.id)" -Body @{
                spa = @{ redirectUris = @($redirects) }
                web = @{ redirectUris = @($redirects) }
            } | Out-Null
            Write-Done 'Redirect URIs reconciled'
        }
    }
}
else {
    Write-Skip 'No redirect URIs to reconcile'
}

# --- 7. Service principal + assignmentRequired --------------------------------
Write-Section 'Service principal (assignment required)'
$sp = $null
if ($clientId -and $clientId -notlike '<*') {
    $spResp = Invoke-Graph -Method GET -Url "$graph/servicePrincipals?`$filter=appId eq '$clientId'&`$select=id,appId,appRoleAssignmentRequired"
    if ($spResp.value.Count -gt 0) { $sp = $spResp.value[0] }
}
if (-not $sp) {
    Write-Plan "Create service principal for appId=$clientId with appRoleAssignmentRequired=true"
    if ($Apply -and $app) {
        $sp = Invoke-Graph -Method POST -Url "$graph/servicePrincipals" -Body @{ appId = $clientId }
        Invoke-Graph -Method PATCH -Url "$graph/servicePrincipals/$($sp.id)" -Body @{ appRoleAssignmentRequired = $true } | Out-Null
        Write-Done "Service principal created (id=$($sp.id)), assignment required = true"
    }
}
else {
    Write-Skip "Service principal exists (id=$($sp.id))"
    if (-not $sp.appRoleAssignmentRequired) {
        Write-Plan 'Set appRoleAssignmentRequired = true'
        if ($Apply) {
            Invoke-Graph -Method PATCH -Url "$graph/servicePrincipals/$($sp.id)" -Body @{ appRoleAssignmentRequired = $true } | Out-Null
            Write-Done 'appRoleAssignmentRequired set to true'
        }
    }
    else {
        Write-Skip 'appRoleAssignmentRequired already true'
    }
}

# --- 8. Assign the operator the app role --------------------------------------
Write-Section "App-role assignment for $AssignUserUpn"
if ($sp -and $Apply) {
    $user = Invoke-Graph -Method GET -Url "$graph/users/$AssignUserUpn`?`$select=id,userPrincipalName"
    # Resolve the role id from the SP's published appRoles (post-apply it exists).
    $spRoles = Invoke-Graph -Method GET -Url "$graph/servicePrincipals/$($sp.id)?`$select=appRoles"
    $targetRole = @($spRoles.appRoles) | Where-Object { $_.value -eq $AppRoleValue } | Select-Object -First 1
    if (-not $targetRole) { throw "App role '$AppRoleValue' not found on service principal yet." }
    $existingAssignments = Invoke-Graph -Method GET -Url "$graph/servicePrincipals/$($sp.id)/appRoleAssignedTo?`$filter=principalId eq $($user.id)"
    $already = @($existingAssignments.value) | Where-Object { $_.appRoleId -eq $targetRole.id }
    if ($already) {
        Write-Skip "$AssignUserUpn already assigned to '$AppRoleValue'"
    }
    else {
        Invoke-Graph -Method POST -Url "$graph/servicePrincipals/$($sp.id)/appRoleAssignedTo" -Body @{
            principalId = $user.id
            resourceId  = $sp.id
            appRoleId   = $targetRole.id
        } | Out-Null
        Write-Done "Assigned $AssignUserUpn to '$AppRoleValue'"
    }
}
else {
    Write-Plan "Assign $AssignUserUpn to app role '$AppRoleValue' on the service principal"
}

# --- 9. Emit SAFE config (no secrets) -----------------------------------------
Write-Section 'Safe configuration output (non-secret)'
$finalScope = "$audience/$ApiScopeName"
[PSCustomObject]@{
    TenantId  = $TenantId
    ClientId  = $clientId
    Audience  = $audience
    ApiScope  = $ApiScopeName
    ApiScopeUri = $finalScope
    AppRole   = $AppRoleValue
} | Format-List | Out-String | Write-Host

Write-Host 'Next — wire these into azd (public identifiers only, safe to commit to your azd env):' -ForegroundColor Cyan
Write-Host "  azd env set RETAIL_PULSE_ENTRA_TENANT_ID $TenantId"
Write-Host "  azd env set RETAIL_PULSE_ENTRA_CLIENT_ID $clientId"
Write-Host "  azd env set RETAIL_PULSE_ENTRA_API_SCOPE $ApiScopeName"
Write-Host "  azd env set RETAIL_PULSE_ENTRA_AUDIENCE $audience"
Write-Host ''
Write-Host "Then verify with: ./scripts/Verify-EntraAuth.ps1 -TenantId $TenantId -ClientId $clientId" -ForegroundColor Cyan
if (-not $Apply) {
    Write-Host "`nPreview complete. Re-run with -Apply to make these changes." -ForegroundColor Magenta
}
