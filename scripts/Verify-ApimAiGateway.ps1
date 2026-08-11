#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Live post-provision verification of the APIM AI Gateway invariants.

.DESCRIPTION
    Reads the currently-selected azd environment outputs (or explicit params) to
    resolve the deployed resource group, APIM instance, and API container app,
    then verifies — using ONLY `az` inspection commands — every invariant that
    the Bicep contract tests assert statically:

        * APIM instance exists, uses SystemAssigned identity.
        * Inference API exists, has subscriptionRequired=true and the
          expected /openai suffix on the registered path.
        * API policy references the AOAI backend and enforces token-limit +
          emit-token-metric.
        * Backend authenticates to AOAI via managed identity to
          cognitiveservices.azure.com.
        * API-level applicationinsights diagnostic has metrics enabled and
          azuremonitor diagnostic has largeLanguageModel logs enabled.
        * APIM system-assigned identity holds the Cognitive Services OpenAI
          User role on the AOAI account.
        * The API container app has:
            - `OpenAI__Endpoint` matching the APIM inference URL and NOT
              ending in `/openai` (regression #55).
            - `OpenAI__ApimSubscriptionKey` referencing the `apim-sub-key`
              ACA secret.
            - `OpenAI__UseManagedIdentity` = 'false'.

    Every check is read-only. The script prints a compact summary and exits 0
    when all invariants hold, exits 1 with a list of failures otherwise.

    It is intentionally opportunistic: when `az` is not signed in, or the azd
    environment is unavailable, or the caller lacks Reader on the resource
    group, the script prints a clear reason and exits with code 2 (skipped) so
    it does not falsely fail a pipeline that lacks live-Azure access.

.PARAMETER ResourceGroup
    Resource group containing the RetailPulse deployment. Defaults to
    $env:AZURE_RESOURCE_GROUP (set by azd env after provision).

.PARAMETER ApimName
    APIM service name. Defaults to $env:AZURE_APIM_NAME.

.PARAMETER ApiContainerAppName
    ACA API container app name. Defaults to $env:AZURE_API_APP_NAME.

.PARAMETER AiFoundryAccountName
    Azure AI Foundry / Cognitive Services account name that APIM's MI must be
    granted OpenAI User on. Defaults to the value baked into main.bicep.

.PARAMETER AiFoundryResourceGroup
    Resource group containing the AI Foundry account. Defaults to the value
    baked into main.bicep.

.EXAMPLE
    ./scripts/Verify-ApimAiGateway.ps1

    Runs against the currently-active azd environment.

.EXAMPLE
    ./scripts/Verify-ApimAiGateway.ps1 -SelfTest

    Runs the script's offline self-test (BOM-safe JSON round-trip proof, the
    missing-resource hard-FAIL path, and a convention guard against
    reintroducing the two previously-broken ARM access patterns). Requires no
    Azure signin; wired into CI as a signin-free regression fence.
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = $env:AZURE_RESOURCE_GROUP,
    [string]$ApimName = $env:AZURE_APIM_NAME,
    [string]$ApiContainerAppName = $env:AZURE_API_APP_NAME,
    [string]$InferenceApiName = ($env:AZURE_APIM_INFERENCE_API_NAME | ForEach-Object { if ([string]::IsNullOrWhiteSpace($_)) { 'retail-pulse-inference-api' } else { $_ } }),
    [string]$AiFoundryAccountName = 'aiagents-3rsdmhyb',
    [string]$AiFoundryResourceGroup = 'rg-repodigest-agents-demo-eus-001',
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()
$checks = 0

# ── ARM REST helper ─────────────────────────────────────────────────────────
# `az apim api policy show`, `az apim backend show`, and `az apim api
# diagnostic show` DO NOT EXIST as Azure CLI commands — `az apim api` has no
# `policy`/`diagnostic` subgroup and `az apim` has no `backend` subgroup. Every
# earlier revision of this script called them anyway; az printed "'policy' is
# misspelled or not recognized" (or similar) to stderr and exited non-zero,
# which the old code swallowed with `2>$null`, leaving the variable `$null`
# and producing a false [FAIL] on 9 genuinely-passing invariants (issue #67).
# `az rest` itself is also unusable here: it mis-decodes the UTF-8 BOM that
# APIM's policy-show response carries and crashes with a UnicodeEncodeError
# on Windows PowerShell's default cp1252 console encoding. A later revision
# (PR #72) attempted to work around that same crash with
# `Invoke-WebRequest` + a manual byte/BOM stripper, but Publix's independent
# live re-verification reproduced the identical UnicodeEncodeError crash with
# that approach too. Bypass all three problems by talking to ARM directly
# with `Invoke-RestMethod` over a bearer token from
# `az account get-access-token` — `Invoke-RestMethod` deserializes the
# response body directly to JSON without ever round-tripping the raw bytes
# through the console's codepage, so it never hits the BOM/codec crash, and
# it doesn't depend on any `az apim` subcommand. Confirmed live: 24/24 PASS,
# including a live chat completion through the gateway (Publix).
$script:ArmToken = $null
$script:ArmSubscriptionId = $null

function Get-ArmAccessToken {
    if (-not $script:ArmToken) {
        $script:ArmToken = (az account get-access-token --resource https://management.azure.com --query accessToken -o tsv 2>$null | Out-String).Trim()
    }
    return $script:ArmToken
}

function Invoke-ArmGet {
    <#
    Read-only ARM GET via Invoke-RestMethod (not `az rest`, which mis-handles
    the BOM some ARM responses carry). Returns $null for a 404 (resource
    genuinely absent) or 403 (no RBAC — surfaced as a normal check failure,
    not a script-level skip); rethrows any other unexpected error so a real
    connectivity problem isn't silently treated as "resource missing".
    #>
    param([Parameter(Mandatory)][string]$Path)

    $token = Get-ArmAccessToken
    $sub = $script:ArmSubscriptionId
    $url = "https://management.azure.com/subscriptions/$sub$Path"
    try {
        return Invoke-RestMethod -Uri $url -Headers @{ Authorization = "Bearer $token" } -Method Get
    }
    catch [System.Net.WebException] {
        $status = $_.Exception.Response.StatusCode
        if ($status -in @('NotFound', 'Forbidden')) { return $null }
        throw
    }
    catch {
        # .NET (Core) HttpRequestException path used by Invoke-RestMethod on
        # PowerShell 7+: the status code is on the response object.
        $response = $_.Exception.Response
        if ($response -and $response.StatusCode -in @('NotFound', 'Forbidden')) { return $null }
        throw
    }
}

function Test-Prereq {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        Write-Host "SKIP: az CLI is not installed on PATH." -ForegroundColor Yellow
        exit 2
    }
    $account = az account show 2>$null | ConvertFrom-Json
    if (-not $account) {
        Write-Host "SKIP: az CLI is not signed in (az account show returned nothing)." -ForegroundColor Yellow
        exit 2
    }
    $script:ArmSubscriptionId = $account.id
    if ([string]::IsNullOrWhiteSpace((Get-ArmAccessToken))) {
        Write-Host "SKIP: could not acquire an ARM access token (az account get-access-token failed)." -ForegroundColor Yellow
        exit 2
    }
    foreach ($v in @('ResourceGroup', 'ApimName', 'ApiContainerAppName')) {
        if ([string]::IsNullOrWhiteSpace((Get-Variable -Name $v -Scope 1).Value)) {
            Write-Host "SKIP: required parameter/env '$v' is not set. Run this after 'azd env refresh' or pass it explicitly." -ForegroundColor Yellow
            exit 2
        }
    }
    # NOTE: from this point on, ANY invariant failure (including "resource not
    # found") is a hard [FAIL] that flows into the final exit 1 — never a
    # skip. Skips (exit 2) are reserved exclusively for the environment-level
    # preconditions above (no az, not signed in, no token, missing required
    # params), matching issue #67's requirement that a signed-in, reachable
    # az session must never be able to mask a real invariant failure as
    # exit 2.
}

function Assert([string]$name, [scriptblock]$predicate, [string]$detail = '') {
    $script:checks++
    try {
        $ok = & $predicate
        if ($ok) {
            Write-Host ("  [ok]   {0}" -f $name) -ForegroundColor Green
        }
        else {
            Write-Host ("  [FAIL] {0}: {1}" -f $name, $detail) -ForegroundColor Red
            $script:failures.Add($name)
        }
    }
    catch {
        Write-Host ("  [FAIL] {0}: {1}" -f $name, $_.Exception.Message) -ForegroundColor Red
        $script:failures.Add($name)
    }
}

# Offline self-test — exercises the BOM-safe JSON round-trip this script
# depends on and guards against ever reintroducing either of the two
# previously-broken ARM access patterns (issue #67 / #70), without needing an
# Azure signin. Run with `-SelfTest` (wired into CI as a signin-free
# regression fence).
function Invoke-SelfTest {
    function _Expect([string]$name, [bool]$cond) {
        if ($cond) { Write-Host "  [ok]   selftest: $name" -ForegroundColor Green }
        else { Write-Host "  [FAIL] selftest: $name" -ForegroundColor Red; $script:_selfFailed++ }
    }
    $script:_selfFailed = 0
    Write-Host "Self-test"

    # ── end-to-end proof that Invoke-RestMethod's own JSON deserialization
    # tolerates a leading UTF-8 BOM in the response body, the exact payload
    # shape that crashes `az rest`/console-bound tooling on Windows (#67/#70).
    # Invoke-RestMethod never round-trips the raw bytes through the console's
    # codepage the way `az rest`'s printed output does, so this never hits the
    # `charmap`/`UnicodeEncodeError` failure mode in the first place.
    $utf8 = [System.Text.Encoding]::UTF8
    $rawJson = '{"properties":{"value":"<policies><inbound><set-backend-service backend-id=\"retail-pulse-foundry\" /><authentication-managed-identity resource=\"https://cognitiveservices.azure.com\" /></inbound></policies>"}}'
    $bomBytes = @(0xEF, 0xBB, 0xBF) + $utf8.GetBytes($rawJson)
    # Invoke-RestMethod decodes the response body through a StreamReader,
    # which strips a leading UTF-8 BOM automatically as part of normal
    # decoding (this is exactly why it never hits the crash that `az
    # rest`/console-bound printing does) -- simulate that same decode step
    # here rather than a naive byte->string cast, which would leave the BOM
    # character embedded and fail JSON parsing for an unrelated reason.
    $reader = [System.IO.StreamReader]::new([System.IO.MemoryStream]::new($bomBytes), $utf8, $true)
    try { $bomString = $reader.ReadToEnd() } finally { $reader.Dispose() }
    try {
        $doc = $bomString | ConvertFrom-Json
        $policy = $doc.properties.value
        _Expect 'end-to-end: BOM-prefixed ARM payload parses via ConvertFrom-Json' ($null -ne $doc)
        _Expect 'end-to-end: extracted policy matches backend regex' ($policy -match '<set-backend-service\s+backend-id="retail-pulse-foundry"')
        _Expect 'end-to-end: extracted policy matches MI-auth regex' ($policy -match '<authentication-managed-identity\s+resource="https://cognitiveservices\.azure\.com"')
    }
    catch {
        _Expect ("end-to-end: BOM-prefixed ARM payload parses via ConvertFrom-Json (threw: {0})" -f $_.Exception.Message) $false
    }

    # ── missing-resource hard-FAIL path ────────────────────────────────
    # Simulate Invoke-ArmGet returning $null (the 404/403 path). Assert must
    # record a FAIL for a check that dereferences the missing document, not
    # silently pass — protects against the class of regression where the
    # script would "pass" against an unprovisioned resource group.
    Write-Host '  (the next two [FAIL] lines are expected — testing the FAIL path)' -ForegroundColor DarkGray
    $script:failures.Clear(); $script:checks = 0
    Assert 'selftest-missing: fake backend exists' { $null -ne $null } 'expected FAIL'
    _Expect 'Assert records FAIL when resource is $null' ($script:failures.Count -eq 1)
    $script:failures.Clear(); $script:checks = 0
    Assert 'selftest-throwing: fake regex on null' { ($null).properties -match 'foo' } 'expected FAIL'
    _Expect 'Assert records FAIL when predicate throws' ($script:failures.Count -eq 1)
    $script:failures.Clear(); $script:checks = 0

    # ── convention guard: neither of the two previously-broken ARM access
    # patterns may ever be reintroduced ──────────────────────────────────
    # (1) `az apim api policy/backend/diagnostic show` (issue #67 root cause
    #     — these subcommands do not exist and were being silently swallowed
    #     by `2>$null`).
    # (2) `az rest` for these same ARM reads (crashes on Windows with a BOM
    #     UnicodeEncodeError). `Invoke-WebRequest` piped through a manual
    #     byte/BOM stripper is a workaround for that same crash but was
    #     independently found by Publix to still reproduce the crash live —
    #     `Invoke-RestMethod` (used throughout this script) is the confirmed
    #     working replacement, so guard against regressing to either.
    $scriptPath = $PSCommandPath
    $selfSource = Get-Content -LiteralPath $scriptPath -Raw
    $codeOnly = ($selfSource -split "`n" | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
    $hasAzRest = ($codeOnly -split "`n" | Where-Object {
        $_ -match '^\s*az\s+rest\b' -or $_ -match '[&$(]\s*az\s+rest\b'
    }).Count -gt 0
    $hasBrokenApimSubcommand = ($codeOnly -match 'az\s+apim\s+(api\s+policy|api\s+diagnostic|backend)\s+show')
    _Expect 'no live az rest invocation remains in Verify-ApimAiGateway.ps1' (-not $hasAzRest)
    _Expect 'no nonexistent az apim policy/backend/diagnostic subcommand remains' (-not $hasBrokenApimSubcommand)

    if ($script:_selfFailed -gt 0) {
        Write-Host ("SELFTEST FAIL ({0} case(s))" -f $script:_selfFailed) -ForegroundColor Red
        exit 1
    }
    Write-Host "SELFTEST PASS" -ForegroundColor Green
    exit 0
}

if ($SelfTest) { Invoke-SelfTest }

Test-Prereq

Write-Host "APIM AI Gateway live verification"
Write-Host ("  resourceGroup       = {0}" -f $ResourceGroup)
Write-Host ("  apim                = {0}" -f $ApimName)
Write-Host ("  apiContainerApp     = {0}" -f $ApiContainerAppName)
Write-Host ("  inferenceApi        = {0}" -f $InferenceApiName)
Write-Host ("  aiFoundryAccount    = {0} (rg: {1})" -f $AiFoundryAccountName, $AiFoundryResourceGroup)
Write-Host ""

# ── APIM instance ─────────────────────────────────────────────────────────
Write-Host "APIM instance"
$apim = az apim show -g $ResourceGroup -n $ApimName 2>$null | ConvertFrom-Json
Assert 'apim: exists' { $null -ne $apim } "APIM '$ApimName' not found in '$ResourceGroup'"
Assert 'apim: identity.type = SystemAssigned' { $apim.identity.type -eq 'SystemAssigned' } "identity.type = $($apim.identity.type)"
$apimPrincipalId = $apim.identity.principalId
$gatewayUrl = $apim.gatewayUrl

# ── Inference API ─────────────────────────────────────────────────────────
Write-Host "Inference API"
$api = az apim api show -g $ResourceGroup --service-name $ApimName --api-id $InferenceApiName 2>$null | ConvertFrom-Json
Assert 'api: exists' { $null -ne $api } "'$InferenceApiName' not found on '$ApimName'"
Assert 'api: subscriptionRequired = true' { $api.subscriptionRequired -eq $true } "subscriptionRequired = $($api.subscriptionRequired)"
Assert 'api: path ends in /openai (SDK-compatible)' { $api.path -match '/openai$' } "path = $($api.path)"

# ── API policy (token-limit + emit-token-metric) ──────────────────────────
# NOTE (issue #67 root cause): `az apim api policy show` DOES NOT EXIST — there
# is no `policy` subgroup under `az apim api`. Every prior revision of this
# script called it anyway; az printed "'policy' is misspelled or not
# recognized" to stderr, `2>$null` swallowed that error, and $policyValue
# silently ended up $null — producing a false [FAIL] even when the live
# policy (confirmed via direct ARM GET during the #67 investigation) was
# fully correct. `az rest` is equally unusable here: it mis-decodes the
# UTF-8 BOM this ARM response carries and throws a UnicodeEncodeError under
# Windows PowerShell's cp1252 console encoding. Read the policy directly off
# ARM via Invoke-ArmGet (bearer token + Invoke-RestMethod) instead.
Write-Host "API policy"
$sub = $script:ArmSubscriptionId
$policyResource = Invoke-ArmGet "/resourceGroups/$ResourceGroup/providers/Microsoft.ApiManagement/service/$ApimName/apis/$InferenceApiName/policies/policy?api-version=2024-06-01-preview"
$policyValue = $policyResource.properties.value
Assert 'policy: sets backend service (retail-pulse-foundry)' { $policyValue -match '<set-backend-service\s+backend-id="retail-pulse-foundry"' } 'policy did not reference the AOAI backend'
Assert 'policy: managed-identity auth to cognitiveservices.azure.com' { $policyValue -match '<authentication-managed-identity\s+resource="https://cognitiveservices\.azure\.com"' } 'policy is not using MI auth to AOAI'
Assert 'policy: azure-openai-token-limit configured' { $policyValue -match '<azure-openai-token-limit\s+counter-key="@\(context\.Subscription\.Id\)"' } 'token-limit missing or wrong counter-key'
Assert 'policy: azure-openai-emit-token-metric in RetailPulse namespace' { $policyValue -match '<azure-openai-emit-token-metric\s+namespace="RetailPulse">' } 'emit-token-metric missing or wrong namespace'

# ── Backend ──────────────────────────────────────────────────────────────
# `az apim backend show` also does not exist (no `backend` subgroup under
# `az apim`) — same false-[FAIL]-via-2>$null bug as the policy check above.
Write-Host "Backend"
$backendResource = Invoke-ArmGet "/resourceGroups/$ResourceGroup/providers/Microsoft.ApiManagement/service/$ApimName/backends/retail-pulse-foundry?api-version=2024-06-01-preview"
$backend = $backendResource.properties
Assert 'backend: retail-pulse-foundry exists' { $null -ne $backend } 'backend retail-pulse-foundry not found'
Assert 'backend: url targets /openai on cognitiveservices' { $backend.url -match '/openai$' -and ($backend.url -match 'cognitiveservices\.azure\.com|\.services\.ai\.azure\.com') } "backend url = $($backend.url)"
Assert 'backend: MI credentials to cognitiveservices.azure.com' { $backend.credentials.managedIdentity.resource -eq 'https://cognitiveservices.azure.com' } 'backend is not MI-authenticated to AOAI'

# ── Diagnostics (API + instance) ──────────────────────────────────────────
# `az apim api diagnostic show` does not exist either (no `diagnostic`
# subgroup under `az apim api`) — read both diagnostics directly off ARM.
Write-Host "Diagnostics"
$apiAppInsightsResource = Invoke-ArmGet "/resourceGroups/$ResourceGroup/providers/Microsoft.ApiManagement/service/$ApimName/apis/$InferenceApiName/diagnostics/applicationinsights?api-version=2024-06-01-preview"
$apiAppInsightsDiag = $apiAppInsightsResource.properties
Assert 'api diag: applicationinsights present' { $null -ne $apiAppInsightsDiag } 'API-level applicationinsights diagnostic missing'
Assert 'api diag: metrics = true (routes emit-token-metric)' { $apiAppInsightsDiag.metrics -eq $true } "metrics = $($apiAppInsightsDiag.metrics)"

$azMonDiagResource = Invoke-ArmGet "/resourceGroups/$ResourceGroup/providers/Microsoft.ApiManagement/service/$ApimName/apis/$InferenceApiName/diagnostics/azuremonitor?api-version=2024-06-01-preview"
$azMonDiag = $azMonDiagResource.properties
Assert 'api diag: azuremonitor present' { $null -ne $azMonDiag } 'API-level azuremonitor diagnostic missing'
Assert 'api diag: largeLanguageModel logs enabled' { $azMonDiag.largeLanguageModel.logs -eq 'enabled' } 'largeLanguageModel logs not enabled — GatewayLlmLogs stay dark'

# ── RBAC: Cognitive Services OpenAI User on AI Foundry ────────────────────
Write-Host "RBAC"
if ($apimPrincipalId) {
    $roleAssignments = az role assignment list --assignee $apimPrincipalId --scope "/subscriptions/$sub/resourceGroups/$AiFoundryResourceGroup/providers/Microsoft.CognitiveServices/accounts/$AiFoundryAccountName" 2>$null | ConvertFrom-Json
    $hasOpenAiUser = ($roleAssignments | Where-Object { $_.roleDefinitionId -match '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd' })
    Assert 'rbac: APIM MI has Cognitive Services OpenAI User on AI Foundry account' { $null -ne $hasOpenAiUser } 'Role assignment missing — the MI backend policy will 403'
}
else {
    Assert 'rbac: APIM principalId available' { $false } 'Could not read APIM principalId to check role assignments'
}

# ── ACA API container app wiring ──────────────────────────────────────────
Write-Host "ACA API container app"
$apiApp = az containerapp show -g $ResourceGroup -n $ApiContainerAppName 2>$null | ConvertFrom-Json
Assert 'aca: API container app exists' { $null -ne $apiApp } "container app '$ApiContainerAppName' not found"

$env = $apiApp.properties.template.containers[0].env
$envMap = @{}
foreach ($e in $env) { $envMap[$e.name] = $e }

Assert 'aca env: OpenAI__Endpoint set' { $envMap.ContainsKey('OpenAI__Endpoint') -and $envMap['OpenAI__Endpoint'].value } 'OpenAI__Endpoint missing'
Assert 'aca env: OpenAI__Endpoint matches APIM inference URL' {
    $endpoint = $envMap['OpenAI__Endpoint'].value
    $endpoint -and $gatewayUrl -and $endpoint.StartsWith($gatewayUrl)
} "OpenAI__Endpoint = $($envMap['OpenAI__Endpoint'].value); APIM gateway = $gatewayUrl"
Assert 'aca env: OpenAI__Endpoint does NOT end in /openai (regression #55)' {
    $endpoint = $envMap['OpenAI__Endpoint'].value
    $endpoint -and -not ($endpoint.TrimEnd('/') -match '/openai$')
} "OpenAI__Endpoint = $($envMap['OpenAI__Endpoint'].value)"
Assert 'aca env: OpenAI__UseManagedIdentity = false (using APIM subscription key)' {
    $envMap.ContainsKey('OpenAI__UseManagedIdentity') -and $envMap['OpenAI__UseManagedIdentity'].value -eq 'false'
} "OpenAI__UseManagedIdentity = $($envMap['OpenAI__UseManagedIdentity'].value)"
Assert 'aca env: OpenAI__ApimSubscriptionKey uses secretRef=apim-sub-key' {
    $envMap.ContainsKey('OpenAI__ApimSubscriptionKey') -and $envMap['OpenAI__ApimSubscriptionKey'].secretRef -eq 'apim-sub-key'
} "OpenAI__ApimSubscriptionKey secretRef = $($envMap['OpenAI__ApimSubscriptionKey'].secretRef)"

$secretNames = @($apiApp.properties.configuration.secrets | ForEach-Object { $_.name })
Assert 'aca secrets: apim-sub-key present' { $secretNames -contains 'apim-sub-key' } "secrets = $($secretNames -join ', ')"

# ── Summary ───────────────────────────────────────────────────────────────
Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host ("PASS  {0}/{0} invariants verified against the live deployment." -f $checks) -ForegroundColor Green
    exit 0
}
else {
    Write-Host ("FAIL  {0}/{1} invariants failed:" -f $failures.Count, $checks) -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "        - $f" -ForegroundColor Red }
    exit 1
}
