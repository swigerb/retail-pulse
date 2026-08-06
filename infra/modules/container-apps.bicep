@description('Location for resources')
param location string

@description('Container Apps managed environment resource ID')
param environmentId string

@description('Tags for resources')
param tags object = {}

@description('Managed-environment storage name (Azure Files) for durable API app data')
param dataStorageName string

@description('Mount path inside the API container for the durable Azure Files volume')
param dataMountPath string = '/mnt/retailpulse-data'

var placeholderImage = 'mcr.microsoft.com/k8se/quickstart:latest'

// Volume name is local to the container template; it binds to the environment
// storage entry (dataStorageName) that maps to the Azure Files share.
var dataVolumeName = 'retailpulse-data'

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
          // Point the app's durable SQLite stores at the mounted Azure Files
          // share. Defined here (not just in the postprovision hook) so a fresh
          // `azd up` provisions a container that already fails fast if the mount
          // is missing — it can never silently regress to ephemeral temp storage.
          env: [
            {
              name: 'RETAIL_PULSE_DATA_DIRECTORY'
              value: dataMountPath
            }
          ]
          volumeMounts: [
            {
              volumeName: dataVolumeName
              mountPath: dataMountPath
            }
          ]
        }
      ]
      // Durable app data lives on Azure Files, so the API is a single-writer
      // store: scale-to-zero is fine (history survives on the share) but max=1
      // avoids two replicas writing the same SQLite files over SMB.
      volumes: [
        {
          name: dataVolumeName
          storageType: 'AzureFile'
          storageName: dataStorageName
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
