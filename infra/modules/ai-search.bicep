@description('Location for the Azure AI Search service.')
param location string

@description('Deterministic azd resource token used for stable name generation.')
param resourceToken string

@description('Common tags applied to every resource.')
param tags object

@description('SKU for the Search service. Basic includes semantic search (free tier) and 2 GB of storage — enough for the demo corpus.')
@allowed([
  'basic'
  'standard'
  'standard2'
  'standard3'
])
param skuName string = 'basic'

@description('Replica count for query throughput. Basic supports 1 replica.')
param replicaCount int = 1

@description('Partition count for index size. Basic supports 1 partition.')
param partitionCount int = 1

@description('Semantic search tier. "free" is included with Basic+ SKUs and enables the semantic ranker demo.')
@allowed([
  'disabled'
  'free'
  'standard'
])
param semanticSearch string = 'free'

// Only the layering rationale is documented in ADR-012 / docs/architecture.md;
// this module owns the provisioning contract. Key-based auth is disabled so
// callers MUST use Entra tokens obtained via managed identity — the exact
// contract asserted by the deployment contract tests.
var searchServiceName = 'srch-${resourceToken}'

resource searchService 'Microsoft.Search/searchServices@2024-06-01-preview' = {
  name: searchServiceName
  location: location
  tags: tags
  sku: {
    name: skuName
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    replicaCount: replicaCount
    partitionCount: partitionCount
    hostingMode: 'default'
    publicNetworkAccess: 'enabled'
    // disableLocalAuth forces every caller through Entra tokens — no admin
    // keys, no query keys. The API authenticates via DefaultAzureCredential.
    disableLocalAuth: true
    semanticSearch: semanticSearch
    authOptions: null
  }
}

output endpoint string = 'https://${searchService.name}.search.windows.net'
output name string = searchService.name
output resourceId string = searchService.id
output principalId string = searchService.identity.principalId
