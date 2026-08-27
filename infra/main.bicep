targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the environment (e.g., dev, staging, prod)')
param environmentName string

@minLength(1)
@description('Primary location for all resources')
param location string

// ── Entra external auth configuration (non-secret) ─────────────────────────
// Supplied by the operator via `azd env set RETAIL_PULSE_ENTRA_*` and read by
// main.bicepparam. These are public identifiers (tenant/client GUIDs, an API
// scope name and audience) — never secrets — and are round-tripped below as
// VITE_ENTRA_* outputs so the Vite frontend build embeds them into the SPA.
// Empty by default: when unset the SPA builds without an auth gate (local dev)
// and the API's RequireAuth stays governed by the azd hooks.
@description('Entra tenant (directory) ID for the single-tenant SPA/API app registration')
param entraTenantId string = ''

@description('Entra application (client) ID of the SPA/API app registration')
param entraClientId string = ''

@description('Delegated API scope name exposed by the app (e.g. access_as_user)')
param entraApiScope string = 'access_as_user'

@description('API audience / Application ID URI (defaults to api://{clientId} when unset)')
param entraAudience string = ''

@description('Name of the Azure AI Foundry / Cognitive Services account used by the AI gateway backend')
param aiFoundryAccountName string = 'aiagents-3rsdmhyb'

@description('Resource group containing the Azure AI Foundry / Cognitive Services account used by the AI gateway backend')
param aiFoundryResourceGroupName string = 'rg-repodigest-agents-demo-eus-001'

@description('Azure OpenAI deployment name that the API sends chat/completions to (through APIM)')
param openAiDeployment string = 'gpt-5.4-mini-2026-03-17'

@description('Fully-qualified image reference for the API container app. Defaults to the ACA placeholder when SERVICE_API_IMAGE_NAME is empty.')
param apiImageName string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Fully-qualified image reference for the MCP server container app.')
param mcpServerImageName string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Fully-qualified image reference for the Teams bot container app.')
param teamsBotImageName string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Enable the optional Azure AI Content Safety second layer. Disabled by default; no Content Safety account is provisioned when false.')
param contentSafetyEnabled bool = false

@description('Enable the optional Azure AI Search knowledge provider. Disabled by default; no Search resource is provisioned when false.')
param aiSearchEnabled bool = false

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

module staticWebApp './modules/static-web-app.bicep' = {
  name: 'static-web-app'
  scope: rg
  params: {
    resourceToken: resourceToken
    tags: tags
  }
}

module apim './modules/apim.bicep' = {
  name: 'apim'
  scope: rg
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
    logAnalyticsWorkspaceId: monitoring.outputs.logAnalyticsWorkspaceId
    appInsightsId: monitoring.outputs.appInsightsId
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
  }
}

module apimOpenAiApi './modules/apim-openai-api.bicep' = {
  name: 'apim-openai-api'
  scope: rg
  params: {
    apimName: apim.outputs.apimName
    apimPrincipalId: apim.outputs.apimPrincipalId
    aiFoundryAccountName: aiFoundryAccountName
    aiFoundryResourceGroupName: aiFoundryResourceGroupName
  }
}

// containerApps runs AFTER apimOpenAiApi and staticWebApp so it can consume the
// APIM inference endpoint + subscription key (via listSecrets()) and the SWA
// frontend origin declaratively. This is what makes `azd provision` re-assert
// the AI Gateway wiring on every run (§7 fix — the previous ordering left the
// APIM env vars to a postprovision `az containerapp update`, which lost them
// whenever Bicep re-created the container-app resource).
module containerApps './modules/container-apps.bicep' = {
  name: 'container-apps'
  scope: rg
  params: {
    location: location
    environmentId: containerAppsEnv.outputs.environmentId
    tags: tags
    apiImageName: apiImageName
    mcpServerImageName: mcpServerImageName
    teamsBotImageName: teamsBotImageName
    apimInferenceEndpoint: apimOpenAiApi.outputs.inferenceEndpoint
    apimSubscriptionKey: apimOpenAiApi.outputs.subscriptionKey
    openAiDeployment: openAiDeployment
    frontendOrigin: staticWebApp.outputs.staticWebAppUrl
    entraTenantId: entraTenantId
    entraClientId: entraClientId
    entraApiScope: entraApiScope
    containerRegistryLoginServer: containerRegistry.outputs.loginServer
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
output AZURE_APIM_NAME string = apim.outputs.apimName
output AZURE_APIM_GATEWAY_URL string = apim.outputs.gatewayUrl
output AZURE_APIM_INFERENCE_ENDPOINT string = apimOpenAiApi.outputs.inferenceEndpoint
output AZURE_APIM_INFERENCE_API_NAME string = apimOpenAiApi.outputs.inferenceApiName
output AZURE_APIM_INFERENCE_SUBSCRIPTION_NAME string = apimOpenAiApi.outputs.subscriptionName

// ── Optional Azure AI Content Safety second layer (issue #100) ─────────────
// Provisioned only when contentSafetyEnabled = true so a default `azd up`
// remains byte-for-byte identical to the regex-only guardrails baseline. The
// module keys off managed identity — no account keys are ever emitted — and
// the postprovision hook grants each container app system identity the
// `Cognitive Services User` role on this account. When disabled the endpoint
// output is an empty string so downstream consumers can safely branch on it.
module contentSafety './modules/content-safety.bicep' = if (contentSafetyEnabled) {
  name: 'content-safety'
  scope: rg
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
  }
}

output AZURE_CONTENT_SAFETY_ENABLED bool = contentSafetyEnabled
output AZURE_CONTENT_SAFETY_ENDPOINT string = contentSafetyEnabled ? contentSafety!.outputs.endpoint : ''
output AZURE_CONTENT_SAFETY_NAME string = contentSafetyEnabled ? contentSafety!.outputs.name : ''
output AZURE_CONTENT_SAFETY_RESOURCE_ID string = contentSafetyEnabled ? contentSafety!.outputs.resourceId : ''

// ── Optional Azure AI Search knowledge provider (issue #103) ───────────────
// Provisioned only when aiSearchEnabled = true so a default `azd up` remains
// byte-for-byte identical to the InMemory-only baseline. The module disables
// local auth (no admin/query keys anywhere) and forces every caller through
// managed identity. The postprovision hook grants each container app's
// system identity the roles required to auto-create the index and to read
// and write documents. When disabled, endpoint/name/resource-id outputs are
// empty so downstream consumers can safely branch on the enabled flag.
module aiSearch './modules/ai-search.bicep' = if (aiSearchEnabled) {
  name: 'ai-search'
  scope: rg
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
  }
}

output AZURE_AI_SEARCH_ENABLED bool = aiSearchEnabled
output AZURE_AI_SEARCH_ENDPOINT string = aiSearchEnabled ? aiSearch!.outputs.endpoint : ''
output AZURE_AI_SEARCH_NAME string = aiSearchEnabled ? aiSearch!.outputs.name : ''
output AZURE_AI_SEARCH_RESOURCE_ID string = aiSearchEnabled ? aiSearch!.outputs.resourceId : ''

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

// - VITE_AUTH_MODE: the provider-neutral sign-in mode read by the Vite frontend
//   build (import.meta.env) so the deployed SPA renders exactly one provider's
//   sign-in UX. The LIVE environment is pinned to Entra — matching the API's
//   Authentication__Mode (set to Entra by the postprovision hook and committed in
//   appsettings.Production.json). ProviderNeutralDeploymentContractTests asserts
//   this parity. Other (non-production) deployments use a separate template that
//   overrides both halves together (see docs/deployment-azd.md and the
//   appsettings.{GitHub,Anonymous}.example.json / .env.*.example templates).
output VITE_AUTH_MODE string = 'Entra'

// - VITE_ENTRA_*: non-secret Entra identifiers read by the Vite frontend build
//   (import.meta.env) so the deployed SPA can run MSAL PKCE sign-in against the
//   single-tenant app registration. Round-tripped from the entra* params above.
//   When left empty (local dev / not yet provisioned) the SPA builds without an
//   auth gate and relies on the API's Development auth handler.
output VITE_ENTRA_TENANT_ID string = entraTenantId
output VITE_ENTRA_CLIENT_ID string = entraClientId
output VITE_ENTRA_API_SCOPE string = entraApiScope
output VITE_ENTRA_AUDIENCE string = empty(entraAudience) && !empty(entraClientId)
  ? 'api://${entraClientId}'
  : entraAudience

// - VITE_FEATURE_*: build-time capability switches read by the Vite frontend
//   (src/RetailPulse.Web/src/config/featureFlags.ts). Every one of these except
//   observability defaults to FALSE in the SPA, so without these outputs the
//   deployed app hides its own feature surface — campaign planner, competitive
//   dashboard, knowledge base, health council, guardrails/security, adaptive
//   cards, store ops, financials and portfolio were all invisible in the live
//   demo even though the API mapped their endpoints.
//
//   This deployment is a full-capability demo environment, so every flag is on.
//   They are emitted as infra outputs (rather than hardcoded in the SPA) so a
//   differently-scoped deployment can still narrow the surface by overriding
//   them, and so what the frontend renders stays traceable to the environment.
output VITE_FEATURE_CAMPAIGN_PLANNER string = 'true'
output VITE_FEATURE_COMPETITIVE string = 'true'
output VITE_FEATURE_KNOWLEDGE_BASE string = 'true'
output VITE_FEATURE_HEALTH_COUNCIL string = 'true'
output VITE_FEATURE_SECURITY string = 'true'
output VITE_FEATURE_CARDS string = 'true'
output VITE_FEATURE_STORES string = 'true'
output VITE_FEATURE_FINANCIALS string = 'true'
output VITE_FEATURE_PORTFOLIO string = 'true'
output VITE_FEATURE_OBSERVABILITY string = 'true'
