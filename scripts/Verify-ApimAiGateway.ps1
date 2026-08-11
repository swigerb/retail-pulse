#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Live post-provision verification of the APIM AI Gateway invariants.

.DESCRIPTION
    Reads the currently-selected azd environment outputs (or explicit params) to
    resolve the deployed resource group, APIM instance, and API container app,
    then verifies — using ONLY `az` inspection commands — every invariant that
    the Bicep contract tests assert statically:

        * APIM instance exists, uses SystemAssigned identity.
        * Inference API exists, has subscriptionRequired=true and the
          expected /openai suffix on the registered path.
        * API policy references the AOAI backend and enforces token-limit +
          emit-token-metric.
        * Backend authenticates to AOAI via managed identity to
          cognitiveservices.azure.com.
        * API-level applicationinsights diagnostic has metrics enabled and
          azuremonitor diagnostic has largeLanguageModel logs enabled.
        * APIM system-assigned identity holds the Cognitive Services OpenAI
          User role on the AOAI account.
        * The API container app has:
            - `OpenAI__Endpoint` matching the APIM inference URL and NOT
              ending in `/openai` (regression #55).
            - `OpenAI__ApimSubscriptionKey` referencing the `apim-sub-key`
              ACA secret.
            - `OpenAI__UseManagedIdentity` = 'false'.

    Every check is read-only. The script prints a compact summary and exits 0
    when all invariants hold, exits 1 with a list of failures otherwise.

    It is intentionally opportunistic: when `az` is not signed in, or the azd
    environment is unavailable, or the caller lacks Reader on the resource
    group, the script prints a clear reason and exits with code 2 (skipped) so
    it does not falsely fail a pipeline that lacks live-Azure access.

.PARAMETER ResourceGroup
    Resource group containing the RetailPulse deployment. Defaults to
    $env:AZURE_RESOURCE_GROUP (set by azd env after provision).

.PARAMETER ApimName
    APIM service name. Defaults to $env:AZURE_APIM_NAME.

.PARAMETER ApiContainerAppName
    ACA API container app name. Defaults to $env:AZURE_API_APP_NAME.

.PARAMETER AiFoundryAccountName
    Azure AI Foundry / Cognitive Services account name that APIM's MI must be
    granted OpenAI User on. Defaults to the value baked into main.bicep.

.PARAMETER AiFoundryResourceGroup
    Resource group containing the AI Foundry account. Defaults to the value
    baked into main.bicep.

.EXAMPLE
    ./scripts/Verify-ApimAiGateway.ps1

    Runs against the currently-active azd environment.
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = $env:AZURE_RESOURCE_GROUP,
    [string]$ApimName = $env:AZURE_APIM_NAME,
    [string]$ApiContainerAppName = $env:AZURE_API_APP_NAME,
    [string]$InferenceApiName = ($env:AZURE_APIM_INFERENCE_API_NAME | ForEach-Object { if ([string]::IsNullOrWhiteSpace($_)) { 'retail-pulse-inference-api' } else { $_ } }),
    [string]$AiFoundryAccountName = 'aiagents-3rsdmhyb',
    [string]$AiFoundryResourceGroup = 'rg-repodigest-agents-demo-eus-001'
)

$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()
$checks = 0

function Test-Prereq {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        Write-Host "SKIP: az CLI is not installed on PATH." -ForegroundColor Yellow
        exit 2
    }
    $account = az account show 2>$null
    if (-not $account) {
        Write-Host "SKIP: az CLI is not signed in (az account show returned nothing)." -ForegroundColor Yellow
        exit 2
    }
    foreach ($v in @('ResourceGroup', 'ApimName', 'ApiContainerAppName')) {
        if ([string]::IsNullOrWhiteSpace((Get-Variable -Name $v -Scope 1).Value)) {
            Write-Host "SKIP: required parameter/env '$v' is not set. Run this after 'azd env refresh' or pass it explicitly." -ForegroundColor Yellow
            exit 2
        }
    }
}

function Assert([string]$name, [scriptblock]$predicate, [string]$detail = '') {
    $script:checks++
    try {
        $ok = & $predicate
        if ($ok) {
            Write-Host ("  [ok]   {0}" -f $name) -ForegroundColor Green
        }
        else {
            Write-Host ("  [FAIL] {0}: {1}" -f $name, $detail) -ForegroundColor Red
            $script:failures.Add($name)
        }
    }
    catch {
        Write-Host ("  [FAIL] {0}: {1}" -f $name, $_.Exception.Message) -ForegroundColor Red
        $script:failures.Add($name)
    }
}

Test-Prereq

Write-Host "APIM AI Gateway live verification"
Write-Host ("  resourceGroup       = {0}" -f $ResourceGroup)
Write-Host ("  apim                = {0}" -f $ApimName)
Write-Host ("  apiContainerApp     = {0}" -f $ApiContainerAppName)
Write-Host ("  inferenceApi        = {0}" -f $InferenceApiName)
Write-Host ("  aiFoundryAccount    = {0} (rg: {1})" -f $AiFoundryAccountName, $AiFoundryResourceGroup)
Write-Host ""

# ── APIM instance ─────────────────────────────────────────────────────────
Write-Host "APIM instance"
$apim = az apim show -g $ResourceGroup -n $ApimName 2>$null | ConvertFrom-Json
Assert 'apim: exists' { $null -ne $apim } "APIM '$ApimName' not found in '$ResourceGroup'"
Assert 'apim: identity.type = SystemAssigned' { $apim.identity.type -eq 'SystemAssigned' } "identity.type = $($apim.identity.type)"
$apimPrincipalId = $apim.identity.principalId
$gatewayUrl = $apim.gatewayUrl

# ── Inference API ─────────────────────────────────────────────────────────
Write-Host "Inference API"
$api = az apim api show -g $ResourceGroup --service-name $ApimName --api-id $InferenceApiName 2>$null | ConvertFrom-Json
Assert 'api: exists' { $null -ne $api } "'$InferenceApiName' not found on '$ApimName'"
Assert 'api: subscriptionRequired = true' { $api.subscriptionRequired -eq $true } "subscriptionRequired = $($api.subscriptionRequired)"
Assert 'api: path ends in /openai (SDK-compatible)' { $api.path -match '/openai$' } "path = $($api.path)"

# ── API policy (token-limit + emit-token-metric) ──────────────────────────
Write-Host "API policy"
$policyJson = az apim api policy show -g $ResourceGroup --service-name $ApimName --api-id $InferenceApiName 2>$null
$policyValue = ($policyJson | ConvertFrom-Json).value
Assert 'policy: sets backend service (retail-pulse-foundry)' { $policyValue -match '<set-backend-service\s+backend-id="retail-pulse-foundry"' } 'policy did not reference the AOAI backend'
Assert 'policy: managed-identity auth to cognitiveservices.azure.com' { $policyValue -match '<authentication-managed-identity\s+resource="https://cognitiveservices\.azure\.com"' } 'policy is not using MI auth to AOAI'
Assert 'policy: azure-openai-token-limit configured' { $policyValue -match '<azure-openai-token-limit\s+counter-key="@\(context\.Subscription\.Id\)"' } 'token-limit missing or wrong counter-key'
Assert 'policy: azure-openai-emit-token-metric in RetailPulse namespace' { $policyValue -match '<azure-openai-emit-token-metric\s+namespace="RetailPulse">' } 'emit-token-metric missing or wrong namespace'

# ── Backend ──────────────────────────────────────────────────────────────
Write-Host "Backend"
$backend = az apim backend show -g $ResourceGroup --service-name $ApimName --backend-id retail-pulse-foundry 2>$null | ConvertFrom-Json
Assert 'backend: retail-pulse-foundry exists' { $null -ne $backend } 'backend retail-pulse-foundry not found'
Assert 'backend: url targets /openai on cognitiveservices' { $backend.url -match '/openai$' -and ($backend.url -match 'cognitiveservices\.azure\.com|\.services\.ai\.azure\.com') } "backend url = $($backend.url)"
Assert 'backend: MI credentials to cognitiveservices.azure.com' { $backend.credentials.managedIdentity.resource -eq 'https://cognitiveservices.azure.com' } 'backend is not MI-authenticated to AOAI'

# ── Diagnostics (API + instance) ──────────────────────────────────────────
Write-Host "Diagnostics"
$apiAppInsightsDiag = az apim api diagnostic show -g $ResourceGroup --service-name $ApimName --api-id $InferenceApiName --diagnostic-id applicationinsights 2>$null | ConvertFrom-Json
Assert 'api diag: applicationinsights present' { $null -ne $apiAppInsightsDiag } 'API-level applicationinsights diagnostic missing'
Assert 'api diag: metrics = true (routes emit-token-metric)' { $apiAppInsightsDiag.metrics -eq $true } "metrics = $($apiAppInsightsDiag.metrics)"

# azuremonitor + largeLanguageModel logs cannot be read by the api-diagnostic-show CLI (it flattens
# the LLM block); fall back to a direct ARM GET.
$sub = (az account show --query id -o tsv)
$armPath = "/subscriptions/$sub/resourceGroups/$ResourceGroup/providers/Microsoft.ApiManagement/service/$ApimName/apis/$InferenceApiName/diagnostics/azuremonitor?api-version=2024-06-01-preview"
$azMonRaw = az rest --method get --url "https://management.azure.com$armPath" 2>$null
$azMonDiag = $null; if ($azMonRaw) { $azMonDiag = $azMonRaw | ConvertFrom-Json }
Assert 'api diag: azuremonitor present' { $null -ne $azMonDiag } 'API-level azuremonitor diagnostic missing'
Assert 'api diag: largeLanguageModel logs enabled' { $azMonDiag.properties.largeLanguageModel.logs -eq 'enabled' } 'largeLanguageModel logs not enabled — GatewayLlmLogs stay dark'

# ── RBAC: Cognitive Services OpenAI User on AI Foundry ────────────────────
Write-Host "RBAC"
if ($apimPrincipalId) {
    $roleAssignments = az role assignment list --assignee $apimPrincipalId --scope "/subscriptions/$sub/resourceGroups/$AiFoundryResourceGroup/providers/Microsoft.CognitiveServices/accounts/$AiFoundryAccountName" 2>$null | ConvertFrom-Json
    $hasOpenAiUser = ($roleAssignments | Where-Object { $_.roleDefinitionId -match '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd' })
    Assert 'rbac: APIM MI has Cognitive Services OpenAI User on AI Foundry account' { $null -ne $hasOpenAiUser } 'Role assignment missing — the MI backend policy will 403'
}
else {
    Assert 'rbac: APIM principalId available' { $false } 'Could not read APIM principalId to check role assignments'
}

# ── ACA API container app wiring ──────────────────────────────────────────
Write-Host "ACA API container app"
$apiApp = az containerapp show -g $ResourceGroup -n $ApiContainerAppName 2>$null | ConvertFrom-Json
Assert 'aca: API container app exists' { $null -ne $apiApp } "container app '$ApiContainerAppName' not found"

$env = $apiApp.properties.template.containers[0].env
$envMap = @{}
foreach ($e in $env) { $envMap[$e.name] = $e }

Assert 'aca env: OpenAI__Endpoint set' { $envMap.ContainsKey('OpenAI__Endpoint') -and $envMap['OpenAI__Endpoint'].value } 'OpenAI__Endpoint missing'
Assert 'aca env: OpenAI__Endpoint matches APIM inference URL' {
    $endpoint = $envMap['OpenAI__Endpoint'].value
    $endpoint -and $gatewayUrl -and $endpoint.StartsWith($gatewayUrl)
} "OpenAI__Endpoint = $($envMap['OpenAI__Endpoint'].value); APIM gateway = $gatewayUrl"
Assert 'aca env: OpenAI__Endpoint does NOT end in /openai (regression #55)' {
    $endpoint = $envMap['OpenAI__Endpoint'].value
    $endpoint -and -not ($endpoint.TrimEnd('/') -match '/openai$')
} "OpenAI__Endpoint = $($envMap['OpenAI__Endpoint'].value)"
Assert 'aca env: OpenAI__UseManagedIdentity = false (using APIM subscription key)' {
    $envMap.ContainsKey('OpenAI__UseManagedIdentity') -and $envMap['OpenAI__UseManagedIdentity'].value -eq 'false'
} "OpenAI__UseManagedIdentity = $($envMap['OpenAI__UseManagedIdentity'].value)"
Assert 'aca env: OpenAI__ApimSubscriptionKey uses secretRef=apim-sub-key' {
    $envMap.ContainsKey('OpenAI__ApimSubscriptionKey') -and $envMap['OpenAI__ApimSubscriptionKey'].secretRef -eq 'apim-sub-key'
} "OpenAI__ApimSubscriptionKey secretRef = $($envMap['OpenAI__ApimSubscriptionKey'].secretRef)"

$secretNames = @($apiApp.properties.configuration.secrets | ForEach-Object { $_.name })
Assert 'aca secrets: apim-sub-key present' { $secretNames -contains 'apim-sub-key' } "secrets = $($secretNames -join ', ')"

# ── Summary ───────────────────────────────────────────────────────────────
Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host ("PASS  {0}/{0} invariants verified against the live deployment." -f $checks) -ForegroundColor Green
    exit 0
}
else {
    Write-Host ("FAIL  {0}/{1} invariants failed:" -f $failures.Count, $checks) -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "        - $f" -ForegroundColor Red }
    exit 1
}
