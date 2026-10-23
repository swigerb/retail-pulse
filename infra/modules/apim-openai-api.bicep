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

@description('Tokens per minute limit enforced per APIM subscription (one counter per store, channel, application or B2B subscription). Production default.')
param tokensPerMinute int = 80000

@description('Fictional store/channel/application/B2B budget scopes (#332). Each entry creates one APIM product (rp-budget-<kind>-<id>) and one subscription with service-generated keys that are never output. Counter keys derive only from the APIM subscription.')
param tokenBudgetScopes array = []

@description('Deploys the operator-only token budget demo (#332): tiny-TPM named value, arm switch and counter generation. Off by default; even when deployed it stays inactive until rp-budget-demo-armed is set to true.')
param tokenBudgetDemoEnabled bool = false

@description('Deliberately tiny static tokens-per-minute used only while the budget demo is armed. Never affects the normal limit.')
@minValue(1)
@maxValue(500)
param tokenBudgetDemoTokensPerMinute int = 50

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

@description('Enables APIM-level Prompt Shields and text harm enforcement (#331) against the existing Content Safety account. Off by default; application-level checks always remain.')
param gatewaySafetyEnabled bool = false

@description('Name of the existing Azure AI Content Safety account. Required when gatewaySafetyEnabled is true.')
param contentSafetyAccountName string = ''

@description('Resource group of the existing Content Safety account. Defaults to the APIM resource group.')
param contentSafetyResourceGroupName string = resourceGroup().name

@description('Dependency-failure behaviour: demo continues on explicit timeout/429/5xx with allowed_on_safety_error evidence; regulated fails closed with a generic 503. Auth/RBAC misconfiguration always fails closed.')
@allowed([
  'demo'
  'regulated'
])
param gatewaySafetyMode string = 'demo'

@description('Regulated-retail mode (#337) for the synthetic pharmacy scenario: forces gateway safety on in fail-closed regulated mode, disables every APIM body capture, masks the client-visible request id and stamps a non-HIPAA profile header. A configuration posture, NOT a HIPAA certification.')
param regulatedRetailMode bool = false

@description('Enables the APIM semantic-cache lookup/store policies (#339) for allow-listed stable knowledge/policy intents. Off by default. Requires the #338 cache infrastructure, the Redis external cache registered in APIM out of band, an embeddings backend, and gateway safety; otherwise no cache policy is rendered.')
param semanticCachePolicyEnabled bool = false

@description('ID of the pre-registered APIM backend for the embeddings deployment used by semantic cache similarity matching. Never a key or URL.')
param semanticCacheEmbeddingsBackendId string = ''

@description('Deployment-configured retailer/pack cache partition: 1-63 chars of lowercase a-z, 0-9 and hyphen, not starting with a hyphen. Never taken from a caller header. When empty or invalid the cache policy is not rendered (nothing is cached).')
param semanticCachePartition string = ''

@minValue(1)
@maxValue(20)
@description('Similarity distance threshold in hundredths (5 = 0.05). Lower is stricter; APIM documents values above 0.2 as prone to mismatches, so the upper bound is 20.')
param semanticCacheScoreThresholdPercent int = 5

@minValue(60)
@maxValue(3600)
@description('Cache entry lifetime in seconds for stable knowledge/policy answers.')
param semanticCacheTtlSeconds int = 300

@description('Text harm severity (FourSeverityLevels: 0, 2, 4, 6) at or above which a prompt or response is blocked.')
@minValue(2)
@maxValue(6)
param gatewaySafetySeverityThreshold int = 4

var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'
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

// Resource-scoped Cognitive Services User for the APIM identity on the Content Safety account only.
module contentSafetyRoleAssignment './apim-openai-role-assignment.bicep' = if (gatewaySafetyEnabled) {
  name: 'apim-content-safety-user-role-assignment'
  scope: resourceGroup(contentSafetyResourceGroupName)
  params: {
    aiServicesName: contentSafetyAccountName
    principalId: apimPrincipalId
    roleDefinitionId: cognitiveServicesUserRoleId
  }
}

resource contentSafetyAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = if (gatewaySafetyEnabled) {
  name: contentSafetyAccountName
  scope: resourceGroup(contentSafetyResourceGroupName)
}

var effectiveSafetyMode = regulatedRetailMode ? 'regulated' : gatewaySafetyMode
var bodiesDisabled = gatewaySafetyEnabled || regulatedRetailMode
var maskedRequestIdExpr = '@{ var r = context.RequestId.ToString(); return r.Length &lt;= 8 ? "********" : r.Substring(0, 4) + "..." + r.Substring(r.Length - 2); }'
var profileOutboundFragment = regulatedRetailMode
  ? '        <set-header name="x-rp-gw-profile" exists-action="override"><value>regulated-retail-non-hipaa</value></set-header>'
  : ''
var safetyEndpoint = gatewaySafetyEnabled ? contentSafetyAccount!.properties.endpoint : ''
var safetyEndpointNormalized = endsWith(safetyEndpoint, '/') ? safetyEndpoint : '${safetyEndpoint}/'
var safetyInboundFragment = gatewaySafetyEnabled
  ? replace(replace(replace(loadTextContent('apim-openai-safety-inbound.xml'), '{cs-endpoint}', safetyEndpointNormalized), '{safety-mode}', effectiveSafetyMode), '{severity-threshold}', string(gatewaySafetySeverityThreshold))
  : ''
var safetyOutboundFragment = gatewaySafetyEnabled
  ? replace(replace(replace(loadTextContent('apim-openai-safety-outbound.xml'), '{cs-endpoint}', safetyEndpointNormalized), '{safety-mode}', effectiveSafetyMode), '{severity-threshold}', string(gatewaySafetySeverityThreshold))
  : '        <set-header name="x-rp-gw-safety" exists-action="override"><value>not-applicable</value></set-header>'

var semanticCachePartitionChars = map(range(0, length(semanticCachePartition)), i => substring(semanticCachePartition, i, 1))
var semanticCachePartitionValid = length(semanticCachePartition) >= 1 && length(semanticCachePartition) <= 63 && empty(filter(semanticCachePartitionChars, c => !contains('abcdefghijklmnopqrstuvwxyz0123456789-', c))) && !startsWith(semanticCachePartition, '-')
var semanticCachePolicyActive = semanticCachePolicyEnabled && gatewaySafetyEnabled && !empty(semanticCacheEmbeddingsBackendId) && semanticCachePartitionValid
var semanticCacheScoreThreshold = '0.${padLeft(string(semanticCacheScoreThresholdPercent), 2, '0')}'
var semanticCacheInboundFragment = semanticCachePolicyActive
  ? replace(replace(replace(replace(loadTextContent('apim-openai-semantic-cache-inbound.xml'), '{cache-regulated}', string(regulatedRetailMode)), '{cache-partition}', semanticCachePartition), '{cache-score-threshold}', semanticCacheScoreThreshold), '{cache-embeddings-backend-id}', semanticCacheEmbeddingsBackendId)
  : ''
var semanticCacheOutboundFragment = semanticCachePolicyActive
  ? replace(loadTextContent('apim-openai-semantic-cache-outbound.xml'), '{cache-ttl}', string(semanticCacheTtlSeconds))
  : '        <set-header name="x-rp-gw-cache" exists-action="override"><value>disabled</value></set-header>'

var poolEnabled = backendPoolEnabled
var primaryBackendName = 'retail-pulse-foundry'
var secondaryBackendName = 'retail-pulse-foundry-secondary'
var poolBackendName = 'retail-pulse-foundry-pool'

// Policies address the pool when enabled; otherwise the single primary backend.
var routeBackendId = poolEnabled ? poolBackendName : primaryBackendName

var budgetLimitFragment = replace(
  tokenBudgetDemoEnabled ? loadTextContent('apim-openai-budget-limit-demo.xml') : loadTextContent('apim-openai-budget-limit-normal.xml'),
  '{tokens-per-minute}',
  string(tokensPerMinute))
var inferencePolicyBudgeted = replace(loadTextContent('apim-openai-policy.xml'), '{budget-limit}', budgetLimitFragment)
var inferencePolicyBase = replace(replace(replace(inferencePolicyBudgeted, '{semantic-cache-inbound}', semanticCacheInboundFragment), '{semantic-cache-outbound}', semanticCacheOutboundFragment), '{backend-kind}', poolEnabled ? 'pool' : 'backend')

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

// Budget demo named values (#332). "armed" and the counter generation default to false/1 on every
// deployment. Operators arm the demo and bump the generation with az apim nv update; bumping the
// generation is the immediate reset because APIM has no documented clear-counter API.
resource budgetDemoArmed 'Microsoft.ApiManagement/service/namedValues@2024-06-01-preview' = if (tokenBudgetDemoEnabled) {
  parent: apim
  name: 'rp-budget-demo-armed'
  properties: {
    displayName: 'rp-budget-demo-armed'
    secret: false
    value: 'false'
  }
}

resource budgetDemoTpm 'Microsoft.ApiManagement/service/namedValues@2024-06-01-preview' = if (tokenBudgetDemoEnabled) {
  parent: apim
  name: 'rp-budget-demo-tpm'
  properties: {
    displayName: 'rp-budget-demo-tpm'
    secret: false
    value: string(tokenBudgetDemoTokensPerMinute)
  }
}

resource budgetDemoGeneration 'Microsoft.ApiManagement/service/namedValues@2024-06-01-preview' = if (tokenBudgetDemoEnabled) {
  parent: apim
  name: 'rp-budget-demo-generation'
  properties: {
    displayName: 'rp-budget-demo-generation'
    secret: false
    value: '1'
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
          replace(
            replace(
              replace(
                replace(
                replace(inferencePolicyBase, '{backend-id}', routeBackendId),
                '{safety-inbound}',
                safetyInboundFragment),
              '{safety-outbound}',
              safetyOutboundFragment),
            '{demo-inbound}',
            failoverDemoActive
              ? loadTextContent('apim-openai-failover-demo-inbound.xml')
              : '        <set-header name="x-rp-failover-demo" exists-action="delete" />'),
          '{demo-outbound}',
          failoverDemoActive ? loadTextContent('apim-openai-failover-demo-outbound.xml') : ''),
        '{backend-policy}',
        backendPolicy),
      '{tokens-per-minute}',
      string(tokensPerMinute)),
      '{profile-outbound}',
      profileOutboundFragment),
      '@(context.RequestId.ToString())',
      regulatedRetailMode ? maskedRequestIdExpr : '@(context.RequestId.ToString())')
  }
  dependsOn: poolEnabled ? [
    backend
    secondaryBackend
    backendPool
    secondaryRoleAssignment
    demoKeyNamedValue
    demoArmedNamedValue
    budgetDemoArmed
    budgetDemoTpm
    budgetDemoGeneration
    contentSafetyRoleAssignment
  ] : [
    backend
    budgetDemoArmed
    budgetDemoTpm
    budgetDemoGeneration
    contentSafetyRoleAssignment
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
    // #331: with gateway safety on, no request/response body or LLM message content may be captured
    // (prompts and completions are what the gateway inspects); metadata and token metrics still flow.
    verbosity: 'information'
    logClientIp: true
    frontend: {
      request: {
        body: {
          bytes: bodiesDisabled ? 0 : 8192
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
          bytes: bodiesDisabled ? 0 : 8192
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
      logs: bodiesDisabled ? 'disabled' : 'enabled'
      requests: {
        maxSizeInBytes: bodiesDisabled ? 1024 : 32768
        messages: 'all'
      }
      responses: {
        maxSizeInBytes: bodiesDisabled ? 1024 : 32768
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

// One product + subscription per budget scope. Keys are service-generated (never specified in
// source) and intentionally not output; operators read them with the APIM listSecrets API.
// Product ids must start rp-budget-{store|channel|b2b}-; anything else is the application scope.
resource budgetProducts 'Microsoft.ApiManagement/service/products@2024-06-01-preview' = [for scope in tokenBudgetScopes: {
  parent: apim
  name: 'rp-budget-${scope.kind}-${scope.id}'
  properties: {
    displayName: scope.displayName
    description: 'Token budget scope (${scope.kind}). Counter key derives from the product subscription only.'
    subscriptionRequired: true
    approvalRequired: false
    state: 'published'
  }
}]

resource budgetProductApis 'Microsoft.ApiManagement/service/products/apis@2024-06-01-preview' = [for (scope, i) in tokenBudgetScopes: {
  parent: budgetProducts[i]
  name: api.name
}]

resource budgetSubscriptions 'Microsoft.ApiManagement/service/subscriptions@2024-06-01-preview' = [for (scope, i) in tokenBudgetScopes: {
  parent: apim
  name: 'rp-budget-sub-${scope.kind}-${scope.id}'
  properties: {
    allowTracing: false
    displayName: '${scope.displayName} token budget'
    scope: budgetProducts[i].id
    state: 'active'
  }
  dependsOn: [
    budgetProductApis
  ]
}]

output budgetScopeSubscriptionNames array = [for (scope, i) in tokenBudgetScopes: budgetSubscriptions[i].name]
output tokenBudgetDemoEnabled bool = tokenBudgetDemoEnabled
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
output gatewaySafetyEnabled bool = gatewaySafetyEnabled
output gatewaySafetyMode string = effectiveSafetyMode
output regulatedRetailMode bool = regulatedRetailMode
output backendPoolEnabled bool = poolEnabled
output failoverDemoEnabled bool = failoverDemoActive
output semanticCachePolicyActive bool = semanticCachePolicyActive
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
