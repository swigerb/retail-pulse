@description('Location for the Azure API Center service. API Center is only available in a subset of regions, so this is independent of the primary location.')
param location string

@description('Deterministic azd resource token used for stable name generation.')
param resourceToken string

@description('Common tags applied to every resource.')
param tags object

@description('Name of the API Management service (in this resource group) to synchronize into API Center.')
param apimName string

@description('Import API specifications along with API metadata. "always" keeps definitions in sync; "never" syncs metadata only.')
@allowed([
  'always'
  'never'
])
param importSpecification string = 'always'

@description('Lifecycle stage assigned to APIs synchronized from API Management.')
@allowed([
  'design'
  'development'
  'testing'
  'preview'
  'production'
  'deprecated'
  'retired'
])
param targetLifecycleStage string = 'production'

var apiCenterName = 'apic-${resourceToken}'
var apimSourceName = 'apim-${resourceToken}'
// API Management Service Reader Role — lets the API Center identity read APIs
// from the APIM instance for the continuous (one-way) synchronization.
var apimServiceReaderRoleId = '71522526-b88f-4d52-b57f-d31fc3546d0d'

resource apim 'Microsoft.ApiManagement/service@2024-05-01' existing = {
  name: apimName
}

resource apiCenter 'Microsoft.ApiCenter/services@2024-03-01' = {
  name: apiCenterName
  location: location
  // The live RP returns and requires sku=Free for every supported API version,
  // although the published Bicep type schema omits the top-level property.
  #disable-next-line BCP187
  sku: {
    name: 'Free'
  }
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
}

// `default` is created by the service with the API Center; declared existing so
// re-provisioning never tries to recreate it.
resource defaultWorkspace 'Microsoft.ApiCenter/services/workspaces@2024-03-01' existing = {
  parent: apiCenter
  name: 'default'
}

resource apimReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(apim.id, apiCenter.id, apimServiceReaderRoleId)
  scope: apim
  properties: {
    principalId: apiCenter.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', apimServiceReaderRoleId)
  }
}

// Continuous APIM → API Center synchronization (one-way: APIM is the source of
// truth; API Center never writes back). Declarative — no portal step needed.
resource apimSource 'Microsoft.ApiCenter/services/workspaces/apiSources@2024-06-01-preview' = {
  parent: defaultWorkspace
  name: apimSourceName
  properties: {
    azureApiManagementSource: {
      resourceId: apim.id
    }
    importSpecification: importSpecification
    targetLifecycleStage: targetLifecycleStage
  }
  dependsOn: [
    apimReaderRole
  ]
}

output name string = apiCenter.name
output resourceId string = apiCenter.id
output principalId string = apiCenter.identity.principalId
output apimSourceName string = apimSource.name
output apimSourceResourceId string = apimSource.id
