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

echo 'Configuring synthetic-demo runtime settings...'

az containerapp update \
    --name "$AZURE_API_APP_NAME" \
    --resource-group "$resource_group" \
    --set-env-vars \
    "OpenAI__Endpoint=$AZURE_OPENAI_ENDPOINT" \
    'OpenAI__UseManagedIdentity=true' \
    'OpenAI__RouterDeployment=gpt-5.4-mini' \
    "McpServer__BaseUrl=$AZURE_MCP_SERVER_APP_URL" \
    'Security__RequireAuth=false' \
    "Security__AllowedOrigins__0=$RETAIL_PULSE_FRONTEND_ORIGIN" \
    'ASPNETCORE_ENVIRONMENT=Development' \
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

# Linking enables the SWA identity provider. Disable platform auth afterward;
# the synthetic demo uses its fixed Development identity.
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

echo 'Post-provision configuration complete: secretless ACR pull and synthetic-demo runtime settings are ready.'
