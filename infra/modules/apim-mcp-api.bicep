// Governed APIM "Expose REST API as MCP server" runtime (ADR 017, issue #335).
//
// APIM authenticates to the public Retail Pulse API with its system-assigned managed identity.
// The internal MCP server container app and its API key are never referenced here; no
// secret of any kind is stored in APIM named values, policy, or outputs.
//
// The MCP server resource requires API version 2025-09-01-preview or later (Developer SKU is
// supported per Microsoft Learn). There is no REST fallback: if this resource cannot be created
// or tools/list != 4, acceptance fails (ADR 017, "Transport and SKU").

@description('Name of the APIM instance that exposes the MCP server')
param apimName string

@description('FQDN (no scheme) of the public Retail Pulse API container app')
param retailPulseApiFqdn string

@description('Audience (Application ID URI) the Retail Pulse API validates; used for the managed identity token')
param retailPulseApiAudience string

@description('Audience for MCP callers (app registration identifier URI created by scripts/Setup-McpGatewayIdentity.ps1)')
param mcpGatewayAudience string = 'api://retailpulse-mcp-gateway'

@description('Entra tenant id used to pin the caller token issuer')
param tenantId string

@description('Application Insights logger id (service/loggers) used for MCP API diagnostics')
param appInsightsLoggerId string

var mcpServerId = 'retail-pulse-mcp-server'
var backingApiId = 'retail-pulse-mcp-backing'
var productId = 'retailpulse-mcp'

resource apim 'Microsoft.ApiManagement/service@2025-09-01-preview' existing = {
  name: apimName
}

// Non-secret configuration only: audiences, FQDN and tenant id.
resource nvApiFqdn 'Microsoft.ApiManagement/service/namedValues@2025-09-01-preview' = {
  parent: apim
  name: 'retailPulseApiFqdn'
  properties: {
    displayName: 'retailPulseApiFqdn'
    value: retailPulseApiFqdn
    secret: false
  }
}

resource nvApiAudience 'Microsoft.ApiManagement/service/namedValues@2025-09-01-preview' = {
  parent: apim
  name: 'retailPulseApiAudience'
  properties: {
    displayName: 'retailPulseApiAudience'
    value: retailPulseApiAudience
    secret: false
  }
}

resource nvGatewayAudience 'Microsoft.ApiManagement/service/namedValues@2025-09-01-preview' = {
  parent: apim
  name: 'mcpGatewayAudience'
  properties: {
    displayName: 'mcpGatewayAudience'
    value: mcpGatewayAudience
    secret: false
  }
}

resource nvTenantId 'Microsoft.ApiManagement/service/namedValues@2025-09-01-preview' = {
  parent: apim
  name: 'tenantId'
  properties: {
    displayName: 'tenantId'
    value: tenantId
    secret: false
  }
}

// Backing REST API. Only the four approved GET operations are declared (no wildcard operations,
// no implicit import). It is not bound to any product, so it is not callable as plain REST.
resource backingApi 'Microsoft.ApiManagement/service/apis@2025-09-01-preview' = {
  parent: apim
  name: backingApiId
  properties: {
    displayName: 'Retail Pulse MCP backing API (read-only)'
    description: 'Backs the Retail Pulse MCP tools. Not exposed as REST.'
    path: 'retail-pulse-mcp-backing'
    protocols: ['https']
    serviceUrl: 'https://${retailPulseApiFqdn}'
    subscriptionRequired: true
    apiType: 'http'
  }
}

resource opInventory 'Microsoft.ApiManagement/service/apis/operations@2025-09-01-preview' = {
  parent: backingApi
  name: 'get_inventory'
  properties: {
    displayName: 'get_inventory'
    description: 'Item-level inventory (store/SKU identifiers only). Read-only.'
    method: 'GET'
    urlTemplate: '/api/supply/inventory'
    request: {
      queryParameters: [
        { name: 'brand', type: 'string', required: false, description: 'Brand filter' }
        { name: 'region', type: 'string', required: false, description: 'Region filter' }
        { name: 'category', type: 'string', required: false, description: 'Category filter' }
        { name: 'status', type: 'string', required: false, description: 'Stock status filter' }
      ]
    }
    responses: [{ statusCode: 200 }]
  }
}

resource opStockout 'Microsoft.ApiManagement/service/apis/operations@2025-09-01-preview' = {
  parent: backingApi
  name: 'get_stockout_risk'
  properties: {
    displayName: 'get_stockout_risk'
    description: 'Portfolio stockout risks ordered by urgency. limit is clamped to 1-50.'
    method: 'GET'
    urlTemplate: '/api/stores/stockout-risks'
    request: {
      queryParameters: [
        { name: 'region', type: 'string', required: false, description: 'Region filter' }
        { name: 'limit', type: 'integer', required: false, description: 'Maximum rows (1-50, default 20)' }
      ]
    }
    responses: [{ statusCode: 200 }]
  }
}

resource opDisruptions 'Microsoft.ApiManagement/service/apis/operations@2025-09-01-preview' = {
  parent: backingApi
  name: 'get_supply_disruptions'
  properties: {
    displayName: 'get_supply_disruptions'
    description: 'Supply chain disruptions. Read-only.'
    method: 'GET'
    urlTemplate: '/api/supply/disruptions'
    request: {
      queryParameters: [
        { name: 'brand', type: 'string', required: false, description: 'Brand filter' }
        { name: 'region', type: 'string', required: false, description: 'Region filter' }
        { name: 'severity', type: 'string', required: false, description: 'Severity filter' }
        { name: 'activeOnly', type: 'boolean', required: false, description: 'Only active disruptions (default true)' }
      ]
    }
    responses: [{ statusCode: 200 }]
  }
}

resource opForecast 'Microsoft.ApiManagement/service/apis/operations@2025-09-01-preview' = {
  parent: backingApi
  name: 'get_demand_forecast'
  properties: {
    displayName: 'get_demand_forecast'
    description: 'Demand forecast for a brand and region. Read-only.'
    method: 'GET'
    urlTemplate: '/api/demand/forecast'
    request: {
      queryParameters: [
        { name: 'brand', type: 'string', required: true, description: 'Brand (required)' }
        { name: 'region', type: 'string', required: false, description: 'Region (default National)' }
      ]
    }
    responses: [{ statusCode: 200 }]
  }
}

resource backingPolicy 'Microsoft.ApiManagement/service/apis/policies@2025-09-01-preview' = {
  parent: backingApi
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('apim-mcp-backing-policy.xml')
  }
}

resource mcpServer 'Microsoft.ApiManagement/service/apis@2025-09-01-preview' = {
  parent: apim
  name: mcpServerId
  properties: {
    type: 'mcp'
    displayName: 'Retail Pulse MCP Server'
    description: 'Governed read-only Retail Pulse tools: inventory, stockout risk, supply disruptions, demand forecast.'
    path: 'retail-pulse-mcp'
    protocols: ['https']
    subscriptionRequired: true
    subscriptionKeyParameterNames: {
      header: 'Ocp-Apim-Subscription-Key'
      query: 'subscription-key'
    }
  }
}

resource toolInventory 'Microsoft.ApiManagement/service/apis/tools@2025-09-01-preview' = {
  parent: mcpServer
  name: 'get_inventory'
  properties: {
    displayName: 'get_inventory'
    description: 'Item-level inventory. Read-only.'
    operationId: opInventory.id
  }
}

resource toolStockout 'Microsoft.ApiManagement/service/apis/tools@2025-09-01-preview' = {
  parent: mcpServer
  name: 'get_stockout_risk'
  dependsOn: [toolInventory]
  properties: {
    displayName: 'get_stockout_risk'
    description: 'Stockout risks ordered by urgency. Read-only.'
    operationId: opStockout.id
  }
}

resource toolDisruptions 'Microsoft.ApiManagement/service/apis/tools@2025-09-01-preview' = {
  parent: mcpServer
  name: 'get_supply_disruptions'
  dependsOn: [toolStockout]
  properties: {
    displayName: 'get_supply_disruptions'
    description: 'Supply disruptions. Read-only.'
    operationId: opDisruptions.id
  }
}

resource toolForecast 'Microsoft.ApiManagement/service/apis/tools@2025-09-01-preview' = {
  parent: mcpServer
  name: 'get_demand_forecast'
  dependsOn: [toolDisruptions]
  properties: {
    displayName: 'get_demand_forecast'
    description: 'Demand forecast for a brand and region. Read-only.'
    operationId: opForecast.id
  }
}

resource mcpPolicy 'Microsoft.ApiManagement/service/apis/policies@2025-09-01-preview' = {
  parent: mcpServer
  name: 'policy'
  dependsOn: [toolForecast, nvApiFqdn, nvApiAudience, nvGatewayAudience, nvTenantId]
  properties: {
    format: 'rawxml'
    value: loadTextContent('apim-mcp-policy.xml')
  }
}

// Approval-required product: keys identify the calling team for quotas and logs.
resource mcpProduct 'Microsoft.ApiManagement/service/products@2025-09-01-preview' = {
  parent: apim
  name: productId
  properties: {
    displayName: 'Retail Pulse MCP (read-only)'
    description: 'Governed read-only Retail Pulse MCP tools.'
    subscriptionRequired: true
    approvalRequired: true
    subscriptionsLimit: 1
    state: 'published'
  }
}

resource productBinding 'Microsoft.ApiManagement/service/products/apis@2025-09-01-preview' = {
  parent: mcpProduct
  name: mcpServerId
  dependsOn: [mcpServer]
}

// Diagnostics: subscription, operation, status and latency only. No request/response bodies and
// no Authorization / Ocp-Apim-Subscription-Key headers are logged.
resource mcpDiagnostics 'Microsoft.ApiManagement/service/apis/diagnostics@2025-09-01-preview' = {
  parent: mcpServer
  name: 'applicationinsights'
  dependsOn: [mcpPolicy]
  properties: {
    loggerId: appInsightsLoggerId
    alwaysLog: 'allErrors'
    logClientIp: false
    metrics: true
    sampling: {
      samplingType: 'fixed'
      percentage: 100
    }
    verbosity: 'information'
    frontend: {
      request: { headers: [], body: { bytes: 0 } }
      response: { headers: [], body: { bytes: 0 } }
    }
    backend: {
      request: { headers: [], body: { bytes: 0 } }
      response: { headers: [], body: { bytes: 0 } }
    }
  }
}

output mcpServerName string = mcpServer.name
output mcpEndpoint string = '${apim.properties.gatewayUrl}/retail-pulse-mcp/mcp'
output mcpProductName string = mcpProduct.name
output approvedTools array = [
  'get_inventory'
  'get_stockout_risk'
  'get_supply_disruptions'
  'get_demand_forecast'
]
