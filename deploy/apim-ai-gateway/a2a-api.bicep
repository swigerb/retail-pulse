@description('Name of existing APIM instance')
param apimName string = 'bsapim-dev-northcentralus-001'

@description('Gateway URL for the APIM instance')
param gatewayUrl string = 'https://bsapim-dev-northcentralus-001.azure-api.net'

resource apim 'Microsoft.ApiManagement/service@2024-06-01-preview' existing = {
  name: apimName
}

// A2A API - type 'a2a' so the portal discovers it
// Requires isAgent, agent card, agentCardPath, and jsonRpc path
resource a2aApi 'Microsoft.ApiManagement/service/apis@2025-03-01-preview' = {
  parent: apim
  name: 'retail-pulse-a2a-agent'
  properties: {
    displayName: 'Patron Pulse Analytics Agent (A2A)'
    description: 'Agent-to-agent interface for Patron Pulse brand analytics. Accepts natural language queries about Bacardi brand performance.'
    path: 'retail-pulse-a2a'
    protocols: ['https']
    type: 'a2a'
    isAgent: true
    subscriptionRequired: true
    subscriptionKeyParameterNames: {
      header: 'api-key'
      query: 'api-key'
    }
    agent: {
      id: 'retail-pulse-analytics'
      name: 'Patron Pulse Analytics Agent'
      description: 'AI-powered brand analytics agent for Bacardi portfolio. Provides depletion analysis, field sentiment, and market insights.'
      version: '1.0.0'
    }
    a2aProperties: {
      agentCardBackendUrl: '${gatewayUrl}/retail-pulse-a2a'
      agentCardPath: '/.well-known/agent.json'
    }
    jsonRpcProperties: {
      backendUrl: '${gatewayUrl}/retail-pulse-a2a'
      path: '/rpc'
    }
  }
}

output a2aApiId string = a2aApi.id
