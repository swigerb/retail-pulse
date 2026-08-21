# ADR-009: Pluggable knowledge providers (in-memory BM25 as default)

## Status

Accepted (Wave 5 seam - abstraction, configuration surface, degradation
policy, and conformance suite only). Cloud implementations land in later
issues (#103 Azure AI Search, #104 Foundry IQ) and per-agent knowledge
binding lands in #105.

## Context

`InMemoryKnowledgeBase` scores documents with BM25 over a
`ConcurrentDictionary`, capped at 100 documents and 5,000 chunks, and is
entirely volatile. `KnowledgeBaseSeeder` reloads a sample corpus at startup,
so the volatility is invisible until a user uploads their own document and
then loses it on restart.

The current implementation must be preserved. It has zero dependencies,
starts instantly, and keeps `dotnet run --project src/RetailPulse.AppHost`
working on a laptop with no cloud resources. That property is non-negotiable
per the umbrella (#87) constraint "No new hard cloud dependencies."

What we need is the ability to opt in to real semantic retrieval - Azure AI
Search, Foundry IQ - without making cloud resources a requirement. Callers
that consume the knowledge base (`RagContextProvider`, the message-extension
endpoint, the future per-agent knowledge binding in #105) must be
provider-agnostic.

A configured cloud provider that is unreachable at startup or query time
must NEVER silently return zero results. Empty results are reserved for
"no matching documents in the corpus" and must not be conflated with "the
backend is down". The operator must be able to opt into loud failure or
explicit fallback - not have the platform silently limp along.

## Decision

Introduce a thin provider seam behind `IKnowledgeBase` selected by
configuration. Ship only the seam in this issue; the InMemory provider is
the default and remains unchanged.

### 1. Contract extensions

`IKnowledgeBase` (in `RetailPulse.Contracts.Rag`) gains two members:

- `KnowledgeBaseCapabilities GetCapabilities()` - honest, static description
  of the provider: name, relevance kind (`Lexical` / `Semantic` / `Hybrid`),
  persistence, cloud-dependency flag, provider-enforced quotas, and a
  `ScoreSemantics` string that always includes the reminder that scores are
  provider-local and NOT comparable across providers.
- `Task ProbeAsync(CancellationToken ct)` - verifies the backend is
  reachable. The InMemory implementation is a no-op; cloud providers do a
  cheap availability check. On unreachable backends the provider throws
  `KnowledgeProviderUnavailableException`.

`KnowledgeProviderUnavailableException` (also in `Contracts.Rag`) is the
contract signal. Providers MUST throw it rather than returning an empty
result to signal outage.

### 2. Configuration

A new `Knowledge:Provider` subsection binds to `KnowledgeProviderOptions`:

```jsonc
"Knowledge": {
  "Provider": {
    "Mode": "InMemory",              // or "AzureAISearch", "FoundryIQ"
    "Degradation": "FailLoud"        // or "FallbackToInMemory"
  }
}
```

Resolution rules (`KnowledgeProviderSelector`):

- Missing / blank `Mode` -> **InMemory** (documented default; zero cloud
  dependency).
- Missing / blank `Degradation` -> **FailLoud** (a cloud provider is opted
  into deliberately; the operator should hear about outages).
- Unknown / malformed value in either field -> **fails startup** with an
  actionable message that names the field, echoes the offending value, and
  lists the valid options. Bare numeric input is rejected.

The existing `Knowledge` quotas (`MaxDocuments`, `MaxChunks`,
`MaxDocumentSizeBytes`) remain in place and remain meaningful per provider.
Each provider reports its own quotas via `GetCapabilities()`.

### 3. Provider registration

`KnowledgeProviderRegistry` is the reusable seam. Providers register
factories keyed by `KnowledgeProviderMode`:

```csharp
registry.Register(KnowledgeProviderMode.InMemory,
    sp => sp.GetRequiredService<InMemoryKnowledgeBase>());
```

`Program.cs` registers the InMemory factory automatically. Cloud modules
(#103, #104) register their own factories from their opt-in extension
methods. Selecting a mode that has no registered factory throws at startup
with a message that lists the registered modes - an operator who typos
`AzureAiSearch` or selects a mode before wiring its module gets a clear
error, never a silent degradation.

### 4. Degradation policy

`DegradingKnowledgeBase` decorates the resolved primary and holds a
reference to the always-registered in-memory instance as its fallback
target.

- **Startup probe** (`ProbeAsync`): delegates to the primary.
  - `FailLoud`: `KnowledgeProviderUnavailableException` propagates, startup
    aborts.
  - `FallbackToInMemory`: logs a prominent warning and swaps the active
    provider to the in-memory fallback for the remainder of the process
    lifetime.
- **Query-time** (search / ingest / list / delete):
  - `FailLoud`: all exceptions propagate to the caller; the endpoint layer
    surfaces a 5xx.
  - `FallbackToInMemory`: a `KnowledgeProviderUnavailableException` from
    the primary is re-tried against the in-memory fallback for that one
    request; the primary remains the configured provider for future
    requests. Any other exception still propagates.

The decorator NEVER catches an exception and returns an empty result. When
fallback is active the caller sees the fallback's genuine response; when
fallback is disabled the caller sees the failure.

### 5. Consumer wiring

`RagContextProvider` and the knowledge / message-extension endpoints
consume `IKnowledgeBase` only. They never reference `InMemoryKnowledgeBase`
directly and never inspect the concrete provider type - the abstraction is
the only surface.

### 6. Sample-corpus seeding

`KnowledgeBaseSeeder` continues to seed the InMemory instance on startup,
but only when it is the active provider (either as the configured primary
or because startup degradation swapped it in). A healthy cloud provider is
never seeded from process start - its corpus is managed independently by
#103/#104.

### 7. Shared conformance suite

`KnowledgeBaseConformanceTests` (abstract) codifies the invariants every
provider must satisfy: ingest returns an id, search on an empty corpus
returns empty, ingested content is discoverable, list surfaces ingested
documents, delete removes documents and their chunks, capabilities report
a non-empty provider name, scores are non-negative, and a healthy
`ProbeAsync` completes without throwing. `InMemoryKnowledgeBaseConformanceTests`
is the initial concrete instantiation. Future provider issues (#103, #104)
add their own concrete instantiation of the same base class.

## Consequences

### Positive

- The laptop demo is unchanged: default mode is InMemory, degradation
  irrelevant, no cloud packages referenced or restored.
- Adding a cloud provider is now a matter of implementing `IKnowledgeBase`
  and registering a factory. No changes to `Program.cs`, endpoints, or
  `RagContextProvider` are required.
- Degradation behavior is honest and configurable - the operator picks
  fail-loud (default) or explicit fallback, and empty results always mean
  what they say.
- The conformance suite gives every future provider a floor of contract
  invariants without hand-rolled per-provider tests.

### Negative

- `IKnowledgeBase` grows two members. Any external implementer (there are
  none in-repo besides the InMemory provider and test doubles) must
  implement them.
- The DI wiring gains one decorator hop (`DegradingKnowledgeBase` around
  the primary). Overhead is a single virtual call per operation, dominated
  by BM25 scoring or a network round-trip.
- Providers that do not distinguish "unreachable" from "misconfigured" will
  fail loud even under the `FallbackToInMemory` policy. This is intentional
  - misconfiguration should not silently degrade to a different corpus.

### Explicitly out of scope

- No Azure AI Search implementation (#103).
- No per-agent knowledge binding (#105).
- No changes to BM25 semantics, ranking, or the InMemory quotas.
- No changes under `src/RetailPulse.Api/Agents/` (concurrent migration).

## Compliance with umbrella constraints (#87)

- **No new hard cloud dependencies.** InMemory remains the default; no
  cloud packages added.
- **No regressions.** `IKnowledgeBase` behavior for the InMemory provider
  is unchanged; existing InMemory tests pass unmodified.
- **Feature-disabled test coverage.** Selection tests cover the default
  (blank config to InMemory), unknown-mode fail-fast, unknown-degradation
  fail-fast, and the "recognized but unregistered" case. Degradation tests
  cover both policies against a deliberately unreachable test provider.
- **Documentation is part of done.** This ADR, updated `appsettings.json`
  comments, and updated inline XML docs ship in the same PR.
