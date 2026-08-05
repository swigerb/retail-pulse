@description('Location for resources')
param location string

@description('Container Apps managed environment resource ID')
param environmentId string

@description('Tags for resources')
param tags object = {}

var placeholderImage = 'mcr.microsoft.com/k8se/quickstart:latest'

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-retailpulse-api'
  location: location
  tags: union(tags, {
    'azd-service-name': 'api'
  })
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'api'
          image: placeholderImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

resource mcpServer 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-retailpulse-mcp'
  location: location
  tags: union(tags, {
    'azd-service-name': 'mcpserver'
  })
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'mcpserver'
          image: placeholderImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

resource teamsBot 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-retailpulse-teamsbot'
  location: location
  tags: union(tags, {
    'azd-service-name': 'teamsbot'
  })
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'teamsbot'
          image: placeholderImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

output apiName string = api.name
output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output apiPrincipalId string = api.identity.principalId
output mcpServerName string = mcpServer.name
output mcpServerUrl string = 'https://${mcpServer.properties.configuration.ingress.fqdn}'
output mcpServerPrincipalId string = mcpServer.identity.principalId
output teamsBotName string = teamsBot.name
output teamsBotUrl string = 'https://${teamsBot.properties.configuration.ingress.fqdn}'
output teamsBotPrincipalId string = teamsBot.identity.principalId
