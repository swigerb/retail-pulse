#Requires -Version 7.0
<#
.SYNOPSIS
    Read-only verification of the LIVE Retail Pulse production authentication posture.

.DESCRIPTION
    Proves — WITHOUT making any change and WITHOUT ever obtaining, printing, or logging a
    token or secret — that the deployed environment enforces the ONLY supported live security
    posture: Microsoft Entra, single-tenant, fail-closed. It is the Sprint 4 production gate
    for epic #27 (provider-neutral authentication): Entra is the only enabled live mode; GitHub
    and Anonymous must be invisible in production.

    Every check is read-only. The script uses the caller's already-authenticated Azure CLI
    context (`az`, delegated) and anonymous HTTP probes. It never calls `az login`, never reads
    a token, never reads any `.env*` file, and never mutates a resource. It exits NON-ZERO on
    the first-or-any mismatch so it can gate a security review, a release, or a CI step.

    Verified posture (each maps to a row in docs/authentication-matrix.md):

      Context
        * The signed-in az context targets the expected tenant and (optional) subscription.
        * The target resource group exists.

      API container app (the in-process JWT security boundary)
        * The latest revision is Provisioned and Running (healthy).
        * ASPNETCORE_ENVIRONMENT = Production
        * Authentication__Mode   = Entra
        * Security__RequireAuth   = true
        * MicrosoftEntra__TenantId / __ClientId are present, non-empty, and match the expected
          identifiers (compared case-insensitively; only redacted forms are printed).
        * RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE = true (the acknowledged ephemeral-storage pin).
        * NO Anonymous__* or GitHub__* environment variable is present on the app.
        * ACA platform auth (Easy Auth) is DISABLED — the in-process JwtBearer handler is the
          sole gate.

      Live anonymous HTTP probes against the API origin (no credential)
        * POST /api/chat                       -> 401
        * POST /hubs/telemetry/negotiate       -> 401
        * POST /hubs/streaming/negotiate       -> 401
        * GET  /api/chat?access_token=<synth>  -> 401 (query token honored only on /hubs/*)
        * GET  /health                         -> 200
        * GET  /alive                          -> 200

      Static Web App (the SPA)
        * The root serves an Entra (Microsoft sign-in) build.
        * The compiled, PUBLIC bundle does NOT expose the GitHub or Anonymous sign-in mode
          (inspected via public markers only — never any secret).

      Entra app registration (delegates to Verify-EntraAuth.ps1 when present)
        * Single tenant (signInAudience = AzureADMyOrg).
        * No password credential (PKCE public client).
        * Delegated scope + app role present and enabled.
        * Service principal exists with appRoleAssignmentRequired = true.

    Output is redacted: GUIDs are masked to `****last4`; no user assignments, tokens, or secrets
    are printed.

.PARAMETER TenantId
    Expected Entra tenant (directory) GUID. REQUIRED. The signed-in az context must match it.

.PARAMETER ClientId
    Expected Entra application (client) ID. REQUIRED. Compared against the API app's
    MicrosoftEntra__ClientId and used for the Entra app-registration verification.

.PARAMETER ResourceGroup
    The target resource group. REQUIRED. The API container app and Static Web App are discovered
    inside it by their azd service tags unless -ApiAppName / -StaticWebAppName are supplied.

.PARAMETER SubscriptionId
    Optional expected subscription GUID. When supplied the signed-in context must match it.

.PARAMETER ApiAppName
    Optional explicit API container app name. Default: discovered by tag azd-service-name=api.

.PARAMETER StaticWebAppName
    Optional explicit Static Web App name. Default: discovered by tag azd-service-name=frontend.

.PARAMETER ApiOrigin
    Optional explicit API base URL (https://...) for the anonymous HTTP probes. Default: the API
    container app's ingress FQDN.

.PARAMETER ApiScopeName
    Expected delegated scope. Default: access_as_user.

.PARAMETER AppRoleValue
    Expected app role. Default: RetailPulse.User.

.PARAMETER SkipEntraAppRegistration
    Skip the Entra app-registration checks (e.g. when the caller lacks directory read). The rest
    of the posture is still verified.

.PARAMETER SkipHttpProbes
    Skip the live anonymous HTTP probes (e.g. air-gapped review of configuration only).

.EXAMPLE
    ./scripts/Verify-ProductionAuth.ps1 -TenantId <guid> -ClientId <guid> -ResourceGroup rg-prod

.EXAMPLE
    # Preview exactly which checks would run, contacting nothing:
    ./scripts/Verify-ProductionAuth.ps1 -TenantId <guid> -ClientId <guid> -ResourceGroup rg-prod -WhatIf

.NOTES
    Requires an authenticated Azure CLI session (`az login`) with reader access to the resource
    group; the Entra app-registration checks additionally need directory read. This script does
    NOT sign you in and does NOT deploy — the parent performs the live deployment separately.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$TenantId,
    [Parameter(Mandatory = $true)][string]$ClientId,
    [Parameter(Mandatory = $true)][string]$ResourceGroup,
    [string]$SubscriptionId,
    [string]$ApiAppName,
    [string]$StaticWebAppName,
    [string]$ApiOrigin,
    [string]$ApiScopeName = 'access_as_user',
    [string]$AppRoleValue = 'RetailPulse.User',
    [switch]$SkipEntraAppRegistration,
    [switch]$SkipHttpProbes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:failures = 0
$script:checksPlanned = [System.Collections.Generic.List[string]]::new()

# ── redaction / reporting ──────────────────────────────────────────────────
function Format-Redacted([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return '(empty)' }
    $v = $value.Trim()
    if ($v.Length -le 4) { return '****' }
    return ('****' + $v.Substring($v.Length - 4))
}

function Test-Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) {
        Write-Host "  [PASS] $name" -ForegroundColor Green
    }
    else {
        Write-Host "  [FAIL] $name $detail" -ForegroundColor Red
        $script:failures++
    }
}

function Add-PlannedCheck([string]$name) { $script:checksPlanned.Add($name) | Out-Null }

# ── read-only az / http helpers ────────────────────────────────────────────
function Invoke-AzJson([string[]]$Arguments, [string]$FailureMessage) {
    $out = & az @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$FailureMessage (az exited $LASTEXITCODE): $out" }
    $text = ($out | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return ($text | ConvertFrom-Json)
}

# Anonymous HTTP probe: returns the numeric status code, or -1 on transport failure.
# Never sends a credential; never prints a response body.
function Get-HttpStatus([string]$Method, [string]$Url) {
    try {
        $resp = Invoke-WebRequest -Method $Method -Uri $Url -SkipHttpErrorCheck `
            -MaximumRedirection 0 -TimeoutSec 30 -ErrorAction Stop
        return [int]$resp.StatusCode
    }
    catch {
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            return [int]$_.Exception.Response.StatusCode
        }
        return -1
    }
}

function Get-EnvValue($template, [string]$name) {
    if (-not $template -or -not $template.containers) { return $null }
    foreach ($c in $template.containers) {
        if ($c.env) {
            $match = @($c.env) | Where-Object { $_.name -eq $name } | Select-Object -First 1
            if ($match) { return $match.value }
        }
    }
    return $null
}

function Test-AnyEnvWithPrefix($template, [string]$prefix) {
    if (-not $template -or -not $template.containers) { return $false }
    foreach ($c in $template.containers) {
        if ($c.env) {
            if (@($c.env) | Where-Object { $_.name -like "$prefix*" }) { return $true }
        }
    }
    return $false
}

# ── plan the checks (used by -WhatIf and as the running order) ──────────────
Add-PlannedCheck 'Signed-in az context targets the expected tenant (and subscription, if given)'
Add-PlannedCheck "Resource group '$ResourceGroup' exists"
Add-PlannedCheck 'API container app discovered / resolved'
Add-PlannedCheck 'API latest revision Provisioned + Running (healthy)'
Add-PlannedCheck 'API env ASPNETCORE_ENVIRONMENT = Production'
Add-PlannedCheck 'API env Authentication__Mode = Entra'
Add-PlannedCheck 'API env Security__RequireAuth = true'
Add-PlannedCheck 'API env MicrosoftEntra__TenantId present + matches expected'
Add-PlannedCheck 'API env MicrosoftEntra__ClientId present + matches expected'
Add-PlannedCheck 'API env RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE = true'
Add-PlannedCheck 'API has NO Anonymous__* environment variable'
Add-PlannedCheck 'API has NO GitHub__* environment variable'
Add-PlannedCheck 'ACA platform auth (Easy Auth) disabled'
if (-not $SkipHttpProbes) {
    Add-PlannedCheck 'Anonymous POST /api/chat -> 401'
    Add-PlannedCheck 'Anonymous POST /hubs/telemetry/negotiate -> 401'
    Add-PlannedCheck 'Anonymous POST /hubs/streaming/negotiate -> 401'
    Add-PlannedCheck 'Anonymous GET /api/chat?access_token=<synthetic> -> 401 (query token hub-only)'
    Add-PlannedCheck 'GET /health -> 200'
    Add-PlannedCheck 'GET /alive -> 200'
    Add-PlannedCheck 'SWA root serves an Entra build and hides GitHub/Anonymous mode'
}
if (-not $SkipEntraAppRegistration) {
    Add-PlannedCheck 'Entra app registration posture (single-tenant, no secret, scope+role, SP assignmentRequired)'
}

# ── -WhatIf: describe only, contact nothing, do not mutate ──────────────────
if ($WhatIfPreference) {
    Write-Host '=== Verify-ProductionAuth.ps1 (-WhatIf) ===' -ForegroundColor Cyan
    Write-Host 'This is a READ-ONLY verification. It would perform the following checks and mutate nothing:'
    Write-Host ''
    $i = 1
    foreach ($c in $script:checksPlanned) {
        Write-Host ("  {0,2}. {1}" -f $i, $c)
        $i++
    }
    Write-Host ''
    Write-Host ("Target: RG={0}  Tenant={1}  Client={2}" -f `
            $ResourceGroup, (Format-Redacted $TenantId), (Format-Redacted $ClientId))
    Write-Host 'No Azure calls, HTTP requests, or writes were made.' -ForegroundColor Yellow
    exit 0
}

Write-Host '=== Verifying LIVE production authentication posture (read-only) ===' -ForegroundColor Cyan
Write-Host ("Target: RG={0}  Tenant={1}  Client={2}" -f `
        $ResourceGroup, (Format-Redacted $TenantId), (Format-Redacted $ClientId))

# 1) Context ─────────────────────────────────────────────────────────────────
Write-Host "`n[Context]" -ForegroundColor Cyan
$account = Invoke-AzJson @('account', 'show', '--output', 'json') 'Not logged in. Run: az login --tenant <tenantId>'
Test-Check 'Signed-in tenant matches -TenantId' ($account.tenantId -eq $TenantId) "(got $(Format-Redacted $account.tenantId))"
if ($SubscriptionId) {
    Test-Check 'Signed-in subscription matches -SubscriptionId' ($account.id -eq $SubscriptionId) "(got $(Format-Redacted $account.id))"
}

$rg = Invoke-AzJson @('group', 'show', '--name', $ResourceGroup, '--output', 'json') "Resource group '$ResourceGroup' not found"
Test-Check "Resource group '$ResourceGroup' exists" ($null -ne $rg)

# 2) Resolve the API container app ────────────────────────────────────────────
Write-Host "`n[API container app]" -ForegroundColor Cyan
if (-not $ApiAppName) {
    $apps = Invoke-AzJson @('containerapp', 'list', '--resource-group', $ResourceGroup,
        '--query', '[?tags."azd-service-name"==''api''].name', '--output', 'json') 'Failed to list container apps'
    $ApiAppName = if ($apps -and @($apps).Count -gt 0) { @($apps)[0] } else { $null }
}
Test-Check 'API container app resolved' ([bool]$ApiAppName) '(pass -ApiAppName if discovery by tag azd-service-name=api fails)'
if (-not $ApiAppName) {
    Write-Host "`nVerification FAILED: could not resolve the API container app." -ForegroundColor Red
    exit 1
}
Write-Host "  API app: $ApiAppName"

$api = Invoke-AzJson @('containerapp', 'show', '--name', $ApiAppName, '--resource-group', $ResourceGroup, '--output', 'json') `
    "Failed to read container app '$ApiAppName'"

$latestRevisionName = $api.properties.latestRevisionName
$revision = Invoke-AzJson @('containerapp', 'revision', 'show', '--name', $ApiAppName, '--resource-group', $ResourceGroup,
    '--revision', $latestRevisionName, '--output', 'json') "Failed to read revision '$latestRevisionName'"
$provisioned = ($revision.properties.provisioningState -eq 'Provisioned')
$running = ($revision.properties.runningState -in @('Running', 'RunningAtMaxScale'))
Test-Check 'API latest revision Provisioned + Running' ($provisioned -and $running) `
    "(provisioning=$($revision.properties.provisioningState) running=$($revision.properties.runningState))"

$template = $api.properties.template

$env = Get-EnvValue $template 'ASPNETCORE_ENVIRONMENT'
Test-Check 'ASPNETCORE_ENVIRONMENT = Production' ($env -eq 'Production') "(got '$env')"

$mode = Get-EnvValue $template 'Authentication__Mode'
Test-Check 'Authentication__Mode = Entra' ($mode -eq 'Entra') "(got '$mode')"

$requireAuth = Get-EnvValue $template 'Security__RequireAuth'
Test-Check 'Security__RequireAuth = true' ($requireAuth -eq 'true') "(got '$requireAuth')"

$appTenant = Get-EnvValue $template 'MicrosoftEntra__TenantId'
Test-Check 'MicrosoftEntra__TenantId present + matches expected' `
    ((-not [string]::IsNullOrWhiteSpace($appTenant)) -and ($appTenant -ieq $TenantId)) "(got $(Format-Redacted $appTenant))"

$appClient = Get-EnvValue $template 'MicrosoftEntra__ClientId'
Test-Check 'MicrosoftEntra__ClientId present + matches expected' `
    ((-not [string]::IsNullOrWhiteSpace($appClient)) -and ($appClient -ieq $ClientId)) "(got $(Format-Redacted $appClient))"

$ephemeral = Get-EnvValue $template 'RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE'
Test-Check 'RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE = true (acknowledged)' ($ephemeral -eq 'true') "(got '$ephemeral')"

Test-Check 'No Anonymous__* environment variable present' (-not (Test-AnyEnvWithPrefix $template 'Anonymous__'))
Test-Check 'No GitHub__* environment variable present' (-not (Test-AnyEnvWithPrefix $template 'GitHub__'))

# 3) Easy Auth disabled ───────────────────────────────────────────────────────
Write-Host "`n[ACA platform auth]" -ForegroundColor Cyan
$authEnabled = $null
try {
    $authCfg = Invoke-AzJson @('containerapp', 'auth', 'show', '--name', $ApiAppName, '--resource-group', $ResourceGroup, '--output', 'json') `
        'Failed to read container app auth config'
    if ($authCfg -and $authCfg.PSObject.Properties.Name -contains 'platform' -and $authCfg.platform) {
        $authEnabled = [bool]$authCfg.platform.enabled
    }
    else {
        $authEnabled = $false
    }
}
catch {
    # A missing auth config resource means Easy Auth was never enabled — which is the desired state.
    $authEnabled = $false
}
Test-Check 'ACA platform auth (Easy Auth) disabled — in-process JWT boundary' (-not $authEnabled)

# 4) Live anonymous HTTP probes ───────────────────────────────────────────────
if (-not $SkipHttpProbes) {
    Write-Host "`n[Anonymous HTTP probes]" -ForegroundColor Cyan
    if (-not $ApiOrigin) {
        $fqdn = $api.properties.configuration.ingress.fqdn
        if ($fqdn) { $ApiOrigin = "https://$fqdn" }
    }
    if (-not $ApiOrigin) {
        Test-Check 'API origin resolved for probes' $false '(pass -ApiOrigin or -SkipHttpProbes)'
    }
    else {
        $ApiOrigin = $ApiOrigin.TrimEnd('/')
        Write-Host "  Origin: $ApiOrigin"

        Test-Check 'Anonymous POST /api/chat -> 401' ((Get-HttpStatus 'POST' "$ApiOrigin/api/chat") -eq 401)
        Test-Check 'Anonymous POST /hubs/telemetry/negotiate -> 401' ((Get-HttpStatus 'POST' "$ApiOrigin/hubs/telemetry/negotiate") -eq 401)
        Test-Check 'Anonymous POST /hubs/streaming/negotiate -> 401' ((Get-HttpStatus 'POST' "$ApiOrigin/hubs/streaming/negotiate") -eq 401)
        # A REST call with a query-string token must be rejected: ?access_token is hub-only.
        Test-Check 'REST GET /api/chat?access_token=<synthetic> -> 401 (query token hub-only)' `
        ((Get-HttpStatus 'GET' "$ApiOrigin/api/chat?access_token=not-a-real-token") -eq 401)
        Test-Check 'GET /health -> 200' ((Get-HttpStatus 'GET' "$ApiOrigin/health") -eq 200)
        Test-Check 'GET /alive -> 200' ((Get-HttpStatus 'GET' "$ApiOrigin/alive") -eq 200)
    }

    # 5) Static Web App: Entra build only, GitHub/Anonymous mode not exposed ───
    Write-Host "`n[Static Web App]" -ForegroundColor Cyan
    if (-not $StaticWebAppName) {
        $swas = Invoke-AzJson @('staticwebapp', 'list', '--resource-group', $ResourceGroup,
            '--query', '[?tags."azd-service-name"==''frontend''].name', '--output', 'json') 'Failed to list static web apps'
        $StaticWebAppName = if ($swas -and @($swas).Count -gt 0) { @($swas)[0] } else { $null }
    }
    if (-not $StaticWebAppName) {
        Test-Check 'Static Web App resolved' $false '(pass -StaticWebAppName if discovery fails)'
    }
    else {
        Write-Host "  SWA: $StaticWebAppName"
        $swa = Invoke-AzJson @('staticwebapp', 'show', '--name', $StaticWebAppName, '--resource-group', $ResourceGroup, '--output', 'json') `
            "Failed to read Static Web App '$StaticWebAppName'"
        $swaHost = $swa.defaultHostname
        Test-Check 'SWA default hostname resolved' ([bool]$swaHost)
        if ($swaHost) {
            # Public, secret-free inspection of the served SPA. The Entra build embeds the
            # Microsoft identity authority + the Microsoft sign-in gate; the GitHub/Anonymous
            # builds embed their own gate routes/labels. We assert the Entra markers ARE present
            # and the GitHub/Anonymous markers are ABSENT. Nothing sensitive is printed.
            $spa = ''
            try {
                $spa = (Invoke-WebRequest -Uri "https://$swaHost/" -TimeoutSec 30 -SkipHttpErrorCheck).Content
                # The SPA is a shell that lazily loads JS; also pull the referenced bundles.
                foreach ($m in [regex]::Matches($spa, 'src="(/assets/[^"]+\.js)"')) {
                    try { $spa += (Invoke-WebRequest -Uri "https://$swaHost$($m.Groups[1].Value)" -TimeoutSec 30 -SkipHttpErrorCheck).Content } catch { }
                }
            }
            catch {
                Test-Check 'SWA root reachable' $false "($($_.Exception.Message))"
            }
            if ($spa) {
                $hasEntra = ($spa -match 'login\.microsoftonline\.com') -or ($spa -match 'Sign in with Microsoft')
                $exposesGitHub = ($spa -match '/api/auth/github/start') -or ($spa -match 'Continue with GitHub')
                $exposesAnon = ($spa -match '/api/auth/anonymous/session') -or ($spa -match 'Continue in limited demo')
                Test-Check 'SWA serves an Entra (Microsoft sign-in) build' $hasEntra
                Test-Check 'SWA does NOT expose GitHub sign-in mode' (-not $exposesGitHub)
                Test-Check 'SWA does NOT expose Anonymous sign-in mode' (-not $exposesAnon)
            }
        }
    }
}

# 6) Entra app registration posture ──────────────────────────────────────────
if (-not $SkipEntraAppRegistration) {
    Write-Host "`n[Entra app registration]" -ForegroundColor Cyan
    $verifyEntra = Join-Path $PSScriptRoot 'Verify-EntraAuth.ps1'
    if (Test-Path $verifyEntra) {
        # Reuse the dedicated, read-only app-registration verifier. It prints only safe
        # identifiers and returns non-zero on any mismatch; fold its result into ours.
        & $verifyEntra -TenantId $TenantId -ClientId $ClientId -ApiScopeName $ApiScopeName -AppRoleValue $AppRoleValue
        Test-Check 'Entra app registration verification (Verify-EntraAuth.ps1)' ($LASTEXITCODE -eq 0)
    }
    else {
        Test-Check 'Verify-EntraAuth.ps1 present for app-registration checks' $false '(script missing; run with -SkipEntraAppRegistration to bypass)'
    }
}

# ── verdict ──────────────────────────────────────────────────────────────────
Write-Host ''
if ($script:failures -eq 0) {
    Write-Host 'PRODUCTION AUTH POSTURE VERIFIED — Entra-only, fail-closed, GitHub/Anonymous not exposed.' -ForegroundColor Green
    [PSCustomObject]@{
        ResourceGroup = $ResourceGroup
        ApiApp        = $ApiAppName
        Tenant        = (Format-Redacted $TenantId)
        Client        = (Format-Redacted $ClientId)
        Mode          = 'Entra'
    } | Format-List | Out-String | Write-Host
    exit 0
}
else {
    Write-Host "$($script:failures) check(s) FAILED — production auth posture is NOT the expected Entra-only, fail-closed state." -ForegroundColor Red
    exit 1
}
