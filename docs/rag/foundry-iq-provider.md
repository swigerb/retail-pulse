# Foundry IQ knowledge provider — operator guide

This document describes how the optional Foundry IQ (file_search) knowledge
provider (issue #104) is configured, operated, and reasoned about. See ADR-013
for the complete design record and ADR-009 for the provider abstraction
contract that Foundry IQ implements alongside InMemory (baseline) and Azure AI
Search (#103).

## When to use Foundry IQ

Pick Foundry IQ when your team wants Retail Pulse's agents to ground answers
in a corpus that already lives in a **Foundry vector store** (curated by data
stewards outside the API process). The provider is **read-only**: ingest and
delete are unsupported by design so a mis-plumbed pipeline can never mutate
the shared vector store from within Retail Pulse. If you need Retail Pulse to
own ingest/delete, use the Azure AI Search provider (#103) instead.

## Configuration surface

All settings bind to `Knowledge:FoundryIQ`. A blank `ProjectEndpoint` (or a
`ProjectEndpoint` without any vector-store selector) leaves the provider
**fully disabled** — no `PersistentAgentsClient`, no `AIProjectClient`, no
`TokenCredential`, no HTTP handler, no factory on the registry. The default
demo path stays byte-for-byte unchanged when Foundry IQ is not configured.

```json
"Knowledge": {
  "Provider": { "Mode": "FoundryIQ", "Degradation": "FailLoud" },
  "FoundryIQ": {
    "ProjectEndpoint": "https://<foundry>.services.ai.azure.com/api/projects/<project>",
    "VectorStoreName": "retail-merch-standards",
    "VectorStoreId": "",
    "RetrievalAgentName": "retail-pulse-foundry-iq-retrieval",
    "RetrievalAgentId": "",
    "Model": "gpt-5.4-mini",
    "RequestTimeoutMs": 60000,
    "PollIntervalMs": 500,
    "MaxResults": 20,
    "CostTrackingAgentId": "foundry-iq:retrieval"
  }
}
```

| Field | Required | Purpose |
| --- | --- | --- |
| `ProjectEndpoint` | Yes (enabled path) | Foundry project endpoint. Blank = disabled. |
| `VectorStoreName` | Yes (unless `VectorStoreId` set) | Human-friendly name resolved via `VectorStores.GetVectorStoresAsync`. |
| `VectorStoreId` | Optional | Exact `vs_...` id, bypasses the name lookup entirely. |
| `RetrievalAgentName` | Yes (unless `RetrievalAgentId` set) | Retail Pulse creates (get-or-create) this internal agent once per process. Default `retail-pulse-foundry-iq-retrieval`. |
| `RetrievalAgentId` | Optional | Exact `asst_...` id, bypasses the name-based get-or-create. |
| `Model` | Yes (enabled path) | Foundry model deployment used by the retrieval agent (e.g. `gpt-5.4-mini`). Required at startup even when a pre-existing `RetrievalAgentId` is used — misconfigured model catches earliest at startup. |
| `RequestTimeoutMs` | Optional (default 60 000) | Per-search bounded timeout applied through a linked `CancellationTokenSource`. |
| `PollIntervalMs` | Optional (default 500) | Polling interval while a run is queued/in_progress. |
| `MaxResults` | Optional (default 20) | Ceiling on how many file_search hits Retail Pulse asks Foundry for per search. |
| `CostTrackingAgentId` | Optional (default `foundry-iq:retrieval`) | Agent id stamped on `UsageEvent`s raised on the shared cost tracker. |

## Managed identity, no keys

The provider constructs a `DefaultAzureCredential` shared with the Azure AI
Search provider (`TryAddSingleton<TokenCredential>` — first-wins). Assign the
**Azure AI Developer** role at the Foundry project scope to whichever
identity the API runs as (managed identity in production, developer identity
locally via `az login`). No keys are read, stored, or accepted.

## Startup gate and failure modes

`FoundryIQOptions.IsConfigured` returns `true` only when both
`ProjectEndpoint` is non-blank AND (`VectorStoreName` OR `VectorStoreId`) is
non-blank. When `IsConfigured` is `false`, `AddFoundryIQKnowledgeProvider` is
a no-op and the `KnowledgeProviderRegistry` never learns about Foundry IQ —
selecting `Knowledge:Provider:Mode=FoundryIQ` in that state fails startup
with the shared unregistered-mode message from `KnowledgeProviderRegistry`.

When `IsConfigured` is `true`, `FoundryIQOptions.ValidateEnabled()` runs and
fails fast with an actionable message if any required value is missing or
the endpoint is not an absolute http(s) URL. Errors are raised at
registration time, not on the first search.

## Reconciliation with `FoundryAgent:*`

Retail Pulse already uses `PersistentAgentsClient` to run the FoundryAgent
shipment planner (issue #91). The two optional features are gated
independently and can target the same Foundry project, but they currently
construct **independent SDK clients**:

- `AgentServiceExtensions.AddAzureAgent<TAgent>` (the shipment path)
  builds its own `AIProjectClient` + `PersistentAgentsClient` inline and
  hands the client to `PersistentAgentProvider<TAgent>`. It does not
  register a `PersistentAgentsClient` in DI and it does not call
  `FoundryClientAccessor.Register`.
- `FoundryIQ` builds its own `PersistentAgentsClient` lazily via
  `FoundryClientAccessor.GetOrCreate`, keyed by canonicalised endpoint
  (trailing `/` trimmed).

Nothing today wires the two together, so pointing both at the same
`ProjectEndpoint` results in two clients side by side, not one shared
client. `FoundryClientAccessor.Register` is retained as a forward-compatible
seam for a future refactor that wants to share the SDK client across
features, but no other code calls it. This is the "coherent configuration
story" honestly stated — the features share their configuration story
(same endpoint, same managed identity, same MI role assignment), not their
SDK client instances.

`FoundryAgent:Enabled` continues to gate the shipment planner. Foundry IQ is
gated independently by `Knowledge:FoundryIQ:ProjectEndpoint`. Enabling one
does not enable the other, and the operator can run any combination — this
is by design so a rollout of the knowledge provider does not perturb the
existing shipment planner behaviour.

## Capability contract and honest limitations

`FoundryIQKnowledgeBase.GetCapabilities()` reports:

- `ProviderName = "FoundryIQ"`
- `Relevance = Semantic`
- `Persistent = true`
- `RequiresCloud = true`
- **`SupportsMutation = false`** — ingest and delete throw
  `NotSupportedException`. This is a first-class capability signal, not a
  silent no-op. Callers targeting Foundry IQ must query the capability shape
  before calling `IngestDocumentAsync` or `DeleteDocumentAsync`.
- `ScoreSemantics` — the raw file_search score is in `[0..1]`, higher is
  better, and scores are **not comparable** across providers. Cross-provider
  ranking comparisons MUST use rank order, never raw scores.
- `ChunkIndex` — Foundry does not expose a stable chunk id, so
  `SearchResult.ChunkIndex` is a **per-query rank ordinal** (0-based). It
  MUST NOT be persisted or used as a durable identifier. See ADR-013.

### Sources filter

`SearchAsync(query, topK, sources, ct)` accepts an optional `sources`
allowlist. Foundry file_search does not accept per-query file lists, so the
provider filters the mapped hits **case-insensitively by file name** before
applying the `topK` cut. Because `KnowledgeSourceRegistry` (#105) binds a
small named set per agent, this bounded post-hoc filter is intentional.

## Cost attribution and the retrieval/storage gap

Every completed search records a `UsageEvent(AgentId=CostTrackingAgentId,
Model=Model, InputTokens=Usage.PromptTokens, OutputTokens=Usage.CompletionTokens,
ToolName="file_search")` on the shared `ICostTracker`. When
`ThreadRun.Usage` is `null` OR both token counts are zero, the provider
**skips the event and emits a debug log** rather than fabricating a zero
entry (mirrors `ApimEmbeddingClient.RecordCostAsync`).

**Explicit unobservable gap** — Foundry does NOT expose per-search retrieval
cost or per-file storage cost through the SDK. `UsageEvent` therefore
captures only the model-side prompt/completion tokens the retrieval agent
consumes. Any additional Foundry retrieval or storage cost must be
reconciled from the Azure billing surface, not from Retail Pulse telemetry.
ADR-013 records this as an accepted limitation.

## Resilience

Foundry IQ builds its own `ResiliencePipelineBuilder().AddCircuitBreaker(...)`
(the SDK does not run through `IHttpClientFactory`, so the standard
`AddResilienceHandler` path does not apply). The circuit-breaker state is
published to `CircuitBreakerHealthCheck.ReportFoundryIqState`, which surfaces
in the health data with keys `foundryIqCircuitState` and
`foundryIqLastStateChange`. A stuck Foundry run cannot hang an HTTP request
indefinitely — every SDK call chain runs under
`CancellationTokenSource.CreateLinkedTokenSource(ct).CancelAfter(RequestTimeoutMs)`.
The retrieval thread is always deleted in `finally` with
`CancellationToken.None`, matching the existing shipment planner pattern.

## Live integration tests

The unit test suite runs offline and always executes. Live integration
tests skip cleanly (with an explicit skip reason) unless the following
environment variables are set:

| Variable | Required | Notes |
| --- | --- | --- |
| `RETAIL_PULSE_FOUNDRY_IQ_ENDPOINT` | Yes | Foundry project endpoint. |
| `RETAIL_PULSE_FOUNDRY_IQ_VECTOR_STORE_NAME` | Yes (unless `_VECTOR_STORE_ID` set) | Human-friendly name. |
| `RETAIL_PULSE_FOUNDRY_IQ_MODEL` | Yes | Retrieval agent model. |
| `RETAIL_PULSE_FOUNDRY_IQ_VECTOR_STORE_ID` | Optional | Exact `vs_...` id. |
| `RETAIL_PULSE_FOUNDRY_IQ_RETRIEVAL_AGENT_NAME` | Optional | Defaults to `retail-pulse-foundry-iq-retrieval`. |
| `RETAIL_PULSE_FOUNDRY_IQ_RETRIEVAL_AGENT_ID` | Optional | Exact `asst_...` id. |

The test suite includes a guard test
(`LiveTests_AssertSkipReason_WhenUnconfigured`) that asserts the exact skip
reason string is recorded — this prevents a future edit from silently
turning an outage into an unnoticed "passed" result.

## Ranked-relevance methodology

`FoundryIQRetrievalQualityComparisonTests` runs a fixed set of paraphrased
queries against InMemory (always) and Foundry IQ (when live-configured) and
records **Recall@3** per provider. The comparison is **informational only**
— the test never fails on quality, never compares raw scores across
providers, and reports `SKIPPED` for the Foundry column when the live
environment is not configured. Score-semantics vary per provider; only the
rank order of the expected document per query is compared.

## Troubleshooting

| Symptom | Likely cause | Action |
| --- | --- | --- |
| `Knowledge provider mode 'FoundryIQ' is not registered` | `Mode=FoundryIQ` without a configured endpoint | Set `Knowledge:FoundryIQ:ProjectEndpoint` and a vector-store selector, or switch `Mode` back to `InMemory`/`AzureAISearch`. |
| `Knowledge:FoundryIQ:Model is required` | Endpoint + vector store set, model missing | Set `Knowledge:FoundryIQ:Model` to a valid Foundry model deployment. |
| `FoundryIQVectorStoreNotFoundException` | `VectorStoreName` typo or wrong project | Confirm the vector store name in the Foundry portal; use `VectorStoreId` for exact binding. |
| `KnowledgeProviderUnavailableException` with 401/403 | Managed identity missing role | Assign **Azure AI Developer** on the Foundry project. |
| Circuit-breaker open | Repeated Foundry failures within the sliding window | Inspect `foundryIqCircuitState` in the health endpoint and the underlying Foundry status. |
| `NotSupportedException` on ingest/delete | Caller assumed mutation support | Query `GetCapabilities().SupportsMutation` before calling ingest/delete, or route mutation to the Azure AI Search provider. |
