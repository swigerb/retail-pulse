@description('Location for resources')
param location string

@description('Unique resource token')
param resourceToken string

@description('Tags for resources')
param tags object = {}

@description('Log Analytics workspace resource ID for APIM diagnostic settings')
param logAnalyticsWorkspaceId string

@description('Application Insights resource ID used by the APIM logger')
param appInsightsId string

@description('Application Insights instrumentation key used by the APIM logger')
param appInsightsInstrumentationKey string

@description('Publisher email required by Azure API Management')
param publisherEmail string = 'noreply@retail-pulse.example.com'

@description('Publisher name required by Azure API Management')
param publisherName string = 'Retail Pulse'

var abbrs = loadJsonContent('../abbreviations.json')
var apimName = '${abbrs.apiManagementService}${resourceToken}'

resource apim 'Microsoft.ApiManagement/service@2024-06-01-preview' = {
  name: apimName
  location: location
  tags: tags
  sku: {
    name: 'Developer'
    capacity: 1
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    publisherEmail: publisherEmail
    publisherName: publisherName
    publicNetworkAccess: 'Enabled'
    virtualNetworkType: 'None'
  }
}

resource apimDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'apim-to-loganalytics'
  scope: apim
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logAnalyticsDestinationType: 'Dedicated'
    logs: [
      {
        category: 'GatewayLogs'
        enabled: true
      }
      {
        category: 'GatewayLlmLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

resource appInsightsLogger 'Microsoft.ApiManagement/service/loggers@2024-06-01-preview' = {
  parent: apim
  name: 'appinsights-logger'
  properties: {
    credentials: {
      instrumentationKey: appInsightsInstrumentationKey
    }
    description: 'Retail Pulse APIM logger for Application Insights'
    isBuffered: false
    loggerType: 'applicationInsights'
    resourceId: appInsightsId
  }
}

resource azureMonitorLogger 'Microsoft.ApiManagement/service/loggers@2024-06-01-preview' = {
  parent: apim
  name: 'azuremonitor'
  properties: {
    description: 'Retail Pulse APIM logger for Azure Monitor'
    isBuffered: false
    loggerType: 'azureMonitor'
  }
}

output apimId string = apim.id
output apimName string = apim.name
output apimPrincipalId string = apim.identity.principalId
output gatewayUrl string = apim.properties.gatewayUrl
