@description('Location for resources')
param location string

@description('Unique resource token')
param resourceToken string

@description('Tags for resources')
param tags object = {}

// ACR names are globally unique, 5-50 chars, alphanumeric only (no hyphens),
// so the token is concatenated directly rather than with the abbreviations
// pattern used by the hyphen-friendly resource types. resourceToken is a 13-char
// uniqueString(), so the name is always 16 chars — the min-length warning below
// is a false positive because Bicep cannot statically prove the token length.
var registryName = 'acr${resourceToken}'

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  #disable-next-line BCP334
  name: registryName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    // Identity-based pull only — the admin user (username/password secret) is
    // intentionally disabled. Container Apps authenticate to this registry with
    // their system-assigned managed identities (AcrPull), wired by the
    // postprovision hook. Never enable admin creds for this deployment.
    adminUserEnabled: false
  }
}

output loginServer string = containerRegistry.properties.loginServer
output name string = containerRegistry.name
output resourceId string = containerRegistry.id
