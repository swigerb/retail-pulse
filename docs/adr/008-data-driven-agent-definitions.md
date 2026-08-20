# ADR-008: Data-driven agent definitions

## Status

Accepted

## Context

Retail Pulse's headline positioning is that everything material about the
deployment — company, brands, regions, theme — lives in a single
`tenant.yaml` and the platform adapts without code changes. That was true of
the data model. It was not true of the agent roster.

Prior to this ADR, adding a specialist required all of:

1. A new C# class implementing `ISpecialistAgent`.
2. DI registration in `AgentServiceExtensions` (constructor injection, tool
   list, prefetchable variant).
3. Adding the new agent to `RetailOpsRouter`'s hardcoded specialist collection.
4. A `prompts.yaml` entry for the system prompt, model, temperature, and tools.
5. A router intent update (both the enum-like `AgentIntent` list and the
   hardcoded keyword fast-path table in `RetailOpsRouter`).
6. Rebuild and redeploy.

The `AgentDefinition`/`PromptConfiguration` schema already carried name,
model, system prompt, temperature, and tools per agent — and the
`PromptTemplateEngine` already hydrated tenant values into each field. The
individual specialist classes were mostly identical boilerplate: nine of the
ten were under 2 KB, each differing only in which `AgentDefinition` was
passed to the shared `AgentExecutionPipeline`. Two agents did carry real
bespoke logic:

- `MemoryManagementAgent` — orchestrates the conversation-memory store
  (recall / store / clear intents against `IConversationMemory`), not just an
  LLM call.
- `CompetitiveIntelAgent` — scans tool results for competitive threats and
  fires proactive SignalR alerts. Domain-specific side effect, not a prompt
  variation.

The remaining blockers were structural, not conceptual: hardcoded specialist
classes and a hardcoded router intent/keyword list.

## Decision

**Collapse the near-duplicate specialist classes into a single generic
`ConfiguredSpecialistAgent` constructed from an `AgentDefinition`. Derive the
router's intent set and keyword fast-paths from configuration. Preserve
bespoke behavior where it exists.**

Specifically:

1. **`AgentDefinition` schema is extended** with routing metadata:
   `Key`, `DisplayName`, `Intents`, `KeywordFastPaths`, `FallbackReply`,
   `CouncilParticipant`, `ScorecardDimension`, `ScorecardWeight`, `Role`
   (default `"specialist"`), and `Prefetchable`. Every field is optional so
   existing YAML continues to load unchanged.

2. **`ConfiguredSpecialistAgent`** is the sole `ISpecialistAgent` +
   `IPrefetchableAgent` implementation. It runs on the shared
   `IAgentExecutionPipeline` (ADR-007's MAF `ChatClientAgent` execution),
   surfaces `Key` / `SupportedIntents` / `KeywordFastPaths` straight from its
   `AgentDefinition`, and exposes a `protected virtual OnToolResult` hook for
   subclasses that need a domain-specific side effect (e.g. the Competitive
   Intel SignalR alerts).

3. **The nine formerly-specialised classes** (`GeneralAgent`,
   `DemandForecastAgent`, `PromoPlanningAgent`, `FieldSentimentAgent`,
   `SupplyChainAgent`, `StoreOpsAgent`, `PlanogramAgent`, `MarginAgent`,
   `CompetitiveIntelAgent`) become thin sealed shims over
   `ConfiguredSpecialistAgent`. `EnsureDefaults` populates the `Key` /
   primary intent / fallback reply when the caller supplies a
   minimally-populated `AgentDefinition`, so pre-refactor construction
   sites and unit tests keep working unchanged. `PromoPlanningAgent` retains
   its `CheckApprovalAsync` bespoke method; `CompetitiveIntelAgent` retains
   its alert-firing behavior via the `OnToolResult` override.
   `MemoryManagementAgent` keeps its hand-written orchestration logic and
   simply accepts an optional `AgentDefinition` so its Key / Intents /
   keyword table can be declared in `prompts.yaml`.

4. **`AgentToolRegistry`** is a named registry of `AITool` factories. Each
   specialist's `tools:` list in `prompts.yaml` is resolved through it.
   `ValidateAllReferences` is called once at startup; a typo or missing
   registration raises `UnknownToolReferenceException` with the full list of
   missing and known names, so config errors fail loudly before the app
   accepts traffic instead of quietly at first user query.

5. **`RetailOpsRouter`** no longer holds a compiled keyword table. Its
   `_keywordPatterns` is now built at construction time from every
   specialist's `KeywordFastPaths` plus any orchestration-only
   `RouterIntentConfig` records (Portfolio Health Council, Scorecard, etc.).
   `KnownIntents` is exposed so `ParseClassification` can accept a
   config-added intent from the LLM without normalising it to General.

6. **`ConsensusOrchestrator` and `ScorecardOrchestrator`** take optional
   config lists — `councilParticipants` and `scoringDimensions` — with the
   previous hardcoded rosters as the fallback default. The
   composition-root `RouterAgentRoster` singleton derives both from
   `PromptConfiguration` (agents with `council_participant: true` and those
   with a non-empty `scorecard_dimension` + `scorecard_weight`).

7. **`RoutingServiceExtensions.AddAgentRouting`** takes
   `(PromptConfiguration, AgentToolRegistry, IReadOnlyList<RouterIntentConfig>)`
   and enumerates specialists from configuration. It validates every
   tool reference, detects duplicate keys, then registers each specialist by
   well-known key onto its bespoke class (`MemoryManagementAgent`,
   `CompetitiveIntelAgent`) or falls back to `ConfiguredSpecialistAgent` for
   any unrecognised key. This is the "add-a-specialist-by-editing-yaml"
   path: no C# `case` clause is required for a new specialist.

**Adding a specialist now looks like this — no rebuild:**

```yaml
agents:
  # ... existing agents ...

  loyalty-analytics:
    name: "Loyalty Analytics Agent"
    model: "gpt-5.4-mini"
    key: "loyalty-analytics"
    display_name: "Loyalty Analytics"
    role: "specialist"
    intents:
      - "loyalty/analytics"
    keyword_fast_paths:
      - "loyalty program"
      - "reward redemption"
    council_participant: false
    scorecard_dimension: ""
    fallback_reply: "I couldn't generate a loyalty analytics response."
    system_prompt: |
      You are a Loyalty Analytics specialist for {tenant.company}.
      ...
    temperature: 0.2
    tools:
      - GetLoyaltyProgramMetrics
      - CreateChart
```

A restart picks up the new agent, the router advertises the new intent, and
`RouterAgentRoster` includes the agent in council/scorecard fan-outs when
the corresponding flags are set.

## Explicit non-goal

This ADR does **not** open a path for arbitrary users to inject prompts into
a running deployment. `PromptConfiguration` is trusted deployment input at
this stage — the `prompts.yaml` file is committed alongside the app or
delivered through the same deployment channel as `tenant.yaml`. Safety
validation of agent definitions (schema, prompt-injection heuristics, tool
allow-listing per role) is issue #99 and must land before any path exists
for untrusted config to reach the loader. This ADR keeps the loader
unchanged in that respect and does not make issue #99 harder to add on top.

## Consequences

**Positive:**

- The headline "no code changes required" claim is now true for the agent
  roster, matching what was already true for the data model.
- Nine near-identical `.cs` files collapse to sealed shims that exist only
  to preserve the pre-refactor constructor signatures for existing call
  sites and tests. The behaviour lives in one place.
- Config errors are caught at startup with actionable messages
  (`UnknownToolReferenceException` lists the missing name and every
  registered tool). The old fail-at-first-invocation posture is gone.
- The router's intent set, keyword fast-paths, council roster, and
  scorecard dimensions all derive from a single YAML surface. Editing one
  file replaces edits in five.
- The proof test
  (`DataDrivenSpecialistTests.TestOnlySpecialist_AddedThroughConfigurationOnly_IsRoutedAndExecuted`)
  proves the objective with a widget specialist declared purely in
  configuration and exercised end-to-end.

**Negative / trade-offs:**

- `ConfiguredSpecialistAgent` is now the vast majority of the specialist
  code. It has to remain flexible enough for both prefetchable and bespoke
  subclasses. The `protected virtual OnToolResult` hook is the extension
  seam — anything more invasive should keep its bespoke class.
- Behaviour parity vs the pre-refactor keyword table is a hard requirement
  and is enforced by the eval baseline. Any future change to the keyword
  list is now a `prompts.yaml` edit, and reviewers must run the eval
  harness to check for baseline drift.
- The router accepts an intent it doesn't recognise only if that intent is
  either in `AgentIntent.All` or in the config-derived `KnownIntents` set.
  An LLM classification returning a wholly unknown intent still degrades
  to General, which is intentional but worth reiterating: bad LLM output
  cannot invent a specialist that isn't configured.

## Alternatives considered

1. **Keep classes, extract shared trait.** Would still require a new C#
   class per specialist plus DI wiring. Does not deliver the "config-only"
   objective.

2. **A generic specialist plus per-agent JSON descriptor files.** Duplicates
   `prompts.yaml` responsibility. Rejected in favour of extending the
   existing schema.

3. **Reflection-based auto-registration from a specialists namespace.**
   Solves fewer problems (still requires C# classes), introduces reflection
   at startup that is harder to reason about than a data-driven loop, and
   makes fail-loud tool validation harder because attributes would need to
   be scanned before DI is composed.

4. **Leave the two hardcoded orchestrators (`ConsensusOrchestrator`,
   `ScorecardOrchestrator`) untouched and only make specialists data-driven.**
   The council roster and scorecard weight table would then diverge from
   `prompts.yaml`, so a new agent added by editing YAML would silently miss
   the council. Rejected in favour of a single source of truth.

## References

- Issue #98 — Data-driven agent definitions: configurable specialists and router intents
- Issue #87 — Wave 3: platform configurability
- Issue #99 — Safety validation of agent definitions (blocks untrusted config)
- ADR-001 — Multi-agent routing (baseline intent set)
- ADR-003 — Consensus Council (fan-out roster now data-driven)
- ADR-007 — MAF agent primitives (execution stack preserved)
