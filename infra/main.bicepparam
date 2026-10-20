using './main.bicep'

param environmentName = readEnvironmentVariable('AZURE_ENV_NAME', 'dev')
param location = readEnvironmentVariable('AZURE_LOCATION', 'northcentralus')

// Non-secret Entra identifiers for the single-tenant SPA/API app registration.
// Set with `azd env set RETAIL_PULSE_ENTRA_TENANT_ID <guid>` etc. Left empty
// they disable the SPA auth gate (local dev / pre-provision). Never secrets.
param entraTenantId = readEnvironmentVariable('RETAIL_PULSE_ENTRA_TENANT_ID', '')
param entraClientId = readEnvironmentVariable('RETAIL_PULSE_ENTRA_CLIENT_ID', '')
param entraApiScope = readEnvironmentVariable('RETAIL_PULSE_ENTRA_API_SCOPE', 'access_as_user')
param entraAudience = readEnvironmentVariable('RETAIL_PULSE_ENTRA_AUDIENCE', '')
param aiFoundryAccountName = readEnvironmentVariable('AZURE_OPENAI_ACCOUNT_NAME', 'aiagents-3rsdmhyb')
param aiFoundryResourceGroupName = readEnvironmentVariable('AZURE_OPENAI_RESOURCE_GROUP_NAME', 'rg-repodigest-agents-demo-eus-001')

// Model deployment consumed by the API through APIM. Overridable via `azd env set`.
param apimBackendPoolEnabled = toLower(readEnvironmentVariable('AZURE_APIM_BACKEND_POOL_ENABLED', 'false')) == 'true'
param apimFailoverDemoEnabled = toLower(readEnvironmentVariable('AZURE_APIM_FAILOVER_DEMO_ENABLED', 'false')) == 'true'
param apimTokenBudgetDemoEnabled = toLower(readEnvironmentVariable('AZURE_APIM_TOKEN_BUDGET_DEMO_ENABLED', 'false')) == 'true'
param apimTokenBudgetDemoTokensPerMinute = int(readEnvironmentVariable('AZURE_APIM_TOKEN_BUDGET_DEMO_TPM', '50'))
param apimTokenBudgetScopes = [
  { kind: 'store', id: 'store-1042', displayName: 'Fictional Store 1042' }
  { kind: 'channel', id: 'ecommerce', displayName: 'E-commerce channel' }
  { kind: 'b2b', id: 'contoso-wholesale', displayName: 'Contoso Wholesale (B2B)' }
]
param apimFailoverDemoKey = readEnvironmentVariable('AZURE_APIM_FAILOVER_DEMO_KEY', '')
param secondaryAiFoundryAccountName = readEnvironmentVariable('AZURE_OPENAI_SECONDARY_ACCOUNT_NAME', '')
param secondaryAiFoundryResourceGroupName = readEnvironmentVariable('AZURE_OPENAI_SECONDARY_RESOURCE_GROUP_NAME', readEnvironmentVariable('AZURE_OPENAI_RESOURCE_GROUP_NAME', 'rg-repodigest-agents-demo-eus-001'))
param openAiDeployment = readEnvironmentVariable('AZURE_OPENAI_DEPLOYMENT', 'gpt-5.4-mini-2026-03-17')

// Container image references. azd populates SERVICE_<name>_IMAGE_NAME after a
// successful `azd deploy <service>`; on the FIRST provision these are empty and
// Bicep falls back to the ACA placeholder image (so provision still succeeds).
// On every subsequent provision the previously-deployed image is passed through
// declaratively, which stops the active revision from being reset to the
// placeholder on re-provision (§7 fix).
param apiImageName = readEnvironmentVariable('SERVICE_API_IMAGE_NAME', 'mcr.microsoft.com/k8se/quickstart:latest')
param mcpServerImageName = readEnvironmentVariable('SERVICE_MCPSERVER_IMAGE_NAME', 'mcr.microsoft.com/k8se/quickstart:latest')
param teamsBotImageName = readEnvironmentVariable('SERVICE_TEAMSBOT_IMAGE_NAME', 'mcr.microsoft.com/k8se/quickstart:latest')

// Optional Azure AI Content Safety second layer. Disabled by
// default so `azd up` keeps working unchanged. Enable per environment with
// `azd env set AZURE_CONTENT_SAFETY_ENABLED true`.
param contentSafetyEnabled = toLower(readEnvironmentVariable('AZURE_CONTENT_SAFETY_ENABLED', 'false')) == 'true'
param apimGatewaySafetyEnabled = toLower(readEnvironmentVariable('AZURE_APIM_GATEWAY_SAFETY_ENABLED', 'false')) == 'true'
param apimGatewaySafetyMode = readEnvironmentVariable('AZURE_APIM_GATEWAY_SAFETY_MODE', 'demo')

// Optional Azure AI Search knowledge provider. Disabled by
// default so `azd up` keeps working unchanged (no Search resource, no cost).
// Enable per environment with `azd env set AZURE_AI_SEARCH_ENABLED true`.
param aiSearchEnabled = toLower(readEnvironmentVariable('AZURE_AI_SEARCH_ENABLED', 'false')) == 'true'

// Optional Azure API Center synchronized from APIM. Disabled by default.
// Enable with `azd env set AZURE_API_CENTER_ENABLED true`; region (API Center
// is region-limited) via `azd env set AZURE_API_CENTER_LOCATION <region>`.
param apiCenterEnabled = toLower(readEnvironmentVariable('AZURE_API_CENTER_ENABLED', 'false')) == 'true'
param apiCenterLocation = readEnvironmentVariable('AZURE_API_CENTER_LOCATION', 'eastus')

// Optional Azure Managed Redis semantic cache (#338). Disabled by default; paid
// while provisioned. Enable with `azd env set AZURE_SEMANTIC_CACHE_ENABLED true`.
param semanticCacheEnabled = toLower(readEnvironmentVariable('AZURE_SEMANTIC_CACHE_ENABLED', 'false')) == 'true'
param semanticCacheSku = readEnvironmentVariable('AZURE_SEMANTIC_CACHE_SKU', 'Balanced_B0')
param semanticCacheTtlSeconds = int(readEnvironmentVariable('AZURE_SEMANTIC_CACHE_TTL_SECONDS', '300'))
param semanticCachePublicNetworkAccess = readEnvironmentVariable('AZURE_SEMANTIC_CACHE_PUBLIC_NETWORK_ACCESS', 'Disabled')
param semanticCachePrivateEndpointSubnetId = readEnvironmentVariable('AZURE_SEMANTIC_CACHE_PRIVATE_ENDPOINT_SUBNET_ID', '')

// Azure Bot Service registration backing the Teams bot. Empty by default so a
// clean zd up does not claim to have a bot it never registered. Set both with:
//   azd env set AZURE_BOT_APP_ID <appId>
//   azd env set AZURE_BOT_APP_SECRET <secret>
param botAppId = readEnvironmentVariable('AZURE_BOT_APP_ID', '')
param botAppSecret = readEnvironmentVariable('AZURE_BOT_APP_SECRET', '')

// Keep the API and MCP server warm. Defaults true so the demo never greets a
// visitor with a cold start. Set false to save money on a non-demo environment.
param alwaysWarm = toLower(readEnvironmentVariable('AZURE_ALWAYS_WARM', 'true')) == 'true'

param mcpGatewayApimClientId = readEnvironmentVariable('AZURE_MCP_GATEWAY_APIM_CLIENT_ID', '')
param mcpGatewayAudience = readEnvironmentVariable('AZURE_MCP_GATEWAY_AUDIENCE', 'api://retailpulse-mcp-gateway')
