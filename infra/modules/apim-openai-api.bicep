@description('Name of the APIM instance that fronts Azure OpenAI')
param apimName string

@description('Principal ID of the APIM managed identity')
param apimPrincipalId string

@description('Name of the Azure AI Foundry / Cognitive Services account')
param aiFoundryAccountName string

@description('Resource group containing the Azure AI Foundry / Cognitive Services account')
param aiFoundryResourceGroupName string

@description('Inference API path in APIM')
param inferenceApiPath string = 'inference'

@description('Display name for the APIM subscription that callers use for the inference API')
param subscriptionDisplayName string = 'Retail Pulse Demo Inference'

@description('Name for the APIM subscription resource')
param subscriptionName string = 'retail-pulse-inference-sub'

@description('Tokens per minute limit enforced per APIM subscription')
param tokensPerMinute int = 80000

@description('Enables the two-backend priority pool. When false (default) the single primary backend is used directly, which is valid for local or constrained deployments.')
param backendPoolEnabled bool = false

@description('Enables the demo-only failover trigger (#330). Disabled by default, requires backendPoolEnabled, and fails closed unless failoverDemoKey is at least 32 characters.')
param failoverDemoEnabled bool = false

@secure()
@description('Operator secret that authorizes the failover demo trigger. Never given to ordinary callers; store it in a secret manager.')
param failoverDemoKey string = ''

@description('Name of the secondary Azure AI Foundry / Cognitive Services account. Required (and must differ from the primary) when backendPoolEnabled is true.')
param secondaryAiFoundryAccountName string = ''

@description('Resource group containing the secondary Azure AI Foundry account. Defaults to the primary resource group.')
param secondaryAiFoundryResourceGroupName string = aiFoundryResourceGroupName

var cognitiveServicesOpenAIUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

resource apim 'Microsoft.ApiManagement/service@2024-06-01-preview' existing = {
  name: apimName
}

resource aiFoundry 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: aiFoundryAccountName
  scope: resourceGroup(aiFoundryResourceGroupName)
}

module aiFoundryRoleAssignment './apim-openai-role-assignment.bicep' = {
  name: 'apim-openai-user-role-assignment'
  scope: resourceGroup(aiFoundryResourceGroupName)
  params: {
    aiServicesName: aiFoundryAccountName
    principalId: apimPrincipalId
    roleDefinitionId: cognitiveServicesOpenAIUserRoleId
  }
}

var poolEnabled = backendPoolEnabled
var primaryBackendName = 'retail-pulse-foundry'
var secondaryBackendName = 'retail-pulse-foundry-secondary'
var poolBackendName = 'retail-pulse-foundry-pool'

// Policies address the pool when enabled; otherwise the single primary backend.
var routeBackendId = poolEnabled ? poolBackendName : primaryBackendName

var failoverDemoActive = failoverDemoEnabled && poolEnabled && length(failoverDemoKey) >= 32

var failoverDemoBackendFragment = replace(
  replace(loadTextContent('apim-openai-failover-demo-backend.xml'), '{demo-primary-backend}', primaryBackendName),
  '{demo-secondary-backend}',
  secondaryBackendName)

var throttlingCircuitBreaker = {
  rules: [
    {
      name: 'throttlingRule'
      failureCondition: {
        count: 1
        errorReasons: []
        interval: 'PT1M'
        statusCodeRanges: [
          {
            min: 429
            max: 429
          }
        ]
      }
      tripDuration: 'PT1M'
      acceptRetryAfter: true
    }
  ]
}

var poolCircuitBreaker = {
  rules: [
    {
      name: 'poolFailureRule'
      failureCondition: {
        count: 1
        errorReasons: []
        interval: 'PT1M'
        statusCodeRanges: [
          {
            min: 429
            max: 429
          }
          {
            min: 500
            max: 599
          }
        ]
      }
      tripDuration: 'PT1M'
      acceptRetryAfter: true
    }
  ]
}

// A pool retry re-enters backend selection after the response that opens a
// member's circuit breaker. Single-backend mode retains the existing base
// backend behavior and never retries 5xx responses.
var backendPolicy = failoverDemoActive
  ? replace(failoverDemoBackendFragment, '{demo-primary-backend}', primaryBackendName)
  : poolEnabled
  ? '<retry condition="@(context.Response != null &amp;&amp; (context.Response.StatusCode == 429 || context.Response.StatusCode &gt;= 500))" count="1" interval="1" first-fast-retry="true"><forward-request buffer-request-body="true" /></retry>'
  : '<base />'

resource backend 'Microsoft.ApiManagement/service/backends@2024-06-01-preview' = {
  parent: apim
  name: primaryBackendName
  properties: {
    title: 'Retail Pulse AI Foundry Backend'
    protocol: 'http'
    url: '${aiFoundry.properties.endpoint}openai'
    credentials: {
      #disable-next-line BCP037
      managedIdentity: {
        resource: 'https://cognitiveservices.azure.com'
      }
    }
    circuitBreaker: poolEnabled ? poolCircuitBreaker : throttlingCircuitBreaker
  }
}

resource secondaryAiFoundry 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = if (poolEnabled) {
  name: secondaryAiFoundryAccountName
  scope: resourceGroup(secondaryAiFoundryResourceGroupName)
}

module secondaryRoleAssignment './apim-openai-role-assignment.bicep' = if (poolEnabled) {
  name: 'apim-openai-user-role-assignment-secondary'
  scope: resourceGroup(secondaryAiFoundryResourceGroupName)
  params: {
    aiServicesName: secondaryAiFoundryAccountName
    principalId: apimPrincipalId
    roleDefinitionId: cognitiveServicesOpenAIUserRoleId
  }
}

resource secondaryBackend 'Microsoft.ApiManagement/service/backends@2024-06-01-preview' = if (poolEnabled) {
  parent: apim
  name: secondaryBackendName
  properties: {
    title: 'Retail Pulse AI Foundry Backend (secondary)'
    protocol: 'http'
    url: '${secondaryAiFoundry!.properties.endpoint}openai'
    credentials: {
      #disable-next-line BCP037
      managedIdentity: {
        resource: 'https://cognitiveservices.azure.com'
      }
    }
    circuitBreaker: poolCircuitBreaker
  }
}

// Priority pool: the secondary only receives traffic while the primary's
// circuit breaker is open. The client endpoint is unchanged.
resource backendPool 'Microsoft.ApiManagement/service/backends@2024-06-01-preview' = if (poolEnabled) {
  parent: apim
  name: poolBackendName
  properties: {
    title: 'Retail Pulse AI Foundry Backend Pool'
    description: 'Priority failover pool: primary then secondary Azure AI model backends'
    #disable-next-line BCP037
    type: 'Pool'
    #disable-next-line BCP037
    pool: {
      services: [
        {
          id: backend.id
          priority: 1
          weight: 1
        }
        {
          id: secondaryBackend.id
          priority: 2
          weight: 1
        }
      ]
    }
  }
}

resource api 'Microsoft.ApiManagement/service/apis@2024-06-01-preview' = {
  parent: apim
  name: 'retail-pulse-inference-api'
  properties: {
    displayName: 'Retail Pulse Inference API'
    path: '${inferenceApiPath}/openai'
    protocols: [
      'https'
    ]
    subscriptionRequired: true
    subscriptionKeyParameterNames: {
      header: 'api-key'
      query: 'api-key'
    }
    format: 'openapi+json'
    value: string(loadJsonContent('apim-openai-spec.json'))
  }
}

// Named values back the demo gate. The key is secret; "armed" defaults to false on every
// deployment so redeploying is itself a reset. Operators flip armed with az apim nv update.
resource demoKeyNamedValue 'Microsoft.ApiManagement/service/namedValues@2024-06-01-preview' = if (failoverDemoActive) {
  parent: apim
  name: 'rp-failover-demo-key'
  properties: {
    displayName: 'rp-failover-demo-key'
    secret: true
    value: failoverDemoKey
  }
}

resource demoArmedNamedValue 'Microsoft.ApiManagement/service/namedValues@2024-06-01-preview' = if (failoverDemoActive) {
  parent: apim
  name: 'rp-failover-demo-armed'
  properties: {
    displayName: 'rp-failover-demo-armed'
    secret: false
    value: 'false'
  }
}

resource apiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-06-01-preview' = {
  parent: api
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: replace(
      replace(
        replace(
          replace(
            replace(loadTextContent('apim-openai-policy.xml'), '{backend-id}', routeBackendId),
            '{demo-inbound}',
            failoverDemoActive
              ? loadTextContent('apim-openai-failover-demo-inbound.xml')
              : '        <set-header name="x-rp-failover-demo" exists-action="delete" />'),
          '{demo-outbound}',
          failoverDemoActive ? loadTextContent('apim-openai-failover-demo-outbound.xml') : ''),
        '{backend-policy}',
        backendPolicy),
      '{tokens-per-minute}',
      string(tokensPerMinute))
  }
  dependsOn: poolEnabled ? [
    backend
    secondaryBackend
    backendPool
    secondaryRoleAssignment
    demoKeyNamedValue
    demoArmedNamedValue
  ] : [
    backend
  ]
}

// The `listModels` readiness probe overrides the API-scope policy so the
// AI-Gateway token policies — which are defined only for inference payloads —
// never run against a token-free GET. See apim-openai-models-policy.xml.
resource listModelsOperation 'Microsoft.ApiManagement/service/apis/operations@2024-06-01-preview' existing = {
  parent: api
  name: 'listModels'
}

resource listModelsPolicy 'Microsoft.ApiManagement/service/apis/operations/policies@2024-06-01-preview' = {
  parent: listModelsOperation
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: replace(loadTextContent('apim-openai-models-policy.xml'), '{backend-id}', routeBackendId)
  }
  dependsOn: [
    apiPolicy
  ]
}

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
    // `metrics: true` is what actually routes `azure-openai-emit-token-metric`
    // custom metrics (namespace `RetailPulse`, dimensions API/Operation/
    // Subscription ID) into Application Insights `customMetrics` / AppMetrics.
    // Without this flag the emit-token policy fires but the metric channel is
    // silently dropped — request logs still flow but the AI Gateway token
    // dashboard sees zero rows.
    metrics: true
    verbosity: 'information'
    logClientIp: true
    frontend: {
      request: {
        body: {
          bytes: 8192
        }
      }
      response: {
        body: {
          bytes: 0
        }
      }
    }
    backend: {
      request: {
        body: {
          bytes: 8192
        }
      }
      response: {
        body: {
          bytes: 0
        }
      }
    }
  }
}

resource azureMonitorLogger 'Microsoft.ApiManagement/service/loggers@2024-06-01-preview' existing = {
  parent: apim
  name: 'azuremonitor'
}

resource apiAzureMonitorDiagnostics 'Microsoft.ApiManagement/service/apis/diagnostics@2024-06-01-preview' = {
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

resource subscription 'Microsoft.ApiManagement/service/subscriptions@2024-06-01-preview' = {
  parent: apim
  name: subscriptionName
  properties: {
    allowTracing: true
    displayName: subscriptionDisplayName
    scope: api.id
    state: 'active'
  }
}

output inferenceApiName string = api.name
output inferenceApiPath string = api.properties.path
// NOTE: The Azure OpenAI SDK (AzureOpenAIClient) appends '/openai/deployments/{id}/...'
// to whatever endpoint it is given. The APIM API path already ends in '/openai'
// (see `path: '${inferenceApiPath}/openai'` above) purely so the OpenAPI import
// matches the real AOAI '/openai/deployments/...' route shape. If this output
// included that trailing '/openai' segment, the SDK would double it up into
// '.../inference/openai/openai/deployments/...', which APIM rejects with 404
// OperationNotFound (root cause of the 2026-08-11 production incident — see
// .squad/decisions/inbox/kroger-incident-*.md). Emit only the base path
// ('.../inference') so the SDK's own '/openai/deployments/...' suffix lands on
// the API's actual registered path.
output inferenceEndpoint string = '${apim.properties.gatewayUrl}/${inferenceApiPath}'
output subscriptionName string = subscription.name

// These outputs describe deployed configuration, not the backend that served
// an individual request. Per-request route evidence must remain unknown until
// APIM exposes a documented pool-member signal and a live verifier proves it.
output backendPoolEnabled bool = poolEnabled
output failoverDemoEnabled bool = failoverDemoActive
output routeBackendId string = routeBackendId
output primaryBackendId string = backend.name
output secondaryBackendId string = poolEnabled ? secondaryBackendName : ''

// Live APIM subscription primary key, resolved at Bicep deploy time via
// listSecrets(). Consumed by container-apps.bicep to declaratively bind the
// API container app to APIM as an ACA secret + `secretRef` env var — so
// `azd provision` re-asserts the APIM wiring on every run and a subsequent
// re-provision cannot silently strip the AI Gateway configuration off the
// active revision. Marked @secure() so ARM masks it in deployment outputs.
@secure()
output subscriptionKey string = subscription.listSecrets().primaryKey
