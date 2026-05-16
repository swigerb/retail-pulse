# Squad Decisions

## Active Decisions

### Default 3-minute client timeout on chat fetches (2026-05-15)
- **Author:** Chick (Frontend Dev)
- **Context:** The initial-screen suggested-prompt buttons called sendMessage() with a etch('/api/chat') that had no client-side timeout. When the backend stalled, the chat spinner ran indefinitely with no error surfaced — a hard demo blocker.
- **Decision:** All chat fetches in services/api.ts now default to a **180s (3 min) client-side timeout**, aligned with the backend Azure OpenAI network timeout established in commit 02bf3f7. On timeout the request is aborted and a friendly error (Request timed out after 180s. The server may be busy — please try again.) is thrown so the UI clears its loading state.
- **Implications for the team:**
  - New frontend service-layer functions that call long-running backend endpoints **must** include a client-side timeout via AbortController composition. No more naked etch(...) for chat-class operations.
  - If Costco bumps the backend Azure OpenAI timeout, the DEFAULT_TIMEOUT_MS constant in src/RetailPulse.Web/src/services/api.ts should move in lock-step.
  - Tests for any new service should cover both the success and timeout paths (use i.useFakeTimers() + dvanceTimersByTimeAsync).
### 75s request-level timeout on chat endpoints (2026-05-15)
- **Author:** Costco (Backend Dev)
- **Context:** The chat pipeline runs two sequential IChatClient calls (router classify + specialist execute) plus tool calls. With no overall cap and a 3-minute network timeout per attempt, a single stalled AI Gateway hop could leave the UI spinner running long enough that the executive demo broke.
- **Decision:** /api/chat and /api/chat/stream are now bounded by a 75-second per-request CancellationTokenSource linked to the client's HttpContext.RequestAborted. The Azure OpenAI client's NetworkTimeout was lowered from 3 minutes to 60 seconds.
- **Rationale:** 75s is comfortably above the p99 happy-path latency for tool-using chats but firmly bounds worst-case so the FE always gets a real response (504 with code: "request_timeout") instead of infinite spin.
- **Implications for the team:**
  - **Chick (Frontend):** /api/chat may now return 504 Gateway Timeout with { error, code: "request_timeout" }. Treat it like 503 in error UI, but the message text is more informative and worth surfacing verbatim.
  - **Target (Tests):** Any test that mocks IChatClient with an indefinite delay should expect cancellation now. New test ideas: assert that /api/chat returns 504 when the chat client takes longer than the request timeout.
  - **Kroger (Architecture):** If we ever need a longer cap (e.g. async council convene), make it endpoint-specific rather than reverting the chat default.
### Demo readiness tests for default UI prompts (2026-05-15)
- **Author:** Target (Tester)
- **Context:** User (Brian) is presenting Retail Pulse to executive leadership and asked for every default chat prompt to be exercised. The 26 prompts shown on the empty-state UI live in src/RetailPulse.Web/src/components/ChatPanel.tsx (PROMPT_CATEGORIES). They were not covered by any existing test — only individual specialist and router tests existed.
- **Decision:** Added 	ests/RetailPulse.Tests/Integration/DemoReadinessTests.cs (58 cases) that mirrors production RoutingServiceExtensions.AddAgentRouting registration order and parameterizes over every UI prompt. The test data (DefaultUiPrompts) is the source-of-truth mirror of PROMPT_CATEGORIES.
- **Convention going forward:** When PROMPT_CATEGORIES in ChatPanel.tsx changes (new prompt added, copy edit, prompt removed), DefaultUiPrompts in DemoReadinessTests.cs must be updated in the same PR. The xUnit [Theory] will fan out and any new prompt automatically gets routing + dispatch coverage.
- **Findings to address (out of scope for this PR):**
  1. GeneralAgent shadows PromoPlanning, SupplyChain, and CompetitiveIntel specialists at the router because of first-wins TryAdd lookup combined with DI registration order. Confirm intent.
  2. scorecard/portfolio is defined in AgentIntent.All but no agent claims it — silently falls back to General. Either register a specialist or remove the intent.
  3. No HTTP-level integration tests for /api/chat or /api/chat/stream. The endpoints require Azure credentials to start, blocking WebApplicationFactory use. Worth investing in a stub/test harness.


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

### Align Demo Store Regions to Tenant.yaml Naming (2026-05-14)

- **Context:** The demo store data in Dashboard.tsx used "West" as a region name, but tenant.yaml defines "West Coast". This caused a mismatch between what the heatmap displays and the canonical tenant configuration.
- **Decision:** Renamed all "West" region references in demo data to "West Coast" to match tenant.yaml exactly. Added stores for Southwest and Pacific Northwest to ensure all 6 tenant regions are represented.
- **Canonical regions (from tenant.yaml):**
  1. Northeast
  2. Southeast
  3. Midwest
  4. Southwest
  5. West Coast
  6. Pacific Northwest
- **Impact:** Any future demo data or region references should use these exact names.
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

### Sprint 4 Frontend Cleanup (2026-05-13)

- **Context:** Sprint 4 cleanup addressed three frontend code-quality issues: dead streaming state in ChatPanel, timer churn in StreamingMessage, and missing request cancellation in observability API calls.
- **Decisions:**
  1. **Dead streaming state removed (s4-dead-streaming):** Removed component-level `streamingTokens` / `isStreaming` state and their `void` suppressions from `ChatPanel.tsx`. These were placeholder state for future SignalR streaming that never materialized — the actual streaming uses per-message `isStreaming` on `ChatMessage`. The bottom-of-chat streaming preview block (which could never render) was also removed, and the loading indicator condition simplified from `loading && !isStreaming` to just `loading`.
  2. **StreamingMessage timer pattern fixed (s4-timer-churn):** Replaced the per-render `setInterval` pattern in `StreamingMessage.tsx` with a single persistent `requestAnimationFrame` loop. The old pattern created and tore down a new interval on every state change (each tick → new `displayedLength` → effect re-runs → new interval). The new pattern starts one rAF loop on mount, reads the latest target from a ref, and self-throttles at 18ms per tick. Cleanup cancels the single rAF handle on unmount.
  3. **AbortController added to observability fetches (s4-abort-controller):** All five functions in `observabilityApi.ts` now accept an optional `AbortSignal` parameter. Consumer components (`CostDashboard`, `AuditLogViewer`, `ConversationExport`) create an `AbortController` per useEffect and abort on cleanup — this cancels in-flight requests on unmount and on superseding selections (e.g., switching cost dashboard period).
- **Impact:** No new features. Reduces React state churn, eliminates timer leaks, and properly cancels stale network requests. All 249 tests pass, build clean.
- **Owner:** Chick (Frontend Dev)

# Decision: Competitive Intelligence + RAG Knowledge Base UI Architecture

**Date:** 2026-05-13
**Author:** Chick (Frontend Dev)
**Sprint:** 2.2 + 2.3

## Context

Sprint 2.2 required a competitive intelligence dashboard and Sprint 2.3 required a RAG knowledge base management UI. Both needed to integrate into the existing Dashboard shell alongside chat, promo planner, and telemetry views.

## Decisions

### 1. Two new component directories (`competitive/`, `knowledge/`)

Each sprint gets its own directory under `src/components/` with a barrel `index.ts`, matching the pattern established by `forecast/`, `alerts/`, `traces/`, and `promo/`.

### 2. Separate API services per domain

Created `competitiveApi.ts` and `knowledgeApi.ts` as standalone service modules (matching `promoApi.ts`, `memoryApi.ts`, `approvalApi.ts` pattern). Each owns its endpoint paths and response typing.

### 3. Dashboard activeView extension

Extended the `activeView` union type with `'competitive' | 'knowledge'` and added header nav buttons (Shield icon, Library icon). This keeps the single-view-at-a-time pattern rather than introducing tabs or nested routing.

### 4. Inline styles for Griffel-incompatible CSS properties

Griffel's `makeStyles` doesn't accept `borderColor` shorthand or dynamic color values from constants in pseudo-selectors. Solution: keep layout/transform properties in `makeStyles`, apply dynamic colors via inline `style` prop. This is consistent with existing patterns (AlertCard uses inline `style` for `borderLeftColor`).

### 5. Color constants in agentRouting.ts

Added `COMPETITIVE_COLORS` and `KB_COLORS` to the shared constants file, following the established `FORECAST_COLORS`, `PROMO_COLORS` pattern. Components reference these for consistency.

## Impact

- Dashboard now supports 5 views: chat, promo, competitive, knowledge, (plus telemetry drawer)
- 22 new tests bring total to 135 passing
- No breaking changes to existing components

### Forecast Chart Architecture (2026-05-13)

- **Context:** Sprint 1.2 required building the demand forecast visualization — the centerpiece of the Demand Forecasting Agent's output. Needed to render actual vs predicted lines, confidence bands, seasonal annotations, and risk callouts.
- **Decision:**
  1. Forecast components live in `src/components/forecast/` as a self-contained module (ForecastChart, ForecastSummary, DemandRiskCards) with barrel export.
  2. `ForecastData` type added to `types/index.ts` as the canonical contract — matches the backend API shape (historical[], predicted[] with bounds, seasonality[], risks[]).
  3. ChartRenderer integration uses two detection paths: (a) `forecastData` prop for standalone use, (b) `forecast` property on ChartSpec for inline chart detection. Both paths render ForecastChart.
  4. Demand agent color changed from `#3b82f6` (blue) to `#6366f1` (indigo) in AGENT_COLORS to distinguish from the actual-data blue line. New `FORECAST_COLORS` and `SEASONAL_COLORS` constants centralized in `agentRouting.ts`.
  5. ForecastChart uses Recharts `ComposedChart` (not separate Line/Area charts) to layer confidence band, actual line, predicted line, seasonal ReferenceAreas, and today ReferenceLine in a single coordinated view.
- **Impact:** Backend (Costco) should include `forecast: ForecastData` on relevant ChatResponse chart specs when the Demand agent produces forecast output. The frontend will render the full forecast visualization automatically. All existing chart rendering is backward compatible — `forecastData` prop is optional.
- **Owner:** Chick (Frontend Dev)

### Knowledge API Contract Alignment (2026-05-13)

- **Context:** The frontend `knowledgeApi.ts` had significant contract drift from the backend knowledge endpoints in `Program.cs`. Five mismatches were identified across endpoints, request shapes, response shapes, and TypeScript types.
- **Decision:** Aligned the frontend to match the backend contracts exactly:
  1. **Upload:** Changed from `FormData` POST to `/api/knowledge/documents` → JSON POST to `/api/knowledge/upload` with `{ title, content, source? }`. File content is read as text via `file.text()` before sending. Return type is now `KnowledgeUploadResponse { documentId, title, status }` (backend doesn't return full `DocumentInfo` on upload).
  2. **Search:** Changed from `GET /api/knowledge/search?q=` → `POST /api/knowledge/search` with JSON `{ query, topK? }`. Response is unwrapped from `{ query, results }` envelope.
  3. **Types realigned:**
     - `KBDocument`: Removed `sourceType` and `contentPreview` fields (don't exist in backend `DocumentInfo`).
     - `KBSearchResult`: Renamed `documentTitle`→`title`, `chunkPreview`→`chunk`, `relevanceScore`→`score`. Added `source` field.
     - `KBStats`: Changed from `{ totalDocuments, totalChunks, lastIngestionDate, documentsBySourceType, mostCitedDocuments }` → `{ documentCount, chunkCount, averageChunksPerDocument }` to match backend `/api/knowledge/stats`.
  4. **Components updated:** `KnowledgeStats` simplified (removed chart and cited-docs sections that relied on non-existent backend data). `SearchResults` and `KnowledgeBasePanel` updated for new field names. `DocumentUpload` now triggers parent refresh instead of optimistic insert.
  5. **Added `KnowledgeUploadResponse` type** for the upload endpoint's actual response shape.
- **Impact:** All frontend knowledge API calls now match backend routes and DTOs exactly. The `KnowledgeStats` component is simpler but accurate — if the backend adds richer stats later, the component can be enhanced to match. All 249 tests pass, build succeeds.
- **Owner:** Chick (Frontend Dev)

### Cost Tracking: Config-Driven Pricing + Model Passthrough (2026-05-13)

- **Context:** The cost tracker hardcoded `"gpt-4o"` as the model name for every usage event, and `InMemoryCostTracker` maintained a separate hardcoded pricing table that diverged from the `TokenPricing` section in `appsettings.json`. Additionally, `Program.cs` had duplicate singleton registrations for `AdaptiveCardState`, `CostTracker`, `AuditLog`, and `ConversationExporter`.
- **Decision:**
  1. Added `Model` property to `ISpecialistAgent` contract. All specialist agents expose their configured model from `AgentDefinition.Model`. `MemoryManagementAgent` returns `"none"` since it doesn't call an LLM.
  2. `Program.cs` chat endpoint now uses `specialist.Model` instead of hardcoded `"gpt-4o"` when creating `UsageEvent`.
  3. `InMemoryCostTracker` reads pricing from `IConfiguration` (`TokenPricing:*` section) instead of a hardcoded dictionary. Unknown models fall back to $1.00/$5.00 per 1M tokens (input/output).
  4. Removed duplicate DI registrations — each service registered exactly once.
- **Impact:** Cost dashboards now show accurate per-model costs. Adding new model pricing requires only an `appsettings.json` update — no code changes. All 1,413 tests pass.
- **Owner:** Costco (Backend Dev)

### Demand Forecasting MCP Tools + Simulated Data (2026-05-13)

- **Context:** Sprint 1.2 requires a Demand Forecasting Agent. The data layer and MCP tools must exist before the agent can be wired up. Kroger is designing the agent in parallel.
- **Decision:**
  1. Added `DemandHistory` table (Brand, Region, Channel, Date, Volume, Units) with 365 days of daily granularity per brand/region/channel — ~78,840 records seeded from tenant.yaml.
  2. Added `SeasonalFactors` table (Category, Month, Multiplier, EventName, Description) with 38 curated rows covering 6 product categories.
  3. Created 4 MCP tools in `DemandTools.cs`:
     - `GetHistoricalDemand` — weekly-aggregated history, filterable by brand/region/channel/months
     - `GenerateForecast` — trailing avg + seasonal multiplier + trend, ±15% confidence bands
     - `GetSeasonalityFactors` — seasonal multipliers by category with event context
     - `IdentifyDemandRisks` — anomaly detection (drops >20%, spikes >30%, trend reversals >15%)
  4. Forecast algorithm: `trailing_30day_avg × seasonal_multiplier × (1 + trend_slope × days_ahead)`. Simple enough for a demo, produces plausible outputs.
  5. Schema version bumped to v3 — existing databases will re-seed on next startup.
  6. REST endpoints added at `/api/demand/{history|forecast|seasonality|risks}` matching tool signatures.
- **Impact:** Kroger can now wire the Demand Forecasting Agent to these 4 tools. The seasonal factors table is also available for the agent to explain its reasoning. Anomaly injection ensures `IdentifyDemandRisks` always finds something interesting to report.
- **Owner:** Costco (Backend Dev)

# Phase 4 Architectural Decisions

**Author:** Costco (Backend Dev)  
**Date:** Session work  
**Scope:** Phase 4 sprints 4.1, 4.2, 4.3

## Decisions

### 1. Schema v7 — Five New Tables
Added StoreMetrics, ShelfLayouts, SkuVelocity, BrandFinancials, MarginDrivers. Follows existing seeded-SQLite pattern with deterministic hash-based data generation.

### 2. Escalation Levels (L1/L2/L3)
EscalationOrchestrator uses keyword-based complexity detection to route queries through escalation levels. L1 = simple single-dimension, L2 = multi-dimensional cross-analysis, L3 = strategic/executive with exec brief format.

### 3. Scorecard Weighted Dimensions
ScorecardOrchestrator scores brands across 5 dimensions with these weights: Demand Momentum (0.25), Competitive Position (0.20), Supply Reliability (0.20), Store Execution (0.20), Margin Health (0.15). Weights are constants, not yet configurable per tenant.

### 4. Raw String Literal Pattern
Used `$$"""` with `{{variable}}` interpolation for JSON templates in ScorecardOrchestrator to avoid CS9006 errors with literal braces. This pattern should be used for any future agent that builds JSON in raw string literals.

### 5. New AgentIntent Constants
Added slash-separated intents following Kroger's contract convention: `store/operations`, `planogram/optimization`, `margin/analysis`, `scorecard/portfolio`. All added to `AgentIntent.All` for router validation.

### 6. ExplainabilityService Pattern
Captures tool execution traces as structured data (tool name, input, output, duration) and chains them into human-readable explanation narratives. Registered as singleton, not per-request.

### Quotas & Bounded Storage for In-Memory Stores (2026-05-13)

- **Context:** Code review flagged unbounded in-memory stores as DoS vectors — knowledge base accepted unlimited uploads, cost tracker grew without limit, conversation exporter had no session/message caps and used thread-unsafe `List<T>` for concurrent writes.
- **Decision:** All three in-memory stores now enforce configurable quotas via `IOptions<T>` pattern:
  - **Knowledge base:** 10MB per doc, 100 docs max, 5000 chunks max (all configurable under `Knowledge:` config section)
  - **Cost tracker:** 10K events max + 24h TTL eviction on write, ConcurrentQueue for FIFO eviction (configurable under `Observability:` section)
  - **Conversation exporter:** 1K sessions with LRU eviction, 200 messages/session, lock-per-session for thread safety (configurable under `Observability:` section)
  - Options classes: `KnowledgeOptions` and `ObservabilityOptions` in `src/RetailPulse.Api/Configuration/`
- **Key design choices:**
  1. ConcurrentQueue over ConcurrentBag for cost tracker — preserves insertion order for TTL eviction
  2. Lock-per-session over ConcurrentBag for message lists — preserves message order in exports
  3. LRU via `Volatile.Read/Write` on a `long LastActivity` ticks field — avoids heavy locking for activity tracking
  4. Validation happens *before* ingestion/storage, not after — fail fast with clear error messages
- **Impact:** All in-memory stores are now bounded and thread-safe. Quotas are runtime-configurable via appsettings. No contract changes needed.
- **Owner:** Costco (Backend Dev)

### Teams SSO Strict Tenant Validation (2026-05-13)

- **Context:** Code review found `TeamsSsoHandler` fell back to `common` when `MicrosoftEntra:TenantId` was missing and always included the common issuer as valid. This silently accepted tokens from any Entra tenant.
- **Decision:** Three-layer defense:
  1. **Startup guard:** Non-Development environments throw `InvalidOperationException` if `MicrosoftEntra:TenantId` is not configured.
  2. **Issuer lockdown:** When a tenant ID is configured, only tenant-specific issuers are valid. `common` issuer only used in Development when no tenant is configured.
  3. **tid claim validation:** After standard JWT validation, the `tid` claim is compared against the configured tenant. Mismatch = rejection when `MicrosoftEntra:StrictTenantValidation` is true (Production default).
- **Config:** `MicrosoftEntra:StrictTenantValidation` defaults to `true` in `appsettings.json`, overridden to `false` in `appsettings.Development.json`.
- **Impact:** All team members deploying to Production/Staging must ensure `MicrosoftEntra:TenantId` is set in environment config. Local development is unaffected — `common` fallback still works when no tenant is configured.
- **Owner:** Costco (Backend Dev)

### Supply Chain Data Layer + MCP Tools + Council Endpoints (2026-05-13)

- **Context:** Sprint 2.4 requires a supply chain data layer for the new SupplyChainAgent (Kroger building in parallel), plus Portfolio Health Council support endpoints.
- **Decision:**
  1. **Schema v6:** Added three new SQLite tables: `InventoryLevels` (Brand, Region, Category, SKU, CurrentStock, SafetyStock, DaysOfSupply, Status), `SupplyDisruptions` (Brand, Region, DisruptionType, Severity, Description, dates, ImpactedSKUs, IsActive), `FulfillmentRates` (Brand, Region, Period, FillRate, OnTimeRate, BackorderCount). All with COLLATE NOCASE and appropriate indexes.
  2. **Seed data:** ~180 inventory records (60% healthy/20% low/15% critical/5% OOS), 18 active disruptions (logistics 40%/supplier 25%/weather 20%/demand_surge 15%), 6 months × 12 brands × 6 regions fulfillment history with 25% chance of declining trends.
  3. **MCP Tools (SupplyTools.cs):** GetInventoryLevels, GetSupplyDisruptions, GetFulfillmentRate, GetSupplyHealthSummary — follow same `[McpServerToolType]` pattern as DemandTools/CompetitiveTools.
  4. **API Proxy Tools:** InventoryLevelsTool, SupplyDisruptionsTool, FulfillmentRateTool, SupplyHealthTool — follow same HttpClient proxy pattern as existing tools.
  5. **REST endpoints on MCP Server:** `/api/supply/inventory`, `/api/supply/disruptions`, `/api/supply/fulfillment`, `/api/supply/health`
  6. **REST endpoints on API:** Same supply endpoints proxied to MCP, plus `/api/council/convene` (POST), `/api/council/agents` (GET).
  7. **Council convene endpoint:** Returns placeholder CouncilVerdict structure with participant list. ConsensusOrchestrator integration deferred to Kroger's parallel work.
  8. **GetSupplyHealthSummary:** Composite query that aggregates inventory, disruptions, and fulfillment into Green/Yellow/Red assessment per brand/region.
- **Impact:** Kroger can wire SupplyChainAgent tools to these endpoints. Council endpoints ready for ConsensusOrchestrator integration. SchemaVersion bumped to 6 forces re-seed.
- **Owner:** Costco (Backend Dev)

# Competitive Intelligence Agent Architecture

**Date:** 2026-05-20
**Author:** Kroger (Lead Architect)
**Sprint:** 2.2

## Decision

Implemented the Competitive Intelligence Agent as a full specialist in the multi-agent routing pipeline, with three key architectural choices:

### 1. Inline Proactive Alert Integration
CompetitiveIntelAgent is the first specialist to fire proactive alerts inline during tool result processing (not via the background ProactiveAlertService). When `DetectThreats` or pricing tools return high-severity results, the agent immediately fires `competitive_threat` alerts via SignalR, using the same SqliteAlertService throttling (1 alert per type/brand/region per hour).

**Rationale:** Competitive threats are time-sensitive — waiting for the next background check cycle (5 min) could delay reaction. Inline firing provides sub-second alert delivery while the user is actively analyzing competitive data.

### 2. Defensive Strategy Framework (MATCH/DIFFERENTIATE/IGNORE/PREEMPT)
The system prompt codifies a four-strategy defensive framework for competitive responses, with clear triggers:
- **MATCH** — price gap >15% and losing share
- **DIFFERENTIATE** — price gap 5-15% with strong brand loyalty
- **IGNORE** — niche/regional/temporary competitor moves
- **PREEMPT** — early signals of competitive entry

**Rationale:** Provides consistent, actionable recommendations instead of generic "monitor the situation" advice. The framework is embedded in the system prompt (not code) so it can be tuned without deployments.

### 3. Temperature 0.4 (Higher Than Other Analytical Agents)
Competitive intelligence uses temperature 0.4 vs 0.3 for demand forecasting and promo planning.

**Rationale:** Competitive strategy requires more creative thinking than pure numerical analysis. The slightly higher temperature allows the agent to suggest innovative defensive strategies while still grounding responses in data from tools.

## Alternatives Considered

- **Background-only alerts:** Would add 0-5 minute latency to competitive threat notifications. Rejected for time-sensitivity reasons.
- **Separate alert agent:** Over-engineering for the current scope. The inline pattern can be extracted later if needed.
- **Same temperature as other analysts (0.3):** Produced overly conservative recommendations in testing. 0.4 struck the right balance.

## Impact

- New files: 8 (4 MCP/API proxy tools, 1 MCP tool class, 1 specialist agent, 1 decisions file)
- Modified files: 4 (RetailPulseDb.cs, McpServer/Program.cs, Api/Program.cs, RoutingServiceExtensions.cs, prompts.yaml)
- Schema version: 4 → 5 (forces re-seed for competitive tables)
- Router already had `competitive/market` intent — no router prompt changes needed

### Documentation Structure for Multi-Agent Features (2026-05-13)

- **Context:** The demo walkthrough (Acts 0–5) and architecture doc only covered the original single-agent system. Sprints 1.2–4.3 added 8 specialist agents, escalation chain, portfolio scorecard, memory, guardrails, streaming, caching, collaborative cards, and observability — none of which were documented for demo or architecture audiences.
- **Decision:** Extended both docs additively:
  1. **Demo walkthrough** gets Acts 6–10: Multi-agent routing (Act 6), Promo planning + approval gates (Act 7), Competitive intel + escalation chain (Act 8), Portfolio scorecard + explainability + council (Act 9), Enterprise shield — guardrails/streaming/caching/memory/observability (Act 10). 15 new impressive queries added.
  2. **Architecture doc** gets 7 new sections between Resilience Patterns and Deployment Topology: Multi-Agent Router Architecture (dispatch flow, specialist registry, registration pattern), Escalation Chain (L1→L2→L3), Portfolio Health Council (consensus pattern), Memory Middleware Pipeline, Guardrails/Cache/Streaming Pipeline, Collaborative Adaptive Cards, Decision Explainability.
- **Principles:** No existing content removed. Demo acts follow the same narrative style (narration, action, what happens, key talking points). Architecture sections use the same diagram + table format as existing sections.
- **Impact:** Demo time extended from ~12 to ~25 minutes with audience-specific guidance. Architecture doc now reflects the full multi-agent system.
- **Owner:** Kroger (Lead)

### Documentation Alignment with Sprint 2–3 Hardening (2026-05-13)

- **Context:** Documentation had drifted from the codebase after Sprints 1–3 added auth hardening, rate limiting, endpoint extensions, bounded channels, and health check configuration. The testing guide conflated local emulator expectations with real Teams SSO requirements, leading to confused acceptance criteria.
- **Decision:** Updated five documentation files to align with current code:
  1. `docs/testing-guide.md` — Split into explicit local dev (DevelopmentAuthHandler, no SSO, no tenant validation) vs real Teams (TeamsSsoHandler, JWT Bearer, StrictTenantValidation) sections with environment-specific test scenarios.
  2. `docs/teams-setup.md` — Added "SSO & Tenant Validation" section documenting StrictTenantValidation config flag, MicrosoftEntra:TenantId requirement, and HealthMode (fail-fast vs degraded) configuration.
  3. `docs/architecture.md` — Added Sprint 2 endpoint extensions pattern (14 endpoint group classes), Sprint 3 bounded channels (TelemetryPushChannel, MemoryExtractionChannel with capacity 1000), updated security table with rate limiting/auth/tenant isolation, updated component diagram description.
  4. `docs/demo-walkthrough.md` — Added auth/rate-limiting notes to prerequisites and troubleshooting table.
  5. `docs/code-review-report.md` — Added "Resolution Status" section mapping all 24 findings to their resolution sprint (23 resolved, 1 tracked for SpecialistAgentBase refactoring).
- **Impact:** All documentation now accurately reflects the codebase as of Sprint 4. New contributors can correctly distinguish local dev auth bypass from production SSO requirements. The code review report serves as a living audit trail.
- **Owner:** Kroger (Lead Architect)

### Proactive Sales Alert Architecture (2026-05-13)

- **Context:** Sprint 1.5 required a background system that monitors demand/supply data for anomalies and proactively pushes alerts to the dashboard via SignalR. This is the first "unsolicited" intelligence feature — the system speaks first, not the user.
- **Decision:** Implemented as a `BackgroundService` (IHostedService) with a dedicated SQLite database (`data/alerts.db`) for alert persistence, throttling, and snooze state. Key architecture choices:
  1. **IAlertService** contract in `RetailPulse.Contracts.Alerts` — `CheckForAlertsAsync`, `SnoozeAsync`, `DismissAsync`, `GetHistoryAsync`, `GetActiveAlertsAsync`. Alert record includes Id, Type, Severity, Title, Description, Brand, Region, RecommendedAction, DetectedAt, Metadata dictionary.
  2. **ProactiveAlertService** (BackgroundService) — runs on a configurable timer (`Alerts:CheckIntervalMinutes`, default 5). Fetches demand data from MCP server's `/api/historical-demand` endpoint (same data source as DemandForecastAgent). Initial 30-second delay on startup so the rest of the app can initialize.
  3. **Anomaly Detection** — three simple rules comparing 7-day current period vs 8-37 day baseline: demand_spike (>20%, high if >40%), supply_drop (<-15%, high if <-30%), trend_reversal (sign change + >10% magnitude). Linear regression for trend direction.
  4. **Throttling** — `AlertThrottles` table prevents the same (type, brand, region) from firing more than once per hour. Configurable window.
  5. **Snooze/Dismiss** — `AlertSnoozes` table per-user, `AlertDismissals` table for acknowledgments. Snooze applies to alert type (optionally scoped to brand/region).
  6. **SignalR Push** — `alert_fired` event broadcast to all TelemetryHub clients with full Alert payload (id, type, severity, title, description, brand, region, recommendedAction, detectedAt, metadata).
  7. **OTel** — `RetailPulse.Alerts` ActivitySource with `alert.check_cycle` spans including `alerts.detected` count tag.
- **Registration:** `builder.Services.AddProactiveAlerts(dbPath)` registers SqliteAlertService as singleton + IAlertService, and ProactiveAlertService as IHostedService.
- **Data Access:** The alert service does NOT access SQLite demand data directly. It calls the MCP server's REST endpoints via HttpClient (same `McpServer` named client). This maintains the clean separation between data layer (McpServer) and API layer.
- **Impact:** Dashboard can subscribe to `alert_fired` SignalR events to render real-time alert cards. REST endpoints allow history viewing, snoozing, and dismissing. Future sprints can add Teams bot integration to push alerts as proactive Teams cards.
- **Owner:** Kroger (Lead Architect)

# Decision: Streaming & Guardrails Middleware Architecture

**Author:** Kroger (Lead Architect)  
**Date:** 2026-05-16  
**Sprint:** 3.1 + 3.2  

## Context

Sprint 3.1 (Streaming/Caching) and 3.2 (Guardrails/Content Filtering) required adding middleware layers to the existing chat pipeline without disrupting the multi-agent router architecture from Sprint 1.1.

## Decisions

### 1. Guardrails as Scoped Middleware (not HTTP middleware)

**Decision:** GuardrailsMiddleware is a scoped DI service called explicitly in the chat endpoint, not an ASP.NET Core middleware registered in the HTTP pipeline.

**Rationale:** Guardrails need access to `ChatRequest` typed objects, not raw HTTP requests. Making it a DI service keeps it testable and allows fine-grained control over where in the pipeline it runs (before routing for input, after agent execution for output).

### 2. Pipeline Ordering: Guardrails → Cache → Route → Agent → Cache Store → PII Redact

**Decision:** Input guardrails run first (can reject before any work), then cache check (avoid redundant LLM calls), then normal agent pipeline, then cache store + PII redaction on output.

**Rationale:** This ordering minimizes wasted computation — blocked requests never hit the cache or router, and cached responses skip agent execution entirely.

### 3. Cache Key: Pre-Route SHA256

**Decision:** Cache key is `SHA256("pre-route|normalized_query")` — computed before routing, so the same query always hits cache regardless of which agent would handle it.

**Rationale:** The router is deterministic, so the same query always routes to the same agent. Keying pre-route means cache hits skip both routing and agent execution.

### 4. Deterministic Detection for Cache Eligibility

**Decision:** `CacheHelpers.IsCacheable()` uses a keyword blocklist (forecast, predict, recommend, suggest, etc.) to exclude non-deterministic queries from caching.

**Rationale:** Caching forecasts or recommendations would serve stale/incorrect data. Factual queries ("what are current prices for X") are safe to cache with 5-minute TTL.

### 5. Streaming via SignalR Fallback

**Decision:** The `/api/chat/stream` endpoint uses `StreamResponseFallbackAsync` to push pre-computed responses as word-boundary tokens via SignalR, rather than requiring IChatClient streaming support from agents.

**Rationale:** The specialist agents return full responses via `HandleAsync`. True token-level streaming would require refactoring every agent to expose `IAsyncEnumerable<string>`. The fallback approach provides streaming UX with zero agent changes, and can be upgraded to true streaming per-agent later.

## Impact

- All existing agents are unaffected — guardrails and caching are transparent middleware
- New endpoint `/api/chat/stream` available for streaming-capable clients
- PII redaction applies globally to all agent responses

# Decision: Demand Forecasting Test Strategy

**Author:** Target (Tester)  
**Date:** 2026-05-14  
**Sprint:** 1.2  
**Status:** Proposed

## Context

Sprint 1.2 introduces the Demand Forecasting Agent with 4 MCP tools backed by seeded SQLite data (~79K rows). Tests needed to cover the agent contract, tool behavior, data integrity, and routing integration without duplicating Sprint 1.1 patterns.

## Decision

### Test Architecture (4-layer coverage)

1. **Agent contract tests** (`DemandForecastAgentTests.cs`, 28 tests) — validate `ISpecialistAgent` compliance, response shape, and tool isolation using mocked `IChatClient` (same pattern as `GeneralAgentTests`).

2. **Tool/query tests** (`DemandToolTests.cs`, 30 tests) — test the 4 DB query methods directly against real SQLite with seeded data. Validates filtering, aggregation, anomaly detection, and seasonal adjustment logic.

3. **Data integrity tests** (`DemandDataTests.cs`, 46 tests) — validate the seed data itself: brand/region/channel coverage, time span completeness, seasonal patterns, and volume integrity. These catch seed regressions that would silently break tool tests.

4. **Routing integration tests** (5 tests added to existing `RouterIntegrationTests.cs`) — verify demand intents route to `DemandForecastAgent` and coexist with `GeneralAgent`.

### Key Patterns

- **Real DB, not mocks** for tool/data tests — matches Sprint 1.1 precedent (`UpdateMetricsToolTests`). Seeded data is deterministic so assertions are stable.
- **Parameterized brand tests** — `[Theory]` + `[InlineData]` across all 12 brands ensures no brand is silently missing from forecasts.
- **Seasonal pattern validation** — tests verify that multipliers actually vary by month and that known peaks (spirits → Nov/Dec) are correct.

### Bug Fixes Found During Testing

| File | Issue | Fix |
|------|-------|-----|
| `GenerateForecastTool.cs:21` | Extra `channel` param not in DB method | Removed param |
| `RetailPulseDb.cs:~1311` | Extension method on `dynamic` type fails | Explicit `(string)` cast |

### Risk: Duplicate MCP Tool Registration

Both individual tool files (`GetHistoricalDemandTool.cs`, etc.) and `DemandTools.cs` define `[McpServerTool]` attributes with identical names. This compiles but may cause runtime duplicate registration errors. **Costco should resolve which pattern to keep.**

## Impact

- Test count: 237 → 346 (+109 new)
- All tests pass in ~14s
- Regression safety net for demand forecasting feature before Sprint 1.3

# Decision: Sprint 1.5/1.6 Test Strategy — Alerts, Tracing & Phase 1 Regression

**Author:** Target (Tester)
**Date:** 2026-05-15
**Status:** Accepted

## Context

Sprints 1.5 (Proactive Alerts) and 1.6 (Distributed Tracing) introduced new subsystems requiring comprehensive test coverage. Additionally, with all Phase 1 features complete, a regression suite was needed to validate cross-feature interactions.

## Decision

### Alert Testing (45 tests across 4 files)

- **InMemoryAlertService** created as testable implementation of `IAlertService` with deterministic anomaly detection, configurable throttle windows, and snooze/dismiss support.
- Tests validate deviation thresholds (>40% = high, >20% = medium), throttle key specificity (brand|region), and cross-user isolation.
- Method naming: `SnoozeAsync` implements the interface contract (3 params); `SnoozeWithDetailsAsync` adds optional brand/region specificity (avoids C# overload ambiguity).

### Tracing Testing (25 tests across 2 files)

- Tests target `InMemoryTraceCollector` (backend team's implementation with SignalR).
- Ring buffer eviction, concurrent capture, and structured summary generation validated.
- `CapturedSpan` bridge record created to fix pre-existing build error in OTelAgentMiddleware.

### Phase 1 Regression (15 tests)

- Integration tests exercise cross-feature flows: router → memory → approval → alerts → tracing.
- DI registration smoke tests ensure all Sprint 1.x services resolve correctly.
- Backward compatibility tests confirm existing `/api/chat` pipeline still works.

## Consequences

- Total test count: **540** (443 existing + 97 new). All passing.
- Alert service uses string-based Type/Severity (matching backend team's contract choice), not enums.
- `SnoozeWithDetailsAsync` naming convention established for extended interface methods.

### Use Fluent UI v9 Accordion Primitives for Collapsible Sections (2026-05-14)

- **Context:** `CollapsibleSection.tsx` was a hand-rolled accordion with anti-patterns: `▶` text chevron, `maxHeight: 5000px` CSS hack, manual ARIA/keyboard handling, and custom CSS variables that bypass Fluent theming.
- **Decision:** Replace all hand-rolled collapsible/accordion UI with Fluent UI v9's `Accordion`, `AccordionItem`, `AccordionHeader`, `AccordionPanel` from `@fluentui/react-components`. These primitives handle chevron rendering, keyboard navigation, ARIA attributes, and expand/collapse animation natively.
- **Implications:**
  - **New collapsible sections** should use `CollapsibleSection` (which wraps Fluent Accordion) or use Fluent Accordion primitives directly — no hand-rolling.
  - **Theming:** Use Fluent `tokens.*` for colors instead of custom CSS variables like `var(--color-text-subtle)`. This ensures proper theming under `teamsDarkTheme`.
  - **Accessibility:** Fluent Accordion provides WCAG-compliant keyboard nav and ARIA out of the box — don't add manual `role="button"` or `aria-expanded` on top.
- **Owner:** Chick (Frontend Dev)
- **Status:** Implemented

### Fluent UI v9 Compliance — Standing User Directive (2026-05-14)

- **Directive:** All UX must follow Fluent UI v9 guidelines. Use native Fluent UI v9 components (Accordion, Button, Drawer, etc.) instead of hand-rolled alternatives. The frontend agent (Chick) must treat this as a standing rule for all UI work.
- **Rationale:** User request — captured for team memory
- **Owner:** Brian Swiger (via Copilot)

### Azure OpenAI Client Network Timeout + Timeout Error Handling (2026-05-14)

- **Context:** The multi-tool Promo Planning Agent (`PromoPlanningAgent`) triggers 4+ sequential function-calling round-trips (PromoHistoryTool, CalculateLiftTool, EvaluateTimingTool, EstimateROITool, MarginProxyTools). When MCP server latency is high or unreachable, the cumulative time exceeds the default 100-second `HttpClient.Timeout`, causing `TaskCanceledException` that surfaces as the generic "Something went wrong" error.
- **Decision:**
  1. **Set `NetworkTimeout = 3 minutes`** on `AzureOpenAIClientOptions` passed to the `AzureOpenAIClient` constructor in `Program.cs`. This gives multi-tool agents enough headroom for their function-calling loops.
  2. **Catch `TaskCanceledException` and `OperationCanceledException`** (when not user-initiated cancellation) in `AgentExecutionPipeline.ExecuteAsync` BEFORE the generic `Exception` catch. Return a dedicated timeout message: "⏳ The request took too long to complete..."
- **Rationale:**
  - 3 minutes is generous but bounded — prevents indefinite hangs while supporting complex multi-step analyses.
  - The `when (!ct.IsCancellationRequested)` filter ensures user-initiated cancellations still propagate normally.
  - Timeout-specific messaging helps users understand the failure mode vs. a generic server error.
- **Impact:**
  - All agents benefit from the increased timeout (single shared `AzureOpenAIClient`).
  - No behavioral change for queries that complete within 100s — only extends the ceiling.
  - Telemetry tags `error.type = "timeout"` for observability filtering.
- **Owner:** Costco (Backend Dev)
- **Status:** Implemented

### Stores Page UX Overhaul (2026-05-14)
- **Context:** Brian reported 3 UX issues on the Stores page: vertical scroll from oversized heatmap, useless Planogram section, and store click doing nothing visible.
- **Decision:**
  1. **Planogram removed** from Stores page rendering entirely. `PlanogramDiagram.tsx` file kept in case it's useful later, but not rendered. Demo data removed from Dashboard.tsx.
  2. **Heatmap compacted** — cells reduced from 110px min-width to 80px, padding from 14px to 8px, gap from 8px to 6px. Reduces vertical footprint significantly.
  3. **Store click** now opens a Fluent UI v9 `Dialog` showing store details (name, region, revenue, target, performance level, issues, recommendations) via new `StoreDetailDialog.tsx` component.
  4. **Layout reordered** — Heatmap → Performance Table → Stockout Risks (was: Heatmap → Stockout → Planogram → Table).
- **Impact:** All 249 tests pass. Build clean. The PlanogramDiagram component file and its tests remain untouched. Dashboard no longer imports PlanogramDiagram or the PlanogramLayout type.
- **Owner:** Chick (Frontend Dev)

### Dynamic Planogram — Agent-Driven Shelf Optimization

`PlanogramDiagram.tsx` exists, is tested, and renders before/after shelf layouts beautifully — but was removed from the Stores page because it rendered static hardcoded data. Brian wants this to be a genuine AI-driven capability triggered by natural language:

> "Optimize shelf layout for Store X with Product Y"

The existing MCP tool `OptimizePlanogram` already returns optimization metadata (uplift %, notes) but its response shape doesn't match the frontend's `PlanogramLayout` type — it returns raw slot arrays without brand names, colors, or eye-level indicators. We need to bridge that gap.

## Architecture

### 1. MCP Tool: `GeneratePlanogramLayout`

A **new** MCP tool (not a rename of `OptimizePlanogram`) that returns data shaped for direct frontend rendering.

**Tool Signature:**
```csharp
[McpServerTool(Name = "GeneratePlanogramLayout")]
[Description("Generate before/after planogram layouts for shelf optimization. Returns full rendering data including brand colors, eye-level indicators, and per-slot predicted uplift.")]
public static object GeneratePlanogramLayout(
    RetailPulseDb data,
    [Description("Store ID (required, e.g. 'STR-0001')")] string storeId,
    [Description("Aisle or category to optimize (e.g. 'snacks', 'beverages', 'A-003')")] string category,
    [Description("Optional: specific product/brand to feature (e.g. 'Apex Grill')")] string? featureProduct = null)
```

**Response Schema (JSON):**
```json
{
  "storeId": "STR-0001",
  "storeName": "Westfield Market",
  "category": "snacks",
  "featureProduct": "Apex Grill",
  "summary": "Moved Apex Grill to eye-level shelf 3, expanded facing from 1→2. Predicted +6.3% category revenue.",
  "predictedUpliftPercent": 6.3,
  "before": {
    "shelfCount": 5,
    "eyeLevelShelves": [2, 3],
    "slots": [
      {
        "shelfLevel": 1,
        "position": 0,
        "skuName": "Classic Chips 12oz",
        "brand": "SnackCo",
        "brandColor": "#4A90D9",
        "facingWidth": 2,
        "predictedUplift": null
      }
    ]
  },
  "after": {
    "shelfCount": 5,
    "eyeLevelShelves": [2, 3],
    "slots": [
      {
        "shelfLevel": 3,
        "position": 0,
        "skuName": "Apex Grill Smoky BBQ",
        "brand": "Apex Grill",
        "brandColor": "#E8451C",
        "facingWidth": 2,
        "predictedUplift": 12.1
      }
    ]
  }
}
```

The `before` and `after` fields map 1:1 to the existing `PlanogramLayout` TypeScript interface. No type changes needed.

**Simulation Logic (demo-grade):**
1. Query `ShelfLayouts` + `SkuVelocity` for the store/aisle to build the "before" state
2. Resolve SKU names and brand names from the `Products` table (new lookup — currently `GetShelfLayout` only returns skuId)
3. Assign deterministic `brandColor` from a palette keyed by brand name hash
4. Build "after" by applying optimization rules:
   - Move `featureProduct` (if specified) to eye-level shelf
   - Sort remaining slots by `dailyVelocity` descending → higher velocity gets eye-level priority
   - Expand facing width for top 20% velocity SKUs, shrink bottom 20%
   - Assign `predictedUplift` per slot based on position improvement (eye-level bonus = +8-15%, demotion = -3-5%)
5. Calculate aggregate `predictedUpliftPercent` as weighted average of slot uplifts by velocity

### 2. Agent Integration

**Router Classification:**
- New intent: `store-ops/planogram` added to `AgentIntent.cs`
- The existing `StoreOpsAgent` (or a new `PlanogramAgent` specialist — team to decide) handles this intent
- Trigger phrases: "optimize shelf", "planogram", "shelf layout", "rearrange", "product placement"

**Agent Flow:**
1. Router classifies → `store-ops/planogram` at confidence > 0.6
2. Agent extracts parameters from natural language:
   - Store: resolved from query ("Store X", "Westfield", store ID)
   - Category/Aisle: extracted or defaulted to the store's highest-revenue aisle
   - Feature product: extracted if mentioned, otherwise null
3. Agent calls `GeneratePlanogramLayout` MCP tool via existing proxy pattern
4. Agent returns structured response with:
   - `reply`: narrative summary (the `summary` field from tool response, expanded with context)
   - `planogram`: the `{ before, after }` payload as a new optional field on `ChatResponse`

**ChatResponse Extension:**
```typescript
export interface ChatResponse {
  reply: string;
  sessionId: string;
  spans: AgentSpan[];
  charts?: ChartSpec[];
  planogram?: PlanogramResponse;  // ← NEW
  routing?: RoutingInfo;
  totalDurationMs?: number;
  tokenUsage?: TokenUsage;
  memoryContext?: MemoryContext;
}

export interface PlanogramResponse {
  storeId: string;
  storeName: string;
  category: string;
  featureProduct?: string;
  summary: string;
  predictedUpliftPercent: number;
  before: PlanogramLayout;
  after: PlanogramLayout;
}
```

### 3. Frontend Rendering

**Rendering Location:** Inline in chat, below the agent's text reply — same pattern as `ChartSpec` rendering.

**Implementation:**
```tsx
// In ChatMessage rendering (where charts are already rendered):
{message.planogram && (
  <PlanogramDiagram
    before={message.planogram.before}
    after={message.planogram.after}
    comparisonMode={true}
  />
)}
```

The existing `PlanogramDiagram` component already supports `comparisonMode` with side-by-side "Before" / "After" panels, uplift badges, and eye-level indicators. No component changes needed.

**Optional Enhancement (Phase 2):** Also show the planogram on the Stores page when the user navigates there after an optimization query — surface the last optimization result from memory/session context.

### 4. Demo Data Strategy

The simulation must feel intelligent without real ML. Strategy:

| Rule | Logic |
|------|-------|
| **Eye-level premium** | Shelves 2-3 (of 5) are eye level. Products placed here get +8-15% predicted uplift |
| **Velocity-based ranking** | Use actual `SkuVelocity.DailyUnits` from seeded data. High-velocity items earn eye-level placement |
| **Feature product boost** | If user specifies a product, it always moves to eye-level with expanded facing |
| **Brand color consistency** | Deterministic color from brand name hash → same brand always same color across queries |
| **Realistic slot count** | 5 shelves × 4-6 positions = 20-30 slots per layout (matches real gondola sections) |
| **Aggregate uplift** | 3-10% range — believable for planogram optimization |

The key insight: by using **real velocity data from the seeded database**, the optimization feels data-driven even though the algorithm is just "sort by velocity and put fast movers at eye level."

### 5. Demo Script Entry

**User Query:**
> "Optimize the shelf layout for Westfield Market's snack aisle, featuring Apex Grill products"

**What They See:**
1. Agent routing indicator shows "Store Ops" agent (orange pill)
2. Text reply: "I've analyzed the snack aisle at Westfield Market and generated an optimized planogram. By moving Apex Grill Smoky BBQ and Hickory Wings to eye-level (shelves 2-3) and expanding their facing width, the model predicts a **+6.3% category revenue uplift**."
3. Below the text: `PlanogramDiagram` renders in comparison mode showing:
   - **Before:** Current layout with Apex Grill products on bottom shelf, small facing
   - **After:** Apex Grill at eye level with green uplift badges (+12.1%, +9.8%), slower-moving items shifted down
4. Eye-level indicator stripe highlights shelves 2-3 in both panels
5. Telemetry drawer shows the `GeneratePlanogramLayout` tool call span

## Implementation Plan

| # | Task | Owner | Dependencies |
|---|------|-------|-------------|
| 1 | `GeneratePlanogramLayout` MCP tool + `RetailPulseDb` method | Costco | None |
| 2 | API proxy tool (`PlanogramLayoutTool.cs`) + REST endpoint | Costco | #1 |
| 3 | Add `store-ops/planogram` intent to router + specialist wiring | Costco | #2 |
| 4 | `PlanogramResponse` type on `ChatResponse` contract | Costco | #3 |
| 5 | Frontend: add `planogram?` to ChatResponse type | Chick | #4 |
| 6 | Frontend: render `PlanogramDiagram` inline in chat messages | Chick | #5 |
| 7 | Tests: MCP tool unit tests + agent integration tests | Target | #1-#4 |
| 8 | Demo script validation: end-to-end query → rendered planogram | All | #1-#6 |

## Constraints Respected

- ✅ Uses existing `PlanogramLayout` / `PlanogramSlot` types unchanged
- ✅ Uses existing `PlanogramDiagram` component unchanged
- ✅ Follows Aspire → McpServer → Api → Frontend data flow
- ✅ Follows `charts?` pattern for optional structured data on ChatResponse
- ✅ Fluent UI v9 (component already uses `makeStyles`)
- ✅ Demo-grade — no real ML, uses seeded velocity data for realism

## Alternatives Considered

1. **Reuse existing `OptimizePlanogram` tool** — Rejected because its response shape (raw slots without brand names/colors) doesn't match the frontend type. A new tool with the right shape is cleaner than adapter logic.
2. **Render in side panel instead of inline** — Rejected because inline follows the established `ChartSpec` pattern and keeps the demo flow linear (ask → see result).
3. **Add to Stores page permanently** — Rejected for Phase 1; can be a Phase 2 enhancement where last optimization is cached per store.



## Archived Decisions & Recent Merges

### Frontend chat message sanitization (defense-in-depth) (2026-05-15)

# Decision: Frontend chat message sanitization (defense-in-depth)

**Date:** 2026-05-15
**Author:** Chick (Frontend Dev)
**Status:** Implemented

## Context
Backend tool-call artifacts (`to=functions.IdentifyDemandRisks {...json}`) were leaking into rendered chat messages, including garbled Unicode characters.

## Decision
Added `src/utils/sanitizeMessage.ts` as a defense-in-depth layer that strips tool-call patterns before rendering in ChatPanel. This is NOT a replacement for backend fixing the root cause — it's a safety net.

## Patterns stripped:
- `to=functions.*` prefixes
- JSON payloads following tool-call markers
- Garbled CJK Unicode in tool-call context lines

## Impact on Costco (Backend)
- Backend should still fix the root cause of tool-call content leaking into response text
- Frontend sanitization means the demo is unblocked regardless of backend fix timeline
- If backend adds new tool-call patterns, the frontend regex may need updating

## Convention
All assistant message content should pass through `sanitizeMessage()` before rendering. Applied to both static and streaming message paths.


### Sanitize AI response text before returning to client (2026-05-15)

# Decision: Sanitize AI response text before returning to client

**Date:** 2026-05-15
**Author:** Costco (Backend Dev)
**Status:** Implemented

## Context

The AI model occasionally emits raw function call syntax (`to=functions.ToolName`) and corrupted CJK characters as part of its text response. This leaked directly to the UI because `response.Text` was used without filtering.

## Decision

`AgentExecutionPipeline.SanitizeReplyText()` now strips:
1. Lines matching `to=functions.\w+` (OpenAI-style function call leakage)
2. Lines with CJK characters adjacent to `json`/`function`/`tool` keywords (corrupted hallucinations)
3. If the entire reply is garbage after filtering, returns a graceful "unable to generate" message

## Also Fixed

- **Tool call timing:** Removed the `totalDuration / toolCount` hack that fabricated identical per-tool durations. Individual tool_call spans now report 0ms; the parent "thought" span carries real wall-clock.

## Implications

- **Chick (FE):** The `AgentRoutingIndicator` shows `routing.confidence` as a percentage badge. This is router classification confidence, NOT answer quality. Consider relabeling or adding a tooltip like "Routing confidence" to avoid user confusion.
- **Target (Tests):** New `SanitizeReplyText` tests added in `AgentPipelineTests.cs`. If adding new sanitization patterns, add corresponding test cases.
- **All agents:** Tool call spans will now show 0ms individually. The "thought" span is the authoritative duration for the full tool-calling pipeline.


### Aggressive fast-fail timeouts for chat endpoints (2026-05-16)

### Aggressive fast-fail timeouts for chat endpoints (2026-05-16)
- **Author:** Costco (Backend Dev)
- **Context:** Executive demo blocked by 504 timeouts. Root cause: timeout math was broken — `MaxIterations=2 × NetworkTimeout=90s = 180s` exceeded the `150s` request timeout, guaranteeing second-iteration cancellation. Azure SDK retry policy compounded the problem by retrying timed-out calls.
- **Decision:** Tightened all timeout parameters for fast-fail:
  - `NetworkTimeout`: 90s → 30s (single LLM call ceiling)
  - `MaximumIterationsPerRequest`: 2 → 1 (single round-trip, no second timeout)
  - Request-level timeout: 150s → 60s (both `/api/chat` and `/api/chat/stream`)
  - Azure SDK retry: disabled via `ClientRetryPolicy(maxRetries: 0)`
- **Rationale:** The budget math now works: 1 iteration × 30s network + routing overhead fits comfortably within 60s. Users get a clear error in ≤60s instead of waiting 2.5 minutes for a guaranteed timeout.
- **Implications for the team:**
  - **Chick (Frontend):** Update `DEFAULT_TIMEOUT_MS` in `src/RetailPulse.Web/src/services/api.ts` from 180s to ~90s to stay aligned with the 60s backend cap (give ~30s buffer for network).
  - **Target (Tests):** Any test mocking slow IChatClient should expect cancellation at 60s, not 150s. Tests with `MaximumIterationsPerRequest` assertions should expect 1, not 2.
  - **Kroger (Architecture):** If a future agent genuinely needs multi-iteration tool calling, it should get an endpoint-specific timeout override rather than raising the global cap.


