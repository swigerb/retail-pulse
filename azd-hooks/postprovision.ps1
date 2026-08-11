$ErrorActionPreference = 'Stop'

# Post-provision hook (Windows/pwsh) — wires ACR pull auth for every Container
# App to its own system-assigned managed identity, with no registry secrets,
# and links the API container app to the Static Web App backend.
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
# Runtime configuration for the API (APIM endpoint, subscription-key secret,
# Entra auth mode, allowed origins, ASPNETCORE_ENVIRONMENT=Production) now lives
# in `infra/modules/container-apps.bicep`. It used to live here as a series of
# `az containerapp update --set-env-vars` calls, which meant a re-provision that
# recreated the API resource from Bicep would leave the active revision with no
# APIM wiring (the §7 regression on issue #51). Keeping runtime config in Bicep
# closes that loop — this hook now only handles the identity/registry/backend
# links that genuinely require post-resource-creation steps.
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
$staticWebApp   = Get-RequiredEnv 'AZURE_STATIC_WEB_APP_NAME'
$location       = Get-RequiredEnv 'AZURE_LOCATION'
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
# handler (Security__RequireAuth=true, set in Bicep) is the real security boundary.
Invoke-Az `
    -Arguments @('containerapp', 'auth', 'update', '--name', $apps[0], '--resource-group', $resourceGroup, '--enabled', 'false', '--output', 'none') `
    -FailureMessage "Failed to disable Container Apps platform auth for the API '$($apps[0])'" | Out-Null

Write-Host 'Post-provision configuration complete: secretless ACR pull, SWA linked backend, and ACA platform-auth disabled.'
