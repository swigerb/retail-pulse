// Cross-resource-group role assignment module
// Deployed into the AI Services resource group so the role assignment
// targets the correct scope.

@description('Name of the AI Services resource')
param aiServicesName string

@description('Principal ID of the APIM managed identity')
param principalId string

@description('Role definition ID to assign')
param roleDefinitionId string

resource aiServices 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: aiServicesName
}

resource roleAssign 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiServices.id, principalId, roleDefinitionId)
  scope: aiServices
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleDefinitionId)
    principalType: 'ServicePrincipal'
  }
}
