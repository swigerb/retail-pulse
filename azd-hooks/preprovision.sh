#!/bin/sh
# Pre-provision hook (POSIX/sh) — validates prerequisites before provision.
#
# The production frontend build runs during the frontend (Static Web Apps)
# service deploy phase, which happens AFTER provisioning. That ordering is
# required: the Vite build reads VITE_API_ORIGIN (the provisioned ACA API
# origin) from the azd environment, and the API FQDN does not exist yet at
# preprovision time. Do NOT build the frontend here — azd builds and deploys
# dist/ after provision, so building now would be both premature and redundant.

set -e

echo 'Checking prerequisites...'

for cmd in az azd dotnet node npm pwsh; do
    if ! command -v "$cmd" > /dev/null 2>&1; then
        echo "Required command '$cmd' is not installed or is not on PATH." >&2
        exit 1
    fi
done

# ── Auth-mode fail-closed guard ────────────────────────────────────────────
# The live deployment defaults to the Entra provider (see infra/main.bicep's
# VITE_AUTH_MODE output and the postprovision Authentication__Mode pin). An
# Entra deployment MUST carry non-empty, non-placeholder tenant + client IDs,
# otherwise the SPA would build a silent, unauthenticated shell. Fail the whole
# provision here — before any resource is created — when they are missing.
# GitHub/Anonymous deployments set RETAIL_PULSE_AUTH_MODE explicitly and skip
# this Entra-specific check (they never require Entra IDs).
AUTH_MODE="${RETAIL_PULSE_AUTH_MODE:-Entra}"

is_entra_id_placeholder() {
    value="$1"
    # trim leading/trailing whitespace
    trimmed="$(printf '%s' "$value" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
    [ -z "$trimmed" ] && return 0
    [ "$trimmed" = '00000000-0000-0000-0000-000000000000' ] && return 0
    # angle-bracket template, embedded whitespace, or a well-known scaffold token → placeholder.
    printf '%s' "$trimmed" | grep -Eq '[<>[:space:]]' && return 0
    printf '%s' "$trimmed" | grep -Eiq '(your[-_]?|placeholder|changeme|example|todo|xxxx+|fixme)' && return 0
    return 1
}

lower_auth_mode="$(printf '%s' "$AUTH_MODE" | tr '[:upper:]' '[:lower:]')"
if [ "$lower_auth_mode" = 'entra' ]; then
    if is_entra_id_placeholder "${RETAIL_PULSE_ENTRA_TENANT_ID:-}" || \
       is_entra_id_placeholder "${RETAIL_PULSE_ENTRA_CLIENT_ID:-}"; then
        echo "Entra is the selected auth mode but RETAIL_PULSE_ENTRA_TENANT_ID and/or" >&2
        echo "RETAIL_PULSE_ENTRA_CLIENT_ID are empty or placeholders. Set both to the real" >&2
        echo "single-tenant app-registration GUIDs (e.g. 'azd env set RETAIL_PULSE_ENTRA_TENANT_ID <guid>')" >&2
        echo "before provisioning. Refusing to provision an Entra deployment with empty auth configuration." >&2
        exit 1
    fi
    echo "Auth mode 'Entra' validated: tenant/client IDs are present."
else
    echo "Auth mode '$AUTH_MODE' selected: skipping the Entra-specific ID check."
fi

# ── APIM backend-pool fail-closed guard ────────────────────────────────────
if [ "$(printf '%s' "${AZURE_APIM_BACKEND_POOL_ENABLED:-false}" | tr '[:upper:]' '[:lower:]')" = 'true' ]; then
    primary_account="${AZURE_OPENAI_ACCOUNT_NAME:-aiagents-3rsdmhyb}"
    secondary_account="${AZURE_OPENAI_SECONDARY_ACCOUNT_NAME:-}"
    if [ -z "$secondary_account" ]; then
        echo 'AZURE_APIM_BACKEND_POOL_ENABLED=true requires AZURE_OPENAI_SECONDARY_ACCOUNT_NAME.' >&2
        exit 1
    fi
    if [ "$(printf '%s' "$secondary_account" | tr '[:upper:]' '[:lower:]')" = "$(printf '%s' "$primary_account" | tr '[:upper:]' '[:lower:]')" ]; then
        echo 'The secondary Azure AI account must differ from AZURE_OPENAI_ACCOUNT_NAME.' >&2
        exit 1
    fi
    echo 'APIM backend pool validated: primary and secondary accounts are distinct.'
fi

# Bootstrap the gateway app before ARM so Bicep receives the exact v2 JWT audience (the
# application client-id GUID), never the distinct api:// resource identifier.
loaded_gateway_audience="${AZURE_MCP_GATEWAY_AUDIENCE:-}"
pwsh -NoProfile -File "$(dirname "$0")/../scripts/Setup-McpGatewayIdentity.ps1" -BootstrapOnly
persisted_gateway_audience="$(azd env get-value AZURE_MCP_GATEWAY_AUDIENCE)"
if ! printf '%s' "$persisted_gateway_audience" | grep -Eq '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$'; then
    echo 'The MCP gateway bootstrap did not persist a valid AZURE_MCP_GATEWAY_AUDIENCE client-id GUID.' >&2
    exit 1
fi
if [ "$loaded_gateway_audience" != "$persisted_gateway_audience" ]; then
    echo "MCP gateway identity bootstrap completed and persisted audience $persisted_gateway_audience." >&2
    echo 'azd does not hot-reload values written by preprovision hooks; rerun the same azd provision/up command so ARM receives the exact client-id audience. No Azure resources were provisioned by this run.' >&2
    exit 1
fi

echo 'All prerequisites met. Proceeding with provisioning...'
