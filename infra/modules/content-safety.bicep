@description('Location for the Content Safety (Cognitive Services) account.')
param location string

@description('Deterministic azd resource token used for stable name generation.')
param resourceToken string

@description('Common tags applied to every resource.')
param tags object

@description('SKU for the Content Safety account. S0 is the standard tier.')
param skuName string = 'S0'

// Only the layering rationale is documented in ADR-010 / docs/security.md;
// this module owns the provisioning contract. The account is a pure
// Cognitive Services account with kind=ContentSafety — no keys are surfaced;
// callers obtain a token via managed identity + Azure.Identity in the API.
var accountName = 'cs-${resourceToken}'

resource contentSafety 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: accountName
  location: location
  tags: tags
  kind: 'ContentSafety'
  sku: {
    name: skuName
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: accountName
    publicNetworkAccess: 'Enabled'
    // Key-based auth is disabled so callers MUST use Entra tokens obtained via
    // managed identity. This is the RBAC contract asserted by the deployment
    // contract tests and by the "no key on config" runtime test.
    disableLocalAuth: true
  }
}

output endpoint string = contentSafety.properties.endpoint
output name string = contentSafety.name
output resourceId string = contentSafety.id
output principalId string = contentSafety.identity.principalId
