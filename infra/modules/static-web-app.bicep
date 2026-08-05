@description('Unique resource token')
param resourceToken string

@description('Tags for resources')
param tags object

var staticWebAppName = 'swa-frontend-${resourceToken}'

resource staticWebApp 'Microsoft.Web/staticSites@2025-03-01' = {
  name: staticWebAppName
  location: 'eastus2'
  tags: union(tags, {
    'azd-service-name': 'frontend'
  })
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    allowConfigFileUpdates: true
    buildProperties: {
      appLocation: '/src/RetailPulse.Web'
      apiLocation: ''
      outputLocation: 'dist'
      appBuildCommand: 'npm run build'
      skipGithubActionWorkflowGeneration: true
    }
    stagingEnvironmentPolicy: 'Disabled'
  }
}

output staticWebAppName string = staticWebApp.name
output staticWebAppUrl string = 'https://${staticWebApp.properties.defaultHostname}'
