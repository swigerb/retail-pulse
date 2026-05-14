targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the environment (e.g., dev, staging, prod)')
param environmentName string

@minLength(1)
@description('Primary location for all resources')
param location string

@description('Application Insights connection string (output)')
param appInsightsConnectionString string = ''

@description('Azure OpenAI endpoint URL')
param openAiEndpoint string = ''

@description('Azure OpenAI API key')
@secure()
param openAiApiKey string = ''

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

module appService './modules/app-service.bicep' = {
  name: 'app-service'
  scope: rg
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
  }
}

output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = containerAppsEnv.outputs.environmentId
output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = containerAppsEnv.outputs.environmentName
output APPLICATIONINSIGHTS_CONNECTION_STRING string = monitoring.outputs.appInsightsConnectionString
output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = monitoring.outputs.logAnalyticsWorkspaceId
output AZURE_APP_SERVICE_PLAN_ID string = appService.outputs.appServicePlanId
output AZURE_FRONTEND_APP_NAME string = appService.outputs.frontendAppName
output AZURE_FRONTEND_APP_URL string = appService.outputs.frontendAppUrl
