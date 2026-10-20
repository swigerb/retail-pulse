#Requires -Version 7.0
<#
.SYNOPSIS
    Idempotent identity provisioning for the governed APIM MCP runtime (ADR 017, issue #335).

.DESCRIPTION
    Called by the azd postprovision hook (the only provisioning path). Uses Microsoft Graph via
    `az rest` as the deploy identity and is safe to re-run:

      1. Find the gateway app registration by identifier URI (default api://retailpulse-mcp-gateway);
         create it with the app role RetailPulse.Mcp.Invoke (Application members only) only if
         missing, add the role only if absent, and ensure its service principal exists.
      2. Resolve the Retail Pulse API service principal and its RetailPulse.User app role id.
      3. Resolve the APIM system-assigned managed identity service principal and create the
         appRoleAssignment for RetailPulse.User only if no identical assignment exists.
      4. Emit AZURE_MCP_GATEWAY_AUDIENCE and AZURE_MCP_GATEWAY_APIM_CLIENT_ID as azd env values
         (consumed by main.bicepparam -> MicrosoftEntra__AllowedAppClientIds__1). No secret is
         created or printed.

    Required Graph permissions for the deploy identity (admin-consented):
      * Application.ReadWrite.All       - create/update the gateway app registration and its role
      * AppRoleAssignment.ReadWrite.All - assign RetailPulse.User to the APIM managed identity
    Application.ReadWrite.OwnedBy is NOT sufficient. The Cloud Application Administrator directory
    role covers both for interactive deployers.

    Fail-closed: on 403 / Authorization_RequestDenied the script exits non-zero naming the missing
    permission; it never skips the assignment. Transient 429/5xx are retried 3 times with backoff;
    a 404 from service-principal propagation delay (APIM and freshly created gateway SP) is retried up to 5 times at 10 s intervals.
    It then applies and verifies the gateway env on the API Container App so the first provision works without a second one.

.PARAMETER ApimPrincipalId
    Object id of the APIM system-assigned identity (azd output AZURE_APIM_PRINCIPAL_ID).

.PARAMETER ApiClientId
    Application (client) id of the Retail Pulse API app registration (RETAIL_PULSE_ENTRA_CLIENT_ID).
#>
[CmdletBinding()]
param(
    [string]$ApimPrincipalId = $env:AZURE_APIM_PRINCIPAL_ID,
    [string]$ApiClientId = $env:RETAIL_PULSE_ENTRA_CLIENT_ID,
    [string]$GatewayAudience = $(if ($env:AZURE_MCP_GATEWAY_AUDIENCE) { $env:AZURE_MCP_GATEWAY_AUDIENCE } else { 'api://retailpulse-mcp-gateway' }),
    [string]$ApiAppRole = 'RetailPulse.User',
    [string]$InvokeRole = 'RetailPulse.Mcp.Invoke',
    [string]$ResourceGroup = $env:AZURE_RESOURCE_GROUP,
    [string]$ApiAppName = $env:AZURE_API_APP_NAME,
    [string[]]$AllowedOperations = @(
        'GET /api/supply/inventory',
        'GET /api/stores/stockout-risks',
        'GET /api/supply/disruptions',
        'GET /api/demand/forecast'),
    [switch]$SkipContainerAppUpdate,
    [switch]$SkipAzdEnv
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$graph = 'https://graph.microsoft.com/v1.0'
$guidPattern = '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$'

foreach ($required in @(
        @{ Name = 'ApimPrincipalId (AZURE_APIM_PRINCIPAL_ID)'; Value = $ApimPrincipalId },
        @{ Name = 'ApiClientId (RETAIL_PULSE_ENTRA_CLIENT_ID)'; Value = $ApiClientId })) {
    if ([string]::IsNullOrWhiteSpace($required.Value) -or $required.Value -notmatch $guidPattern) {
        throw "Setup-McpGatewayIdentity: $($required.Name) must be a GUID."
    }
}
if ($GatewayAudience -notmatch '^api://[A-Za-z0-9._-]+$') {
    throw "Setup-McpGatewayIdentity: gateway audience '$GatewayAudience' must look like api://<name>."
}

function Invoke-Graph {
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PATCH')][string]$Method,
        [Parameter(Mandatory)][string]$Url,
        $Body = $null,
        [switch]$RetryNotFound
    )

    $bodyFile = $null
    $transientAttempts = 0
    $notFoundAttempts = 0
    try {
        if ($null -ne $Body) {
            $bodyFile = [System.IO.Path]::GetTempFileName()
            [System.IO.File]::WriteAllText($bodyFile, ($Body | ConvertTo-Json -Depth 10 -Compress))
        }

        while ($true) {
            $arguments = @('rest', '--method', $Method, '--url', $Url, '--only-show-errors')
            if ($bodyFile) { $arguments += @('--headers', 'Content-Type=application/json', '--body', "@$bodyFile") }

            $output = & az @arguments 2>&1 | Out-String
            if ($LASTEXITCODE -eq 0) {
                if ([string]::IsNullOrWhiteSpace($output)) { return $null }
                return $output | ConvertFrom-Json
            }

            if ($output -match 'Authorization_RequestDenied|Insufficient privileges|Forbidden|\(403\)') {
                throw ('Microsoft Graph denied the request ({0} {1}). The deploy identity needs admin-consented ' -f $Method, $Url) +
                    'Graph permissions Application.ReadWrite.All (gateway app registration) and ' +
                    'AppRoleAssignment.ReadWrite.All (RetailPulse.User assignment to the APIM managed identity). ' +
                    'The Cloud Application Administrator role covers both for interactive deployers.'
            }

            if ($output -match 'Request_ResourceNotFound|\(404\)|NotFound' -and $RetryNotFound -and $notFoundAttempts -lt 5) {
                $notFoundAttempts++
                Write-Host "   waiting for service principal propagation (attempt $notFoundAttempts/5)..."
                Start-Sleep -Seconds 10
                continue
            }

            if ($output -match '429|TooManyRequests|Too Many Requests|\(50[0-9]\)|ServiceUnavailable|InternalServerError' -and $transientAttempts -lt 3) {
                $transientAttempts++
                Start-Sleep -Seconds (5 * $transientAttempts)
                continue
            }

            throw "Graph $Method $Url failed: $output"
        }
    }
    finally {
        if ($bodyFile -and (Test-Path $bodyFile)) { Remove-Item $bodyFile -Force }
    }
}

function Get-FirstOrNull($Response) {
    if ($null -eq $Response -or -not $Response.PSObject.Properties['value']) { return $null }
    $items = @($Response.value)
    if ($items.Count -eq 0) { return $null }
    return $items[0]
}

Write-Host "Setting up MCP gateway identity (audience $GatewayAudience)..."

# 1. Gateway app registration + role + service principal.
$escapedAudience = $GatewayAudience.Replace("'", "''")
$filter = [uri]::EscapeDataString("identifierUris/any(u:u eq '$escapedAudience')")
$gatewayApp = Get-FirstOrNull (Invoke-Graph -Method GET -Url "$graph/applications?`$filter=$filter&`$select=id,appId,appRoles")

if (-not $gatewayApp) {
    Write-Host '-> creating gateway app registration'
    $gatewayApp = Invoke-Graph -Method POST -Url "$graph/applications" -Body @{
        displayName    = 'Retail Pulse MCP Gateway'
        identifierUris = @($GatewayAudience)
        signInAudience = 'AzureADMyOrg'
        appRoles       = @(@{
                id                 = [guid]::NewGuid().ToString()
                allowedMemberTypes = @('Application')
                displayName        = 'Invoke Retail Pulse MCP tools'
                description        = 'Allows an application to call the read-only Retail Pulse MCP tools through APIM.'
                value              = $InvokeRole
                isEnabled          = $true
            })
    }
}
else {
    Write-Host '-> gateway app registration already present'
    $existingRoles = @($gatewayApp.appRoles)
    if (-not ($existingRoles | Where-Object { $_.value -eq $InvokeRole })) {
        Write-Host "-> adding app role $InvokeRole"
        $existingRoles += @{
            id                 = [guid]::NewGuid().ToString()
            allowedMemberTypes = @('Application')
            displayName        = 'Invoke Retail Pulse MCP tools'
            description        = 'Allows an application to call the read-only Retail Pulse MCP tools through APIM.'
            value              = $InvokeRole
            isEnabled          = $true
        }
        Invoke-Graph -Method PATCH -Url "$graph/applications/$($gatewayApp.id)" -Body @{ appRoles = $existingRoles } | Out-Null
    }
}

$gatewaySp = Get-FirstOrNull (Invoke-Graph -Method GET -Url "$graph/servicePrincipals?`$filter=appId eq '$($gatewayApp.appId)'&`$select=id")
if (-not $gatewaySp) {
    Write-Host '-> creating gateway service principal'
    $created = Invoke-Graph -Method POST -Url "$graph/servicePrincipals" -Body @{ appId = $gatewayApp.appId }
    # A fresh service principal can 404 for a while; retry up to 5 times at 10 s intervals before moving on.
    Invoke-Graph -Method GET -Url "$graph/servicePrincipals/$($created.id)?`$select=id" -RetryNotFound | Out-Null
}

# 2. Retail Pulse API service principal and its app role id.
$apiSp = Get-FirstOrNull (Invoke-Graph -Method GET -Url "$graph/servicePrincipals?`$filter=appId eq '$ApiClientId'&`$select=id,appRoles")
if (-not $apiSp) { throw "Retail Pulse API service principal for client id $ApiClientId was not found." }
$apiRole = @($apiSp.appRoles) | Where-Object { $_.value -eq $ApiAppRole -and $_.allowedMemberTypes -contains 'Application' } | Select-Object -First 1
if (-not $apiRole) { throw "The API app registration has no Application-assignable app role '$ApiAppRole'. Run scripts/Setup-EntraAuth.ps1 first." }

# 3. APIM managed identity service principal and the RetailPulse.User assignment.
$apimSp = Invoke-Graph -Method GET -Url "$graph/servicePrincipals/$ApimPrincipalId`?`$select=id,appId" -RetryNotFound
$assignments = Invoke-Graph -Method GET -Url "$graph/servicePrincipals/$($apimSp.id)/appRoleAssignments"
$already = @($assignments.value) | Where-Object { $_.resourceId -eq $apiSp.id -and $_.appRoleId -eq $apiRole.id }
if ($already) {
    Write-Host "-> $ApiAppRole assignment already present for the APIM managed identity"
}
else {
    Write-Host "-> assigning $ApiAppRole to the APIM managed identity"
    Invoke-Graph -Method POST -Url "$graph/servicePrincipals/$($apimSp.id)/appRoleAssignments" -Body @{
        principalId = $apimSp.id
        resourceId  = $apiSp.id
        appRoleId   = $apiRole.id
    } | Out-Null
}

# 4. Emit non-secret azd env values (consumed on the next provision by main.bicepparam).
if (-not $SkipAzdEnv) {
    & azd env set AZURE_MCP_GATEWAY_AUDIENCE $GatewayAudience
    if ($LASTEXITCODE -ne 0) { throw 'azd env set AZURE_MCP_GATEWAY_AUDIENCE failed.' }
    & azd env set AZURE_MCP_GATEWAY_APIM_CLIENT_ID $apimSp.appId
    if ($LASTEXITCODE -ne 0) { throw 'azd env set AZURE_MCP_GATEWAY_APIM_CLIENT_ID failed.' }
}

# 5. First-provision fix: the APIM client id did not exist when ARM ran, so the API Container App was
# deployed with the gateway slot (McpGateway__Clients__0, AllowedAppClientIds__1) empty. Apply it here, in
# the same idempotent flow, and verify the live configuration. Slot keys match container-apps.bicep, so a
# later azd provision (which reads AZURE_MCP_GATEWAY_APIM_CLIENT_ID) writes identical values.
if (-not $SkipContainerAppUpdate) {
    if ([string]::IsNullOrWhiteSpace($ResourceGroup) -or [string]::IsNullOrWhiteSpace($ApiAppName)) {
        throw 'Setup-McpGatewayIdentity: ResourceGroup (AZURE_RESOURCE_GROUP) and ApiAppName (AZURE_API_APP_NAME) are required to apply the gateway configuration.'
    }

    $desired = [ordered]@{
        'MicrosoftEntra__AllowAppOnlyTokens'     = 'true'
        'MicrosoftEntra__AllowedAppClientIds__1' = $apimSp.appId
        'McpGateway__Clients__0__ClientId'       = $apimSp.appId
    }
    for ($i = 0; $i -lt $AllowedOperations.Count; $i++) {
        $desired["McpGateway__Clients__0__Operations__$i"] = $AllowedOperations[$i]
    }

    $settings = @($desired.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
    Write-Host '-> applying MCP gateway configuration to the API Container App'
    & az containerapp update --name $ApiAppName --resource-group $ResourceGroup --set-env-vars @settings --only-show-errors | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "az containerapp update failed for $ApiAppName; the APIM identity is not trusted by the API." }

    $liveJson = & az containerapp show --name $ApiAppName --resource-group $ResourceGroup --query 'properties.template.containers[0].env' -o json --only-show-errors | Out-String
    if ($LASTEXITCODE -ne 0) { throw "az containerapp show failed for $ApiAppName." }
    $live = @{}
    foreach ($entry in ($liveJson | ConvertFrom-Json)) { $live[$entry.name] = $entry.value }
    foreach ($key in $desired.Keys) {
        if ($live[$key] -ne $desired[$key]) {
            throw "Container App $ApiAppName env '$key' was not applied (expected '$($desired[$key])'). MCP backend access is not configured."
        }
    }
    Write-Host '-> verified MCP gateway configuration on the API Container App'
}

Write-Host "MCP gateway identity ready. Audience: $GatewayAudience; APIM managed identity client id: $($apimSp.appId)"
