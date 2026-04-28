@description('Name of existing APIM instance')
param apimName string = 'bsapim-dev-northcentralus-001'

@description('Backend URL for the MCP server')
param mcpServerUrl string = 'http://localhost:5200'

resource apim 'Microsoft.ApiManagement/service@2024-06-01-preview' existing = {
  name: apimName
}

// Backend for MCP server
resource mcpBackend 'Microsoft.ApiManagement/service/backends@2024-06-01-preview' = {
  parent: apim
  name: 'retail-pulse-mcp'
  properties: {
    title: 'Retail Pulse MCP Server'
    protocol: 'http'
    url: mcpServerUrl
  }
}

// MCP API - type 'mcp' so the portal discovers it
// backendId must be the backend name (not full resource ID)
resource mcpApi 'Microsoft.ApiManagement/service/apis@2025-03-01-preview' = {
  parent: apim
  name: 'retail-pulse-mcp-server'
  properties: {
    displayName: 'Retail Pulse MCP Server'
    description: 'Retail Pulse MCP tools for brand analytics (GetDepletionStats, GetFieldSentiment)'
    path: 'retail-pulse-mcp'
    protocols: ['https']
    type: 'mcp'
    subscriptionRequired: true
    subscriptionKeyParameterNames: {
      header: 'api-key'
      query: 'api-key'
    }
    backendId: mcpBackend.name
  }
}

output mcpApiId string = mcpApi.id
