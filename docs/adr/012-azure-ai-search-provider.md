# ADR-012: Optional Azure AI Search knowledge provider

## Status

Accepted (issue #103 — Wave 5 cloud-provider implementation).

## Context

ADR-009 shipped the provider seam behind `IKnowledgeBase` and left the
cloud implementations for follow-on issues. `InMemoryKnowledgeBase` scores
documents with BM25 over a `ConcurrentDictionary`, capped at 100 documents
and 5,000 chunks, and is entirely volatile.

For deployments that want to ground responses in a substantial corpus
(planogram standards, supplier terms, merchandising playbooks, category
guidelines) the in-memory provider has two hard ceilings the abstraction
layer cannot paper over:

- Lexical BM25 misses paraphrase. "How do I coordinate with vendors on
  shelf layouts?" and "Category management defines the role and metrics
  for every merchandising category." belong together, but BM25 finds few
  overlapping terms and scores them apart.
- The corpus does not survive a restart. Uploaded documents live in
  process memory only.

We want an opt-in provider that adds durable, semantically-retrievable
storage without turning cloud resources into a hard dependency. The
laptop demo must remain a laptop demo — no `azd up` required.

## Decision

Implement `AzureAISearchKnowledgeBase` as an opt-in `IKnowledgeBase`
provider that plugs into the ADR-009 seam. The provider is fully
optional at every layer:

- Configuration: `Knowledge:AzureAISearch:Endpoint` blank → nothing
  registered.
- DI: When the endpoint is blank the extension method is a no-op — no
  Search SDK client, no HTTP handler, no credential, no
  `IKnowledgeProviderContribution`.
- Infrastructure: `aiSearchEnabled=false` (the default) leaves the Bicep
  path unchanged — no Search service, no role assignments, no cost.
- Runtime: `Knowledge:Provider:Mode` defaults to `InMemory`; selecting
  `AzureAISearch` without wiring the extension throws with an actionable
  message via `KnowledgeProviderRegistry` (never a silent degradation).

### 1. Retrieval

Queries use Azure AI Search's hybrid ranking:

1. Compute the query embedding via the APIM AI Gateway.
2. Issue a hybrid Search query that combines a BM25 lexical pass over
   `title` + `content` with a k-nearest-neighbour vector pass over
   `contentVector`.
3. When `SemanticRankingEnabled=true`, the top-k RRF-fused hits are
   reranked by the semantic ranker.

Scores are provider-local: the RRF score has no relationship to a BM25
score, and the semantic reranker returns its own scale. `GetCapabilities`
reports the honest range and includes the ADR-009 reminder that scores
are NOT comparable across providers.

### 2. Index schema

A single index holds one document per chunk. Fields:

| Field | Type | Notes |
| --- | --- | --- |
| `id` | Edm.String, key, filterable | `<documentId>_<chunkIndex:D4>` |
| `documentId` | Edm.String, filterable | Groups chunks per document |
| `chunkIndex` | Edm.Int32, filterable, sortable | Ordering |
| `title` | Edm.String, searchable+filterable+sortable, en.lucene | |
| `content` | Edm.String, searchable, en.lucene | Chunk text |
| `source` | Edm.String, filterable, facetable | Ingest source |
| `sectionHeader` | Edm.String, retrievable | From DocumentChunker |
| `ingestedAt` | Edm.DateTimeOffset, filterable, sortable | |
| `schemaVersion` | Edm.String, filterable | Bumped on schema change |
| `agentScope` | Collection(Edm.String), filterable | Reserved for #105 |
| `contentVector` | Collection(Edm.Single) w/ HNSW | Vector search |

HNSW uses cosine similarity. When semantic ranking is enabled the index
carries one semantic configuration prioritising the title field with the
content as the content field.

`AzureAISearchIndexSchema.Build` is the single source of truth for the
shape; `DetectMismatch` inspects a live index and returns the first
offending drift so operators know exactly what to reindex — the provider
never silently mutates an existing index.

### 3. Ingestion

Ingestion reuses `DocumentChunker` so chunk boundaries stay identical
across providers. For each chunk:

1. Compute the embedding through `ApimEmbeddingClient` (single batched
   HTTP call per document).
2. `MergeOrUpload` a `SearchDocument` with the schema fields above.
3. Verify all documents succeeded; a partial failure surfaces with the
   first backend error message.

### 4. Embedding through APIM

Embeddings MUST traverse the APIM AI Gateway. The client uses the Azure
OpenAI `/openai/deployments/{deployment}/embeddings` shape and sends a
managed-identity bearer token (with an optional `api-key` header for
local dev). Every successful call raises a `UsageEvent` on the shared
`ICostTracker` so embedding token spend rolls into the same cost
dashboard as completions. Failures wrap in
`KnowledgeProviderUnavailableException` so the degradation policy sees a
single contract signal — never an empty result.

### 5. Resilience

- Search SDK: built-in retry with exponential backoff; per-call network
  timeout bounded by `RequestTimeoutMs`.
- Embeddings HTTP path: `AddKnowledgeEmbeddingsResilienceHandler` adds
  bounded timeout + retry-inside-circuit-breaker with breaker state
  reported on `CircuitBreakerHealthCheck` alongside MCP and Content
  Safety.

### 6. Index lifecycle

- **Create.** `AutoCreateIndex=true` (default) creates the index on the
  first probe.
- **Migrate.** Any change to a field name, type, analyzer, or vector
  dimension requires bumping `SchemaVersion` and running the reindex
  procedure documented in `docs/rag/azure-ai-search-index.md`.
- **Drift.** `DetectMismatch` runs on every ProbeAsync so a live index
  that no longer matches the code shape surfaces as a
  `KnowledgeProviderUnavailableException` rather than a search-time 404.

### 7. Managed identity everywhere

`disableLocalAuth=true` on the Search service (Bicep) forces every
caller through Entra tokens. The postprovision hook grants each
container app system identity two roles idempotently:

- `Search Service Contributor` — needed for
  `SearchIndexClient.CreateIndexAsync`.
- `Search Index Data Contributor` — needed for document CRUD.

No admin keys, no query keys, no keys in configuration.

### 8. Cost tracking extension

Embedding usage flows through the same `ICostTracker.TrackUsageAsync`
path as chat completions. The `AgentId` is
`azure-ai-search:embeddings`, and `Model` defaults to the deployment
name so `TokenPricing` picks it up. Two default pricing rows
(`text-embedding-3-small`, `text-embedding-3-large`) ship in
`appsettings.json`.

## Consequences

### Positive

- The laptop demo is unchanged: default mode is InMemory, no Search
  package restore, no Bicep resource, no cost.
- Turning the provider on is a matter of `azd env set
  AZURE_AI_SEARCH_ENABLED true`, `azd env set Knowledge__Provider__Mode
  AzureAISearch`, and setting the endpoint/embeddings inputs — no code
  changes.
- The provider is honest about score semantics and drift. An
  unreachable backend never returns an empty result.
- Reuse of `DocumentChunker` keeps chunk boundaries identical to the
  in-memory provider so retrieval behaviour is portable across
  providers.

### Negative

- Adds an `Azure.Search.Documents` dependency to `RetailPulse.Api`
  (bundled with the SDK; no direct Nuget.org access needed).
- Startup on the enabled path needs the Search service reachable to
  probe successfully. Operators who want fallback behaviour set
  `Knowledge:Provider:Degradation=FallbackToInMemory` — ADR-009 covers
  the semantics.
- Semantic reranking is a paid feature; the default is off, and the
  `basic` SKU includes the `free` tier for demo use.

### Explicitly out of scope

- Per-agent knowledge binding lands in #105. The provider already
  publishes the `agentScope` field so #105 can filter without another
  schema bump.
- Foundry IQ implementation is #104.
- No changes to BM25 semantics or the InMemory quotas.
- No changes under `src/RetailPulse.Api/Agents/` in this PR (concurrent
  work per issue #93).

## Compliance with umbrella constraints (#87)

- **No new hard cloud dependencies.** Provider is fully optional.
- **No regressions.** Existing InMemory tests pass unmodified; the
  disabled-default startup test proves the DI graph shape is unchanged
  when the endpoint is blank.
- **Feature-disabled test coverage.**
  `AzureAISearchDefaultDisabledStartupTests` and the DI shape tests
  encode the disabled contract.
- **Documentation is part of done.** This ADR, `docs/architecture.md`,
  `docs/rag/azure-ai-search-index.md`, and `docs/deployment-azd.md`
  ship in the same PR.

## References

- ADR-009 (provider seam)
- Issue #87 (Wave 5 umbrella)
- Issue #103 (this ADR)
- Issue #105 (per-agent knowledge binding — future)
