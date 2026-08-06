$ErrorActionPreference = 'Stop'

# Post-provision hook (Windows/pwsh) — wires ACR pull auth for every Container
# App to its own system-assigned managed identity, with no registry secrets.
#
# Why this runs as a hook instead of purely in Bicep:
# Configuring a Container App to pull from ACR using its OWN system-assigned
# identity is circular in a single ARM/Bicep pass — the app's principalId does
# not exist until the app is created, and the AcrPull role assignment that the
# registry-identity binding depends on needs that principalId. Expressing all of
# that inline makes provisioning circular and unreliable (and a re-provision can
# strip the registry block azd set during deploy, which is exactly the
# `UNAUTHORIZED` image-pull failure this fixes). Doing it here, after provision,
# breaks the cycle deterministically. It is fully idempotent: every `azd up` /
# `azd provision` re-asserts the desired state, so clean and repeated deploys are
# self-contained.
#
# Values are derived from the azd environment (the infra outputs captured by
# `azd provision`), exposed to this hook as process environment variables.

function Get-RequiredEnv {
    param([Parameter(Mandatory)][string] $Name)

    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Required azd environment value '$Name' is missing. Ensure 'azd provision' captured the infra outputs before this hook ran."
    }
    return $value.Trim()
}

function Get-OptionalEnv {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Default
    )

    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $Default
    }
    return $value.Trim()
}

function Invoke-Az {
    param([Parameter(Mandatory)][string[]] $Arguments, [Parameter(Mandatory)][string] $FailureMessage)

    $result = & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (az exited $LASTEXITCODE)."
    }
    return $result
}

$resourceGroup  = Get-RequiredEnv 'AZURE_RESOURCE_GROUP'
$registryName   = Get-RequiredEnv 'AZURE_CONTAINER_REGISTRY_NAME'
$registryServer = Get-RequiredEnv 'AZURE_CONTAINER_REGISTRY_ENDPOINT'
$registryId     = Get-RequiredEnv 'AZURE_CONTAINER_REGISTRY_RESOURCE_ID'
$openAiEndpoint = Get-RequiredEnv 'AZURE_OPENAI_ENDPOINT'
$apiUrl         = Get-RequiredEnv 'AZURE_API_APP_URL'
$mcpServerUrl   = Get-RequiredEnv 'AZURE_MCP_SERVER_APP_URL'
$frontendOrigin = Get-RequiredEnv 'RETAIL_PULSE_FRONTEND_ORIGIN'
$staticWebApp   = Get-RequiredEnv 'AZURE_STATIC_WEB_APP_NAME'
$location       = Get-RequiredEnv 'AZURE_LOCATION'
# Entra auth configuration. Tenant/client IDs are CONFIGURATION, not secrets; the
# parent captures them via `azd env set` from the Setup-EntraAuth.ps1 output. These
# are read with Get-RequiredEnv so a deploy fails fast rather than silently shipping
# an anonymous, Development-mode API (the exact regression this hook now prevents).
$entraTenantId  = Get-RequiredEnv 'RETAIL_PULSE_ENTRA_TENANT_ID'
$entraClientId  = Get-RequiredEnv 'RETAIL_PULSE_ENTRA_CLIENT_ID'
$entraApiScope  = Get-OptionalEnv 'RETAIL_PULSE_ENTRA_API_SCOPE' 'access_as_user'
$entraAppRole   = Get-OptionalEnv 'RETAIL_PULSE_ENTRA_APP_ROLE'  'RetailPulse.User'
$apps = @(
    (Get-RequiredEnv 'AZURE_API_APP_NAME'),
    (Get-RequiredEnv 'AZURE_MCP_SERVER_APP_NAME'),
    (Get-RequiredEnv 'AZURE_TEAMS_BOT_APP_NAME')
)

Write-Host "Configuring ACR pull via system-assigned identity for $($apps.Count) container app(s) on registry '$registryName'..."

foreach ($app in $apps) {
    Write-Host "-> $app"

    $principalId = (Invoke-Az `
        -Arguments @('containerapp', 'show', '--name', $app, '--resource-group', $resourceGroup, '--query', 'identity.principalId', '--output', 'tsv') `
        -FailureMessage "Failed to read container app '$app' in resource group '$resourceGroup'" | Out-String).Trim()

    if ([string]::IsNullOrWhiteSpace($principalId)) {
        throw "Container app '$app' has no system-assigned identity principalId. Expected identity.type = SystemAssigned."
    }

    # Idempotent AcrPull grant. The JMESPath filter matches on principalId
    # client-side so we avoid an AAD Graph lookup on a freshly created identity.
    $existing = (Invoke-Az `
        -Arguments @('role', 'assignment', 'list', '--scope', $registryId, '--query', "[?principalId=='$principalId' && roleDefinitionName=='AcrPull'].id", '--output', 'tsv') `
        -FailureMessage "Failed to list role assignments on registry '$registryName' for '$app'" | Out-String).Trim()

    if ([string]::IsNullOrWhiteSpace($existing)) {
        Write-Host "   granting AcrPull to $principalId"
        Invoke-Az `
            -Arguments @('role', 'assignment', 'create', '--assignee-object-id', $principalId, '--assignee-principal-type', 'ServicePrincipal', '--role', 'AcrPull', '--scope', $registryId, '--output', 'none') `
            -FailureMessage "Failed to grant AcrPull to '$app' ($principalId) on registry '$registryName'" | Out-Null
    }
    else {
        Write-Host '   AcrPull already present'
    }

    # Bind the app's registry auth to its system identity — no admin creds/secrets.
    Invoke-Az `
        -Arguments @('containerapp', 'registry', 'set', '--name', $app, '--resource-group', $resourceGroup, '--server', $registryServer, '--identity', 'system', '--output', 'none') `
        -FailureMessage "Failed to set system-identity registry auth for '$app' on '$registryServer'" | Out-Null

    Write-Host '   registry auth bound to system identity'
}

Write-Host 'Configuring production auth + runtime settings for the API...'

# The API is the security boundary for the SWA + ACA architecture. It deploys as
# Production with real Entra JWT validation enabled (Security__RequireAuth=true).
# ACA platform (Easy Auth) stays disabled below so the in-process JwtBearer handler
# is the sole gate; direct ACA REST/SignalR are protected independent of SWA routing.
Invoke-Az `
    -Arguments @(
        'containerapp', 'update',
        '--name', $apps[0],
        '--resource-group', $resourceGroup,
        '--set-env-vars',
        "OpenAI__Endpoint=$openAiEndpoint",
        'OpenAI__UseManagedIdentity=true',
        'OpenAI__Deployment=gpt-5.4-mini-2026-03-17',
        'OpenAI__RouterDeployment=gpt-5.4-mini-2026-03-17',
        "McpServer__BaseUrl=$mcpServerUrl",
        'Security__RequireAuth=true',
        # Provider-neutral auth is explicitly pinned to Entra for production (see
        # Security/ProviderNeutralAuthentication.cs). This is a deploy-time re-assertion of the
        # committed appsettings.Production.json value; the API fails closed on a missing/unknown
        # mode and refuses to start under GitHub/Anonymous.
        'Authentication__Mode=Entra',
        "Security__AllowedOrigins__0=$frontendOrigin",
        "MicrosoftEntra__TenantId=$entraTenantId",
        "MicrosoftEntra__ClientId=$entraClientId",
        "MicrosoftEntra__ApiScope=$entraApiScope",
        "MicrosoftEntra__AppRole=$entraAppRole",
        # The governance hotfix removed the Azure Files durable mount, so there is no
        # durable data-directory path to pin. Flipping the API to Production would
        # otherwise fail closed in DataDirectoryResolver. This is a synthetic demo, so
        # we explicitly opt in to a writable per-replica ephemeral data directory —
        # honestly non-durable: observability history resets on replica replacement
        # (see docs/deployment-azd.md). A future policy-compatible durable backing can
        # drop this flag and configure a mounted durable path instead. This is NOT the
        # durable-storage requirement flag; an explicit require-durable=true still
        # fails closed.
        'RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE=true',
        'ASPNETCORE_ENVIRONMENT=Production',
        '--output', 'none'
    ) `
    -FailureMessage "Failed to configure API runtime settings for '$($apps[0])'" | Out-Null

# SWA proxies relative /api requests to ACA. SignalR intentionally bypasses this
# link and uses VITE_API_ORIGIN because linked backends do not proxy WebSockets.
$apiResourceId = (Invoke-Az `
    -Arguments @('containerapp', 'show', '--name', $apps[0], '--resource-group', $resourceGroup, '--query', 'id', '--output', 'tsv') `
    -FailureMessage "Failed to read API resource id for '$($apps[0])'" | Out-String).Trim()
$linkedBackends = (Invoke-Az `
    -Arguments @('staticwebapp', 'backends', 'show', '--name', $staticWebApp, '--resource-group', $resourceGroup, '--output', 'json') `
    -FailureMessage "Failed to list linked backends for Static Web App '$staticWebApp'" | Out-String) | ConvertFrom-Json

if (-not ($linkedBackends | Where-Object { $_.backendResourceId -eq $apiResourceId })) {
    Invoke-Az `
        -Arguments @('staticwebapp', 'backends', 'link', '--name', $staticWebApp, '--resource-group', $resourceGroup, '--backend-resource-id', $apiResourceId, '--backend-region', $location, '--output', 'none') `
        -FailureMessage "Failed to link API '$($apps[0])' to Static Web App '$staticWebApp'" | Out-Null
}

# Linking enables the SWA identity provider on the /api proxy path, but ACA platform
# (Easy Auth) is deliberately kept DISABLED: it would issue login redirects that break
# bearer-token REST/SignalR clients calling ACA directly. The in-process Entra JwtBearer
# handler (Security__RequireAuth=true above) is the real security boundary.
Invoke-Az `
    -Arguments @('containerapp', 'auth', 'update', '--name', $apps[0], '--resource-group', $resourceGroup, '--enabled', 'false', '--output', 'none') `
    -FailureMessage "Failed to disable Container Apps platform auth for the API '$($apps[0])'" | Out-Null

Invoke-Az `
    -Arguments @('containerapp', 'update', '--name', $apps[1], '--resource-group', $resourceGroup, '--set-env-vars', 'ASPNETCORE_ENVIRONMENT=Development', '--output', 'none') `
    -FailureMessage "Failed to configure MCP runtime settings for '$($apps[1])'" | Out-Null

Invoke-Az `
    -Arguments @(
        'containerapp', 'update',
        '--name', $apps[2],
        '--resource-group', $resourceGroup,
        '--set-env-vars',
        'ASPNETCORE_ENVIRONMENT=Development',
        "TeamsBot__ApiBaseUrl=$apiUrl",
        '--output', 'none'
    ) `
    -FailureMessage "Failed to configure Teams bot runtime settings for '$($apps[2])'" | Out-Null

Write-Host 'Post-provision configuration complete: secretless ACR pull and production Entra auth runtime settings are ready.'
