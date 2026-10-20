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

@description('APIM API id of the governed MCP server (issue #335). Synchronization names the API Center API after it.')
param mcpServerApiName string = 'retail-pulse-mcp-server'

@description('Remote MCP endpoint served by APIM (https://<gateway>/retail-pulse-mcp/mcp). The only runtime endpoint; carries no credentials.')
param mcpEndpoint string

@description('Lifecycle stage of the MCP API version registered in API Center.')
@allowed([
  'design'
  'development'
  'testing'
  'preview'
  'production'
  'deprecated'
  'retired'
])
param mcpLifecycleStage string = 'production'

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

// ── Retail Pulse MCP registration (issue #336) ─────────────────────────────
// Registration metadata that APIM synchronization does not supply: version,
// remote endpoint, API Center environment/deployment linkage, lifecycle, tool
// schemas and discovery metadata. Declared under the same API id that APIM
// synchronization uses, so the synchronized API and this overlay converge on
// one catalog entry. APIM remains the only runtime endpoint (one-way sync).
var mcpVersionName = 'v1-0'
var mcpDefinitionName = 'mcp-tools'
var mcpEnvironmentName = 'apim-gateway'
var mcpDeploymentName = 'apim-remote-mcp'
var approvedTools = [
  'get_inventory'
  'get_stockout_risk'
  'get_supply_disruptions'
  'get_demand_forecast'
]
var toolSchemas = {
  get_inventory: {
    readOnly: true
    inputSchema: {
      type: 'object'
      properties: {
        brand: { type: 'string' }
        region: { type: 'string' }
        category: { type: 'string' }
        status: { type: 'string' }
      }
      additionalProperties: false
    }
  }
  get_stockout_risk: {
    readOnly: true
    inputSchema: {
      type: 'object'
      properties: {
        region: { type: 'string' }
        limit: { type: 'integer', minimum: 1, maximum: 50 }
      }
      additionalProperties: false
    }
  }
  get_supply_disruptions: {
    readOnly: true
    inputSchema: {
      type: 'object'
      properties: {
        brand: { type: 'string' }
        region: { type: 'string' }
        severity: { type: 'string' }
        activeOnly: { type: 'boolean' }
      }
      additionalProperties: false
    }
  }
  get_demand_forecast: {
    readOnly: true
    inputSchema: {
      type: 'object'
      properties: {
        brand: { type: 'string' }
        region: { type: 'string' }
      }
      required: ['brand']
      additionalProperties: false
    }
  }
}

resource mcpMetadataSchema 'Microsoft.ApiCenter/services/metadataSchemas@2024-03-01' = {
  parent: apiCenter
  name: 'retailPulseMcp'
  properties: {
    assignedTo: [
      {
        entity: 'api'
        required: false
      }
    ]
    schema: string({
      title: 'retailPulseMcp'
      type: 'object'
      properties: {
        approvedTools: { type: 'array', items: { type: 'string' } }
        toolSchemas: { type: 'object' }
        readOnly: { type: 'boolean' }
        runtime: { type: 'string' }
      }
    })
  }
}

resource mcpApi 'Microsoft.ApiCenter/services/workspaces/apis@2024-03-01' = {
  parent: defaultWorkspace
  name: mcpServerApiName
  properties: {
    title: 'Retail Pulse MCP Server'
    kind: 'mcp'
    summary: 'Governed read-only Retail Pulse MCP server: inventory, stockout risk, supply disruptions, demand forecast.'
    description: 'Remote MCP server served by Azure API Management. Four read-only tools; callers need an approved APIM subscription and a gateway-audience Entra token.'
    externalDocumentation: [
      {
        title: 'Retail Pulse MCP tools'
        url: 'https://github.com/swigerb/retail-pulse/blob/dev/docs/MCP-TOOLS.md'
      }
    ]
    customProperties: {
      retailPulseMcp: {
        approvedTools: approvedTools
        toolSchemas: toolSchemas
        readOnly: true
        runtime: 'azure-api-management'
      }
    }
  }
  dependsOn: [
    mcpMetadataSchema
    apimSource
  ]
}

resource mcpVersion 'Microsoft.ApiCenter/services/workspaces/apis/versions@2024-03-01' = {
  parent: mcpApi
  name: mcpVersionName
  properties: {
    title: 'v1'
    lifecycleStage: mcpLifecycleStage
  }
}

resource mcpDefinition 'Microsoft.ApiCenter/services/workspaces/apis/versions/definitions@2024-03-01' = {
  parent: mcpVersion
  name: mcpDefinitionName
  properties: {
    title: 'Retail Pulse MCP tools (read-only)'
    description: 'Approved tools: ${join(approvedTools, ', ')}.'
  }
}

resource mcpEnvironment 'Microsoft.ApiCenter/services/workspaces/environments@2024-03-01' = {
  parent: defaultWorkspace
  name: mcpEnvironmentName
  properties: {
    title: 'Retail Pulse APIM gateway'
    description: 'Azure API Management gateway hosting the governed Retail Pulse MCP runtime.'
    kind: 'production'
    server: {
      type: 'Azure API Management'
      managementPortalUri: [
        '${environment().portal}/#@/resource${apim.id}'
      ]
    }
    onboarding: {
      developerPortalUri: [
        apim.properties.developerPortalUrl
      ]
    }
  }
}

resource mcpDeployment 'Microsoft.ApiCenter/services/workspaces/apis/deployments@2024-03-01' = {
  parent: mcpApi
  name: mcpDeploymentName
  properties: {
    title: 'APIM remote MCP endpoint'
    description: 'Remote MCP endpoint served by APIM; the only runtime endpoint.'
    environmentId: '/workspaces/default/environments/${mcpEnvironment.name}'
    definitionId: '/workspaces/default/apis/${mcpApi.name}/versions/${mcpVersion.name}/definitions/${mcpDefinition.name}'
    state: 'active'
    server: {
      runtimeUri: [
        mcpEndpoint
      ]
    }
  }
}

output name string = apiCenter.name
output resourceId string = apiCenter.id
output principalId string = apiCenter.identity.principalId
output apimSourceName string = apimSource.name
output apimSourceResourceId string = apimSource.id
output mcpApiName string = mcpApi.name
output mcpVersionName string = mcpVersion.name
output mcpEnvironmentName string = mcpEnvironment.name
output mcpDeploymentName string = mcpDeployment.name
