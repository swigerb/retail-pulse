@description('Location for the Azure Managed Redis cluster.')
param location string

@description('Deterministic azd resource token used for stable name generation.')
param resourceToken string

@description('Common tags applied to every resource.')
param tags object

@description('Principal ID of the APIM system-assigned identity that is granted data access.')
param apimPrincipalId string

@description('Azure Managed Redis SKU. Balanced_B0 is the lowest SKU that can host the RediSearch module APIM semantic caching needs.')
@allowed([
  'Balanced_B0'
  'Balanced_B1'
  'Balanced_B3'
])
param skuName string = 'Balanced_B0'

@description('Default semantic cache entry TTL in seconds (consumed by the APIM semantic cache policy).')
@minValue(60)
@maxValue(86400)
param defaultTtlSeconds int = 300

@description('Public network access. Disabled by default; supply privateEndpointSubnetId so APIM reaches the cache privately.')
@allowed([
  'Disabled'
  'Enabled'
])
param publicNetworkAccess string = 'Disabled'

@description('Optional subnet resource ID for a private endpoint to the cache. Empty creates no private endpoint.')
param privateEndpointSubnetId string = ''

var redisName = 'redis-${resourceToken}'

resource redis 'Microsoft.Cache/redisEnterprise@2025-07-01' = {
  name: redisName
  location: location
  tags: tags
  sku: {
    name: skuName
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: publicNetworkAccess
  }
}

resource database 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' = {
  parent: redis
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    port: 10000
    clusteringPolicy: 'EnterpriseCluster'
    evictionPolicy: 'NoEviction'
    // Access keys stay disabled: callers authenticate with Entra identities only.
    accessKeysAuthentication: 'Disabled'
    modules: [
      {
        name: 'RediSearch'
      }
    ]
  }
}

resource apimAccess 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-07-01' = {
  parent: database
  name: guid(redis.id, apimPrincipalId, 'default')
  properties: {
    accessPolicyName: 'default'
    user: {
      objectId: apimPrincipalId
    }
  }
}

resource privateEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (!empty(privateEndpointSubnetId)) {
  name: 'pe-${redisName}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'redisEnterprise'
        properties: {
          privateLinkServiceId: redis.id
          groupIds: [
            'redisEnterprise'
          ]
        }
      }
    ]
  }
}

output name string = redis.name
output resourceId string = redis.id
output hostName string = redis.properties.hostName
output port int = database.properties.port
output defaultTtlSeconds int = defaultTtlSeconds
output publicNetworkAccess string = publicNetworkAccess
