@description('Location for resources')
param location string

@description('Unique resource token')
param resourceToken string

@description('Tags for resources')
param tags object = {}

@description('Name of the Azure Files share that holds durable SQLite stores for the app')
param fileShareName string = 'retailpulse-data'

@minValue(1)
@description('Provisioned quota (GiB) for the durable app-data share. Kept tiny — the SQLite stores are bounded and rounding up to the smallest useful quota keeps cost negligible.')
param fileShareQuotaGiB int = 1

// Storage account names are globally unique, 3-24 chars, lowercase alphanumeric
// only (no hyphens), so the token is concatenated directly the same way the ACR
// module does. resourceToken is a 13-char uniqueString(), so 'st' + token = 15
// chars — always inside the 3-24 bound. The BCP334 min-length warning is a false
// positive because Bicep cannot statically prove the token length.
var storageAccountName = 'st${resourceToken}'

// Least-cost durable option for a single-replica demo: a Standard general-purpose
// v2 account with locally-redundant storage. No geo-replication, no premium tier.
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  #disable-next-line BCP334
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    // Harden the account: TLS 1.2 floor, no anonymous blob access, key-based SMB
    // for the Azure Files mount (Container Apps storage authenticates with the
    // account key fetched inside ARM — never surfaced to azd/logs/repo).
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

// Private (no anonymous access) SMB file share for the API's durable app data:
// audit.db, costs.db, memory.db, approvals.db, alerts.db.
resource fileShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileService
  name: fileShareName
  properties: {
    shareQuota: fileShareQuotaGiB
    enabledProtocols: 'SMB'
    accessTier: 'TransactionOptimized'
  }
}

output storageAccountName string = storageAccount.name
output storageAccountId string = storageAccount.id
output fileShareName string = fileShare.name
