@description('Location for resources')
param location string

@description('Unique resource token')
param resourceToken string

@description('Tags for resources')
param tags object

@description('Log Analytics workspace ID for diagnostics')
param logAnalyticsWorkspaceId string

@description('Storage account backing the durable Azure Files share for app data')
param storageAccountName string

@description('Azure Files share (on storageAccountName) mounted for durable app data')
param fileShareName string

@description('Name of the managed-environment storage entry that container apps reference in volumes')
param dataStorageName string = 'retailpulse-data'

var environmentName = 'cae-${resourceToken}'

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: reference(logAnalyticsWorkspaceId, '2023-09-01').customerId
        sharedKey: listKeys(logAnalyticsWorkspaceId, '2023-09-01').primarySharedKey
      }
    }
  }
}

// Existing reference so the account key is obtained *inside* ARM/Bicep via
// listKeys() and handed straight to the environment storage entry. The key is
// never emitted as an output, written to the azd environment, or logged.
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

// Register the Azure Files share with the Container Apps Environment. Apps in
// this environment mount it by referencing dataStorageName in a volume. Access
// is read-write so the API can persist its SQLite stores.
resource dataStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: containerAppsEnvironment
  name: dataStorageName
  properties: {
    azureFile: {
      accountName: storageAccountName
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: fileShareName
      accessMode: 'ReadWrite'
    }
  }
}

output environmentId string = containerAppsEnvironment.id
output environmentName string = containerAppsEnvironment.name
output defaultDomain string = containerAppsEnvironment.properties.defaultDomain
output dataStorageName string = dataStorage.name
