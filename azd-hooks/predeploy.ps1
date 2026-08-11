$ErrorActionPreference = 'Stop'

# Pre-deploy hook (Windows/pwsh) — serializes the shared-project build before
# azd's per-service deploy phase.
#
# Root cause (issue #67): `azd up` / `azd deploy` builds/publishes the api,
# mcpserver, and teamsbot container-app services IN PARALLEL. All three
# reference the shared `RetailPulse.ServiceDefaults` project. Each parallel
# `dotnet publish` independently restores + builds that shared project and
# writes its generated `RetailPulse.ServiceDefaults.sourcelink.json` into the
# SAME shared `obj/` intermediate directory (no per-service
# BaseIntermediateOutputPath isolation) — three writers racing on one file.
# The loser(s) fail with a file-lock/access-denied error, so `dotnet publish`
# for teamsbot/api/mcp intermittently fails mid-`azd up`. When that happened
# in the P0 incident, the operator worked around it with a manual sequential
# redeploy (api, then mcpserver, then teamsbot, then frontend) — a recovery
# path that bypasses this hook and, per the incident timeline, is suspected
# of skipping/short-circuiting later provisioning steps.
#
# Fix: run a single, SEQUENTIAL `dotnet restore` + `dotnet build` of the whole
# solution here, before azd's parallel per-service publish starts. This
# fully populates and up-to-date-checks every project's `obj/` output
# (including RetailPulse.ServiceDefaults' sourcelink.json) exactly once, with
# no concurrent writers. The later parallel `dotnet publish` calls MSBuild
# performs for each service then see that shared project as already built and
# incrementally skip re-generating it, so no writer race can occur — the
# canonical `azd up` / `azd deploy` path becomes reliable without requiring
# manual sequential intervention.
Write-Host 'Pre-deploy: building the full solution once (sequential) to avoid a shared-project publish race...'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'RetailPulse.slnx'

& dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw "dotnet restore '$solution' failed (exit $LASTEXITCODE)." }

& dotnet build $solution --no-restore --configuration Release
if ($LASTEXITCODE -ne 0) { throw "dotnet build '$solution' failed (exit $LASTEXITCODE)." }

Write-Host 'Pre-deploy: solution build complete. Parallel per-service publish can now proceed without racing on shared intermediate output.'
