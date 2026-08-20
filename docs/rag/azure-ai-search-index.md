# Azure AI Search — index lifecycle & reindex procedure

This document describes how the Retail Pulse Azure AI Search knowledge
provider (issue #103) manages its index. See ADR-012 for the full design
decision and ADR-009 for the provider abstraction contract.

## Configuration surface

All settings bind to `Knowledge:AzureAISearch` and are documented inline
in `src/RetailPulse.Api/appsettings.json`. A blank `Endpoint` disables
the provider entirely — the demo path stays byte-for-byte unchanged.

```json
"Knowledge": {
  "Provider": { "Mode": "AzureAISearch", "Degradation": "FailLoud" },
  "AzureAISearch": {
    "Endpoint": "https://<search>.search.windows.net",
    "IndexName": "retail-pulse-knowledge",
    "AutoCreateIndex": true,
    "SchemaVersion": "v1",
    "SemanticRankingEnabled": true,
    "Embeddings": {
      "Endpoint": "https://<apim>.azure-api.net/inference",
      "Deployment": "text-embedding-3-small",
      "Dimensions": 1536
    }
  }
}
```

## Index shape

The complete schema is expressed in
`src/RetailPulse.Api/Rag/AzureAISearch/AzureAISearchIndexSchema.cs`. It
carries every field the provider reads at runtime plus a
`schemaVersion` string on every chunk, an `agentScope` collection
reserved for #105, and a `contentVector` field configured for cosine
similarity through an HNSW profile.

### Fields

| Field | Type | Notes |
| --- | --- | --- |
| `id` | Edm.String, key | `<documentId>_<chunkIndex:D4>` |
| `documentId` | Edm.String, filterable | Group chunks per document |
| `chunkIndex` | Edm.Int32, filterable, sortable | Ordering |
| `title` | Edm.String, searchable + filterable + sortable, en.lucene | |
| `content` | Edm.String, searchable, en.lucene | Chunk text |
| `source` | Edm.String, filterable, facetable | Ingest source |
| `sectionHeader` | Edm.String, retrievable | From DocumentChunker |
| `ingestedAt` | Edm.DateTimeOffset, filterable, sortable | UTC |
| `schemaVersion` | Edm.String, filterable | Bumped on schema change |
| `agentScope` | Collection(Edm.String), filterable | Reserved for #105 |
| `contentVector` | Collection(Edm.Single) HNSW | Vector search |

### Vector search profile

- Algorithm: HNSW, cosine metric.
- Dimensions: `Knowledge:AzureAISearch:Embeddings:Dimensions`. Must
  match the embedding deployment output size — the client rejects a
  vector of the wrong length rather than writing bad data.

### Semantic configuration

When `SemanticRankingEnabled=true` the index carries one semantic
configuration (`retail-pulse-semantic` by default) that prioritises the
title field and uses `content` as the content field. Requires the
Search service to be provisioned with the semantic search feature
enabled (Basic-SKU includes the free tier).

## Index creation

The provider creates the index at first probe when `AutoCreateIndex` is
true. The call is idempotent and never mutates an existing index. When
`AutoCreateIndex` is false and the index does not exist,
`ProbeAsync` throws `KnowledgeProviderUnavailableException` — startup
fails loud so operators run the migration explicitly.

## Drift detection

`AzureAISearchIndexSchema.DetectMismatch` runs on every probe. It
returns the first field the live index is missing (or the first type /
vector-dimension mismatch) with an actionable message. The provider
surfaces the mismatch as an unavailable-provider exception rather than
attempt an in-place mutation.

## Reindex procedure

Any change to the schema (field, type, analyzer, or vector dimension)
requires a reindex:

1. Bump `Knowledge:AzureAISearch:SchemaVersion` in the deployment config
   for the new shape.
2. Update `AzureAISearchIndexSchema.cs` to reflect the new shape.
3. Choose a new `IndexName` (e.g. `retail-pulse-knowledge-v2`) for the
   next generation index. Deploy the API with the new index name.
4. The provider auto-creates the new index on first probe and starts
   ingesting into it. Old chunks continue to serve reads from the
   previous index until you re-point clients.
5. Re-ingest the corpus into the new index (via the ingest endpoint or
   your ingestion pipeline).
6. When traffic has moved and the previous index is no longer needed,
   delete it in the Azure portal or via `az search index delete`.

### Why not in-place migrations?

Azure AI Search does not support field mutation on an existing index —
adding a field is possible, changing type or dimension is not. Trying
to shoe-horn schema changes into an in-place update risks corrupting
the retrieval quality gradient. The explicit new-index path keeps
the two shapes side-by-side while traffic moves.

## Managed identity & RBAC

`disableLocalAuth=true` is set on the Search service; only Entra tokens
are accepted. The postprovision hook grants each container app's
system-assigned identity two roles idempotently:

- `Search Service Contributor` — needed for
  `SearchIndexClient.CreateIndexAsync`.
- `Search Index Data Contributor` — needed for document CRUD.

No admin keys, no query keys, no keys in configuration.

## Failure modes

| Failure | Provider signal | Consumer effect |
| --- | --- | --- |
| Service unreachable | `KnowledgeProviderUnavailableException` | `FailLoud` propagates; `FallbackToInMemory` swaps to in-memory |
| Index missing (AutoCreate=false) | `KnowledgeProviderUnavailableException` | Same as above |
| Schema drift | `KnowledgeProviderUnavailableException` with drift message | Same as above |
| Embedding call throttled | Retried inside the circuit breaker; if breaker opens, wrap as unavailable | Same as above |
| Embedding dimension mismatch | `KnowledgeProviderUnavailableException` (loud) | Startup fails — bump SchemaVersion + reindex |
| Search returns 404 on index during query | `KnowledgeProviderUnavailableException` | Same as above |

## Cost tracking

Every successful embedding call raises a `UsageEvent` on
`ICostTracker`:

```
AgentId  = azure-ai-search:embeddings
Model    = <deployment name, or Embeddings.ModelId when set>
InputTokens = prompt_tokens from Azure OpenAI response
OutputTokens = 0
ToolName = embeddings
```

`TokenPricing` ships two default rows (`text-embedding-3-small`,
`text-embedding-3-large`) so cost math works out of the box.

## Retrieval quality expectations

The provider does hybrid vector + BM25 with optional semantic
reranking. The in-memory BM25 baseline is expected to underperform on
paraphrased queries (the executable "why we need semantic" test lives
in
`tests/RetailPulse.Tests/Rag/AzureAISearch/AzureAISearchRetrievalQualityComparisonTests.cs`).
The comparison harness records Recall@3 for both providers on a fixed
query set when the live environment is configured; the numbers are
reported to the CI test output and to the PR body honestly.

Do not compare hybrid scores to BM25 scores directly. Scores are
provider-local — this is codified in `GetCapabilities().ScoreSemantics`
and the shared conformance suite.

## References

- ADR-009: pluggable knowledge providers
- ADR-012: Azure AI Search provider
- `src/RetailPulse.Api/Rag/AzureAISearch/AzureAISearchIndexSchema.cs`
- `tests/RetailPulse.Tests/Rag/AzureAISearch/`
- `infra/modules/ai-search.bicep`
- `azd-hooks/postprovision.*`
