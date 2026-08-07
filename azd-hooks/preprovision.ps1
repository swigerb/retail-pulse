$ErrorActionPreference = 'Stop'

# Pre-provision hook (Windows/pwsh) — validates prerequisites before provision.
#
# The production frontend build runs during the frontend (Static Web Apps)
# service deploy phase, which happens AFTER provisioning. That ordering is
# required: the Vite build reads VITE_API_ORIGIN (the provisioned ACA API
# origin) from the azd environment, and the API FQDN does not exist yet at
# preprovision time. Do NOT build the frontend here — azd builds and deploys
# dist/ after provision, so building now would be both premature and redundant.

Write-Host 'Checking prerequisites...'

foreach ($command in 'az', 'dotnet', 'node', 'npm') {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required command '$command' is not installed or is not on PATH."
    }
}

# ── Auth-mode fail-closed guard ────────────────────────────────────────────
# The live deployment defaults to the Entra provider (see infra/main.bicep's
# VITE_AUTH_MODE output and the postprovision Authentication__Mode pin). An
# Entra deployment MUST carry non-empty, non-placeholder tenant + client IDs,
# otherwise the SPA would build a silent, unauthenticated shell. Fail the whole
# provision here — before any resource is created — when they are missing.
# GitHub/Anonymous deployments set RETAIL_PULSE_AUTH_MODE explicitly and skip
# this Entra-specific check (they never require Entra IDs).
$authMode = $env:RETAIL_PULSE_AUTH_MODE
if ([string]::IsNullOrWhiteSpace($authMode)) { $authMode = 'Entra' }

function Test-EntraIdPlaceholder([string] $value) {
    $v = ($value ?? '').Trim()
    if ([string]::IsNullOrWhiteSpace($v)) { return $true }
    if ($v -eq '00000000-0000-0000-0000-000000000000') { return $true }
    if ($v -match '[<>]' -or $v -match '\s') { return $true }
    if ($v -match '(?i)(your[-_]?|placeholder|changeme|example|todo|xxxx+|fixme)') { return $true }
    return $false
}

if ($authMode -ieq 'Entra') {
    $tenant = $env:RETAIL_PULSE_ENTRA_TENANT_ID
    $client = $env:RETAIL_PULSE_ENTRA_CLIENT_ID
    if ((Test-EntraIdPlaceholder $tenant) -or (Test-EntraIdPlaceholder $client)) {
        throw "Entra is the selected auth mode but RETAIL_PULSE_ENTRA_TENANT_ID and/or " +
              "RETAIL_PULSE_ENTRA_CLIENT_ID are empty or placeholders. Set both to the real " +
              "single-tenant app-registration GUIDs (e.g. ``azd env set RETAIL_PULSE_ENTRA_TENANT_ID <guid>``) " +
              "before provisioning. Refusing to provision an Entra deployment with empty auth configuration."
    }
    Write-Host "Auth mode 'Entra' validated: tenant/client IDs are present."
}
else {
    Write-Host "Auth mode '$authMode' selected: skipping the Entra-specific ID check."
}

Write-Host 'All prerequisites met. Proceeding with provisioning...'
