@description('Name of the Azure AI Foundry / Cognitive Services account')
param aiServicesName string

@description('Principal ID of the APIM managed identity')
param principalId string

@description('Role definition ID to assign')
param roleDefinitionId string

resource aiServices 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: aiServicesName
}

resource roleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiServices.id, principalId, roleDefinitionId)
  scope: aiServices
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleDefinitionId)
    principalType: 'ServicePrincipal'
  }
}
