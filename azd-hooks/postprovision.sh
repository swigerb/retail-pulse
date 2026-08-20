#!/bin/sh
# Post-provision hook (POSIX/sh) — parity with postprovision.ps1. See that file
# for the full rationale.
#
# Wires ACR pull auth for every Container App to its own system-assigned managed
# identity, with no registry secrets, and links the API container app to the
# Static Web App backend. Doing these here (after provision) breaks the
# ARM/Bicep identity-sequencing cycle deterministically and is fully idempotent:
# every `azd up` / `azd provision` re-asserts the desired state.
#
# Runtime configuration for the API (APIM endpoint, subscription-key secret,
# Entra auth mode, allowed origins, ASPNETCORE_ENVIRONMENT=Production) now lives
# in `infra/modules/container-apps.bicep`. It used to live here as `az
# containerapp update --set-env-vars` calls, which meant a re-provision that
# recreated the API resource from Bicep left the active revision with no APIM
# wiring (the §7 regression on issue #51). Keeping runtime config in Bicep
# closes that loop — this hook now only handles the identity/registry/backend
# links that genuinely require post-resource-creation steps.

set -eu

require_env() {
    # $1 = variable name; fail loudly if unset or empty.
    eval "_value=\${$1:-}"
    if [ -z "$_value" ]; then
        echo "Required azd environment value '$1' is missing. Ensure 'azd provision' captured the infra outputs before this hook ran." >&2
        exit 1
    fi
}

require_env AZURE_RESOURCE_GROUP
require_env AZURE_CONTAINER_REGISTRY_NAME
require_env AZURE_CONTAINER_REGISTRY_ENDPOINT
require_env AZURE_CONTAINER_REGISTRY_RESOURCE_ID
require_env AZURE_API_APP_NAME
require_env AZURE_MCP_SERVER_APP_NAME
require_env AZURE_TEAMS_BOT_APP_NAME
require_env AZURE_STATIC_WEB_APP_NAME
require_env AZURE_LOCATION

resource_group="$AZURE_RESOURCE_GROUP"
registry_name="$AZURE_CONTAINER_REGISTRY_NAME"
registry_server="$AZURE_CONTAINER_REGISTRY_ENDPOINT"
registry_id="$AZURE_CONTAINER_REGISTRY_RESOURCE_ID"

echo "Configuring ACR pull via system-assigned identity on registry '$registry_name'..."

for app in "$AZURE_API_APP_NAME" "$AZURE_MCP_SERVER_APP_NAME" "$AZURE_TEAMS_BOT_APP_NAME"; do
    echo "-> $app"

    principal_id=$(az containerapp show \
        --name "$app" \
        --resource-group "$resource_group" \
        --query 'identity.principalId' \
        --output tsv)
    if [ -z "$principal_id" ]; then
        echo "Container app '$app' has no system-assigned identity principalId. Expected identity.type = SystemAssigned." >&2
        exit 1
    fi

    # Idempotent AcrPull grant. The JMESPath filter matches on principalId
    # client-side so we avoid an AAD Graph lookup on a freshly created identity.
    existing=$(az role assignment list \
        --scope "$registry_id" \
        --query "[?principalId=='$principal_id' && roleDefinitionName=='AcrPull'].id" \
        --output tsv)

    if [ -z "$existing" ]; then
        echo "   granting AcrPull to $principal_id"
        az role assignment create \
            --assignee-object-id "$principal_id" \
            --assignee-principal-type ServicePrincipal \
            --role AcrPull \
            --scope "$registry_id" \
            --output none
    else
        echo '   AcrPull already present'
    fi

    # Bind the app's registry auth to its system identity — no admin creds/secrets.
    az containerapp registry set \
        --name "$app" \
        --resource-group "$resource_group" \
        --server "$registry_server" \
        --identity system \
        --output none

    echo '   registry auth bound to system identity'
done

# SWA proxies relative /api requests to ACA. SignalR intentionally bypasses
# this link via VITE_API_ORIGIN because linked backends do not proxy WebSockets.
api_resource_id=$(az containerapp show \
    --name "$AZURE_API_APP_NAME" \
    --resource-group "$resource_group" \
    --query id \
    --output tsv)

linked=$(az staticwebapp backends show \
    --name "$AZURE_STATIC_WEB_APP_NAME" \
    --resource-group "$resource_group" \
    --query "[?backendResourceId=='$api_resource_id'].id" \
    --output tsv)

if [ -z "$linked" ]; then
    az staticwebapp backends link \
        --name "$AZURE_STATIC_WEB_APP_NAME" \
        --resource-group "$resource_group" \
        --backend-resource-id "$api_resource_id" \
        --backend-region "$AZURE_LOCATION" \
        --output none
fi

# Linking enables the SWA identity provider on the /api proxy path, but ACA platform
# (Easy Auth) is deliberately kept DISABLED: it would issue login redirects that break
# bearer-token REST/SignalR clients calling ACA directly. The in-process Entra JwtBearer
# handler (Security__RequireAuth=true, set in Bicep) is the real security boundary.
az containerapp auth update \
    --name "$AZURE_API_APP_NAME" \
    --resource-group "$resource_group" \
    --enabled false \
    --output none

echo 'Post-provision configuration complete: secretless ACR pull, SWA linked backend, and ACA platform-auth disabled.'

# ── Optional Content Safety RBAC (issue #100) ──────────────────────────────
# When AZURE_CONTENT_SAFETY_ENABLED=true (captured from the infra output),
# grant every container app's system-assigned identity `Cognitive Services
# User` on the Content Safety account so the API can call AnalyzeText /
# shieldPrompt with a managed-identity token — no keys anywhere in config.
# The assignment is idempotent: the JMESPath filter matches on principalId
# client-side, so a re-provision never duplicates the role.
content_safety_enabled=$(printf '%s' "${AZURE_CONTENT_SAFETY_ENABLED:-}" | tr '[:upper:]' '[:lower:]' | tr -d '[:space:]')
content_safety_resource_id="${AZURE_CONTENT_SAFETY_RESOURCE_ID:-}"
if [ "$content_safety_enabled" = "true" ] && [ -n "$content_safety_resource_id" ]; then
    echo ''
    echo 'Granting Cognitive Services User on the Content Safety account to each container app system identity...'
    for app in "$AZURE_API_APP_NAME" "$AZURE_MCP_SERVER_APP_NAME" "$AZURE_TEAMS_BOT_APP_NAME"; do
        cs_principal_id=$(az containerapp show \
            --name "$app" \
            --resource-group "$resource_group" \
            --query 'identity.principalId' \
            --output tsv)
        if [ -z "$cs_principal_id" ]; then
            continue
        fi

        cs_existing=$(az role assignment list \
            --scope "$content_safety_resource_id" \
            --query "[?principalId=='$cs_principal_id' && roleDefinitionName=='Cognitive Services User'].id" \
            --output tsv)

        if [ -z "$cs_existing" ]; then
            echo "-> $app : granting Cognitive Services User to $cs_principal_id"
            az role assignment create \
                --assignee-object-id "$cs_principal_id" \
                --assignee-principal-type ServicePrincipal \
                --role 'Cognitive Services User' \
                --scope "$content_safety_resource_id" \
                --output none
        else
            echo "-> $app : Cognitive Services User already present"
        fi
    done
fi

# ── Optional Azure AI Search RBAC (issue #103) ─────────────────────────────
# When AZURE_AI_SEARCH_ENABLED=true, grant every container app's system-assigned
# identity the two roles required by the API:
#   * "Search Service Contributor" — needed once, so the app can auto-create /
#     inspect the index (Program.cs ensures the index exists at first probe).
#   * "Search Index Data Contributor" — required for ingest + delete +
#     document CRUD against the target index.
# Both assignments are idempotent (JMESPath filter on principalId + role name
# client-side), so a re-provision never duplicates the role.
ai_search_enabled=$(printf '%s' "${AZURE_AI_SEARCH_ENABLED:-}" | tr '[:upper:]' '[:lower:]' | tr -d '[:space:]')
ai_search_resource_id="${AZURE_AI_SEARCH_RESOURCE_ID:-}"
if [ "$ai_search_enabled" = "true" ] && [ -n "$ai_search_resource_id" ]; then
    echo ''
    echo 'Granting Azure AI Search roles to each container app system identity...'
    for app in "$AZURE_API_APP_NAME" "$AZURE_MCP_SERVER_APP_NAME" "$AZURE_TEAMS_BOT_APP_NAME"; do
        search_principal_id=$(az containerapp show \
            --name "$app" \
            --resource-group "$resource_group" \
            --query 'identity.principalId' \
            --output tsv)
        if [ -z "$search_principal_id" ]; then
            continue
        fi

        for role in 'Search Service Contributor' 'Search Index Data Contributor'; do
            search_existing=$(az role assignment list \
                --scope "$ai_search_resource_id" \
                --query "[?principalId=='$search_principal_id' && roleDefinitionName=='$role'].id" \
                --output tsv)

            if [ -z "$search_existing" ]; then
                echo "-> $app : granting '$role' to $search_principal_id"
                az role assignment create \
                    --assignee-object-id "$search_principal_id" \
                    --assignee-principal-type ServicePrincipal \
                    --role "$role" \
                    --scope "$ai_search_resource_id" \
                    --output none
            else
                echo "-> $app : '$role' already present"
            fi
        done
    done
fi

# ── Mandatory APIM AI Gateway live gate (issue #67) ────────────────────────
# See postprovision.ps1 for the full rationale: a successful `azd provision`
# only means the ARM deployments succeeded, not that the AI Gateway
# invariants (backend, policy, token-limit, emit-token-metric, diagnostics,
# RBAC, ACA wiring) are correct on the live resources. Run the verifier here
# so a live invariant failure fails `azd up`/`azd provision` itself.
# Verify-ApimAiGateway.ps1 is pwsh (cross-platform); invoke it via `pwsh`,
# which ships alongside `az`/`azd` on every supported posix CI/dev image.
echo ''
echo 'Running mandatory APIM AI Gateway live verification gate...'
verify_script="$(dirname "$0")/../scripts/Verify-ApimAiGateway.ps1"
set +e
pwsh -NoProfile -File "$verify_script"
verify_exit_code=$?
set -e

if [ "$verify_exit_code" -eq 0 ]; then
    echo 'APIM AI Gateway live verification: PASS. Provisioning gate satisfied.'
elif [ "$verify_exit_code" -eq 2 ]; then
    echo 'APIM AI Gateway live verification: SKIPPED (environment precondition not met — see script output above). Provisioning continues, but this environment has NOT been live-verified.'
else
    echo "APIM AI Gateway live verification FAILED (Verify-ApimAiGateway.ps1 exited $verify_exit_code). One or more AI Gateway invariants are missing on the live deployment — see failures listed above. Failing 'azd provision'/'azd up' rather than reporting false success (issue #67)." >&2
    exit 1
fi
