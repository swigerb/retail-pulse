# APIM AI gateway live test plan

> **Status:** Proactive prep only — written ahead of Kroger/Costco landing the final infra + app wiring so live validation can start immediately after merge.

## Scope

This plan validates the new Developer-tier Azure API Management gateway for the `retailpulse-demo-eus-001` azd environment in **eastus** / subscription **44847a42-6b69-4e6c-b7e5-ce7140469dd6**.

- Resource group: `rg-retailpulse-demo-eus-001`
- API container app: `ca-retailpulse-api`
- AI Foundry account: `aiagents-3rsdmhyb`
- Expected APIM behaviors: managed-identity backend auth, token-per-minute limiting, token custom metrics, LLM diagnostics, and application traffic flowing through APIM

> **Name placeholders:** this draft assumes Kroger keeps the prototype child names from `deploy\apim-ai-gateway\` (`retail-pulse-inference-api`, `retail-pulse-foundry`, `retail-pulse-sub`). If the landed `infra\modules\apim*.bicep` uses different child names or outputs, replace those three variables before execution.

## Operator setup

Run all commands from the repo root (`C:\src\worktrees\retail-pulse-apim-gateway`) in **PowerShell**.

```powershell
$SubscriptionId = '44847a42-6b69-4e6c-b7e5-ce7140469dd6'
$EnvName = 'retailpulse-demo-eus-001'
$ResourceGroup = 'rg-retailpulse-demo-eus-001'
$Location = 'eastus'
$AiFoundryName = 'aiagents-3rsdmhyb'
$AiFoundryResourceGroup = '<UPDATE-WHEN-KROGER-LANDS-OUTPUT-OR-CONFIRMED-RG>'
$ApiContainerAppName = 'ca-retailpulse-api'
$ApimApiName = 'retail-pulse-inference-api'
$ApimBackendName = 'retail-pulse-foundry'
$ApimSubscriptionName = 'retail-pulse-sub'
$DeploymentName = 'gpt-5.4-mini-2026-03-17' # replace if infra/app wiring uses a different deployment
$ApiVersion = '2025-03-01-preview'
$Marker = "apim-live-test-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))"

az account set --subscription $SubscriptionId

$ApimId = az resource list --resource-group $ResourceGroup --resource-type Microsoft.ApiManagement/service --query "[0].id" --output tsv
$ApimName = az resource list --resource-group $ResourceGroup --resource-type Microsoft.ApiManagement/service --query "[0].name" --output tsv
$ApimGatewayUrl = az resource show --ids $ApimId --query "properties.gatewayUrl" --output tsv
$AppInsightsName = az resource list --resource-group $ResourceGroup --resource-type Microsoft.Insights/components --query "[0].name" --output tsv
$WorkspaceId = az resource list --resource-group $ResourceGroup --resource-type Microsoft.OperationalInsights/workspaces --query "[0].id" --output tsv
$ApiFqdn = az containerapp show --name $ApiContainerAppName --resource-group $ResourceGroup --query "properties.configuration.ingress.fqdn" --output tsv
$ApiOrigin = "https://$ApiFqdn"
$FrontendHost = az staticwebapp list --resource-group $ResourceGroup --query "[0].defaultHostname" --output tsv
$FrontendOrigin = "https://$FrontendHost"
$AiFoundryResourceId = az cognitiveservices account show --name $AiFoundryName --resource-group $AiFoundryResourceGroup --query id --output tsv
$ApimPrincipalId = az resource show --ids $ApimId --query "identity.principalId" --output tsv
$InferenceUrl = "$ApimGatewayUrl/inference/openai/deployments/$DeploymentName/chat/completions?api-version=$ApiVersion"
```

## 1) Infra provisioning verification

### Goal

Prove `azd provision`/`azd up` succeeds and the APIM instance exists in the demo resource group with **Developer** SKU, system-assigned identity, and cross-RG role assignment on the AI Foundry account.

### Commands

```powershell
azd provision -e $EnvName
```

If the team wants the full app deployment path instead:

```powershell
azd up -e $EnvName
```

Then verify the provisioned APIM resource:

```powershell
az resource show --ids $ApimId --query "{name:name,location:location,sku:sku.name,principalId:identity.principalId}" --output json
```

Verify the cross-RG APIM managed-identity role assignment on the AI Foundry account:

```powershell
az role assignment list `
  --assignee $ApimPrincipalId `
  --scope $AiFoundryResourceId `
  --query "[].{role:roleDefinitionName,principalId:principalId,scope:scope}" `
  --output table
```

### Pass criteria

- `azd provision` or `azd up` exits 0.
- APIM resource exists in `rg-retailpulse-demo-eus-001`.
- `sku` is `Developer`.
- `principalId` is non-empty.
- Role assignment list includes `Cognitive Services OpenAI User`.

### Fail triage / rollback

- If provision fails: inspect deployment operations first:
  ```powershell
  az deployment sub list --query "[?contains(name, '$EnvName')].[name,properties.provisioningState]" --output table
  ```
- If `principalId` is empty: APIM system-assigned identity was not enabled; fix infra before any gateway tests.
- If the role assignment is missing: fix the APIM→Foundry RBAC module and re-run `azd provision`.
- **Rollback for blocked demo:** revert Kroger's APIM IaC change and re-provision without the new APIM module.

## 2) Gateway connectivity (direct APIM inference call)

### Goal

Prove the APIM inference endpoint can be called directly with the APIM subscription key and returns an OpenAI-shaped `200` response.

### Commands

Fetch the APIM subscription key:

```powershell
$ApimSubscriptionKey = az rest `
  --method post `
  --url "https://management.azure.com/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.ApiManagement/service/$ApimName/subscriptions/$ApimSubscriptionName/listSecrets?api-version=2024-06-01-preview" `
  --query primaryKey `
  --output tsv
```

Send a minimal chat/completions request through the gateway:

```powershell
$GatewayPayload = @{
  messages = @(
    @{
      role = 'user'
      content = "Reply with the exact text APIM_GATEWAY_OK. Marker: $Marker"
    }
  )
  max_completion_tokens = 32
} | ConvertTo-Json -Depth 10

$GatewayResponse = Invoke-RestMethod `
  -Method Post `
  -Uri $InferenceUrl `
  -Headers @{ 'api-key' = $ApimSubscriptionKey } `
  -ContentType 'application/json' `
  -Body $GatewayPayload

$GatewayResponse | ConvertTo-Json -Depth 20
```

### Pass criteria

- HTTP `200`.
- Response contains `choices[0].message.content`.
- Response contains `usage.prompt_tokens`, `usage.completion_tokens`, and `usage.total_tokens`.

### Fail triage / rollback

- `401/403`: wrong APIM subscription key, inactive APIM subscription, or API child name mismatch.
- `404`: wrong APIM path / deployment name / api-version.
- `5xx`: inspect backend + diagnostics before continuing to app wiring.
- **Rollback for blocked demo:** keep the app pointed at direct AOAI until the APIM inference path returns `200`.

## 3) Managed identity backend auth (APIM → AOAI without backend API key)

### Goal

Prove APIM authenticates to Azure OpenAI with its **system-assigned managed identity**, not with a backend API key.

### Commands

Verify the landed IaC contains managed-identity backend wiring:

```powershell
rg -n "managedIdentity|authentication-managed-identity|azure-openai-token-limit|api-key" infra\modules\apim.bicep infra\main.bicep deploy\apim-ai-gateway\main.bicep deploy\apim-ai-gateway\policy.xml
```

Inspect the live backend resource:

```powershell
az rest `
  --method get `
  --url "https://management.azure.com/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.ApiManagement/service/$ApimName/backends/$ApimBackendName?api-version=2024-06-01-preview"
```

Inspect the live API policy:

```powershell
az rest `
  --method get `
  --url "https://management.azure.com/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.ApiManagement/service/$ApimName/apis/$ApimApiName/policies/policy?api-version=2024-06-01-preview" `
  --query "properties.value" `
  --output tsv
```

### Pass criteria

- IaC/backend resource shows `credentials.managedIdentity.resource = https://cognitiveservices.azure.com`.
- No backend API key / authorization header credential is configured on the APIM backend.
- API policy contains `<authentication-managed-identity resource="https://cognitiveservices.azure.com" />`.
- The live call from section 2 succeeds.

### Fail triage / rollback

- If backend config contains key material: fail the check — the requirement is MI, not key auth.
- If the policy is missing `authentication-managed-identity`: APIM will not obtain a token for the Foundry backend.
- If role assignment exists but calls still fail: wait 2-5 minutes for RBAC propagation, then retry once.
- **Rollback for blocked demo:** revert the APIM backend change and keep direct AOAI access until MI auth is fixed.

## 4) Token-per-minute rate limiting

### Goal

Intentionally exceed the configured TPM policy and prove APIM returns `429` plus `Retry-After`, showing the `azure-openai-token-limit` policy is active.

### Commands

```powershell
$RateLimitPayload = @{
  messages = @(
    @{
      role = 'user'
      content = "Return a very long answer. Marker: $Marker. Repeat 'Retail Pulse' many times."
    }
  )
  max_completion_tokens = 4000
} | ConvertTo-Json -Depth 10

$Saw429 = $false

for ($i = 1; $i -le 20; $i++) {
  try {
    $resp = Invoke-WebRequest `
      -Method Post `
      -Uri $InferenceUrl `
      -Headers @{ 'api-key' = $ApimSubscriptionKey } `
      -ContentType 'application/json' `
      -Body $RateLimitPayload

    Write-Host "[$i] HTTP $($resp.StatusCode) x-tokens-consumed=$($resp.Headers['x-tokens-consumed'])"
  }
  catch {
    $status = [int]$_.Exception.Response.StatusCode
    $retryAfter = $_.Exception.Response.Headers['Retry-After']
    Write-Host "[$i] HTTP $status Retry-After=$retryAfter"
    if ($status -eq 429 -and -not [string]::IsNullOrWhiteSpace($retryAfter)) {
      $Saw429 = $true
      break
    }
  }
}

if (-not $Saw429) {
  throw 'Expected APIM to return 429 with Retry-After after exceeding the configured tokens-per-minute limit.'
}
```

### Pass criteria

- At least one request returns `429`.
- The `Retry-After` header is present.
- Earlier successful responses show `x-tokens-consumed`, proving the APIM token policy is evaluating requests.

### Fail triage / rollback

- If no `429` occurs: the configured TPM limit may be too high for this loop; first inspect the applied policy value, then increase the request count or prompt size.
- If `429` occurs but no `Retry-After`: fail — the APIM circuit-breaker / policy behavior is incomplete.
- **Rollback for blocked demo:** remove the new APIM endpoint from the app path until the throttle behavior is deterministic and documented.

## 5) Token metric emission to Application Insights (`customMetrics`)

### Goal

Prove live APIM traffic emits token metrics into Application Insights `customMetrics`.

### Commands

Wait 5-15 minutes after section 2 or 4 generates traffic, then query:

```powershell
$TokenMetricQuery = @"
customMetrics
| where timestamp > ago(30m)
| where name == "Total Tokens"
| project timestamp, name, value, apiId=tostring(customDimensions["API ID"]), operationId=tostring(customDimensions["Operation ID"]), subscriptionId=tostring(customDimensions["Subscription ID"])
| order by timestamp desc
| take 20
"@

az monitor app-insights query `
  --app $AppInsightsName `
  --resource-group $ResourceGroup `
  --analytics-query $TokenMetricQuery
```

### Pass criteria

- Query returns fresh rows after live gateway traffic.
- `value` is non-zero for recent requests.
- `API ID` / `Operation ID` custom dimensions are populated.

### Fail triage / rollback

- If no rows appear after 15 minutes: verify the API-level `applicationinsights` diagnostic exists and that the inbound policy includes `azure-openai-emit-token-metric`.
- If rows exist but values are always zero: inspect the request shape and model response for missing usage data.
- **Rollback for blocked demo:** treat missing token metrics as a release blocker for observability-driven demos; revert the APIM cutover until metrics appear.

## 6) LLM diagnostics (`ApiManagementGatewayLlmLog`)

### Goal

Prove APIM writes LLM-aware logs to Log Analytics via `largeLanguageModel: { logs: 'enabled' }`.

### Commands

Check the live API diagnostic resource first:

```powershell
az rest `
  --method get `
  --url "https://management.azure.com/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.ApiManagement/service/$ApimName/apis/$ApimApiName/diagnostics/azuremonitor?api-version=2024-06-01-preview"
```

Query the Log Analytics table:

```powershell
$LlmLogQuery = @"
ApiManagementGatewayLlmLog
| where TimeGenerated > ago(30m)
| project TimeGenerated, ApiId, ModelId, PromptTokens, CompletionTokens, TotalTokens, BackendResponseCode, GatewayResponseCode
| order by TimeGenerated desc
| take 20
"@

az monitor log-analytics query `
  --workspace $WorkspaceId `
  --analytics-query $LlmLogQuery
```

To prove a specific test call arrived, search for the unique marker:

```powershell
$LlmMarkerQuery = @"
ApiManagementGatewayLlmLog
| where TimeGenerated > ago(30m)
| where tostring(pack_all()) has "$Marker"
| project TimeGenerated, ApiId, ModelId, PromptTokens, CompletionTokens, TotalTokens
| order by TimeGenerated desc
"@

az monitor log-analytics query `
  --workspace $WorkspaceId `
  --analytics-query $LlmMarkerQuery
```

### Pass criteria

- The `azuremonitor` API diagnostic exists.
- Diagnostic JSON shows `largeLanguageModel.logs = enabled`.
- `ApiManagementGatewayLlmLog` returns rows after live traffic.
- Recent rows include non-empty `ModelId`, `PromptTokens`, and `CompletionTokens`.

### Fail triage / rollback

- If the table does not exist yet: wait 15-30 minutes after the first successful APIM call, then retry once.
- If the table still stays empty: verify the instance diagnostic setting includes the `GatewayLlmLogs` category and that API-level `azuremonitor` diagnostic sampling is `100`.
- **Rollback for blocked demo:** do not cut the demo to APIM until LLM logs are visible; the AI Gateway analytics story depends on this table.

## 7) End-to-end application wiring (frontend/API → APIM → AOAI)

### Goal

Prove the deployed app path uses APIM rather than direct AOAI access.

> **Important:** downstream AOAI logs are **not** the primary proof here because APIM legitimately forwards the request to AOAI. The high-signal proof is: **(1)** the app is configured with an APIM gateway endpoint, **and** **(2)** an app-originated request shows up in `ApiManagementGatewayLlmLog` / token metrics with the same unique marker.

### Commands

First inspect the deployed API container app configuration:

```powershell
az containerapp show `
  --name $ApiContainerAppName `
  --resource-group $ResourceGroup `
  --query "properties.template.containers[0].env[?name=='OpenAI__Endpoint' || name=='OpenAI__UseManagedIdentity' || name=='OpenAI__ApiKey' || name=='AiGateway__SubscriptionKey'].{name:name,value:value,secretRef:secretRef}" `
  --output table
```

Expected config outcome:

- `OpenAI__Endpoint` points to `https://<apim>.azure-api.net/inference`
- **Either**
  - `OpenAI__UseManagedIdentity = true`
- **Or**
  - an APIM key/secret reference is present (`OpenAI__ApiKey` or `AiGateway__SubscriptionKey` uses `secretRef`)

Acquire an Entra bearer token for the API and send an end-to-end chat request directly to the deployed API:

```powershell
$ApiClientId = az containerapp show `
  --name $ApiContainerAppName `
  --resource-group $ResourceGroup `
  --query "properties.template.containers[0].env[?name=='MicrosoftEntra__ClientId'].value | [0]" `
  --output tsv

$AccessToken = az account get-access-token `
  --scope "api://$ApiClientId/access_as_user" `
  --query accessToken `
  --output tsv

$ChatPayload = @{
  message = "Reply with the exact text APIM_APP_PATH_OK. Marker: $Marker"
  sessionId = [guid]::NewGuid().ToString('N')
  history = @()
} | ConvertTo-Json -Depth 10

$ApiResponse = Invoke-RestMethod `
  -Method Post `
  -Uri "$ApiOrigin/api/chat" `
  -Headers @{ Authorization = "Bearer $AccessToken" } `
  -ContentType 'application/json' `
  -Body $ChatPayload

$ApiResponse | ConvertTo-Json -Depth 20
```

Optional browser smoke (same marker, same expectation):

```powershell
Start-Process $FrontendOrigin
```

Then re-run both telemetry queries from sections 5 and 6, especially the marker-filtered `ApiManagementGatewayLlmLog` query.

### Pass criteria

- API container app config points `OpenAI__Endpoint` at the APIM gateway, not the raw AOAI endpoint.
- The live `POST /api/chat` call returns `200` with a normal Retail Pulse chat payload.
- The same request window yields fresh `customMetrics` token rows and an `ApiManagementGatewayLlmLog` row.
- Marker query proves the app-generated request traversed APIM.

### Fail triage / rollback

- If the API is still pointed at `*.openai.azure.com` or `*.services.ai.azure.com`: Costco's app wiring did not land; stop and fix wiring before live demo.
- If config points at APIM but telemetry stays dark: inspect APIM diagnostics and app env vars before blaming the app.
- If the API direct call fails auth: validate Entra app registration / API scope before re-running gateway checks.
- **Rollback for blocked demo:** set the app back to its known-good direct AOAI endpoint and redeploy while the APIM integration is repaired.

## Highest-signal telemetry checks

These are the two fastest live-proof commands after traffic generation:

```powershell
az monitor app-insights query `
  --app $AppInsightsName `
  --resource-group $ResourceGroup `
  --analytics-query "customMetrics | where timestamp > ago(30m) | where name == 'Total Tokens' | project timestamp, name, value, tostring(customDimensions['API ID']), tostring(customDimensions['Operation ID']), tostring(customDimensions['Subscription ID']) | order by timestamp desc | take 20"
```

```powershell
az monitor log-analytics query `
  --workspace $WorkspaceId `
  --analytics-query "ApiManagementGatewayLlmLog | where TimeGenerated > ago(30m) | project TimeGenerated, ApiId, ModelId, PromptTokens, CompletionTokens, TotalTokens, BackendResponseCode, GatewayResponseCode | order by TimeGenerated desc | take 20"
```

## Exit criteria

The APIM gateway is ready for demo use only when **all** of the following are true:

1. Provision succeeds.
2. APIM Developer SKU + MI + RBAC are correct.
3. Direct APIM inference call returns `200`.
4. Rate-limit test returns `429` + `Retry-After`.
5. `customMetrics` token rows appear.
6. `ApiManagementGatewayLlmLog` rows appear with model + token fields populated.
7. The deployed Retail Pulse app hits APIM, not direct AOAI.
