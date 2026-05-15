<#
.SYNOPSIS
    Runs Stryker.NET mutation testing against RetailPulse.Api.

.DESCRIPTION
    Executes mutation tests targeting routing, validation, and caching logic.
    Requires dotnet-stryker tool to be installed:
      dotnet tool install -g dotnet-stryker

.EXAMPLE
    .\tests\run-mutation-tests.ps1
#>

$ErrorActionPreference = "Stop"

Write-Host "=== RetailPulse Mutation Testing (Stryker.NET) ===" -ForegroundColor Cyan

# Check if stryker is installed
$strykerInstalled = dotnet tool list -g | Select-String "dotnet-stryker"
if (-not $strykerInstalled) {
    Write-Host "Installing dotnet-stryker globally..." -ForegroundColor Yellow
    dotnet tool install -g dotnet-stryker
}

# Navigate to test project directory
$testProject = Join-Path $PSScriptRoot "RetailPulse.Tests"
Push-Location $testProject

try {
    Write-Host "`nRunning mutation tests..." -ForegroundColor Cyan
    Write-Host "  Target: RetailPulse.Api (Agents/Routing, Validation, Caching)" -ForegroundColor Gray
    Write-Host "  Thresholds: high=80%, low=60%, break=50%" -ForegroundColor Gray

    dotnet stryker --config-file "../../stryker-config.json"

    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        Write-Host "`n=== Mutation testing PASSED ===" -ForegroundColor Green
    } else {
        Write-Host "`n=== Mutation testing FAILED (below break threshold) ===" -ForegroundColor Red
    }

    # Report location
    $reportPath = Join-Path $testProject "StrykerOutput"
    if (Test-Path $reportPath) {
        $latestReport = Get-ChildItem $reportPath -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($latestReport) {
            Write-Host "HTML report: $($latestReport.FullName)\reports\mutation-report.html" -ForegroundColor Gray
        }
    }
} finally {
    Pop-Location
}

exit $exitCode
