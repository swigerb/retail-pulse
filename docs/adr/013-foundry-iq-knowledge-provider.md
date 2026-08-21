# ADR-013: Optional Foundry IQ (file_search) knowledge provider

## Status

Accepted (issue #104 — Wave 5 cloud-provider implementation).

## Context

ADR-009 shipped the `IKnowledgeBase` provider seam. ADR-012 added the
optional Azure AI Search provider (issue #103) as the persistent hybrid
retrieval story. Some deployments provision their grounding corpus
directly in an Azure AI Foundry project instead of Azure AI Search:
a Foundry-managed vector store owned by whoever runs the Foundry
project, with an embedding index built by Foundry's file search
tooling.

We want to reach that corpus from Retail Pulse without:

- pulling the corpus into our own persistent store;
- forcing operators to duplicate documents into a second index; or
- introducing a second cloud dependency on the demo path.

The Foundry file_search retrieval surface differs from Azure AI
Search in three ways that materially affect the contract:

1. There is no direct `VectorStores.Search(query)` RPC in the GA
   SDK (`Azure.AI.Agents.Persistent` 1.1.0). File search is only
   exposed as an agent tool call: post the query as a user message to
   a thread, run an agent that has the file_search tool bound to the
   target vector store, and parse the file-search results out of the
   run steps.
2. Foundry file_search scores are on `[0..1]` and provider-local;
   they are not comparable to BM25 (in-memory) or hybrid RRF (Azure
   AI Search) scores.
3. Foundry does not expose a per-file chunk index. A hit carries
   `FileId`, `FileName`, `Score`, and (opt-in) chunk text; ordering
   is rank order for the query.

## Decision

Implement `FoundryIQKnowledgeBase` as an opt-in `IKnowledgeBase`
provider that plugs into the ADR-009 seam. The provider is fully
optional at every layer, exactly matching the AzureAISearch
optionality story:

- Configuration: `Knowledge:FoundryIQ:ProjectEndpoint` blank
  (default) means nothing registered. Blank plus no vector-store
  selector also means blank.
- DI: `AddFoundryIQKnowledgeProvider` is a no-op on the disabled
  path — no `AIProjectClient`, no `PersistentAgentsClient`, no
  `TokenCredential`, no `IKnowledgeProviderContribution`, no
  `IKnowledgeBase` registration. The default demo path stays
  byte-for-byte identical to the `InMemory`-only baseline.
- Runtime: `Knowledge:Provider:Mode` defaults to `InMemory`;
  selecting `FoundryIQ` without wiring the extension throws with
  the shared `KnowledgeProviderRegistry` "not registered" message.

### Retrieval bridge — how file_search maps to IKnowledgeBase

`SearchAsync` executes the following on every call:

1. Resolve the target vector store id from
   `FoundryIQVectorStoreResolver` (get-by-id when configured,
   otherwise page `GetVectorStoresAsync` and match by `Name`, then
   cache).
2. Resolve the retrieval agent id from
   `FoundryIQRetrievalAgentProvider` — a lazy get-or-create keyed
   by `RetrievalAgentName`. The agent is created once with a
   `FileSearchToolDefinition` bound to the resolved vector store
   and reused across calls (same pattern as
   `PersistentAgentProvider`, no cleanup at shutdown).
3. Create a fresh thread. Always delete the thread in `finally`
   with `CancellationToken.None` — the `FoundryShipmentAgent`
   pattern; leaking threads would pile up Foundry-side quota.
4. Post the raw query as `MessageRole.User`.
5. Start the run with the
   `RunAdditionalFieldList.FileSearchContents` include — the ONLY
   way to receive chunk text back from Foundry 1.1.0 GA.
6. Poll `GetRunAsync` on a bounded timeout
   (`FoundryIQOptions.RequestTimeoutMs`, default 60 s, poll every
   `PollIntervalMs` ms, default 500 ms).
7. On `Completed`, enumerate `Runs.GetRunStepsAsync` and collect
   every `RunStepFileSearchToolCall.Results.Results` entry. Dedupe
   by `FileId + Content.Text` in the incoming rank order.
8. Map each hit to `SearchResult`:
   - `DocumentId = result.FileId`
   - `Title = result.FileName` (fall back to `FileId`)
   - `Chunk = concatenated Text content items`
   - `Source = result.FileName` (matches the ingested filename
     semantics the InMemory / AzureAISearch providers already use, so
     issue #105's named-source filter matches without a special case)
   - `Score = (double)result.Score` (already 0..1, no clamp)
   - `ChunkIndex = ordinal in the rank-ordered result list`.
     **This is a per-query rank position, not a stable identifier.**
     Foundry does not expose a per-file chunk index in this SDK
     version; the field is populated for surface compatibility.
9. `sources` filter is post-hoc: Foundry does not accept a per-query
   file list on the tool call, so we filter the mapped hits by
   `Source` case-insensitively before the topK cut. #105's
   `KnowledgeSourceRegistry` binds a small named set at startup, so
   post-hoc filtering is bounded and acceptable — see "Consequences".

### Supported vs unsupported operations — first-class capability

`FoundryIQKnowledgeBase` is a **read-only** provider. Its corpus
is owned outside Retail Pulse; the honest boundary is to surface
ingest and delete as unsupported rather than silently mutate a
corpus we do not own.

To make that honesty a first-class contract signal we added
`KnowledgeBaseCapabilities.SupportsMutation` (default `true` so
existing providers stay honest without changes). The FoundryIQ
provider reports `SupportsMutation = false`. The shared
conformance suite in
`tests/RetailPulse.Tests/Rag/KnowledgeBaseConformanceTests.cs`
gates its ingest / list / delete assertions on the flag and
asserts a new invariant on the read-only path:

- `IngestDocumentAsync` throws `NotSupportedException`.
- `DeleteDocumentAsync` throws `NotSupportedException`.

`NotSupportedException` (not `KnowledgeProviderUnavailableException`)
is the deliberate choice: `DegradingKnowledgeBase.WithFallbackAsync`
treats availability failures as fallback triggers, and silently
running an InMemory ingest on Foundry IQ's behalf would lie about
where the document went. `DegradingKnowledgeBaseFoundryIQTests`
asserts the propagation.

### Chunk identity and score semantics

- `ChunkIndex` is per-query rank position starting at 0. It is
  documented on `KnowledgeBaseCapabilities.ScoreSemantics` and in
  the operator guide. Callers MUST NOT persist it as a stable
  identifier for a chunk within a file.
- `Score` is the Foundry `[0..1]` file_search score, verbatim.
  `ScoreSemantics` returns exactly:
  `Foundry file_search score in [0,1]. Higher is better. Scores are provider-local and NOT comparable across providers.`
  The words "not comparable" appear verbatim so the retrieval
  quality harness and future providers can key on the same
  invariant string.

### Managed identity, resilience, and cost attribution

- **MI only, no keys.** The extension registers
  `TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential())`
  so a process running both AzureAISearch and FoundryIQ shares one
  credential. Foundry IQ does **not** route through the APIM AI
  Gateway — the Foundry data plane authenticates directly on the
  SDK client via MI. Only chat completions and embeddings flow
  through APIM.
- **Bounded timeout + circuit breaker.** The 1.1.0 GA client owns
  its own HTTP pipeline; every SDK call is wrapped in
  `CancellationTokenSource.CreateLinkedTokenSource(ct).CancelAfter(RequestTimeoutMs)`.
  A private `Polly` `ResiliencePipeline` inside
  `FoundryIQKnowledgeBase` enforces the shared
  `5 failures / 30 s sampling / 30 s open` semantics identical to
  `AddKnowledgeEmbeddingsResilienceHandler`. Breaker state is
  reported on `CircuitBreakerHealthCheck` under
  `foundryIqCircuitState` so ops sees FoundryIQ / AzureAISearch /
  MCP / Content Safety on the same probe.
- **Cost attribution — model tokens.** Every completed run's
  `ThreadRun.Usage.PromptTokens` / `CompletionTokens` produces a
  `UsageEvent` on the shared `ICostTracker`:
  - `AgentId = options.CostTrackingAgentId` (default `"foundry-iq:retrieval"`)
  - `Model = options.Model` (the retrieval agent's deployment name)
  - `InputTokens = usage.PromptTokens`, `OutputTokens = usage.CompletionTokens`
  - `ToolName = "file_search"`
  - `Timestamp = DateTime.UtcNow`.

  When `Usage` is `null` (SDK reports it only on terminal runs) the
  provider logs a debug line and skips the event —
  `ApimEmbeddingClient.RecordCostAsync` behaves the same way.

### Explicit cost attribution gap

Foundry may separately bill vector-store storage and retrieval-side
token usage against the Foundry project. The 1.1.0 GA SDK does
**not** report those figures on `RunCompletionUsage`.

**Retail Pulse attributes only the retrieval agent's model tokens
per `SearchAsync` call.** Vector store storage plus Foundry-side
retrieval charges accrue directly to the Foundry project and are
visible in the Azure Cost Management view for that project — not
in Retail Pulse's cost dashboard. `docs/rag/foundry-iq-provider.md`
repeats the gap in the operator guide.

### Relationship with `FoundryAgent:*`

`FoundryAgent:*` (issue #83 / shipment specialist) and
`Knowledge:FoundryIQ:*` (this ADR) serve different intents but may
point at the same Foundry project:

| Case | Behavior |
|---|---|
| Both configured, same `ProjectEndpoint` | Share the singleton `PersistentAgentsClient` via `FoundryClientAccessor.TryAdd`. No duplicate client. |
| `FoundryAgent:Enabled=false`, `Knowledge:FoundryIQ:*` configured | FoundryIQ builds its own `AIProjectClient` + `PersistentAgentsClient`. Shipment specialist stays disabled; file-search corpus works independently. |
| Different endpoints | Both clients coexist, keyed by endpoint URL. Startup logs an info-level line so operators see both target projects. |
| Neither configured | Nothing materialized. Default demo path is unchanged. |

`AgentServiceExtensions.AddAzureAgent<TAgent>` is untouched. The
shared-client seam is a new opt-in helper
(`FoundryClientAccessor`); it does not rewrite the shipment
wiring. If a future refactor promotes the shipment specialist to
use the shared accessor, ADR-013's shared-client contract is the
forward-compatible starting point.

## Consequences

**Wins**

- The Foundry-managed grounding corpus is reachable without pulling
  documents into Retail Pulse's own store.
- The FoundryIQ provider and the shipment specialist are genuinely
  orthogonal (each can be enabled independently) but share the
  `PersistentAgentsClient` when they target the same project.
- `SupportsMutation` is a first-class capability, so the shared
  conformance suite runs against every provider — no evasion of
  the invariant "an ingested document must be discoverable".

**Costs**

- Every `SearchAsync` incurs one Foundry run: create-thread +
  create-message + create-run + poll + list-run-steps +
  delete-thread. Even a `topK=1` query is not free. Callers that
  need sub-second retrieval should stay on `InMemory` or
  `AzureAISearch`.
- Vector store storage and Foundry-side retrieval token costs are
  **not** visible in Retail Pulse's cost dashboard. Operators must
  read Azure Cost Management on the Foundry project.
- `sources` filtering is post-hoc. The `KnowledgeSourceRegistry`
  in #105 keeps the named set bounded, so overfetching is bounded
  too, but a caller that asks for a huge `topK` scoped to a small
  source may drop many out-of-scope hits before returning.
- Foundry file_search does not expose a per-file `ChunkIndex`; the
  field carries per-query rank position. Consumers that treated
  `ChunkIndex` as a stable chunk identifier must migrate to
  `DocumentId + Chunk` content-hashing.

## Explicitly out of scope

- No in-app ingest surface for Foundry IQ. Operators seed the
  vector store through Foundry directly.
- No per-agent binding UX — that lives in #105 and only requires
  the four-argument `SearchAsync` overload, which this provider
  honors.
- No plan-review / checkpoint / ChatEndpoints surface (#94) — this
  is a KB-only feature.
- No changes to `FoundryShipmentAgent`, `PersistentAgentProvider`,
  or `AgentServiceExtensions.AddAzureAgent<TAgent>`. When both
  features target the same project the shared client is added via
  a NEW extension helper; the shipment path is not rewritten.
- No SDK upgrade. `Azure.AI.Agents.Persistent` stays pinned at
  1.1.0 (nuget.org disabled, `azure-default` feed only; central
  package management in `Directory.Packages.props`).
