#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Backend authentication provider matrix with machine-readable results and a fail-closed gate
    (Sprint 4, epic #27).

.DESCRIPTION
    Runs the backend authentication/authorization provider matrix — the Security + Deployment
    xUnit suites — via `dotnet test`, emits a machine-readable TRX, then parses that TRX and
    ENFORCES a conservative gate so CI can never "pass" on a silently empty or partially-broken
    run:

        * failed  == 0   (any failed or errored test fails the gate)
        * error   == 0
        * executed > 0   (a zero-match / zero-executed run is treated as a HARD FAILURE, not a
                          vacuous pass — a bad --filter must never look green)
        * total   >= -MinimumCount (default 400; the suite currently yields ~477, so this floor
                          catches a filter or discovery regression that silently drops suites)

    Nothing here reaches a live tenant, uses a real secret, or reads a .env* file. The matrix is
    driven entirely by in-process synthetic configuration inside the test host.

    The TRX is written under -ResultsDirectory (default ./test-results-matrix, which is
    .gitignored) so it is uploadable as a CI artifact but never committed.

.PARAMETER Configuration
    dotnet test configuration. Default: Release.

.PARAMETER Filter
    The dotnet test --filter expression selecting the matrix suites. Default selects the
    Security and Deployment suites.

.PARAMETER Project
    The test project to run. Default: tests/RetailPulse.Tests/RetailPulse.Tests.csproj. Targeting
    one project guarantees exactly one TRX (running the whole solution lets 0-match projects
    overwrite the shared TRX filename non-deterministically).

.PARAMETER MinimumCount
    Conservative minimum total test count. The gate fails if fewer tests are discovered/run.
    Default: 400.

.PARAMETER ResultsDirectory
    Directory for the emitted TRX. Default: ./test-results-matrix (gitignored).

.PARAMETER TrxFileName
    TRX file name. Default: backend-auth-matrix.trx.

.PARAMETER NoBuild
    Pass --no-build to dotnet test (assumes a prior build/restore).

.PARAMETER NoRestore
    Pass --no-restore to dotnet test.

.EXAMPLE
    pwsh scripts/Test-BackendAuthMatrix.ps1
    Runs the Security + Deployment matrix, writes test-results-matrix/backend-auth-matrix.trx,
    and enforces >=400 tests with zero failures.

.NOTES
    Read-only with respect to Azure and OAuth providers. Exits non-zero on any gate violation.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $Filter = 'FullyQualifiedName~RetailPulse.Tests.Security|FullyQualifiedName~RetailPulse.Tests.Deployment',

    [string] $Project = 'tests/RetailPulse.Tests/RetailPulse.Tests.csproj',

    [int] $MinimumCount = 400,

    [string] $ResultsDirectory = 'test-results-matrix',

    [string] $TrxFileName = 'backend-auth-matrix.trx',

    [switch] $NoBuild,

    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Find-RepoRoot {
    $dir = $PSScriptRoot
    while ($dir) {
        if (Test-Path (Join-Path $dir 'RetailPulse.slnx')) { return $dir }
        $parent = Split-Path $dir -Parent
        if ($parent -eq $dir) { break }
        $dir = $parent
    }
    throw 'Could not locate repo root (RetailPulse.slnx not found above this script).'
}

$repoRoot = Find-RepoRoot

# Resolve the results directory (relative paths are relative to the repo root) and the TRX path.
$resultsDir = if ([System.IO.Path]::IsPathRooted($ResultsDirectory)) { $ResultsDirectory }
else { Join-Path $repoRoot $ResultsDirectory }
$null = New-Item -ItemType Directory -Path $resultsDir -Force
$trxPath = Join-Path $resultsDir $TrxFileName
if (Test-Path $trxPath) { Remove-Item $trxPath -Force }

# Target a single test project so exactly one TRX is produced (running the whole solution makes
# the 0-match projects overwrite the same TRX filename non-deterministically). Relative paths are
# resolved against the repo root.
$projectPath = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $repoRoot $Project }
if (-not (Test-Path $projectPath)) { throw "Test project not found: $projectPath" }

Write-Host '==== Backend auth provider matrix (Security + Deployment) ====' -ForegroundColor Cyan
Write-Host "  Project   : $projectPath"
Write-Host "  Filter    : $Filter"
Write-Host "  TRX       : $trxPath"
Write-Host "  Min count : $MinimumCount (conservative floor)"

$dotnetArgs = @(
    'test', $projectPath,
    '--configuration', $Configuration,
    '--filter', $Filter,
    '--logger', "trx;LogFileName=$TrxFileName",
    '--results-directory', $resultsDir,
    '--nologo'
)
if ($NoBuild) { $dotnetArgs += '--no-build' }
if ($NoRestore) { $dotnetArgs += '--no-restore' }

Push-Location $repoRoot
try {
    & dotnet @dotnetArgs
    $testExit = $LASTEXITCODE
}
finally { Pop-Location }

# ── Parse the machine-readable TRX and enforce the conservative gate ──────────
if (-not (Test-Path $trxPath)) {
    Write-Host "[FAIL] No TRX produced at '$trxPath' — the matrix did not run (state undetermined)." -ForegroundColor Red
    exit 1
}

[xml]$trx = Get-Content -Path $trxPath -Raw
$counters = $trx.TestRun.ResultSummary.Counters
if (-not $counters) {
    Write-Host "[FAIL] TRX '$trxPath' has no ResultSummary/Counters — cannot verify the matrix." -ForegroundColor Red
    exit 1
}

$total = [int]$counters.total
$executed = [int]$counters.executed
$passed = [int]$counters.passed
$failed = [int]$counters.failed
$errors = [int]$counters.error

Write-Host ''
Write-Host '==== Backend matrix results (from TRX) ====' -ForegroundColor Cyan
Write-Host ("  total={0} executed={1} passed={2} failed={3} error={4}" -f $total, $executed, $passed, $failed, $errors)

$violations = [System.Collections.Generic.List[string]]::new()
if ($testExit -ne 0) { $violations.Add("dotnet test exited $testExit") }
if ($executed -le 0) { $violations.Add('zero tests executed (zero-match run is a hard failure, not a pass)') }
if ($total -lt $MinimumCount) { $violations.Add("total $total is below the conservative minimum $MinimumCount") }
if ($failed -gt 0) { $violations.Add("$failed test(s) failed") }
if ($errors -gt 0) { $violations.Add("$errors test(s) errored") }

if ($violations.Count -gt 0) {
    Write-Host ''
    foreach ($v in $violations) { Write-Host "  [FAIL] $v" -ForegroundColor Red }
    Write-Host "Backend auth matrix FAILED the gate." -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host ("Backend auth matrix PASSED: {0} tests, 0 failures (>= {1} required)." -f $total, $MinimumCount) -ForegroundColor Green
exit 0
