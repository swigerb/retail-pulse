#Requires -Version 7.0
<#
.SYNOPSIS
    One-click local setup and launch for Patron Pulse.

.DESCRIPTION
    Restores NuGet packages, installs npm dependencies, builds the .NET solution,
    and optionally starts the Aspire orchestrator and React frontend.

.PARAMETER SkipBuild
    Skip the .NET build step (useful if already built).

.PARAMETER StartAll
    Start the Aspire AppHost and React frontend after building.

.PARAMETER ApiKey
    OpenAI API key to configure via user-secrets. If not provided, skips key setup.

.PARAMETER Endpoint
    OpenAI endpoint URL (for Azure OpenAI). Defaults to OpenAI's public endpoint.

.EXAMPLE
    .\deploy\deploy.ps1
    # Restore, install, and build only

.EXAMPLE
    .\deploy\deploy.ps1 -StartAll
    # Build and start everything

.EXAMPLE
    .\deploy\deploy.ps1 -StartAll -ApiKey "sk-your-key-here"
    # Configure API key, build, and start everything
#>

param(
    [switch]$SkipBuild,
    [switch]$StartAll,
    [string]$ApiKey,
    [string]$Endpoint
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Patron Pulse — Local Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# ── Prerequisites Check ──────────────────────────────────────────────

Write-Host "[1/6] Checking prerequisites..." -ForegroundColor Yellow

$dotnetVersion = dotnet --version 2>$null
if (-not $dotnetVersion) {
    Write-Error ".NET SDK not found. Install from https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
}
Write-Host "  .NET SDK: $dotnetVersion" -ForegroundColor Green

$nodeVersion = node --version 2>$null
if (-not $nodeVersion) {
    Write-Error "Node.js not found. Install from https://nodejs.org/"
    exit 1
}
Write-Host "  Node.js:  $nodeVersion" -ForegroundColor Green

$npmVersion = npm --version 2>$null
Write-Host "  npm:      $npmVersion" -ForegroundColor Green

# ── API Key Configuration ────────────────────────────────────────────

if ($ApiKey) {
    Write-Host ""
    Write-Host "[2/6] Configuring OpenAI API key..." -ForegroundColor Yellow

    Push-Location "$RepoRoot\src\RetailPulse.Api"
    dotnet user-secrets init 2>$null
    dotnet user-secrets set "OpenAI:ApiKey" $ApiKey
    if ($Endpoint) {
        dotnet user-secrets set "OpenAI:Endpoint" $Endpoint
    }
    Pop-Location

    Write-Host "  API key configured via user-secrets" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "[2/6] Skipping API key setup (use -ApiKey to configure)" -ForegroundColor DarkGray
}

# ── NuGet Restore ────────────────────────────────────────────────────

Write-Host ""
Write-Host "[3/6] Restoring NuGet packages..." -ForegroundColor Yellow

dotnet restore "$RepoRoot\RetailPulse.slnx" --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Error "NuGet restore failed"
    exit 1
}
Write-Host "  NuGet packages restored" -ForegroundColor Green

# ── npm Install ──────────────────────────────────────────────────────

Write-Host ""
Write-Host "[4/6] Installing npm packages..." -ForegroundColor Yellow

Push-Location "$RepoRoot\src\RetailPulse.Web"
npm install --silent 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Error "npm install failed for RetailPulse.Web"
    Pop-Location
    exit 1
}
Pop-Location
Write-Host "  npm packages installed (RetailPulse.Web)" -ForegroundColor Green

# ── Build ────────────────────────────────────────────────────────────

if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "[5/6] Building solution..." -ForegroundColor Yellow

    dotnet build "$RepoRoot\RetailPulse.slnx" --configuration Release --verbosity quiet --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed"
        exit 1
    }
    Write-Host "  Solution built successfully" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "[5/6] Skipping build (-SkipBuild)" -ForegroundColor DarkGray
}

# ── Start Services ───────────────────────────────────────────────────

if ($StartAll) {
    Write-Host ""
    Write-Host "[6/6] Starting services..." -ForegroundColor Yellow
    Write-Host ""

    Write-Host "  Starting Aspire AppHost (API :5100, MCP :5200, Frontend :5173)..." -ForegroundColor Cyan
    $aspireJob = Start-Job -ScriptBlock {
        param($root)
        Set-Location $root
        dotnet run --project "src\RetailPulse.AppHost" --no-build --configuration Release
    } -ArgumentList $RepoRoot

    Start-Sleep -Seconds 5

    Write-Host "  Starting React frontend (:5173)..." -ForegroundColor Cyan
    $webJob = Start-Job -ScriptBlock {
        param($root)
        Set-Location "$root\src\RetailPulse.Web"
        npm run dev
    } -ArgumentList $RepoRoot

    Start-Sleep -Seconds 3

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  Patron Pulse is running!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "  Dashboard:       http://localhost:5173" -ForegroundColor White
    Write-Host "  API:             http://localhost:5100" -ForegroundColor White
    Write-Host "  MCP Server:      http://localhost:5200" -ForegroundColor White
    Write-Host "  Aspire Dashboard: check terminal for login URL" -ForegroundColor White
    Write-Host ""
    Write-Host "  Press Ctrl+C to stop all services" -ForegroundColor DarkGray
    Write-Host ""

    try {
        Wait-Job -Job $aspireJob, $webJob -Any
    } finally {
        Write-Host "Stopping services..." -ForegroundColor Yellow
        Stop-Job -Job $aspireJob, $webJob -ErrorAction SilentlyContinue
        Remove-Job -Job $aspireJob, $webJob -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Host ""
    Write-Host "[6/6] Skipping service start (use -StartAll to launch)" -ForegroundColor DarkGray

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  Setup complete!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "  To start manually:" -ForegroundColor White
    Write-Host "    Terminal 1: dotnet run --project src/RetailPulse.AppHost" -ForegroundColor White
    Write-Host "    Terminal 2: cd src/RetailPulse.Web && npm run dev" -ForegroundColor White
    Write-Host ""
    Write-Host "  Then open: http://localhost:5173" -ForegroundColor White
    Write-Host ""
}
