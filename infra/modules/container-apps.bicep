@description('Location for resources')
param location string

@description('Container Apps managed environment resource ID')
param environmentId string

@description('Tags for resources')
param tags object = {}

@description('Fully-qualified container image reference for the API. Defaults to the ACA placeholder when the service has not been deployed yet.')
param apiImageName string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Fully-qualified container image reference for the MCP server. Defaults to the ACA placeholder when the service has not been deployed yet.')
param mcpServerImageName string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Fully-qualified container image reference for the Teams bot. Defaults to the ACA placeholder when the service has not been deployed yet.')
param teamsBotImageName string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('APIM inference endpoint (e.g. https://<apim>.azure-api.net/inference/openai) that the API routes model calls through')
param apimInferenceEndpoint string

@description('APIM subscription primary key used by the API to authenticate to the AI Gateway. Stored as an ACA secret.')
@secure()
param apimSubscriptionKey string

@description('Azure OpenAI deployment name that the API sends chat/completions to (through APIM)')
param openAiDeployment string = 'gpt-5.4-mini-2026-03-17'

@description('Allowed frontend origin (Static Web App URL) for API CORS')
param frontendOrigin string

@description('MCP server base URL exposed to the API. Optional — resolved from the MCP container app FQDN when empty.')
param mcpServerBaseUrl string = ''

@description('Entra tenant (directory) ID for the API JwtBearer handler')
param entraTenantId string

@description('Entra application (client) ID for the API')
param entraClientId string

@description('Entra API scope name (e.g. access_as_user)')
param entraApiScope string = 'access_as_user'

@description('Entra API app role required by the API')
param entraAppRole string = 'RetailPulse.User'

@description('ACR login server for the private registry that hosts the service images. When set, containers pull via system-assigned identity so `azd provision` re-asserts the registry auth binding declaratively.')
param containerRegistryLoginServer string = ''

@description('Shared secret the API presents to the MCP server as X-Api-Key. Stored as an ACA secret on both apps. Defaults to a value derived deterministically from the resource group so repeat provisions are idempotent — supply an explicit value to rotate it.')
@secure()
param mcpApiKey string = '${uniqueString(resourceGroup().id, 'mcp-api-key')}${uniqueString(subscription().subscriptionId, 'retail-pulse-mcp')}'

@description('Azure AI Content Safety endpoint. Empty disables the optional second guardrail layer, leaving the regex-only baseline in place.')
param contentSafetyEndpoint string = ''

var apimSubscriptionKeySecretName = 'apim-sub-key'
var mcpApiKeySecretName = 'mcp-api-key'

// The `registries` block binds a container app's image-pull auth to its own
// system-assigned identity, with no admin credentials. It is only emitted when
// the image points at the private ACR — an omitted block on a first-ever
// create using the mcr.microsoft.com placeholder keeps the initial provision
// unauthenticated (which is what the placeholder allows). Once `azd deploy`
// has pushed real images and captured SERVICE_<name>_IMAGE_NAME into the azd
// env, subsequent provisions include the block and the AcrPull grant issued by
// the postprovision hook (idempotent) satisfies it. This is what closes the
// §7 regression where a re-provision would silently drop the registry block
// off the API container app and revert the active revision to the placeholder.
var usePrivateRegistry = !empty(containerRegistryLoginServer)
var privateRegistryBlock = usePrivateRegistry
  ? [
      {
        server: containerRegistryLoginServer
        identity: 'system'
      }
    ]
  : []
var apiUsesPrivateRegistry = usePrivateRegistry && startsWith(apiImageName, containerRegistryLoginServer)
var mcpUsesPrivateRegistry = usePrivateRegistry && startsWith(mcpServerImageName, containerRegistryLoginServer)
var teamsBotUsesPrivateRegistry = usePrivateRegistry && startsWith(teamsBotImageName, containerRegistryLoginServer)

resource mcpServer 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-retailpulse-mcp'
  location: location
  tags: union(tags, {
    'azd-service-name': 'mcpserver'
  })
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      registries: mcpUsesPrivateRegistry ? privateRegistryBlock : []
      secrets: [
        {
          name: mcpApiKeySecretName
          value: mcpApiKey
        }
      ]
      // The MCP server is a server-to-server dependency of the API, never a
      // browser-facing surface. `external: false` keeps it addressable only from
      // inside the Container Apps environment, so the tool transport and the REST
      // data endpoints are unreachable from the public internet.
      ingress: {
        external: false
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'mcpserver'
          image: mcpServerImageName
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          // Production (not Development) so the API-key gate is enforced and the
          // OpenAPI document is not published. Running this app as Development is
          // what previously disabled the gate entirely.
          env: [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'ApiKey__Enabled'
              value: 'true'
            }
            {
              name: 'ApiKey__Value'
              secretRef: mcpApiKeySecretName
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

var effectiveMcpBaseUrl = empty(mcpServerBaseUrl)
  ? 'https://${mcpServer.properties.configuration.ingress.fqdn}'
  : mcpServerBaseUrl

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-retailpulse-api'
  location: location
  tags: union(tags, {
    'azd-service-name': 'api'
  })
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      registries: apiUsesPrivateRegistry ? privateRegistryBlock : []
      // The APIM subscription primary key comes in through Bicep-time
      // listSecrets() on the APIM subscription and lands here as an ACA
      // secret. Declaring it in Bicep is what makes `azd provision`
      // idempotent for the AI Gateway wiring: a subsequent re-provision
      // cannot silently strip the APIM subscription-key `secretRef` off the
      // active revision the way the previous `az containerapp update` -only
      // path could.
      secrets: [
        {
          name: apimSubscriptionKeySecretName
          value: apimSubscriptionKey
        }
        {
          name: mcpApiKeySecretName
          value: mcpApiKey
        }
      ]
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImageName
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          // Runtime configuration for the deployed API. Declared in Bicep so
          // every `azd provision` re-asserts the AI Gateway path:
          //   OpenAI__Endpoint  → APIM inference API (never direct AOAI)
          //   OpenAI__ApimSubscriptionKey → ACA secret `apim-sub-key`
          //   Authentication__Mode = Entra (production, fail-closed)
          //   Security__RequireAuth = true (JWT bearer gate)
          // This is what closes the §7 regression where a re-provision would
          // drop these values off the active revision.
          env: [
            {
              name: 'OpenAI__Endpoint'
              value: apimInferenceEndpoint
            }
            {
              name: 'OpenAI__UseManagedIdentity'
              value: 'false'
            }
            {
              name: 'OpenAI__ApimSubscriptionKey'
              secretRef: apimSubscriptionKeySecretName
            }
            {
              name: 'OpenAI__Deployment'
              value: openAiDeployment
            }
            {
              name: 'OpenAI__RouterDeployment'
              value: openAiDeployment
            }
            {
              name: 'McpServer__BaseUrl'
              value: effectiveMcpBaseUrl
            }
            {
              name: 'McpServer__ApiKey'
              secretRef: mcpApiKeySecretName
            }
            {
              name: 'Security__RequireAuth'
              value: 'true'
            }
            {
              name: 'Authentication__Mode'
              value: 'Entra'
            }
            {
              name: 'Security__AllowedOrigins__0'
              value: frontendOrigin
            }
            {
              name: 'MicrosoftEntra__TenantId'
              value: entraTenantId
            }
            {
              name: 'MicrosoftEntra__ClientId'
              value: entraClientId
            }
            {
              name: 'MicrosoftEntra__ApiScope'
              value: entraApiScope
            }
            {
              name: 'MicrosoftEntra__AppRole'
              value: entraAppRole
            }
            {
              name: 'RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE'
              value: 'true'
            }
            // Plan-first orchestration (issue #93). Enabling this registers
            // IPlanStore, which in turn maps /api/plans/* and wires the
            // PlanOrchestrator. With it off the "Plan" execution-path option is
            // an inert control and the Plans panel gets a 404 from a route that
            // was never mapped.
            //
            // DURABILITY: the plan store shares the ephemeral per-replica temp
            // directory described below, so plans survive within a warm replica
            // but reset on revision change, replica replacement, or scale-to-
            // zero. That is acceptable for this demo — plan history is a live
            // view of the current session, not a system of record.
            //
            // The human-in-the-loop review gate (PlanReview, issue #94) is a
            // SEPARATE opt-in and stays off: with it enabled every plan pauses
            // for approval. Plans here generate and execute straight through.
            {
              name: 'PlanPersistence__Enabled'
              value: 'true'
            }
            // Human-in-the-loop plan review (issue #94). With this on, a plan-first
            // request pauses and surfaces an approval card the reviewer can approve,
            // edit, or reject with feedback (bounded replan rounds) before any step
            // executes. This is the headline demo of the approval gate, so it is on
            // here — note it makes every plan-path turn interactive by design.
            //
            // Timeouts are bounded (30m review / 15m clarification) and the replan
            // cap is finite, so the coordinator cannot hang waiting on a human.
            {
              name: 'PlanReview__Enabled'
              value: 'true'
            }
            // Durable server-side conversation history (issue #90). Enabled for the
            // full-capability demo so /api/sessions/* is mapped and chats survive a
            // reload. Anonymous callers still never persist, and PII redaction on
            // write stays on — those are hard gates, not config.
            //
            // Same ephemeral-storage caveat as the plan store: session rows live in
            // the per-replica temp directory and reset on revision change, replica
            // replacement, or scale-to-zero.
            {
              name: 'SessionPersistence__Enabled'
              value: 'true'
            }
            // Azure AI Content Safety second layer (issue #100). The regex
            // guardrails always run first; this adds text moderation and Prompt
            // Shields on top. Authentication is managed identity — there is no
            // key here by design, and the endpoint is injected only when the
            // account was provisioned (contentSafetyEnabled). An empty endpoint
            // leaves the layer off and the regex-only baseline in place.
            {
              name: 'Guardrails__ContentSafety__Enabled'
              value: empty(contentSafetyEndpoint) ? 'false' : 'true'
            }
            {
              name: 'Guardrails__ContentSafety__Endpoint'
              value: contentSafetyEndpoint
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
          ]
        }
      ]
      // The API's SQLite stores (cost/audit/memory/approvals/alerts) live in the
      // container's local temp directory. Under this tenant's governance posture,
      // account-key-based Azure Files mounts are not permitted (policy forces
      // allowSharedKeyAccess=false / publicNetworkAccess=Disabled, which breaks the
      // ACA CIFS mount), so no durable volume is attached. Observability history
      // therefore lives only within the current replica and resets on replica
      // replacement, new revisions, or scale-to-zero. maxReplicas: 1 keeps a single
      // SQLite writer; see docs/deployment-azd.md for the policy-compatible durable
      // options under evaluation.
      //
      // maxReplicas: 1 is ALSO a hard requirement for the (non-Production) Anonymous
      // authentication mode AND the Sprint 2 GitHub confidential-OAuth BFF mode: the
      // Anonymous billable-use circuit breaker (daily request/token/cost ceilings) and
      // rate-limit windows, and the GitHub OAuth state/redemption one-time stores and
      // login rate limiters, are all replica-local in-memory state, so exact global
      // enforcement and cross-request continuity (a callback handled by replica A cannot
      // redeem a code on replica B) only hold with a single replica (and all of it resets
      // on restart or replica replacement). This live deployment stays
      // Authentication:Mode=Entra; the constraint is documented here so a future Anonymous
      // or GitHub demo deployment cannot scale out and silently bypass the ceilings or
      // break the OAuth handshake. Hosted GitHub additionally requires an explicit
      // GitHub:AcknowledgeSingleReplica=true fail-closed acknowledgement of this pin. See
      // docs/adr/005 and docs/security.md.
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

resource teamsBot 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-retailpulse-teamsbot'
  location: location
  tags: union(tags, {
    'azd-service-name': 'teamsbot'
  })
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      registries: teamsBotUsesPrivateRegistry ? privateRegistryBlock : []
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'teamsbot'
          image: teamsBotImageName
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          // Production (not Development) so the M365 Agents SDK maps the messaging
          // endpoints with requireAuth: true and inbound Activities are validated
          // against the Bot Framework channel. Running this app as Development is
          // what previously disabled inbound channel authentication on a publicly
          // exposed endpoint.
          env: [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'TeamsBot__ApiBaseUrl'
              value: 'https://${api.properties.configuration.ingress.fqdn}'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

output apiName string = api.name
output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output apiPrincipalId string = api.identity.principalId
output mcpServerName string = mcpServer.name
output mcpServerUrl string = 'https://${mcpServer.properties.configuration.ingress.fqdn}'
output mcpServerPrincipalId string = mcpServer.identity.principalId
output teamsBotName string = teamsBot.name
output teamsBotUrl string = 'https://${teamsBot.properties.configuration.ingress.fqdn}'
output teamsBotPrincipalId string = teamsBot.identity.principalId
