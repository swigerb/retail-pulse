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

// Optional Azure AI Content Safety second layer (issue #100). Disabled by
// default so `azd up` keeps working unchanged. Enable per environment with
// `azd env set AZURE_CONTENT_SAFETY_ENABLED true`.
param contentSafetyEnabled = toLower(readEnvironmentVariable('AZURE_CONTENT_SAFETY_ENABLED', 'false')) == 'true'

// Optional Azure AI Search knowledge provider (issue #103). Disabled by
// default so `azd up` keeps working unchanged (no Search resource, no cost).
// Enable per environment with `azd env set AZURE_AI_SEARCH_ENABLED true`.
param aiSearchEnabled = toLower(readEnvironmentVariable('AZURE_AI_SEARCH_ENABLED', 'false')) == 'true'
