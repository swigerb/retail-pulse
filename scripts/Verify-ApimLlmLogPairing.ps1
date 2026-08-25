#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Deterministic APIM LLM-log request/response pairing smoke test.

.DESCRIPTION
    Fires N low-token direct APIM chat/completions calls, each tagged with a
    unique marker string embedded in the user message, then queries the
    ApiManagementGatewayLlmLog table in Log Analytics for the same window and
    asserts every marker is paired 1:1 (one SequenceNumber=1 "request" row AND
    one SequenceNumber=0 "response" row).

    Motivated by the non-blocking observation on PR #52 (issue #54, item 1):
    ~30% of small-token direct APIM calls produced only SequenceNumber=1 rows
    in ApiManagementGatewayLlmLog and no matching SequenceNumber=0 rows on
    APIM 'apim-5aldk7aotqods' after 3c39ae4 landed. Publix's later 5/5 clean
    re-check on 9fdc2ab showed pairing recovered, so the failure mode is
    inconsistent under short bursts. This smoke reproduces the burst pattern
    deterministically and asserts the pairing invariant so a regression is
    caught before the demo.

    Mirrors the conventions and offline-first shape of
    ./scripts/Verify-ApimAiGateway.ps1:

      * Read-only against the live APIM (data plane call to /openai/…/chat/completions)
        and against Log Analytics (KQL over ApiManagementGatewayLlmLog).
      * Every parameter has an explicit env-var fallback for CI/scheduled use.
      * `-SelfTest` runs a signin-free offline unit test of the pairing
        analysis so CI can wire it as a regression fence without needing an
        APIM instance.
      * Non-zero exit only on real failure or clearly-explained skip.

    Live verification is DEFERRED in this repo: no live APIM instance /
    Log Analytics workspace is available from the environment producing this
    script, so live-mode results are not asserted. The offline `-SelfTest`
    validates the analysis half so the live half can be trusted the moment
    parameters are supplied.

.PARAMETER Endpoint
    APIM inference endpoint URL, e.g.
    https://apim-5aldk7aotqods.azure-api.net/inference (NO trailing '/openai';
    the deployment path is appended). Defaults to $env:APIM_INFERENCE_ENDPOINT.

.PARAMETER Deployment
    AOAI deployment name to invoke. Defaults to $env:APIM_DEPLOYMENT_NAME.

.PARAMETER ApiVersion
    Chat-completions API version. Defaults to '2024-08-01-preview' — the
    version pinned in production APIM policy.

.PARAMETER SubscriptionKey
    APIM subscription key for the inference product. Defaults to
    $env:APIM_SUBSCRIPTION_KEY. Passed via the Ocp-Apim-Subscription-Key
    header on every call; never logged, never echoed.

.PARAMETER WorkspaceId
    Log Analytics workspace ID that receives the API-level
    `azuremonitor` diagnostic (the sink populating ApiManagementGatewayLlmLog).
    Defaults to $env:APIM_LOG_ANALYTICS_WORKSPACE_ID.

.PARAMETER Count
    Number of low-token direct APIM calls to fire. Defaults to 5 — matches
    the sample size Publix used when observing the pairing drop.

.PARAMETER SettleSeconds
    Bounded settle window between the last call and the Log Analytics query.
    Defaults to 180 (three minutes) — the ingestion SLO for
    ApiManagementGatewayLlmLog is < 5 minutes, so this window is conservative
    but bounded enough to fail loudly if a response row is dropped rather
    than merely late.

.PARAMETER MaxTokens
    Response token cap per direct APIM call. Defaults to 8. The low ceiling
    exercises the exact failure mode from the PR #52 observation
    (small-token responses were the ones that dropped SequenceNumber=0
    rows).

.PARAMETER SelfTest
    Runs an offline unit test of the pairing analysis and exits without
    calling APIM or Log Analytics. Wired into CI as a signin-free fence.

.EXAMPLE
    ./scripts/Verify-ApimLlmLogPairing.ps1 -SelfTest

    Runs offline self-test only. Zero external calls, no auth required.

.EXAMPLE
    $env:APIM_INFERENCE_ENDPOINT      = 'https://apim-5aldk7aotqods.azure-api.net/inference'
    $env:APIM_DEPLOYMENT_NAME         = 'gpt-4o-mini'
    $env:APIM_SUBSCRIPTION_KEY        = '…'
    $env:APIM_LOG_ANALYTICS_WORKSPACE_ID = 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx'
    ./scripts/Verify-ApimLlmLogPairing.ps1

    Live run: fires 5 low-token calls, waits 180s, asserts every marker is
    paired 1:1 in ApiManagementGatewayLlmLog.
#>
[CmdletBinding()]
param(
    [string]$Endpoint         = $env:APIM_INFERENCE_ENDPOINT,
    [string]$Deployment       = $env:APIM_DEPLOYMENT_NAME,
    [string]$ApiVersion       = '2024-08-01-preview',
    [string]$SubscriptionKey  = $env:APIM_SUBSCRIPTION_KEY,
    [string]$WorkspaceId      = $env:APIM_LOG_ANALYTICS_WORKSPACE_ID,
    [ValidateRange(1, 100)]
    [int]$Count               = 5,
    [ValidateRange(30, 900)]
    [int]$SettleSeconds       = 180,
    [ValidateRange(1, 128)]
    [int]$MaxTokens           = 8,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

# ── Pairing analysis ────────────────────────────────────────────────────────
# The analyzer takes:
#   * the markers we generated (one per direct APIM call),
#   * a list of log rows shaped as PSCustomObject with .Marker and
#     .SequenceNumber (0 or 1) — either from a real Log Analytics query or
#     from the offline self-test fixture.
# and returns an object recording per-marker pass/fail plus overall totals.
#
# Kept as a pure function so `-SelfTest` can exercise it without touching
# Azure. This function does NOT return early on the first missing pair — it
# reports the complete picture so the operator can see whether the drop is
# uniform or bursty.
function Test-LlmLogPairing {
    param(
        [Parameter(Mandatory)][string[]]$Markers,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Rows
    )
    $per = [System.Collections.Generic.List[object]]::new()
    foreach ($m in $Markers) {
        $requestRow  = $Rows | Where-Object { $_.Marker -eq $m -and $_.SequenceNumber -eq 1 } | Select-Object -First 1
        $responseRow = $Rows | Where-Object { $_.Marker -eq $m -and $_.SequenceNumber -eq 0 } | Select-Object -First 1
        $per.Add([PSCustomObject]@{
            Marker      = $m
            HasRequest  = [bool]$requestRow
            HasResponse = [bool]$responseRow
            Paired      = [bool]($requestRow -and $responseRow)
        })
    }
    $paired      = ($per | Where-Object Paired).Count
    $missingResp = ($per | Where-Object { $_.HasRequest -and -not $_.HasResponse }).Count
    $missingReq  = ($per | Where-Object { -not $_.HasRequest -and $_.HasResponse }).Count
    $missingBoth = ($per | Where-Object { -not $_.HasRequest -and -not $_.HasResponse }).Count
    return [PSCustomObject]@{
        Markers         = $Markers.Count
        Paired          = $paired
        MissingResponse = $missingResp
        MissingRequest  = $missingReq
        MissingBoth     = $missingBoth
        Details         = $per
        Ok              = ($paired -eq $Markers.Count)
    }
}

# ── Offline self-test ───────────────────────────────────────────────────────
# Exercises the pairing analyzer with three fixtures:
#   1. Every marker paired (the healthy case; must return Ok=true).
#   2. One marker missing SequenceNumber=0 (the exact PR #52 symptom; must
#      report MissingResponse=1 and Ok=false).
#   3. One marker paired 1:1 while an unrelated stray SequenceNumber=1 row
#      belongs to a different marker (must NOT count against the paired
#      marker; asserts the analyzer scopes by marker).
# Exits 0 on all-pass, 1 on any failure. No Azure signin required.
function Invoke-SelfTest {
    function _Expect([string]$name, [bool]$cond) {
        if ($cond) { Write-Host "  [ok]   selftest: $name" -ForegroundColor Green }
        else       { Write-Host "  [FAIL] selftest: $name" -ForegroundColor Red; $script:_selfFailed++ }
    }
    $script:_selfFailed = 0
    Write-Host 'Self-test: APIM LLM-log pairing analyzer'

    # Fixture 1 — every marker paired.
    $markers1 = @('m-alpha', 'm-beta', 'm-gamma')
    $rows1 = @(
        [PSCustomObject]@{ Marker = 'm-alpha'; SequenceNumber = 1 },
        [PSCustomObject]@{ Marker = 'm-alpha'; SequenceNumber = 0 },
        [PSCustomObject]@{ Marker = 'm-beta';  SequenceNumber = 1 },
        [PSCustomObject]@{ Marker = 'm-beta';  SequenceNumber = 0 },
        [PSCustomObject]@{ Marker = 'm-gamma'; SequenceNumber = 1 },
        [PSCustomObject]@{ Marker = 'm-gamma'; SequenceNumber = 0 }
    )
    $r1 = Test-LlmLogPairing -Markers $markers1 -Rows $rows1
    _Expect 'healthy case: Ok=true'         ($r1.Ok -eq $true)
    _Expect 'healthy case: Paired=3'        ($r1.Paired -eq 3)
    _Expect 'healthy case: MissingResponse=0' ($r1.MissingResponse -eq 0)

    # Fixture 2 — one marker missing SequenceNumber=0 (the PR #52 symptom).
    $markers2 = @('m-alpha', 'm-beta')
    $rows2 = @(
        [PSCustomObject]@{ Marker = 'm-alpha'; SequenceNumber = 1 },
        [PSCustomObject]@{ Marker = 'm-alpha'; SequenceNumber = 0 },
        [PSCustomObject]@{ Marker = 'm-beta';  SequenceNumber = 1 }
        # response row for m-beta intentionally absent
    )
    $r2 = Test-LlmLogPairing -Markers $markers2 -Rows $rows2
    _Expect 'PR #52 symptom: Ok=false'          ($r2.Ok -eq $false)
    _Expect 'PR #52 symptom: MissingResponse=1' ($r2.MissingResponse -eq 1)
    _Expect 'PR #52 symptom: Paired=1'          ($r2.Paired -eq 1)
    $failedMarker = ($r2.Details | Where-Object { -not $_.Paired } | Select-Object -First 1).Marker
    _Expect 'PR #52 symptom: failed marker is m-beta' ($failedMarker -eq 'm-beta')

    # Fixture 3 — stray row from a different marker must not falsely pair.
    $markers3 = @('m-alpha')
    $rows3 = @(
        [PSCustomObject]@{ Marker = 'm-alpha';  SequenceNumber = 1 },
        [PSCustomObject]@{ Marker = 'm-alpha';  SequenceNumber = 0 },
        [PSCustomObject]@{ Marker = 'm-stray';  SequenceNumber = 1 }
    )
    $r3 = Test-LlmLogPairing -Markers $markers3 -Rows $rows3
    _Expect 'scoped by marker: Ok=true'  ($r3.Ok -eq $true)
    _Expect 'scoped by marker: Paired=1' ($r3.Paired -eq 1)

    # Fixture 4 — marker string chosen for uniqueness never collides. Generate
    # 10 markers via the same generator the live path uses and assert they are
    # distinct AND at least 24 characters (retail-pulse-marker- + 8 hex).
    $generated = 1..10 | ForEach-Object { New-Marker }
    _Expect 'marker generator produces distinct values' (($generated | Sort-Object -Unique).Count -eq 10)
    _Expect 'marker generator produces long tokens'     (($generated | Where-Object { $_.Length -ge 24 }).Count -eq 10)

    if ($script:_selfFailed -gt 0) {
        Write-Host ("SELFTEST FAIL ({0} case(s))" -f $script:_selfFailed) -ForegroundColor Red
        exit 1
    }
    Write-Host 'SELFTEST PASS' -ForegroundColor Green
    exit 0
}

function New-Marker {
    # 8 hex chars gives 2^32 possibilities — enough that N=100 calls in a
    # single burst have vanishing collision probability. Prefixed so the KQL
    # `contains` filter narrows the log window to this smoke's rows only.
    $rand = -join ((1..8) | ForEach-Object { '{0:x}' -f (Get-Random -Minimum 0 -Maximum 16) })
    return "retail-pulse-marker-$rand"
}

if ($SelfTest) { Invoke-SelfTest }

# ── Parameter validation for live mode ─────────────────────────────────────

$missing = [System.Collections.Generic.List[string]]::new()
if ([string]::IsNullOrWhiteSpace($Endpoint))        { $missing.Add('Endpoint (or $env:APIM_INFERENCE_ENDPOINT)') }
if ([string]::IsNullOrWhiteSpace($Deployment))      { $missing.Add('Deployment (or $env:APIM_DEPLOYMENT_NAME)') }
if ([string]::IsNullOrWhiteSpace($SubscriptionKey)) { $missing.Add('SubscriptionKey (or $env:APIM_SUBSCRIPTION_KEY)') }
if ([string]::IsNullOrWhiteSpace($WorkspaceId))     { $missing.Add('WorkspaceId (or $env:APIM_LOG_ANALYTICS_WORKSPACE_ID)') }

if ($missing.Count -gt 0) {
    Write-Host 'APIM LLM-log pairing smoke — missing required parameters:' -ForegroundColor Yellow
    foreach ($m in $missing) { Write-Host ("  - {0}" -f $m) -ForegroundColor Yellow }
    Write-Host ''
    Write-Host 'This script is DEFERRED for live verification: no live APIM instance /' -ForegroundColor Yellow
    Write-Host 'Log Analytics workspace is available from the environment producing it.' -ForegroundColor Yellow
    Write-Host 'Run with -SelfTest to exercise the offline pairing analysis, or supply' -ForegroundColor Yellow
    Write-Host 'the parameters above to run the live smoke.' -ForegroundColor Yellow
    exit 2
}

# `az` is only strictly required for the Log Analytics query (Invoke-KqlQuery).
# Do a soft check so the operator sees an actionable message if `az` is absent.
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Host '[skip] `az` CLI not found on PATH — required for `az monitor log-analytics query`.' -ForegroundColor Yellow
    exit 2
}

# ── APIM call ──────────────────────────────────────────────────────────────

function Invoke-ApimChat {
    param(
        [Parameter(Mandatory)][string]$Marker,
        [Parameter(Mandatory)][string]$Endpoint,
        [Parameter(Mandatory)][string]$Deployment,
        [Parameter(Mandatory)][string]$ApiVersion,
        [Parameter(Mandatory)][string]$SubscriptionKey,
        [Parameter(Mandatory)][int]$MaxTokens
    )

    $trimmedEndpoint = $Endpoint.TrimEnd('/')
    # If the caller passes an endpoint already ending in `/openai`, respect it;
    # otherwise the standard APIM inference product exposes the OpenAI-shaped
    # path at `/openai/deployments/...` so append it.
    if ($trimmedEndpoint -notmatch '/openai$') { $trimmedEndpoint = "$trimmedEndpoint/openai" }
    $uri = "$trimmedEndpoint/deployments/$Deployment/chat/completions?api-version=$ApiVersion"

    $body = @{
        max_tokens = $MaxTokens
        temperature = 0
        messages = @(
            @{ role = 'system'; content = 'Reply with a single word.' },
            @{ role = 'user';   content = "marker $Marker : ping" }
        )
    } | ConvertTo-Json -Depth 5

    $headers = @{
        'Ocp-Apim-Subscription-Key' = $SubscriptionKey
        'Content-Type'              = 'application/json'
    }

    try {
        $resp = Invoke-RestMethod -Method Post -Uri $uri -Headers $headers -Body $body -ErrorAction Stop
        return [PSCustomObject]@{ Marker = $Marker; Ok = $true; Status = 'ok'; Error = $null }
    }
    catch {
        return [PSCustomObject]@{ Marker = $Marker; Ok = $false; Status = 'apim-error'; Error = $_.Exception.Message }
    }
}

# ── Log Analytics query ────────────────────────────────────────────────────

function Invoke-KqlQuery {
    param(
        [Parameter(Mandatory)][string]$WorkspaceId,
        [Parameter(Mandatory)][string[]]$Markers,
        [Parameter(Mandatory)][int]$LookbackMinutes
    )
    # Build an `in (…)` list of markers so the KQL server-side filter narrows
    # aggressively rather than pulling every row and post-filtering client-side.
    $markerList = ($Markers | ForEach-Object { "'$_'" }) -join ','
    $kql = @"
ApiManagementGatewayLlmLog
| where TimeGenerated > ago(${LookbackMinutes}m)
| extend Marker = tostring(extract('retail-pulse-marker-([a-f0-9]{8})', 0, tostring(Content)))
| where isnotempty(Marker)
| where Marker in ($markerList)
| project TimeGenerated, Marker, SequenceNumber
| order by TimeGenerated asc
"@

    $raw = az monitor log-analytics query `
        --workspace $WorkspaceId `
        --analytics-query $kql `
        --output json 2>$null

    if (-not $raw) {
        return @()
    }
    return ($raw | ConvertFrom-Json)
}

# ── Live smoke ─────────────────────────────────────────────────────────────

Write-Host 'APIM LLM-log pairing smoke'
Write-Host ("  endpoint       = {0}" -f $Endpoint)
Write-Host ("  deployment     = {0}" -f $Deployment)
Write-Host ("  apiVersion     = {0}" -f $ApiVersion)
Write-Host ("  workspaceId    = {0}" -f $WorkspaceId)
Write-Host ("  count          = {0}" -f $Count)
Write-Host ("  settleSeconds  = {0}" -f $SettleSeconds)
Write-Host ("  maxTokens      = {0}" -f $MaxTokens)
Write-Host ''

$markers = 1..$Count | ForEach-Object { New-Marker }
$startedUtc = [DateTime]::UtcNow

Write-Host 'Firing direct APIM calls…'
$callResults = @()
foreach ($m in $markers) {
    $r = Invoke-ApimChat -Marker $m -Endpoint $Endpoint -Deployment $Deployment -ApiVersion $ApiVersion -SubscriptionKey $SubscriptionKey -MaxTokens $MaxTokens
    $callResults += $r
    if ($r.Ok) { Write-Host ("  [call] {0} ok" -f $m) -ForegroundColor Green }
    else       { Write-Host ("  [call] {0} FAIL ({1})" -f $m, $r.Error) -ForegroundColor Red }
}

$apimFailures = ($callResults | Where-Object { -not $_.Ok }).Count
if ($apimFailures -gt 0) {
    Write-Host ''
    Write-Host ("APIM SMOKE FAIL: {0}/{1} direct APIM calls failed before ingest analysis" -f $apimFailures, $Count) -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host ("Waiting {0}s for ApiManagementGatewayLlmLog ingest…" -f $SettleSeconds)
Start-Sleep -Seconds $SettleSeconds

# Query a lookback window that comfortably covers the burst + settle window
# plus a small margin, so an outlier that reached the workspace slightly late
# is still in scope.
$lookbackMinutes = [int][math]::Ceiling(($SettleSeconds + ([DateTime]::UtcNow - $startedUtc).TotalSeconds) / 60.0) + 2

Write-Host 'Querying ApiManagementGatewayLlmLog…'
$rows = Invoke-KqlQuery -WorkspaceId $WorkspaceId -Markers $markers -LookbackMinutes $lookbackMinutes
Write-Host ("  {0} row(s) matched" -f $rows.Count)

$analysis = Test-LlmLogPairing -Markers $markers -Rows $rows

Write-Host ''
Write-Host 'Pairing analysis'
Write-Host ("  markers         = {0}" -f $analysis.Markers)
Write-Host ("  paired 1:1      = {0}" -f $analysis.Paired)
Write-Host ("  missingResponse = {0}" -f $analysis.MissingResponse)
Write-Host ("  missingRequest  = {0}" -f $analysis.MissingRequest)
Write-Host ("  missingBoth     = {0}" -f $analysis.MissingBoth)

if (-not $analysis.Ok) {
    Write-Host ''
    Write-Host 'PAIRING FAIL — the following markers are not 1:1' -ForegroundColor Red
    foreach ($d in ($analysis.Details | Where-Object { -not $_.Paired })) {
        Write-Host ("  - {0}: hasRequest={1} hasResponse={2}" -f $d.Marker, $d.HasRequest, $d.HasResponse) -ForegroundColor Red
    }
    exit 1
}

Write-Host ''
Write-Host 'PAIRING PASS — every marker recorded both SequenceNumber=1 and SequenceNumber=0' -ForegroundColor Green
exit 0
