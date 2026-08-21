# Knowledge provider parity matrix

**Scope:** Wave 5 RAG retrieval providers, verified against the shared
`IKnowledgeBase` contract in `RetailPulse.Contracts/Rag/IKnowledgeBase.cs`.

**Status:** Every legitimate divergence in this table is explicit and rooted
in a documented ADR or capability flag; nothing is an accident. Divergences
are asserted by static tests (see
`tests/RetailPulse.Tests/Rag/Parity/KnowledgeProviderParityMatrixTests.cs`)
so a silent capability drift breaks the build.

## Providers under review

| Provider          | Relevance | Persistent | RequiresCloud | SupportsMutation | Notes                                                                                     |
| ----------------- | --------- | ---------- | ------------- | ---------------- | ----------------------------------------------------------------------------------------- |
| InMemory          | Lexical   | false      | false         | true             | Always-on default. Zero cloud dependency. Volatile corpus. ADR-009.                       |
| Azure AI Search   | Hybrid    | true       | true          | true             | Hybrid vector + BM25, optional semantic reranker. Embeddings traverse APIM. ADR-012.      |
| Foundry IQ        | Semantic  | true       | true          | **false**        | Read-only file_search agent over externally-managed vector store. ADR-013.                |

## Per-operation parity

| Operation                                      | InMemory                                    | Azure AI Search                             | Foundry IQ                                                            |
| ---------------------------------------------- | ------------------------------------------- | ------------------------------------------- | --------------------------------------------------------------------- |
| `GetCapabilities()`                            | InMemory / Lexical / not persistent         | AzureAISearch / Hybrid / persistent / cloud | FoundryIQ / Semantic / persistent / cloud / **read-only**             |
| `ProbeAsync()` when healthy                    | Completes                                   | Ensures index exists, completes             | Resolves vector store + retrieval agent, completes                    |
| `ProbeAsync()` when unreachable                | N/A (no network)                            | Throws `KnowledgeProviderUnavailableException` | Throws `KnowledgeProviderUnavailableException`                     |
| `SearchAsync(query, topK)` empty corpus        | Returns `[]`                                | Returns `[]`                                | Returns `[]`                                                          |
| `SearchAsync` scoring                          | BM25, normalized 0-1, tie-order stable-by-insertion (see baseline note) | Hybrid RRF or semantic re-rank, provider-local | Semantic score from Foundry file_search, provider-local        |
| `SearchAsync(query, topK, sources)` empty/null | Unscoped                                    | Unscoped                                    | Unscoped                                                              |
| `SearchAsync(query, topK, sources)` populated  | Pre-scoring filter on `Source` (OrdinalIgnoreCase) | Pre-scoring OData filter on `Source` (case-sensitive; upstream limitation, tracked in ADR-012) | Pre-scoring server-side attribute filter (OrdinalIgnoreCase) |
| `IngestDocumentAsync()`                        | Chunks, tokenizes, updates BM25 stats       | Chunks, embeds via APIM, upserts documents   | **Throws `NotSupportedException`** (read-only)                        |
| `ListDocumentsAsync()`                         | Returns snapshot ordered by `IngestedAt desc` | Returns snapshot ordered by `IngestedAt desc` | Returns files in the bound vector store                              |
| `DeleteDocumentAsync()`                        | Removes chunks + stats                      | Deletes documents by id                     | **Throws `NotSupportedException`** (read-only)                        |
| Silent-empty on outage                         | **Impossible** (no backend)                 | **Impossible** — throws `KnowledgeProviderUnavailableException` on transport failures | **Impossible** — same contract |
| Indirect-injection safety                      | Retrieved chunks flow through Content Safety at `RetrievedKnowledge` stage (unchanged from pre-Wave-5) | Same | Same |
| Cost telemetry                                 | No embeddings; no APIM traversal            | Embedding calls emit `ICostTracker` events; APIM Ocp-Apim-Subscription-Key on the wire | Retrieval and file_search calls emit `ICostTracker` events per ADR-013 |
| Cross-subject / source scoping                 | Source-string filter                        | Source-string filter                        | Vector store binding + optional attribute filter                      |
| Byte-for-byte pre-Wave-5 equivalence           | Verified by `PreWave5InMemoryBaselineTests` | N/A (new provider, opt-in)                  | N/A (new provider, opt-in)                                            |

## Documented divergences

1. **Azure AI Search case-sensitive source filter.** The upstream OData
   `search.in()` predicate is case-sensitive. Retail Pulse normalizes source
   filenames at ingest so real corpora do not observe this in practice, but
   the divergence is honestly documented so operators are not surprised by an
   ad-hoc query.
2. **Foundry IQ is read-only.** Mutation entry points throw
   `NotSupportedException` because the vector store lifecycle is owned by the
   Foundry project outside Retail Pulse. Callers rely on
   `KnowledgeBaseCapabilities.SupportsMutation` to gate ingest / delete UI.
3. **Scoring is not comparable across providers.** Every provider reports its
   score semantics through `KnowledgeBaseCapabilities.ScoreSemantics`. The
   Wave-5 UI and telemetry surface the provider name alongside every score so
   callers never rank results across providers.

## Change control

If a legitimate parity change is introduced, update this table AND the
static parity assertion test in
`tests/RetailPulse.Tests/Rag/Parity/KnowledgeProviderParityMatrixTests.cs`.
If the two disagree, the test fails and the build blocks — this is the
release gate for Wave-5 knowledge providers.
