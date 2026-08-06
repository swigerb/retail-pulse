targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the environment (e.g., dev, staging, prod)')
param environmentName string

@minLength(1)
@description('Primary location for all resources')
param location string

var abbrs = loadJsonContent('abbreviations.json')
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))
var tags = {
  'azd-env-name': environmentName
  application: 'retail-pulse'
}

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: '${abbrs.resourcesResourceGroups}${environmentName}'
  location: location
  tags: tags
}

module monitoring './modules/monitoring.bicep' = {
  name: 'monitoring'
  scope: rg
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
  }
}

module containerRegistry './modules/container-registry.bicep' = {
  name: 'container-registry'
  scope: rg
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
  }
}

module containerAppsEnv './modules/container-apps-env.bicep' = {
  name: 'container-apps-env'
  scope: rg
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
    logAnalyticsWorkspaceId: monitoring.outputs.logAnalyticsWorkspaceId
  }
}

module containerApps './modules/container-apps.bicep' = {
  name: 'container-apps'
  scope: rg
  params: {
    location: location
    environmentId: containerAppsEnv.outputs.environmentId
    tags: tags
  }
}

module staticWebApp './modules/static-web-app.bicep' = {
  name: 'static-web-app'
  scope: rg
  params: {
    resourceToken: resourceToken
    tags: tags
  }
}

output AZURE_RESOURCE_GROUP string = rg.name
// Dedicated Basic ACR. Emitting AZURE_CONTAINER_REGISTRY_ENDPOINT is what tells
// azd to push service images to THIS registry instead of provisioning its own
// throwaway one — keeping the registry a first-class, self-contained part of the
// infra so clean and repeated `azd up` runs are deterministic and idempotent.
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = containerRegistry.outputs.loginServer
output AZURE_CONTAINER_REGISTRY_NAME string = containerRegistry.outputs.name
output AZURE_CONTAINER_REGISTRY_RESOURCE_ID string = containerRegistry.outputs.resourceId
output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = containerAppsEnv.outputs.environmentId
output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = containerAppsEnv.outputs.environmentName
output AZURE_API_APP_NAME string = containerApps.outputs.apiName
output AZURE_API_APP_URL string = containerApps.outputs.apiUrl
output AZURE_MCP_SERVER_APP_NAME string = containerApps.outputs.mcpServerName
output AZURE_MCP_SERVER_APP_URL string = containerApps.outputs.mcpServerUrl
output AZURE_TEAMS_BOT_APP_NAME string = containerApps.outputs.teamsBotName
output AZURE_TEAMS_BOT_APP_URL string = containerApps.outputs.teamsBotUrl
output APPLICATIONINSIGHTS_CONNECTION_STRING string = monitoring.outputs.appInsightsConnectionString
output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = monitoring.outputs.logAnalyticsWorkspaceId
output AZURE_STATIC_WEB_APP_NAME string = staticWebApp.outputs.staticWebAppName
output AZURE_FRONTEND_APP_NAME string = staticWebApp.outputs.staticWebAppName
output AZURE_FRONTEND_APP_URL string = staticWebApp.outputs.staticWebAppUrl

// ── azd environment aliases ────────────────────────────────────────────────
// These outputs are captured into the azd environment (.azure/<env>/.env) and
// exposed as process env vars to service build/deploy commands and to the
// ${...} substitutions in azure.yaml. Their names match the placeholders that
// azure.yaml and the Vite build consume, so keep them exact. The AZURE_*
// outputs above are retained for external/tooling compatibility.
//
// - MCP_SERVER_BASE_URL / RETAIL_PULSE_FRONTEND_ORIGIN: substituted into the
//   api container app's runtime environmentVariables (McpServer__BaseUrl and
//   Security__AllowedOrigins__0) during `azd deploy api`, which runs after
//   provision — so both FQDNs already exist.
// - VITE_API_ORIGIN: read by the Vite frontend build (import.meta.env) during
//   the frontend staticwebapp deploy (also after provision) so the deployed SPA
//   targets the ACA API origin directly for /hubs/telemetry SignalR. Local
//   Aspire/Vite leaves it unset and keeps the relative /hubs proxy behavior.
output MCP_SERVER_BASE_URL string = containerApps.outputs.mcpServerUrl
output RETAIL_PULSE_FRONTEND_ORIGIN string = staticWebApp.outputs.staticWebAppUrl
output VITE_API_ORIGIN string = containerApps.outputs.apiUrl
