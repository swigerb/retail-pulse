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

for cmd in az dotnet node npm; do
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

echo 'All prerequisites met. Proceeding with provisioning...'
