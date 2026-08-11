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

resource backend 'Microsoft.ApiManagement/service/backends@2024-06-01-preview' = {
  parent: apim
  name: 'retail-pulse-foundry'
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
    circuitBreaker: {
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

resource apiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-06-01-preview' = {
  parent: api
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: replace(
      replace(loadTextContent('apim-openai-policy.xml'), '{backend-id}', backend.name),
      '{tokens-per-minute}',
      string(tokensPerMinute))
  }
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
output inferenceEndpoint string = '${apim.properties.gatewayUrl}/${api.properties.path}'
output subscriptionName string = subscription.name

// Live APIM subscription primary key, resolved at Bicep deploy time via
// listSecrets(). Consumed by container-apps.bicep to declaratively bind the
// API container app to APIM as an ACA secret + `secretRef` env var — so
// `azd provision` re-asserts the APIM wiring on every run and a subsequent
// re-provision cannot silently strip the AI Gateway configuration off the
// active revision. Marked @secure() so ARM masks it in deployment outputs.
@secure()
output subscriptionKey string = subscription.listSecrets().primaryKey
