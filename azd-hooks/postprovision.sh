#!/bin/sh
# Post-provision hook (POSIX/sh) — parity with postprovision.ps1. See that file
# for the full rationale.
#
# Wires ACR pull auth for every Container App to its own system-assigned managed
# identity, with no registry secrets. Doing it here (after provision) breaks the
# ARM/Bicep identity-sequencing cycle deterministically and is fully idempotent:
# every `azd up` / `azd provision` re-asserts the desired state, so clean and
# repeated deploys are self-contained. Values are derived from the azd
# environment (infra outputs captured by `azd provision`), exposed to this hook
# as process environment variables.

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
require_env AZURE_API_APP_URL
require_env AZURE_MCP_SERVER_APP_NAME
require_env AZURE_MCP_SERVER_APP_URL
require_env AZURE_TEAMS_BOT_APP_NAME
require_env AZURE_OPENAI_ENDPOINT
require_env RETAIL_PULSE_FRONTEND_ORIGIN
require_env RETAIL_PULSE_DATA_DIRECTORY
require_env RETAIL_PULSE_REQUIRE_DURABLE_STORAGE
require_env AZURE_STATIC_WEB_APP_NAME
require_env AZURE_LOCATION
# Entra auth configuration. Tenant/client IDs are CONFIGURATION, not secrets; the
# parent captures them via `azd env set` from the Setup-EntraAuth.ps1 output. Read
# with require_env so a deploy fails fast rather than silently shipping an anonymous,
# Development-mode API (the exact regression this hook now prevents).
require_env RETAIL_PULSE_ENTRA_TENANT_ID
require_env RETAIL_PULSE_ENTRA_CLIENT_ID

resource_group="$AZURE_RESOURCE_GROUP"
registry_name="$AZURE_CONTAINER_REGISTRY_NAME"
registry_server="$AZURE_CONTAINER_REGISTRY_ENDPOINT"
registry_id="$AZURE_CONTAINER_REGISTRY_RESOURCE_ID"
entra_tenant_id="$RETAIL_PULSE_ENTRA_TENANT_ID"
entra_client_id="$RETAIL_PULSE_ENTRA_CLIENT_ID"
entra_api_scope="${RETAIL_PULSE_ENTRA_API_SCOPE:-access_as_user}"
entra_app_role="${RETAIL_PULSE_ENTRA_APP_ROLE:-RetailPulse.User}"

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

echo 'Configuring production auth + runtime settings for the API...'

# The API is the security boundary. It deploys as Production with real Entra JWT
# validation enabled (Security__RequireAuth=true). ACA platform (Easy Auth) stays
# disabled below so the in-process JwtBearer handler is the sole gate; direct ACA
# REST/SignalR are protected independent of SWA routing.
az containerapp update \
    --name "$AZURE_API_APP_NAME" \
    --resource-group "$resource_group" \
    --set-env-vars \
    "OpenAI__Endpoint=$AZURE_OPENAI_ENDPOINT" \
    'OpenAI__UseManagedIdentity=true' \
    'OpenAI__Deployment=gpt-5.4-mini-2026-03-17' \
    'OpenAI__RouterDeployment=gpt-5.4-mini-2026-03-17' \
    "McpServer__BaseUrl=$AZURE_MCP_SERVER_APP_URL" \
    'Security__RequireAuth=true' \
    "Security__AllowedOrigins__0=$RETAIL_PULSE_FRONTEND_ORIGIN" \
    "MicrosoftEntra__TenantId=$entra_tenant_id" \
    "MicrosoftEntra__ClientId=$entra_client_id" \
    "MicrosoftEntra__ApiScope=$entra_api_scope" \
    "MicrosoftEntra__AppRole=$entra_app_role" \
    "RETAIL_PULSE_DATA_DIRECTORY=$RETAIL_PULSE_DATA_DIRECTORY" \
    "RETAIL_PULSE_REQUIRE_DURABLE_STORAGE=$RETAIL_PULSE_REQUIRE_DURABLE_STORAGE" \
    'ASPNETCORE_ENVIRONMENT=Production' \
    --output none

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
# handler (Security__RequireAuth=true above) is the real security boundary.
az containerapp auth update \
    --name "$AZURE_API_APP_NAME" \
    --resource-group "$resource_group" \
    --enabled false \
    --output none

az containerapp update \
    --name "$AZURE_MCP_SERVER_APP_NAME" \
    --resource-group "$resource_group" \
    --set-env-vars 'ASPNETCORE_ENVIRONMENT=Development' \
    --output none

az containerapp update \
    --name "$AZURE_TEAMS_BOT_APP_NAME" \
    --resource-group "$resource_group" \
    --set-env-vars \
    'ASPNETCORE_ENVIRONMENT=Development' \
    "TeamsBot__ApiBaseUrl=$AZURE_API_APP_URL" \
    --output none

echo 'Post-provision configuration complete: secretless ACR pull and production Entra auth runtime settings are ready.'
