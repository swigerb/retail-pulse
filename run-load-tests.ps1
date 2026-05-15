<#
.SYNOPSIS
    Runs NBomber load tests against a running RetailPulse API instance.

.DESCRIPTION
    This script starts the load test scenarios defined in the RetailPulse.LoadTests project.
    Ensure the API is running before executing this script.

.PARAMETER BaseUrl
    Base URL of the running API instance. Defaults to http://localhost:5000.

.EXAMPLE
    .\run-load-tests.ps1
    .\run-load-tests.ps1 -BaseUrl "https://my-staging-api.azurewebsites.net"
#>
param(
    [string]$BaseUrl = "http://localhost:5000"
)

$ErrorActionPreference = "Stop"

Write-Host "=== RetailPulse Load Tests ===" -ForegroundColor Cyan
Write-Host "Target: $BaseUrl" -ForegroundColor Yellow

# Verify API is reachable
Write-Host "`nChecking API availability..." -ForegroundColor Gray
try {
    $health = Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 10
    Write-Host "  API is healthy: $($health | ConvertTo-Json -Compress)" -ForegroundColor Green
} catch {
    Write-Host "  ERROR: API not reachable at $BaseUrl/health" -ForegroundColor Red
    Write-Host "  Start the API first: dotnet run --project src/RetailPulse.Api" -ForegroundColor Yellow
    exit 1
}

# Run load tests
Write-Host "`nRunning load tests..." -ForegroundColor Cyan
$testProject = Join-Path $PSScriptRoot "tests" "RetailPulse.LoadTests"

dotnet test $testProject `
    --filter "Category=LoadTest" `
    --no-build `
    --logger "console;verbosity=detailed" `
    -- RunConfiguration.EnvironmentVariables.BASE_URL=$BaseUrl

$exitCode = $LASTEXITCODE

if ($exitCode -eq 0) {
    Write-Host "`n=== Load tests PASSED ===" -ForegroundColor Green
} else {
    Write-Host "`n=== Load tests FAILED ===" -ForegroundColor Red
}

# Report location
$reportPath = Join-Path $testProject "load-test-reports"
if (Test-Path $reportPath) {
    Write-Host "Reports saved to: $reportPath" -ForegroundColor Gray
}

exit $exitCode
