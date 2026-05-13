# Squad Decisions

## Active Decisions

### Multi-Agent Router Architecture (2026-05-13)

- **Context:** The single RetailPulseAgent handled all user queries with one system prompt and all 7 tools. As we add domain-specific specialists, we need a routing layer that classifies user intent and dispatches to the right agent.
- **Decision:** Implemented a three-layer architecture:
  1. **`IAgentRouter`** (in Contracts) — classifies intent, returns `RoutingDecision` with agent key + confidence score.
  2. **`ISpecialistAgent`** (in Contracts) — interface every specialist implements (`Key`, `DisplayName`, `SupportedIntents`, `HandleAsync`).
  3. **`RetailOpsRouter`** (in Api) — LLM-based router using a dedicated low-temperature classification prompt. Falls back to General agent when confidence < 0.6 or on error.
  4. **`GeneralAgent`** — refactored from RetailPulseAgent. Implements `ISpecialistAgent`, handles all 7 tools, catches all intent categories as fallback.
- **Registration pattern:** `AddAgentRouting()` extension method on `IServiceCollection`. Each specialist registers as `ISpecialistAgent` via DI. Router discovers specialists via `IEnumerable<ISpecialistAgent>`. Adding a new specialist = one class + one DI registration.
- **Tenant-awareness:** Router accepts `tenantId` parameter. Current implementation routes uniformly; future sprints can filter agent availability by tenant config.
- **Backward compatibility:** Legacy `RetailPulseAgent` class preserved as a thin wrapper. Existing `/api/chat` endpoint now routes through the pipeline. All 174 existing tests pass unchanged.
- **Telemetry:** Router classification emits `agent.routing` OTel spans with intent, confidence, and fallback tags.
- **Impact:** All future specialist agents (Demand, Supply, Sentiment, etc.) implement `ISpecialistAgent` and register via DI. The router prompt lives in `prompts.yaml` under `agents.router`.
- **Owner:** Kroger (Lead Architect)

### Agent Routing UI Architecture (2026-05-13)

- **Context:** Sprint 1.1 required making the multi-agent routing visible in the frontend — both subtly per-message in chat and prominently in the telemetry dashboard.
- **Decision:** Agent routing colors, emojis, and labels are centralized in `src/RetailPulse.Web/src/constants/agentRouting.ts` (demand=blue, promo=green, supply=orange, competitive=red, sentiment=purple, general=gray). The `RoutingInfo` type is on the shared `ChatResponse` contract as an optional field. Two new components: `AgentRoutingIndicator` (subtle pill in chat) and `AgentRoutingPanel` (statistics widget in telemetry drawer). The `AgentSpan.type` union now includes `'routing'` for trace visualization.
- **Impact:** Backend (Costco) should include `routing: { agentId, agentName, intentCategory, confidence, reasoning? }` on `ChatResponse` when the router makes a decision. The frontend will gracefully degrade when `routing` is absent (backward compatible). All agent-type coloring should reference the shared constants file.
- **Owner:** Chick (Frontend Dev)

### Router Test Infrastructure (2026-05-13)

- **Context:** Sprint 1.1 required comprehensive tests for the multi-agent routing system (`RetailOpsRouter`, `GeneralAgent`, `ISpecialistAgent`). The interfaces exist in `RetailPulse.Contracts.Routing` namespace, and the implementations are in `RetailPulse.Api.Agents.Routing` and `RetailPulse.Api.Agents.Specialists`.
- **Decision:** Tests target the `Contracts.Routing` interface surface (`IAgentRouter`, `ISpecialistAgent`, `RoutingDecision`, `AgentIntent`) exclusively — not the earlier prototype interfaces that were removed. The `ParseClassification` internal method on `RetailOpsRouter` is tested directly since it's marked `internal` and the API project has `InternalsVisibleTo` for the test project. Mock `IChatClient` returns fixed JSON to test classification deterministically.
- **Test Coverage:**
  - `tests/RetailPulse.Tests/Agents/Router/RetailOpsRouterTests.cs` — 33 tests: intent classification for all 6 categories, confidence threshold (0.6), fallback, ParseClassification edge cases, multi-intent, history propagation, error handling
  - `tests/RetailPulse.Tests/Agents/Specialists/GeneralAgentTests.cs` — 21 tests: ISpecialistAgent identity, HandleAsync contract, backward compatibility, error handling, token usage/cost
  - `tests/RetailPulse.Tests/Integration/RouterIntegrationTests.cs` — 9 tests: full pipeline (route → agent → response), DI registration smoke tests, telemetry span verification, multi-tenant scenarios
  - `tests/RetailPulse.Tests/Fixtures/AgentTestFixtures.cs` — shared factory methods for mocks
- **Impact:** 63 new tests, all 237 total tests pass. Test coverage now includes the routing layer as a regression safety net for ongoing Sprint 1 specialist agent work.
- **Owner:** Target (Tester)

### Router Contract Reconciliation (2026-05-08)

- **Context:** Sprint 1.1 required routing contracts, a RetailOpsRouter, GeneralAgent refactor, and DI wiring. Kroger (Architect) and Costco (Backend Dev) both implemented the same feature in parallel.
- **Decision:** Adopted Kroger's contract design (`RetailPulse.Contracts.Routing` namespace) over Costco's root-level contracts. Key reasons:
  1. **Namespace isolation** — `Contracts.Routing` keeps routing types separate from core chat contracts
  2. **Richer intent model** — Slash-separated intents (`"demand/forecasting"`) support future sub-categorization
  3. **Cleaner specialist interface** — `Key`/`SupportedIntents`/`HandleAsync(ChatRequest)` is more idiomatic than `AgentId`/`IntentCategories`/`ChatAsync`
- **Consequences:** Legacy `RetailPulseAgent` kept as thin wrapper for backward compat with existing tests. All new specialist agents must implement `ISpecialistAgent` from `Contracts.Routing`. Router confidence threshold is 0.6 (Kroger's choice, slightly lower than the 0.7 in the original task spec).

### Variant-Level Data in SQLite + GetVariantMix Tool (2026-05-07)

- **Context:** The demo failed on variant-level chart requests (e.g., "donut chart of Apex Grill's variant mix in the Southwest") because the SQLite database only stored brand-level metrics. Variant names existed in tenant.yaml but were not queryable.
- **Decision:**
  1. Added `VariantMix` table to the SQLite schema with deterministic seeding from tenant.yaml variant arrays. Mix percentages are normalized random weights per brand×region×variant (seeded via `GetStableHash("variant|{brand}|{region}")`). DepletionsYoY is ±5% range.
  2. Added `GetVariantMix` MCP tool (brand required, region optional/National). National region averages MixPercent and DepletionsYoY across all regions via SQL GROUP BY.
  3. Updated `prompts.yaml`: registered `GetVariantMix` in tools array and Available Tools, added "variant mix / product breakdown / SKU split" → GetVariantMix to the Concept-to-Tool Mapping, and rewrote "Always Chart Available Data" to call GetVariantMix FIRST for variant requests and chart real data directly (no "Estimated" label).
- **Impact:** Variant-level chart requests now resolve to real seeded data. The "Always Chart Available Data" section still handles non-variant estimated breakdowns, but variant queries are now first-class. No breaking changes to existing tools or tables.
- **Owner:** Costco (Backend Dev)

### Tool Enforcement in System Prompt (2026-05-07)

- **Context:** gpt-5.4-mini was responding to data/visualization requests with text-only responses, skipping available tools (GetPortfolioDepletionStats, CreateChart) entirely. The system prompt described tools but never mandated their use.
- **Decision:** Added a "Critical: Always Use Tools for Data Requests" section to `prompts.yaml` that (1) mandates tool calls for all data questions, (2) maps common business concepts to specific tools (e.g., "market share" → GetPortfolioDepletionStats), and (3) maps data types to chart types (e.g., proportional breakdown → pie chart). This section is placed BEFORE the visualization guidelines so the model encounters the mandate early.
- **Impact:** The model should now reliably call data tools first, then CreateChart for visualizations, instead of producing text-only responses. No C# or frontend changes needed — this is prompt engineering only.
- **Owner:** Costco (Backend Dev)

### Telemetry Total Duration (2026-05-04)

- **Context:** The backend `thought` span covers the full `GetResponseAsync()` wall-clock time, so summing span durations in the web telemetry drawer overstates total request time when tool calls are present.
- **Decision:** Expose `TotalDurationMs` on the shared `ChatResponse` contract and have the telemetry drawer prefer that response-level value, with a fallback to summed spans when the response-level value is absent.
- **Impact:** Individual span durations remain visible, the total duration display reflects real request time, and older clients/responses stay compatible through the fallback path.
- **Owner:** Costco (Backend Dev)

### Logo Placement (2026-05-05)

- **Context:** The app needs both a compact brand mark for general navigation chrome and the full shipped logo image for the chat welcome experience.
- **Decision:** `BrandLogo.tsx` should remain the synthetic RP gradient box plus wordmark component, while `/retail-pulse-logo.jpg` is used only in `ChatPanel.tsx`'s empty-state welcome area as a centered hero image.
- **Impact:** Shared UI keeps the lighter, scalable brand mark, and the large raster logo is confined to the one place where a full-brand splash is desired.
- **Owner:** Chick (Frontend Dev)

## Directives

### All Code Agents Use Claude Opus 4.7 (2026-05-13T10:41:16Z)

- **Authority:** Brian Swiger (via Copilot)
- **Directive:** All agents that write code must use Claude Opus 4.7 model.
- **Rationale:** User directive — captured for team memory

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
