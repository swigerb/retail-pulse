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
