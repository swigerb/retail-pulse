#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Provider build/test matrix orchestrator for Retail Pulse (Sprint 4, epic #27).

.DESCRIPTION
    Runs the repeatable, secret-free authentication provider matrix end to end using the
    repo's existing tooling (dotnet test + the frontend Vite build gate). Nothing here
    reaches a live tenant, uses a real secret, or reads a .env* file.

    Backend matrix (dotnet test, filtered to the Security + Deployment suites):
      * Mode resolution across Development and non-Development hosting.
      * Entra   — authenticated 200 / missing-or-bad token 401 / wrong scope-or-role 403,
                  plus hub authorization.
      * Anonymous — the two-route allow surface (POST /api/chat + anonymous session bootstrap)
                  and 403 for everything else; hubs denied.
      * GitHub  — confidential BFF login -> session token -> REST + hub authorization.
      * Fail-closed — unknown / missing / cross-provider configuration refuses to start.
      * Deployment contract — production Entra pins and frontend/backend mode parity.

    Frontend matrix (scripts/provider-build-matrix.mjs):
      * The fail-closed config gate for every mode (Entra valid/missing/placeholder/empty,
        GitHub, Anonymous, unknown, unset).
      * Real vite builds with SAFE SYNTHETIC PUBLIC identifiers (Entra by default; all three
        with -Full).

.PARAMETER Configuration
    dotnet build/test configuration. Default: Release.

.PARAMETER BackendOnly
    Run only the backend (dotnet) matrix.

.PARAMETER FrontendOnly
    Run only the frontend (Vite) matrix.

.PARAMETER Full
    Ask the frontend matrix to perform full vite builds for ALL three modes (default builds
    only the Entra production mode to avoid duplicate full builds in CI).

.PARAMETER GateOnly
    Ask the frontend matrix to run the config gate only (skip full vite builds). Fastest.

.EXAMPLE
    pwsh scripts/Test-ProviderMatrix.ps1
    Runs the backend suite (Release) and the frontend gate + Entra build.

.EXAMPLE
    pwsh scripts/Test-ProviderMatrix.ps1 -Full
    Runs the backend suite and full frontend builds for Entra, GitHub, and Anonymous.

.NOTES
    Read-only with respect to Azure and OAuth providers. Exits non-zero if any leg fails.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $BackendOnly,
    [switch] $FrontendOnly,
    [switch] $Full,
    [switch] $GateOnly
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
$results = [System.Collections.Generic.List[object]]::new()

function Invoke-Leg {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Action
    )
    Write-Host ""
    Write-Host "==== $Name ====" -ForegroundColor Cyan
    & $Action
    $code = $LASTEXITCODE
    $ok = ($code -eq 0)
    $results.Add([pscustomobject]@{ Leg = $Name; ExitCode = $code; Ok = $ok })
    if ($ok) {
        Write-Host "---- $Name : PASS ----" -ForegroundColor Green
    }
    else {
        Write-Host "---- $Name : FAIL (exit $code) ----" -ForegroundColor Red
    }
}

if (-not $FrontendOnly) {
    Invoke-Leg -Name 'Backend provider matrix (dotnet test: Security + Deployment)' -Action {
        Push-Location $repoRoot
        try {
            dotnet test 'RetailPulse.slnx' `
                --configuration $Configuration `
                --filter 'FullyQualifiedName~RetailPulse.Tests.Security|FullyQualifiedName~RetailPulse.Tests.Deployment' `
                --nologo
        }
        finally { Pop-Location }
    }
}

if (-not $BackendOnly) {
    Invoke-Leg -Name 'Frontend provider matrix (config gate + vite builds)' -Action {
        Push-Location (Join-Path $repoRoot 'src/RetailPulse.Web')
        try {
            $matrixArgs = @('scripts/provider-build-matrix.mjs')
            if ($GateOnly) { $matrixArgs += '--gate-only' }
            elseif ($Full) { $matrixArgs += '--full' }
            node @matrixArgs
        }
        finally { Pop-Location }
    }
}

Write-Host ""
Write-Host "==== Provider matrix summary ====" -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-Host

$failed = @($results | Where-Object { -not $_.Ok })
if ($failed.Count -gt 0) {
    Write-Host "Provider matrix FAILED: $($failed.Count) leg(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host "Provider matrix: ALL LEGS PASSED." -ForegroundColor Green
exit 0
