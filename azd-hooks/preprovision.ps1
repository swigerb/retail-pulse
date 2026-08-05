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

Write-Host 'All prerequisites met. Proceeding with provisioning...'
