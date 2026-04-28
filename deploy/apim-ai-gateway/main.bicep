// ---------------------------------------------------------------------------
// Retail Pulse – APIM AI Gateway Infrastructure
// Deploys an inference API on an EXISTING APIM instance backed by Azure AI Foundry.
// ---------------------------------------------------------------------------

@description('Name of existing APIM instance')
param apimName string = 'bsapim-dev-northcentralus-001'

@description('Name of the Azure AI Services / Foundry resource')
param aiServicesName string = 'bs-dev-swedencentral-aoai'

@description('Resource group of the AI Services resource')
param aiServicesResourceGroup string = 'rg-dev-swedencentral-aoai'

@description('Inference API path in APIM')
param inferenceApiPath string = 'inference'

@description('Display name for the APIM subscription')
param subscriptionDisplayName string = 'Retail Pulse Demo'

@description('Tokens per minute limit')
param tokensPerMinute int = 10000

// ---------------------------------------------------------------------------
// Existing resources
// ---------------------------------------------------------------------------

resource apim 'Microsoft.ApiManagement/service@2024-06-01-preview' existing = {
  name: apimName
}

resource aiServices 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: aiServicesName
  scope: resourceGroup(aiServicesResourceGroup)
}

// ---------------------------------------------------------------------------
// Role assignment – give APIM managed identity "Cognitive Services OpenAI User"
// on the AI Services resource (cross-RG via module)
// ---------------------------------------------------------------------------

var cognitiveServicesOpenAIUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

module roleAssignment 'role-assignment.bicep' = {
  name: 'apim-ai-role-assignment'
  scope: resourceGroup(aiServicesResourceGroup)
  params: {
    aiServicesName: aiServicesName
    principalId: apim.identity.principalId
    roleDefinitionId: cognitiveServicesOpenAIUserRoleId
  }
}

// ---------------------------------------------------------------------------
// Backend – points to Azure AI Foundry OpenAI endpoint
// ---------------------------------------------------------------------------

resource backend 'Microsoft.ApiManagement/service/backends@2024-06-01-preview' = {
  parent: apim
  name: 'retail-pulse-foundry'
  properties: {
    title: 'Retail Pulse AI Foundry Backend'
    protocol: 'http'
    url: '${aiServices.properties.endpoint}openai'
    credentials: {
      #disable-next-line BCP037
      managedIdentity: {
        resource: 'https://cognitiveservices.azure.com'
      }
    }
    circuitBreaker: {
      rules: [
        {
          name: 'throttlingRule'
          failureCondition: {
            count: 1
            errorReasons: []
            interval: 'PT1M'
            statusCodeRanges: [
              { min: 429, max: 429 }
            ]
          }
          tripDuration: 'PT1M'
          acceptRetryAfter: true
        }
      ]
    }
  }
}

// ---------------------------------------------------------------------------
// API – OpenAI chat/completions proxy
// ---------------------------------------------------------------------------

resource api 'Microsoft.ApiManagement/service/apis@2024-06-01-preview' = {
  parent: apim
  name: 'retail-pulse-inference-api'
  properties: {
    displayName: 'Retail Pulse Inference API'
    path: '${inferenceApiPath}/openai'
    protocols: [ 'https' ]
    subscriptionRequired: true
    subscriptionKeyParameterNames: {
      header: 'api-key'
      query: 'api-key'
    }
    format: 'openapi+json'
    value: string(loadJsonContent('openai-spec.json'))
  }
}

// ---------------------------------------------------------------------------
// API Policy – Azure OpenAI token limits + metric emission
// ---------------------------------------------------------------------------

resource apiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-06-01-preview' = {
  parent: api
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: replace(loadTextContent('policy.xml'), '{backend-id}', backend.name)
  }
}

// ---------------------------------------------------------------------------
// API-level Application Insights diagnostics (100% sampling, Information)
// Required for GenAI analytics — token metrics in customMetrics table
// ---------------------------------------------------------------------------

resource appInsightsLogger 'Microsoft.ApiManagement/service/loggers@2024-06-01-preview' existing = {
  parent: apim
  name: 'appinsights-logger'
}

resource apiAppInsightsDiagnostics 'Microsoft.ApiManagement/service/apis/diagnostics@2024-06-01-preview' = {
  parent: api
  name: 'applicationinsights'
  properties: {
    loggerId: appInsightsLogger.id
    sampling: {
      samplingType: 'fixed'
      percentage: 100
    }
    verbosity: 'information'
    logClientIp: true
    frontend: {
      request: { body: { bytes: 8192 } }
      response: { body: { bytes: 0 } }
    }
    backend: {
      request: { body: { bytes: 8192 } }
      response: { body: { bytes: 0 } }
    }
  }
}

// ---------------------------------------------------------------------------
// API-level Azure Monitor diagnostics with largeLanguageModel: enabled
// THIS is the trigger for ApiManagementGatewayLlmLog table population.
// Without this, APIM treats LLM traffic as generic HTTP — no token/model parsing.
// ---------------------------------------------------------------------------

resource azureMonitorLogger 'Microsoft.ApiManagement/service/loggers@2024-06-01-preview' existing = {
  parent: apim
  name: 'azuremonitor'
}

resource apiLlmDiagnostics 'Microsoft.ApiManagement/service/apis/diagnostics@2024-06-01-preview' = {
  parent: api
  name: 'azuremonitor'
  properties: {
    loggerId: azureMonitorLogger.id
    alwaysLog: 'allErrors'
    sampling: {
      samplingType: 'fixed'
      percentage: 100
    }
    logClientIp: true
    #disable-next-line BCP037
    largeLanguageModel: {
      logs: 'enabled'
      requests: {
        maxSizeInBytes: 32768
        messages: 'all'
      }
      responses: {
        maxSizeInBytes: 32768
        messages: 'all'
      }
    }
  }
}

// ---------------------------------------------------------------------------
// Subscription – scoped to the inference API
// ---------------------------------------------------------------------------

resource subscription 'Microsoft.ApiManagement/service/subscriptions@2024-06-01-preview' = {
  parent: apim
  name: 'retail-pulse-sub'
  properties: {
    displayName: subscriptionDisplayName
    scope: api.id
    state: 'active'
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

output apimGatewayUrl string = apim.properties.gatewayUrl

@secure()
output subscriptionKey string = subscription.listSecrets().primaryKey

output inferenceEndpoint string = '${apim.properties.gatewayUrl}/${inferenceApiPath}/openai'
