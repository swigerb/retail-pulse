@description('Location for resources')
param location string

@description('Unique resource token')
param resourceToken string

@description('Tags for resources')
param tags object

@description('Application Insights connection string')
param appInsightsConnectionString string

var appServicePlanName = 'plan-${resourceToken}'
var frontendAppName = 'app-frontend-${resourceToken}'

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource frontendApp 'Microsoft.Web/sites@2023-12-01' = {
  name: frontendAppName
  location: location
  tags: union(tags, {
    'azd-service-name': 'frontend'
  })
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'NODE|20-lts'
      appCommandLine: 'npx serve dist -s -l 8080'
      appSettings: [
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
          value: 'false'
        }
      ]
    }
  }
}

output appServicePlanId string = appServicePlan.id
output frontendAppName string = frontendApp.name
output frontendAppUrl string = 'https://${frontendApp.properties.defaultHostName}'
