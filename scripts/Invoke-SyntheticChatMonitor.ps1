#!/usr/bin/env pwsh
<#
.SYNOPSIS
    OPTIONAL synthetic monitor for the authenticated Retail Pulse chat path.

.DESCRIPTION
    Delivers the "authenticated synthetic monitor" contract described in
    docs/testing/authenticated-synthetic-monitor.md as a fully OPTIONAL,
    credential-gated add-on that ships in the repo, works the moment an
    identity + authorisation exist, skips cleanly with an actionable message
    when they do not, and never fabricates a live result.

    HARD RULES

    * Authentication is workload-identity federation ONLY. In GitHub Actions
      `azure/login@v2` performs the OIDC → Entra token exchange using
      `permissions: id-token: write` plus the (non-secret) client-id and
      tenant-id inputs. Locally, the developer's own `az login` /
      DefaultAzureCredential context is used. This script never accepts,
      reads, prints, generates, or persists a client secret. A convention
      guard in the offline self-test refuses to let a client-secret code path
      be reintroduced.

    * When the deployment is not configured — no API origin, no signed-in
      Azure context, no App ID URI to request a token for, or a token
      request the caller lacks authorisation for — the script prints an
      explicit `SKIP:` line naming exactly what is missing and exits 0. It
      never turns a red CI into a green one by inventing a result, and it
      never turns a green CI into a red one for an unconfigured fork.

    * With `-SelfTest` it runs an offline regression fence (mirrors the shape
      of Verify-ApimAiGateway.ps1 -SelfTest): validates the smoke-prompt
      manifest against ChartAcceptanceManifest.Cases, exercises the response
      validator on well-formed and malformed synthetic payloads, proves the
      config-missing paths report skip (not pass, not fail), and asserts the
      script contains no client-secret code path. No Azure signin required;
      wired into CI as a signin-free regression fence.

    * On a live run: for each curated smoke prompt the script obtains a
      bearer token non-interactively via `az account get-access-token
      --resource <ApiResource>`, sends `POST {ApiOrigin}/api/chat`, and
      asserts the documented response contract — HTTP 200, an assistant
      `text` payload, `charts[]` with the expected chart type and minimum
      mark count, and a non-empty correlation id. It prints per-prompt
      results and a final PASS / FAIL summary. It never prints the token or
      any part of it.

    IDENTITY (non-secret; committed intentionally)

        App registration : retail-pulse-synthetic-monitor
        Client ID        : b8212317-e16d-4f06-996b-955e885ca1ca
        Tenant ID        : 48351615-345c-4547-bb6f-8fcc8d6e2568
        Federated cred   : github-actions-main
                           issuer  = https://token.actions.githubusercontent.com
                           audience = api://AzureADTokenExchange
                           subject = repo:swigerb@1630580/retail-pulse@1223914087:ref:refs/heads/main

    These are configuration, not credentials. There is no accompanying
    client secret to leak.

.PARAMETER ApiOrigin
    Base URL of the deployed API to smoke test, e.g.
    `https://ca-retailpulse-api.<suffix>.azurecontainerapps.io`. Defaults to
    `$env:RETAIL_PULSE_SYNTHETIC_API_ORIGIN` and falls back to
    `$env:AZURE_API_APP_URL` (the value azd env writes after provision).
    Required for a live run; omission → SKIP.

.PARAMETER ApiResource
    Token audience for the API app registration — the App ID URI or a
    scope such as `api://<api-client-id>/.default`. Defaults to
    `$env:RETAIL_PULSE_SYNTHETIC_API_RESOURCE`. Required for a live run;
    omission → SKIP.

.PARAMETER TenantId
    Target Entra tenant GUID. Advisory: the signed-in az context should
    already target this tenant (federation sets it automatically in GitHub
    Actions). Defaults to `$env:AZURE_TENANT_ID` and finally to the
    documented sandbox tenant id.

.PARAMETER ClientId
    Client id of the synthetic monitor app registration. Informational only —
    the actual signed-in principal is whatever `azure/login@v2` federated in
    (in CI) or whatever the developer's `az login` established (locally).
    Defaults to `$env:AZURE_CLIENT_ID` and finally to the documented
    `retail-pulse-synthetic-monitor` client id.

.PARAMETER Prompts
    Optional override for the smoke-prompt set. Defaults to the two curated
    prompts documented in docs/testing/authenticated-synthetic-monitor.md.
    Each entry is a hashtable with `Prompt`, `ExpectedChartType`, and
    `MinMarks`.

.PARAMETER TimeoutSec
    Per-request timeout in seconds. Default 60.

.PARAMETER SelfTest
    Run the offline self-test and exit. No Azure signin required.

.EXAMPLE
    ./scripts/Invoke-SyntheticChatMonitor.ps1 -SelfTest

    Runs the offline regression fence — the same mode CI runs.

.EXAMPLE
    $env:RETAIL_PULSE_SYNTHETIC_API_ORIGIN   = 'https://ca-retailpulse-api.<region>.azurecontainerapps.io'
    $env:RETAIL_PULSE_SYNTHETIC_API_RESOURCE = 'api://<api-client-id>/.default'
    az login  # or, in CI: azure/login@v2 with client-id + tenant-id
    ./scripts/Invoke-SyntheticChatMonitor.ps1

    Live run against the configured deployment.
#>
[CmdletBinding()]
param(
    [string]$ApiOrigin,
    [string]$ApiResource,
    [string]$TenantId,
    [string]$ClientId,
    [object[]]$Prompts,
    [int]$TimeoutSec = 60,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Documented non-secret configuration (issue #57) ────────────────────────
# App registration `retail-pulse-synthetic-monitor` in the MCAP sandbox
# tenant. These are IDs, not credentials — there is no client secret. Auth
# is workload-identity federation only.
$script:DefaultSyntheticClientId = 'b8212317-e16d-4f06-996b-955e885ca1ca'
$script:DefaultSyntheticTenantId = '48351615-345c-4547-bb6f-8fcc8d6e2568'

# Curated smoke set from docs/testing/authenticated-synthetic-monitor.md —
# each prompt maps to a real entry in ChartAcceptanceManifest.Cases and
# exercises a distinct chart data-source family so an independent regression
# in either family surfaces here.
$script:DefaultSmokePrompts = @(
    @{
        Prompt            = 'Show a horizontal bar chart ranking all brands by depletion growth rate'
        ExpectedChartType = 'horizontalBar'
        MinMarks          = 6
        ManifestCase      = 'ChartAcceptanceManifest.Cases[5]'
    },
    @{
        Prompt            = 'Show a grouped bar chart comparing FreshMart and Harvest Table across all regions'
        ExpectedChartType = 'groupedBar'
        MinMarks          = 12
        ManifestCase      = 'ChartAcceptanceManifest.Cases[3]'
    }
)

# ── output helpers ─────────────────────────────────────────────────────────
function Write-Skip([string]$reason) {
    # A skip is an explicit, actionable, non-failing outcome. It NEVER
    # implies a live result.
    Write-Host ("SKIP: {0}" -f $reason) -ForegroundColor Yellow
}

function Write-PromptResult([string]$prompt, [bool]$ok, [string]$detail) {
    $shortPrompt = if ($prompt.Length -gt 80) { $prompt.Substring(0, 77) + '...' } else { $prompt }
    if ($ok) {
        Write-Host ("  [PASS] {0}" -f $shortPrompt) -ForegroundColor Green
        if ($detail) { Write-Host ("         {0}" -f $detail) -ForegroundColor DarkGray }
    }
    else {
        Write-Host ("  [FAIL] {0}" -f $shortPrompt) -ForegroundColor Red
        if ($detail) { Write-Host ("         {0}" -f $detail) -ForegroundColor Red }
    }
}

# ── config resolution ──────────────────────────────────────────────────────
# Returns a hashtable with fully-resolved config, or $null with a reason
# when a live run cannot proceed. NEVER accepts or resolves a client
# secret — federation only.
function Resolve-MonitorConfig {
    param(
        [string]$ApiOrigin,
        [string]$ApiResource,
        [string]$TenantId,
        [string]$ClientId
    )

    if ([string]::IsNullOrWhiteSpace($ApiOrigin)) {
        $ApiOrigin = $env:RETAIL_PULSE_SYNTHETIC_API_ORIGIN
        if ([string]::IsNullOrWhiteSpace($ApiOrigin)) {
            $ApiOrigin = $env:AZURE_API_APP_URL
        }
    }
    if ([string]::IsNullOrWhiteSpace($ApiResource)) {
        $ApiResource = $env:RETAIL_PULSE_SYNTHETIC_API_RESOURCE
    }
    if ([string]::IsNullOrWhiteSpace($TenantId)) {
        $TenantId = $env:AZURE_TENANT_ID
        if ([string]::IsNullOrWhiteSpace($TenantId)) { $TenantId = $script:DefaultSyntheticTenantId }
    }
    if ([string]::IsNullOrWhiteSpace($ClientId)) {
        $ClientId = $env:AZURE_CLIENT_ID
        if ([string]::IsNullOrWhiteSpace($ClientId)) { $ClientId = $script:DefaultSyntheticClientId }
    }

    $missing = @()
    if ([string]::IsNullOrWhiteSpace($ApiOrigin)) { $missing += 'RETAIL_PULSE_SYNTHETIC_API_ORIGIN (or AZURE_API_APP_URL)' }
    if ([string]::IsNullOrWhiteSpace($ApiResource)) { $missing += 'RETAIL_PULSE_SYNTHETIC_API_RESOURCE (App ID URI / scope, e.g. api://<api-client-id>/.default)' }
    if ($missing.Count -gt 0) {
        return @{
            Ok      = $false
            Reason  = "optional synthetic monitor is not configured — missing $($missing -join ', '). This is expected on any deployment that has not opted into the monitor; nothing to do."
        }
    }

    return @{
        Ok          = $true
        ApiOrigin   = $ApiOrigin.TrimEnd('/')
        ApiResource = $ApiResource
        TenantId    = $TenantId
        ClientId    = $ClientId
    }
}

# ── az / token helpers ─────────────────────────────────────────────────────
function Test-AzAvailable {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        return @{ Ok = $false; Reason = "az CLI is not installed on PATH — install Azure CLI or run in a workflow that uses `azure/login@v2`." }
    }
    $account = az account show --only-show-errors 2>$null | ConvertFrom-Json -ErrorAction SilentlyContinue
    if (-not $account) {
        return @{ Ok = $false; Reason = "az CLI is not signed in — run `az login` locally, or ensure the workflow uses `azure/login@v2` with `permissions: id-token: write` and the non-secret client-id/tenant-id inputs." }
    }
    return @{ Ok = $true; Subscription = $account }
}

# Non-interactive federation-only token acquisition. Delegates to
# `az account get-access-token` which honours the signed-in principal
# — the federated SP under azure/login@v2 in CI, or the developer's own
# identity locally. The raw token is returned but NEVER printed or logged.
function Get-ApiAccessToken {
    param(
        [Parameter(Mandatory)][string]$Resource,
        [string]$TenantId
    )
    $args = @('account', 'get-access-token', '--resource', $Resource, '--query', 'accessToken', '-o', 'tsv', '--only-show-errors')
    if (-not [string]::IsNullOrWhiteSpace($TenantId)) { $args += @('--tenant', $TenantId) }
    $errorFile = [IO.Path]::GetTempFileName()
    try {
        $out = & az @args 2>$errorFile
        if ($LASTEXITCODE -ne 0) {
            $err = (Get-Content $errorFile -Raw -ErrorAction SilentlyContinue)
            return @{ Ok = $false; Reason = "az account get-access-token failed for resource '$Resource' — the signed-in principal likely lacks the RetailPulse.User app role on the API app registration (admin consent still required). az error: $($err.Trim())" }
        }
        $token = ($out | Out-String).Trim()
        if ([string]::IsNullOrWhiteSpace($token)) {
            return @{ Ok = $false; Reason = "az account get-access-token returned an empty token for resource '$Resource' — likely a scope or tenant mismatch." }
        }
        return @{ Ok = $true; Token = $token }
    }
    finally {
        Remove-Item $errorFile -Force -ErrorAction SilentlyContinue
    }
}

# ── response contract validator ────────────────────────────────────────────
# Pure function of an already-parsed response body — no HTTP, no side
# effects. This is the surface the offline self-test exercises. Returns
# @{ Ok = $bool; Detail = "..."; CorrelationId = "..." }.
function Test-ChatResponseContract {
    param(
        [AllowNull()]$Body,
        [Parameter(Mandatory)][string]$ExpectedChartType,
        [Parameter(Mandatory)][int]$MinMarks
    )
    if ($null -eq $Body) {
        return @{ Ok = $false; Detail = 'response body was null / could not be parsed as JSON'; CorrelationId = '' }
    }
    $text = $null
    if ($Body.PSObject.Properties['text']) { $text = $Body.text }
    if ([string]::IsNullOrWhiteSpace($text)) {
        return @{ Ok = $false; Detail = 'response body has no non-empty `text` assistant payload'; CorrelationId = '' }
    }
    $charts = $null
    if ($Body.PSObject.Properties['charts']) { $charts = @($Body.charts) }
    if (-not $charts -or $charts.Count -eq 0) {
        return @{ Ok = $false; Detail = 'response body has no `charts[]` entries'; CorrelationId = '' }
    }
    $expected = $charts | Where-Object {
        $_ -and $_.PSObject.Properties['type'] -and $_.type -and
        ($_.type.ToString().Trim().ToLowerInvariant() -eq $ExpectedChartType.ToLowerInvariant())
    } | Select-Object -First 1
    if (-not $expected) {
        $seen = ($charts | ForEach-Object { $_.type }) -join ', '
        return @{ Ok = $false; Detail = "expected chart type '$ExpectedChartType' not present in response (saw: $seen)"; CorrelationId = '' }
    }
    $marks = 0
    if ($expected.PSObject.Properties['data'] -and $expected.data) {
        # Chart data is a set of series each with data points. Count the sum
        # of data-point counts across series ("marks" in the acceptance
        # contract) with a graceful fallback for either a flat shape or a
        # per-series shape.
        $data = $expected.data
        if ($data -is [System.Collections.IEnumerable] -and -not ($data -is [string])) {
            foreach ($series in $data) {
                if ($null -eq $series) { continue }
                if ($series.PSObject.Properties['data'] -and $series.data -is [System.Collections.IEnumerable]) {
                    $marks += @($series.data).Count
                }
                else {
                    $marks += 1
                }
            }
        }
    }
    if ($marks -lt $MinMarks) {
        return @{ Ok = $false; Detail = "chart '$ExpectedChartType' has $marks marks; contract requires >= $MinMarks"; CorrelationId = '' }
    }
    $corr = ''
    foreach ($n in @('traceId', 'sessionId', 'correlationId')) {
        if ($Body.PSObject.Properties[$n] -and -not [string]::IsNullOrWhiteSpace([string]$Body.$n)) {
            $corr = [string]$Body.$n
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($corr)) {
        return @{ Ok = $false; Detail = 'response body has no non-empty traceId / sessionId / correlationId'; CorrelationId = '' }
    }
    # Redact the correlation id in the reported detail — surface only its
    # tail for cross-referencing App Insights.
    $tail = if ($corr.Length -gt 8) { $corr.Substring($corr.Length - 8) } else { $corr }
    return @{ Ok = $true; Detail = ("chartType=$ExpectedChartType marks=$marks correlationId=****$tail"); CorrelationId = $corr }
}

# ── live-run driver ────────────────────────────────────────────────────────
function Invoke-Live {
    param(
        [Parameter(Mandatory)][hashtable]$Config,
        [Parameter(Mandatory)][object[]]$Prompts,
        [Parameter(Mandatory)][int]$TimeoutSec
    )

    Write-Host "Optional authenticated synthetic monitor (issue #57)"
    Write-Host ("  apiOrigin   = {0}" -f $Config.ApiOrigin)
    Write-Host ("  apiResource = {0}" -f $Config.ApiResource)
    Write-Host ("  tenantId    = ****{0}" -f (if ($Config.TenantId.Length -gt 4) { $Config.TenantId.Substring($Config.TenantId.Length - 4) } else { $Config.TenantId }))
    Write-Host ("  clientId    = ****{0}" -f (if ($Config.ClientId.Length -gt 4) { $Config.ClientId.Substring($Config.ClientId.Length - 4) } else { $Config.ClientId }))
    Write-Host ("  prompts     = {0}" -f $Prompts.Count)
    Write-Host ""

    $az = Test-AzAvailable
    if (-not $az.Ok) {
        Write-Skip $az.Reason
        exit 0
    }

    Write-Host "Acquiring API access token via federation (`az account get-access-token`) ..."
    $tok = Get-ApiAccessToken -Resource $Config.ApiResource -TenantId $Config.TenantId
    if (-not $tok.Ok) {
        Write-Skip $tok.Reason
        exit 0
    }
    Write-Host "  [ok]   token acquired (redacted, never printed)" -ForegroundColor Green
    Write-Host ""

    $url = $Config.ApiOrigin.TrimEnd('/') + '/api/chat'
    $headers = @{
        Authorization = "Bearer $($tok.Token)"
        'Content-Type' = 'application/json'
        Accept        = 'application/json'
    }

    $failures = 0
    foreach ($p in $Prompts) {
        $promptText = [string]$p.Prompt
        $expectedType = [string]$p.ExpectedChartType
        $minMarks = [int]$p.MinMarks
        $bodyJson = @{ prompt = $promptText } | ConvertTo-Json -Depth 4 -Compress
        $status = 0
        $parsed = $null
        $detail = ''
        try {
            $resp = Invoke-WebRequest -Method Post -Uri $url -Headers $headers -Body $bodyJson `
                -SkipHttpErrorCheck -MaximumRedirection 0 -TimeoutSec $TimeoutSec -ErrorAction Stop
            $status = [int]$resp.StatusCode
            if ($status -eq 200) {
                try { $parsed = $resp.Content | ConvertFrom-Json -ErrorAction Stop } catch { $parsed = $null }
            }
        }
        catch {
            $status = -1
            $detail = "transport error: $($_.Exception.Message)"
        }
        if ($status -ne 200) {
            $reason = if ($detail) { $detail } else { "HTTP $status (expected 200)" }
            Write-PromptResult $promptText $false $reason
            $failures++
            continue
        }
        $verdict = Test-ChatResponseContract -Body $parsed -ExpectedChartType $expectedType -MinMarks $minMarks
        Write-PromptResult $promptText $verdict.Ok $verdict.Detail
        if (-not $verdict.Ok) { $failures++ }
    }

    Write-Host ""
    if ($failures -eq 0) {
        Write-Host ("PASS  {0}/{0} smoke prompts satisfied the response contract." -f $Prompts.Count) -ForegroundColor Green
        exit 0
    }
    Write-Host ("FAIL  {0}/{1} smoke prompts failed the response contract." -f $failures, $Prompts.Count) -ForegroundColor Red
    exit 1
}

# ── offline self-test ──────────────────────────────────────────────────────
# Regression fence — mirrors Verify-ApimAiGateway.ps1 -SelfTest in shape.
# Exercises: (a) the curated smoke set stays synchronised with
# ChartAcceptanceManifest, (b) the response contract validator distinguishes
# well-formed from malformed payloads, (c) the config-missing path produces
# SKIP (not fabricated PASS, not spurious FAIL), (d) the script contains no
# client-secret code path. No Azure signin required.
function Invoke-SelfTest {
    function _Expect([string]$name, [bool]$cond) {
        if ($cond) { Write-Host "  [ok]   selftest: $name" -ForegroundColor Green }
        else { Write-Host "  [FAIL] selftest: $name" -ForegroundColor Red; $script:_selfFailed++ }
    }
    $script:_selfFailed = 0
    Write-Host "Self-test (offline; no Azure signin required)"

    # (a) Smoke set stays synchronised with ChartAcceptanceManifest — the
    # doc/design references specific cases by index, so if either the
    # prompts or the expected chart types drift, the monitor's live
    # assertions no longer match the contract test suite. The manifest is
    # part of RetailPulse.Contracts and its case list is inspectable via
    # source; assert the two curated cases exist verbatim in the manifest.
    $manifestPath = Join-Path $PSScriptRoot '..\src\RetailPulse.Contracts\Charts\ChartAcceptanceManifest.cs'
    $manifestPath = [System.IO.Path]::GetFullPath($manifestPath)
    _Expect 'ChartAcceptanceManifest.cs is discoverable relative to scripts/' (Test-Path $manifestPath)
    if (Test-Path $manifestPath) {
        $manifestSrc = Get-Content -LiteralPath $manifestPath -Raw
        foreach ($p in $script:DefaultSmokePrompts) {
            _Expect ("manifest contains curated prompt: {0}..." -f $p.Prompt.Substring(0, [Math]::Min(40, $p.Prompt.Length))) `
                ($manifestSrc.IndexOf($p.Prompt) -ge 0)
        }
        _Expect 'manifest declares a horizontalBar case (matches smoke prompt 1)' ($manifestSrc -match 'ChartType:\s*"horizontalBar"')
        _Expect 'manifest declares a groupedBar case (matches smoke prompt 2)' ($manifestSrc -match 'ChartType:\s*"groupedBar"')
    }

    # (b) Response validator — PASS path on a well-formed payload.
    $goodBody = [pscustomobject]@{
        text     = 'Here is the horizontal bar chart ranking depletion growth.'
        traceId  = '00-abcdef1234567890abcdef1234567890-0011223344556677-01'
        sessionId = 'session-000-selftest'
        charts   = @(
            [pscustomobject]@{
                type = 'horizontalBar'
                data = @(
                    [pscustomobject]@{ name = 'series-a'; data = @(
                            [pscustomobject]@{ x = 'BrandA'; y = 12 },
                            [pscustomobject]@{ x = 'BrandB'; y = 8 },
                            [pscustomobject]@{ x = 'BrandC'; y = 5 },
                            [pscustomobject]@{ x = 'BrandD'; y = 3 },
                            [pscustomobject]@{ x = 'BrandE'; y = 1 },
                            [pscustomobject]@{ x = 'BrandF'; y = -2 },
                            [pscustomobject]@{ x = 'BrandG'; y = -4 }
                        ) }
                )
            }
        )
    }
    $good = Test-ChatResponseContract -Body $goodBody -ExpectedChartType 'horizontalBar' -MinMarks 6
    _Expect 'validator PASSes a well-formed horizontalBar response' $good.Ok
    _Expect 'validator surfaces a redacted correlation id (starts with ****)' ($good.Detail -match '\*\*\*\*[a-zA-Z0-9]+')
    _Expect 'validator does NOT print the raw traceId anywhere in Detail' (($good.Detail.IndexOf($goodBody.traceId)) -lt 0)

    # (b) Response validator — FAIL paths on malformed payloads. Each of
    # these should produce Ok=$false with a clear reason, NOT throw and NOT
    # falsely pass. Missing text / missing charts / wrong type / too few
    # marks / missing correlation id / null body.
    $null1 = Test-ChatResponseContract -Body $null -ExpectedChartType 'horizontalBar' -MinMarks 6
    _Expect 'validator FAILs a null body' (-not $null1.Ok)
    $noText = [pscustomobject]@{ charts = @([pscustomobject]@{ type = 'horizontalBar'; data = @() }) }
    $r = Test-ChatResponseContract -Body $noText -ExpectedChartType 'horizontalBar' -MinMarks 6
    _Expect 'validator FAILs a response with no `text` payload' (-not $r.Ok)
    $noCharts = [pscustomobject]@{ text = 'no charts here'; traceId = 't' }
    $r = Test-ChatResponseContract -Body $noCharts -ExpectedChartType 'horizontalBar' -MinMarks 6
    _Expect 'validator FAILs a response with no `charts[]`' (-not $r.Ok)
    $wrongType = [pscustomobject]@{ text = 'wrong type'; traceId = 't'; charts = @([pscustomobject]@{ type = 'pie'; data = @() }) }
    $r = Test-ChatResponseContract -Body $wrongType -ExpectedChartType 'horizontalBar' -MinMarks 6
    _Expect 'validator FAILs when chart type does not match' (-not $r.Ok)
    $tooFewMarks = [pscustomobject]@{
        text = 'too few marks'; traceId = 't'
        charts = @([pscustomobject]@{ type = 'horizontalBar'; data = @([pscustomobject]@{ name = 's'; data = @(1, 2, 3) }) })
    }
    $r = Test-ChatResponseContract -Body $tooFewMarks -ExpectedChartType 'horizontalBar' -MinMarks 6
    _Expect 'validator FAILs when marks < required' (-not $r.Ok)
    $noCorr = [pscustomobject]@{
        text = 'no correlation'
        charts = @([pscustomobject]@{ type = 'horizontalBar'; data = @([pscustomobject]@{ name = 's'; data = 1..8 }) })
    }
    $r = Test-ChatResponseContract -Body $noCorr -ExpectedChartType 'horizontalBar' -MinMarks 6
    _Expect 'validator FAILs when no correlation id is present' (-not $r.Ok)

    # (c) Config-missing SKIP path — Resolve-MonitorConfig with everything
    # blank AND no env fallbacks must return Ok=$false with a reason that
    # names what's missing. It must NEVER return Ok=$true just because
    # nothing was passed.
    $savedOrigin = $env:RETAIL_PULSE_SYNTHETIC_API_ORIGIN
    $savedResource = $env:RETAIL_PULSE_SYNTHETIC_API_RESOURCE
    $savedAppUrl = $env:AZURE_API_APP_URL
    try {
        $env:RETAIL_PULSE_SYNTHETIC_API_ORIGIN = $null
        $env:RETAIL_PULSE_SYNTHETIC_API_RESOURCE = $null
        $env:AZURE_API_APP_URL = $null
        $cfg = Resolve-MonitorConfig -ApiOrigin '' -ApiResource '' -TenantId '' -ClientId ''
        _Expect 'Resolve-MonitorConfig reports skip when nothing is configured' (-not $cfg.Ok)
        _Expect 'skip reason names the missing API origin variable' ($cfg.Reason -match 'RETAIL_PULSE_SYNTHETIC_API_ORIGIN')
        _Expect 'skip reason names the missing API resource variable' ($cfg.Reason -match 'RETAIL_PULSE_SYNTHETIC_API_RESOURCE')
        _Expect 'skip reason explicitly calls the outcome expected / no-op' ($cfg.Reason -match 'not configured')

        # And the happy-path resolution when both are set (env fallback).
        $env:RETAIL_PULSE_SYNTHETIC_API_ORIGIN = 'https://api.example.invalid/'
        $env:RETAIL_PULSE_SYNTHETIC_API_RESOURCE = 'api://11111111-1111-1111-1111-111111111111/.default'
        $cfg = Resolve-MonitorConfig -ApiOrigin '' -ApiResource '' -TenantId '' -ClientId ''
        _Expect 'Resolve-MonitorConfig succeeds when both env vars are set' ($cfg.Ok)
        _Expect 'Resolve-MonitorConfig trims trailing slash from ApiOrigin' ($cfg.ApiOrigin -eq 'https://api.example.invalid')
        _Expect 'Resolve-MonitorConfig defaults ClientId to the documented synthetic monitor id when nothing else is set' ($cfg.ClientId -eq $script:DefaultSyntheticClientId)
        _Expect 'Resolve-MonitorConfig defaults TenantId to the documented sandbox tenant id when nothing else is set' ($cfg.TenantId -eq $script:DefaultSyntheticTenantId)
    }
    finally {
        $env:RETAIL_PULSE_SYNTHETIC_API_ORIGIN = $savedOrigin
        $env:RETAIL_PULSE_SYNTHETIC_API_RESOURCE = $savedResource
        $env:AZURE_API_APP_URL = $savedAppUrl
    }

    # (d) Federation-only guard: this script contains NO code path that
    # reads, prints, generates, or persists a client secret. Regression
    # fence for any future revision that tries to reintroduce a
    # client-credentials-with-secret path (issue #57 non-negotiable).
    # Patterns are pieced together at runtime so the guard's own definition
    # does not appear in source as literal forbidden text.
    $selfSource = Get-Content -LiteralPath $PSCommandPath -Raw
    # Remove the entire <# ... #> doc-comment block (its .DESCRIPTION
    # deliberately explains why federation is used and mentions the very
    # patterns this guard bans; leaving the block in would false-positive).
    $codeOnly = [regex]::Replace($selfSource, '(?s)<#.*?#>', '')
    # Then drop line comments so intentional guard commentary can't trip
    # the guard either.
    $codeOnly = ($codeOnly -split "`n" | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
    # Finally strip single- and double-quoted string literals so the guard's
    # own label strings and any legitimate descriptive text inside a
    # code-level string cannot match — only the actual identifiers,
    # parameter declarations, and calls are scanned.
    $codeOnly = [regex]::Replace($codeOnly, "'[^'`r`n]*'", "''")
    $codeOnly = [regex]::Replace($codeOnly, '"[^"`r`n]*"', '""')
    $cs = 'client' + '_' + 'secret'
    $CS = 'Client' + 'Secret'
    $envSecret = 'RETAIL_PULSE_SYNTHETIC_' + 'CLIENT_' + 'SECRET'
    $forbidden = @(
        @{ Label = 'client_secret token'; Pattern = $cs },
        @{ Label = '$ClientSecret assignment'; Pattern = ('\$' + $CS + '\s*=\s*') },
        @{ Label = '-ClientSecret parameter usage'; Pattern = ('-' + $CS + '\b') },
        @{ Label = 'RETAIL_PULSE_SYNTHETIC_CLIENT_SECRET env var'; Pattern = $envSecret },
        @{ Label = 'client_credentials grant_type'; Pattern = 'grant_type\s*=\s*client_credentials' },
        @{ Label = 'az ad sp credential reset'; Pattern = 'az\s+ad\s+sp\s+credential\s+reset' }
    )
    foreach ($f in $forbidden) {
        $hit = ($codeOnly -match $f.Pattern)
        _Expect ("federation-only guard: no executable code path contains " + $f.Label) (-not $hit)
    }
    # And guard against a client-secret CLI parameter ever being declared.
    $secretParamPattern = '\[string\]\s*\$' + $CS + '\b'
    $hasSecretParam = ($codeOnly -match $secretParamPattern)
    _Expect 'federation-only guard: no [string]$ClientSecret parameter is declared' (-not $hasSecretParam)

    if ($script:_selfFailed -gt 0) {
        Write-Host ("SELFTEST FAIL ({0} case(s))" -f $script:_selfFailed) -ForegroundColor Red
        exit 1
    }
    Write-Host "SELFTEST PASS" -ForegroundColor Green
    exit 0
}

# ── entry point ────────────────────────────────────────────────────────────
if ($SelfTest) { Invoke-SelfTest }

$config = Resolve-MonitorConfig -ApiOrigin $ApiOrigin -ApiResource $ApiResource -TenantId $TenantId -ClientId $ClientId
if (-not $config.Ok) {
    Write-Skip $config.Reason
    exit 0
}

$effectivePrompts = if ($Prompts -and $Prompts.Count -gt 0) { $Prompts } else { $script:DefaultSmokePrompts }
Invoke-Live -Config $config -Prompts $effectivePrompts -TimeoutSec $TimeoutSec
