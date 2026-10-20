@description('Location for the Azure Managed Redis cluster.')
param location string

@description('Deterministic azd resource token used for stable name generation.')
param resourceToken string

@description('Common tags applied to every resource.')
param tags object

@description('Azure Managed Redis SKU. Balanced_B0 is the lowest SKU that can host the RediSearch module APIM semantic caching needs.')
@allowed([
  'Balanced_B0'
  'Balanced_B1'
  'Balanced_B3'
])
param skuName string = 'Balanced_B0'

@description('Advisory handoff value only: the intended semantic cache entry TTL in seconds. It is only emitted as an output and is NOT enforced by anything until issue #339 wires the APIM semantic-cache policy.')
@minValue(60)
@maxValue(86400)
param defaultTtlSeconds int = 300

@description('Public network access. Disabled by default (fail closed): Disabled requires privateEndpointSubnetId and privateDnsZoneId, otherwise the deployment fails. Enabled must be chosen explicitly and exposes the TLS + access-key endpoint to the internet.')
@allowed([
  'Disabled'
  'Enabled'
])
param publicNetworkAccess string = 'Disabled'

@description('Subnet resource ID for the cache private endpoint. Required when publicNetworkAccess is Disabled.')
param privateEndpointSubnetId string = ''

@description('Resource ID of an existing privatelink.redis.azure.net private DNS zone (already linked to the VNet APIM resolves from). Required when publicNetworkAccess is Disabled.')
param privateDnsZoneId string = ''

var redisName = 'redis-${resourceToken}'
var usePrivateEndpoint = publicNetworkAccess == 'Disabled'
var networkPostureValid = !usePrivateEndpoint || (!empty(privateEndpointSubnetId) && !empty(privateDnsZoneId))
// Evaluated by the cache resource, so an invalid posture fails the deployment before anything is created.
var validatedPublicNetworkAccess = networkPostureValid ? publicNetworkAccess : string(json('{"error": "publicNetworkAccess=Disabled requires privateEndpointSubnetId and privateDnsZoneId"'))

resource redis 'Microsoft.Cache/redisEnterprise@2025-07-01' = {
  name: redisName
  location: location
  tags: tags
  sku: {
    name: skuName
  }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: validatedPublicNetworkAccess
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
    // APIM external cache / semantic caching only supports a Redis connection
    // string with an access key (no Entra auth). Keys are enabled on the service
    // but never emitted; the connection string must be supplied to APIM out of band.
    accessKeysAuthentication: 'Enabled'
    modules: [
      {
        name: 'RediSearch'
      }
    ]
  }
}

resource privateEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (usePrivateEndpoint) {
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

resource dnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = if (usePrivateEndpoint) {
  parent: privateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'redis'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

output name string = redis.name
output resourceId string = redis.id
output hostName string = redis.properties.hostName
output port int = database.properties.port
// Advisory handoff only; not enforced until issue #339 wires the APIM semantic-cache policy.
output defaultTtlSeconds int = defaultTtlSeconds
output publicNetworkAccess string = publicNetworkAccess
output networkPosture string = usePrivateEndpoint ? 'PrivateEndpoint' : 'Public'
