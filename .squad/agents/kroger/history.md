# Kroger — History

## 2026-04-30 — Team Initialization

- **Project:** Retail Pulse — a generic pro-code agentic demo for retail & consumer goods organizations (grocers, QSRs, big box retail)
- **Stack:** .NET 10, C#, Aspire (host + OTel, non-containerized), React/Vite/TypeScript, Azure API Management, AI Gateway pattern
- **Owner:** Brian Swiger
- **Context:** Built on Patron Pulse but updated to be generic with tenant configuration, extra organization examples, and corrected diagrams

## Learnings

### 2026-05-13 — Multi-Agent Router Architecture (Sprint 1.1)

**Architecture Decisions:**
- Router uses LLM-based classification with JSON-mode response format at temperature 0.1 for deterministic routing.
- Confidence threshold of 0.6 — below this, everything falls back to General agent. This prevents hallucinated routing.
- Interfaces (`IAgentRouter`, `ISpecialistAgent`) live in `RetailPulse.Contracts` so TeamsBot and any future consumers can reference them without depending on the Api project.
- `RetailOpsRouter` parses JSON classification and validates against `AgentIntent.All` — unknown intents get normalized to `general/fallback`.

**Key File Paths:**
- `src/RetailPulse.Contracts/Routing/` — `IAgentRouter.cs`, `ISpecialistAgent.cs`, `AgentIntent.cs`
- `src/RetailPulse.Api/Agents/Routing/RetailOpsRouter.cs` — the LLM-based router
- `src/RetailPulse.Api/Agents/Specialists/GeneralAgent.cs` — refactored from RetailPulseAgent
- `src/RetailPulse.Api/Agents/RoutingServiceExtensions.cs` — `AddAgentRouting()` DI registration
- `src/RetailPulse.Api/prompts.yaml` — `agents.router` section added

**Patterns:**
- Adding a new specialist: implement `ISpecialistAgent`, register as `services.AddScoped<ISpecialistAgent>(sp => ...)` in `AddAgentRouting()`.
- Router prompt is in YAML — easy to tune without code changes.
- Legacy `RetailPulseAgent` kept as thin wrapper delegating to `GeneralAgent.HandleAsync()` — existing test suite (174 tests) passes unchanged.

## Session Work — 2026-05-13 Sprint 1.1 Multi-Agent Router (Complete)

**Outcome:** ✅ SUCCESS — Lead architect role, all contracts and router implementations complete, 174 tests passing unchanged

**Deliverables:**
- `RetailPulse.Contracts/Routing/IAgentRouter.cs` — routing classification contract with RoutingDecision record
- `RetailPulse.Contracts/Routing/ISpecialistAgent.cs` — specialist agent interface (Key, DisplayName, SupportedIntents, HandleAsync)
- `RetailPulse.Api/Agents/Routing/RetailOpsRouter.cs` — LLM-based router with 0.6 confidence threshold, internal ParseClassification method
- `RetailPulse.Api/Agents/Specialists/GeneralAgent.cs` — refactored from RetailPulseAgent, implements ISpecialistAgent, backward compatible
- `RetailPulse.Api/Extensions/AgentRoutingServiceCollectionExtensions.cs` — `AddAgentRouting()` DI registration
- `src/RetailPulse.Api/prompts.yaml` — router classification prompt under `agents.router` key

**Reconciliation:** Costco parallelized the same work; adopted my contract design (IAgentRouter, ISpecialistAgent, slash-separated intents, Contracts.Routing namespace). Costco reconciled their parallel implementation around my interfaces.

**Cross-Agent Collaboration:**
- Costco (Backend): Contract reconciliation, RoutingInfo on ChatResponse, legacy wrapper verification
- Chick (Frontend): Agent routing UI constants and components (AgentRoutingIndicator, AgentRoutingPanel)
- Target (Tester): 63 comprehensive tests for router classification, GeneralAgent, integration pipeline

**Test Status:** All 174 existing tests pass unchanged; 63 new tests bring total to 237 (all passing)

**Decision Logged:** Multi-Agent Router Architecture

## Session Work — 2026-05-15 Sprint 1.2 DemandForecastAgent (Complete)

**Outcome:** ✅ SUCCESS — Lead architect role, first specialist agent fully implemented, 346 tests passing

**Deliverables:**
- `src/RetailPulse.Api/Agents/Specialists/DemandForecastAgent.cs` — specialist agent implementing ISpecialistAgent with key "demand-forecasting", temperature 0.3
- `src/RetailPulse.McpServer/Tools/GetHistoricalDemandTool.cs` — MCP tool for historical demand queries
- `src/RetailPulse.McpServer/Tools/GenerateForecastTool.cs` — MCP tool for 90-day demand forecasting
- `src/RetailPulse.McpServer/Tools/GetSeasonalityFactorsTool.cs` — MCP tool for seasonal multipliers
- `src/RetailPulse.McpServer/Tools/IdentifyDemandRisksTool.cs` — MCP tool for demand risk detection
- `src/RetailPulse.Api/Tools/HistoricalDemandTool.cs` — API proxy for historical demand
- `src/RetailPulse.Api/Tools/ForecastTool.cs` — API proxy for forecast generation
- `src/RetailPulse.Api/Tools/SeasonalityFactorsTool.cs` — API proxy for seasonality
- `src/RetailPulse.Api/Tools/DemandRisksTool.cs` — API proxy for demand risks
- `src/RetailPulse.Api/prompts.yaml` — `demand-forecast` agent definition (temp 0.3, analytical prompt)
- `src/RetailPulse.Api/Agents/RoutingServiceExtensions.cs` — extended with optional demand agent params
- `src/RetailPulse.McpServer/Program.cs` — 5 new REST endpoints for demand tools

**Reconciliation:** Parallel session had partially implemented demand data layer (seeding, queries, schema). Reconciled by:
- Removing duplicate method blocks in RetailPulseDb.cs (kept the more sophisticated implementation with anomaly injection, weekly aggregation, linear regression)
- Fixing schema table names (`DemandHistory`/`SeasonalFactors` with `Description` column) to match existing code
- Adding default parameter values for backward compatibility

**Architecture Patterns:**
- Specialist agents own intents exclusively — removed `DemandForecasting` from GeneralAgent.SupportedIntents
- RoutingServiceExtensions uses optional parameters for backward compat when adding new specialists
- MCP tools → REST endpoints → API proxy tools pattern maintained consistently
- Lower temperature (0.3 vs 0.7) for analytical/numerical precision

**Test Status:** All 346 tests pass (109 new tests from parallel work + fixes)

**Decision Logged:** DemandForecastAgent Architecture

## Session Work — 2026-05-16 Sprint 1.3 Conversation Memory Architecture (Complete)

**Outcome:** ✅ SUCCESS — Lead architect role, full memory subsystem implemented, 443 tests passing

**Deliverables:**
- `src/RetailPulse.Contracts/Memory/IConversationMemory.cs` — Rewrote contract: MemoryEntry record, MemoryType enum (ConversationSummary, UserPreference, EntityMention), StoreAsync(userId, MemoryEntry), ForgetEntryAsync(userId, memoryId) for privacy scoping
- `src/RetailPulse.Api/Memory/SqliteConversationMemory.cs` — Full SQLite impl with WAL mode, keyword-based relevance scoring with phrase matching, TTL cleanup (30d summaries, 90d preferences/entities)
- `src/RetailPulse.Api/Memory/MemoryExtractionService.cs` — LLM-based extraction of summaries, entities, preferences from conversation turns
- `src/RetailPulse.Api/Memory/ConversationMemoryMiddleware.cs` — Pipeline middleware: BuildMemoryContextAsync (before routing) and ExtractAndStoreAsync (after response), ~500 token budget
- `src/RetailPulse.Api/Memory/MemoryServiceExtensions.cs` — `AddConversationMemory(dbPath)` DI registration helper
- `src/RetailPulse.Api/Agents/Specialists/MemoryManagementAgent.cs` — "Forget everything" specialist agent implementing ISpecialistAgent
- `src/RetailPulse.Api/Program.cs` — Memory DI registration + chat endpoint integration (memory injection before routing, fire-and-forget extraction after)
- `src/RetailPulse.Api/prompts.yaml` — Added memory/management intent to router classifier
- `src/RetailPulse.Contracts/Routing/AgentIntent.cs` — Added MemoryManagement constant

**Reconciliation:** Parallel session had created IConversationMemory.cs and IMemoryMiddleware.cs in Contracts with a different contract shape (MemoryEntryType enum, StoreAsync returning MemoryEntry with individual params, ForgetEntryAsync taking only entryId). Reconciled by:
- Rewrote IConversationMemory to spec: MemoryType enum, StoreAsync(userId, MemoryEntry) → Task, ForgetEntryAsync(userId, memoryId)
- Updated ConversationMemoryTests.cs (22 tests) to match new contract
- Updated MemoryMiddlewareTests.cs (12 tests) to match new contract
- Updated RouterIntegrationTests.cs impls for both IConversationMemory and IApprovalGate new shapes
- Kept IMemoryMiddleware contract for test-side backward compat

**Architecture Patterns:**
- Memory is per-user, scoped by UserContext.ObjectId (falls back to "anonymous")
- SQLite WAL mode for concurrency — same pattern as existing SqliteApprovalGate
- Memory DB at `data/memory.db` alongside `data/approvals.db`
- Singleton IConversationMemory (WAL handles concurrency); scoped extraction/middleware
- Fire-and-forget extraction via Task.Run after response — non-blocking
- Memory context injected as prepended conversation history message (~2000 chars ≈ 500 tokens)
- Phrase-based keyword matching: ParseKeywords adds full query phrase + individual tokens for exact-match boosting

**Test Status:** All 443 tests pass (97 new tests from parallel work + reconciliation fixes)

**Decision Logged:** Conversation Memory Architecture

## Session Work — 2026-05-13 Sprint 1.5 Proactive Sales Alerts (Complete)

**Outcome:** ✅ SUCCESS — Lead architect role, full proactive alert subsystem implemented, 540 tests passing

**Deliverables:**
- `src/RetailPulse.Contracts/Alerts/IAlertService.cs` — Alert record + IAlertService interface (CheckForAlertsAsync, SnoozeAsync, DismissAsync, GetHistoryAsync, GetActiveAlertsAsync)
- `src/RetailPulse.Api/Alerts/AlertDbSchema.cs` — SQLite DDL for Alerts, AlertThrottles, AlertSnoozes, AlertDismissals tables
- `src/RetailPulse.Api/Alerts/SqliteAlertService.cs` — SQLite-backed IAlertService with WAL mode, throttle checking, alert persistence
- `src/RetailPulse.Api/Alerts/ProactiveAlertService.cs` — IHostedService (BackgroundService) with configurable timer, anomaly detection algorithm, SignalR push
- `src/RetailPulse.Api/Alerts/AlertServiceExtensions.cs` — `AddProactiveAlerts(dbPath)` DI registration
- `src/RetailPulse.Api/Program.cs` — DI wiring + 4 REST endpoints (GET active, GET history, POST snooze, POST dismiss)
- `src/RetailPulse.Api/appsettings.json` — `Alerts:CheckIntervalMinutes` configuration

**Architecture Patterns:**
- Singleton SqliteAlertService (WAL handles concurrency) — same pattern as SqliteApprovalGate and SqliteConversationMemory
- BackgroundService with PeriodicTimer — configurable interval via appsettings.json (default 5 min)
- Anomaly detection fetches demand data from MCP server via HttpClient (same `/api/historical-demand` endpoint used by existing tools)
- Three detection rules: demand_spike (>20%), supply_drop (<-15%), trend_reversal (sign change + >10% magnitude)
- Throttling: max 1 alert per (type, brand, region) per hour — prevents spam flooding
- SignalR push: `alert_fired` event broadcast to all connected clients with full Alert payload
- OTel tracing: `RetailPulse.Alerts` ActivitySource with `alert.check_cycle` spans
- Alert DB at `data/alerts.db` alongside `data/approvals.db` and `data/memory.db`

**REST Endpoints:**
- `GET /api/alerts/active` — currently firing alerts (last 24h)
- `GET /api/alerts/history?userId=&limit=` — alert history
- `POST /api/alerts/{alertId}/snooze` — snooze an alert type for a user
- `POST /api/alerts/{alertId}/dismiss` — dismiss/acknowledge an alert

**Test Status:** All 540 tests pass (unchanged from baseline — no existing tests broken)

**Decision Logged:** Proactive Sales Alert Architecture

### 2026-05-14 — Promotion Planning Agent + Task Module (Sprint 2.1)

**Architecture Decisions:**
- PromoPlanningAgent follows same specialist pattern as DemandForecastAgent: implements ISpecialistAgent, uses Key="promo-planning", temp 0.3 for analytical precision.
- Promo tools (GetPromoHistory, CalculateLift, EvaluateTiming, EstimateROI) follow the MCP tool → REST endpoint → API proxy tool chain pattern established in Sprint 1.3.
- ROI model uses diminishing returns on spend: effectiveness = min(spend/optimal, 1.0) × (1 - diminishing_factor). Above MaxEffectiveSpend, additional spend yields declining lift.
- Approval gate integration uses spend thresholds: $500K+ always requires approval, $100K-$500K requires approval when ROI < 2.0x.
- Task Module endpoint (POST /api/taskmodule/promo) orchestrates all promo tools in parallel, then applies approval gating. Returns structured evaluation without LLM involvement.
- PromoHistory seeding uses GetStableHash deterministic seeding with 4-6 campaigns per brand, ~25% poor performers for realistic data distribution.
- LiftCoefficients seeded per category × promo type (6 categories × 5 types = 30 rows) with realistic values for CPG industry.

**Key File Paths:**
- `src/RetailPulse.Api/Agents/Specialists/PromoPlanningAgent.cs` — specialist agent
- `src/RetailPulse.Api/Tools/{PromoHistoryTool,CalculateLiftTool,EvaluateTimingTool,EstimateROITool}.cs` — API proxy tools
- `src/RetailPulse.McpServer/Tools/PromoTools.cs` — MCP server tools
- `src/RetailPulse.McpServer/Data/RetailPulseDb.cs` — seeding + query methods
- `src/RetailPulse.Api/prompts.yaml` — promo-planning agent definition
- `src/RetailPulse.Api/Agents/RoutingServiceExtensions.cs` — promo DI wiring
- `POST /api/taskmodule/promo` — Task Module endpoint in Program.cs

**Test Status:** All 574 tests pass (34 new promo tests added by parallel sessions)

## Session Work — 2026-05-20 Sprint 2.2 Competitive Intelligence Agent (Complete)

**Outcome:** ✅ SUCCESS — Lead architect role, full competitive intelligence subsystem implemented

**Deliverables:**
- `src/RetailPulse.McpServer/Data/RetailPulseDb.cs` — 3 new tables (CompetitorPricing, MarketShare, CompetitorActivity), 3 seed methods, 4 query methods, SchemaVersion bumped to 5
- `src/RetailPulse.McpServer/Tools/CompetitiveTools.cs` — 4 MCP server tools (GetCompetitorPricing, GetMarketShare, DetectThreats, GetCompetitiveLandscape)
- `src/RetailPulse.McpServer/Program.cs` — 4 REST endpoints under `/api/competitive/`
- `src/RetailPulse.Api/Tools/CompetitorPricingTool.cs` — API proxy for competitor pricing
- `src/RetailPulse.Api/Tools/MarketShareTool.cs` — API proxy for market share
- `src/RetailPulse.Api/Tools/DetectThreatsTool.cs` — API proxy for threat detection
- `src/RetailPulse.Api/Tools/CompetitiveLandscapeTool.cs` — API proxy for landscape overview
- `src/RetailPulse.Api/Agents/Specialists/CompetitiveIntelAgent.cs` — specialist agent with SqliteAlertService integration, proactive threat alerts, chart extraction, token cost calculation
- `src/RetailPulse.Api/Agents/RoutingServiceExtensions.cs` — extended with competitiveIntelDef/competitiveToolsFactory params
- `src/RetailPulse.Api/Program.cs` — competitive tool DI registrations, prompt resolution, AddAgentRouting wiring
- `src/RetailPulse.Api/prompts.yaml` — competitive-intel agent definition (temp 0.4, defensive strategy framework)

**Architecture Patterns:**
- CompetitiveIntelAgent is the first specialist to integrate proactive alerts inline — fires `competitive_threat` alerts via SignalR when high-severity threats are detected in tool results
- Temperature 0.4 (higher than demand/promo at 0.3) balances analytical precision with creative strategy recommendations
- Defensive strategy framework: MATCH / DIFFERENTIATE / IGNORE / PREEMPT — codified in system prompt
- Threat detection scans JSON results for high-severity threats and price drops >10%, firing alerts with 1-hour throttling
- 6 competitor categories seeded: Spirits, Grocery, QSR, Home Improvement, Office Supply, Furniture (5-6 competitors each)
- Market share data covers 6 quarters with realistic quarter-over-quarter movements

**Reconciliation:** Parallel session created a basic CompetitiveIntelAgent.cs. Replaced with full implementation adding: SqliteAlertService dependency, CheckAndFireAlertsAsync, ExtractChartSpecs, BuildTokenUsage with cost calculation, OTel tool activity spans.

**REST Endpoints:**
- `GET /api/competitive/pricing?category=&region=` — competitor pricing comparison
- `GET /api/competitive/market-share?category=&region=` — quarterly market share trends
- `GET /api/competitive/threats?category=&region=` — competitive threat detection
- `GET /api/competitive/landscape?category=&region=` — holistic competitive landscape

## Session Work — 2026-05-16 Sprint 3.1+3.2 Streaming/Caching Middleware + Guardrails (Complete)

**Outcome:** ✅ SUCCESS — Lead architect role, guardrails middleware + streaming/caching pipeline integrated, 1061 tests passing

**Deliverables:**
- `src/RetailPulse.Api/Middleware/GuardrailsMiddleware.cs` — Input filtering (jailbreak via GuardrailPatterns compiled regex, SQL injection via substring matching, input length gate via GuardrailsConfig.MaxInputLength, PII detection logging) and output PII redaction with `[REDACTED:{TYPE}]` markers
- `src/RetailPulse.Api/Middleware/StreamingMiddleware.cs` — SignalR token streaming via StreamingEvents helpers, `StreamResponseAsync()` for IChatClient streaming, `StreamResponseFallbackAsync()` for pushing pre-computed responses as simulated tokens, `StreamTokensAsync()` IAsyncEnumerable for SSE
- `src/RetailPulse.Api/Middleware/StreamingMiddleware.cs (CacheHelpers)` — Static `CacheHelpers` class: `BuildCacheKey()` (SHA256 deterministic), `NormalizeQuery()` (lowercase + trim + collapse whitespace + strip trailing punctuation), `IsCacheable()` (rejects forecasts/recommendations/opinions)
- `src/RetailPulse.Api/Program.cs` — DI registration of GuardrailsMiddleware (scoped) + StreamingMiddleware (scoped), guardrails input check before routing in /api/chat, cache lookup/store for deterministic queries, PII redaction on response output, new `/api/chat/stream` endpoint with guardrails + memory + routing + SignalR streaming

**Architecture Patterns:**
- Pipeline ordering: Guardrails (input) → Cache Check → Router → Agent Execute → Cache Store → Guardrails (output/PII) → Return
- GuardrailsMiddleware is transparent to agents — wraps the existing chat pipeline, not injected into agent logic
- Cache uses `CacheHelpers.IsCacheable()` to skip non-deterministic queries (forecasts, recommendations, opinions)
- Cache key: SHA256 of `pre-route|normalized_query` — routes through same key regardless of which agent handles it
- Streaming endpoint pushes tokens via SignalR `StreamingEvents.SendTokenAsync()` — clients subscribe to `stream:{sessionId}` group
- `StreamResponseFallbackAsync` splits full response on word boundaries for simulated streaming UX

**Reconciliation:** Parallel session had created foundational infrastructure:
- `RetailPulse.Api.Caching.InMemoryResponseCache` — full LRU cache with TTL, GenerateKey(), background cleanup
- `RetailPulse.Api.Guardrails.GuardrailPatterns` — source-generated regex for PII + jailbreak
- `RetailPulse.Api.Guardrails.InMemorySuspiciousRequestLog` — ring buffer logger
- `RetailPulse.Contracts.Guardrails.GuardrailsConfig` — runtime toggles
- `RetailPulse.Api.Hubs.StreamingHub` — SignalR hub + StreamingEvents helper
- Existing DI registrations + REST endpoints for cache stats, guardrails log/stats/config
- Reconciled by using all existing contracts and services, only adding middleware layer + pipeline integration

**REST Endpoints:**
- `POST /api/chat` — enhanced with guardrails input check, cache lookup/store, PII output redaction
- `POST /api/chat/stream` — new streaming endpoint with guardrails + memory + routing + SignalR push

**Test Status:** All 1061 tests pass (no regressions)

### 2026-05-13 — Collaborative Adaptive Cards + Observability Suite (Sprint 3.3+3.4)

**Architecture Decisions:**
- Adaptive card state uses ConcurrentDictionary<string, CardState> with per-card locking for atomic action processing.
- State transitions: Active → Voting → Decided → Archived. Once escalated (split vote 50/50), escalation persists and blocks auto-decide — requires explicit escalation action or archive.
- SignalR events: `card:created`, `card:action`, `card:lifecycle` — broadcast to all clients via TelemetryHub.
- Council verdicts auto-create Voting cards with initial votes seeded from agent assessments.
- Observability: InMemoryCostTracker (ConcurrentBag), InMemoryAuditLog (ConcurrentQueue ring buffer, 5000 max), ConversationExporter (ConcurrentDictionary sessions).
- Model pricing table: gpt-5.4-mini ($0.15/$0.60), gpt-4o ($2.50/$10.00), claude-sonnet ($3.00/$15.00) per 1M tokens.
- Chat pipeline instrumented: cost tracking + audit log + conversation export after each agent response.

## Session Work — 2026-05-13 Sprint 3.3+3.4 Collaborative Cards + Observability (Complete)

**Outcome:** ✅ SUCCESS — Lead architect role, both enterprise polish features implemented, 1154 tests passing

**Deliverables:**
- `src/RetailPulse.Contracts/Cards/IAdaptiveCardState.cs` — IAdaptiveCardState interface + AdaptiveCard, CardVote, CardComment, CardAction records + CardType, CardLifecycle, CardActionType enums + CreateCardRequest
- `src/RetailPulse.Api/Cards/InMemoryAdaptiveCardState.cs` — ConcurrentDictionary-backed card state with per-card lock, state machine transitions, escalation detection, SignalR sync
- `src/RetailPulse.Contracts/Observability/ICostTracker.cs` — ICostTracker interface + UsageEvent, CostSummary, AgentCostBreakdown, CostTrend, DailyCost records + CostPeriod enum
- `src/RetailPulse.Contracts/Observability/IAuditLog.cs` — IAuditLog interface + AuditEntry, AuditQuery, AuditStats records
- `src/RetailPulse.Contracts/Observability/IConversationExport.cs` — IConversationExport interface + ExportResult, ExportableSession records + ExportFormat enum
- `src/RetailPulse.Api/Observability/InMemoryCostTracker.cs` — ConcurrentBag-backed cost tracker with model pricing table
- `src/RetailPulse.Api/Observability/InMemoryAuditLog.cs` — ConcurrentQueue ring buffer (5000 entries), filterable queries
- `src/RetailPulse.Api/Observability/MarkdownExporter.cs` — Audit-log-backed Markdown/JSON exporter
- `src/RetailPulse.Api/Observability/ConversationExporter.cs` — Fixed TrackedMessage record (parallel session bug), session-tracking exporter
- `src/RetailPulse.Api/Program.cs` — DI registrations (IAdaptiveCardState, ICostTracker, IAuditLog, IConversationExport), 12 new REST endpoints, chat pipeline instrumentation (cost + audit + conversation tracking), council → card auto-creation

**Reconciliation:** Parallel session had created:
- `ConversationExporter.cs` with a TrackedMessage record bug (positional parameter + property Timestamp conflict) — fixed by converting to property-only record
- Card voting tests with specific escalation persistence expectations — adapted vote logic to match: once escalated, blocks auto-decide

**REST Endpoints (Cards):**
- `POST /api/cards` — create a new card
- `GET /api/cards` — list active cards
- `GET /api/cards/{id}` — get card details
- `POST /api/cards/{id}/action` — perform action (vote, comment, drill-down, escalate)
- `POST /api/cards/{id}/archive` — archive card

**REST Endpoints (Observability):**
- `GET /api/observability/costs?period=week` — cost summary
- `GET /api/observability/costs/agents?period=week` — per-agent breakdown
- `GET /api/observability/costs/trend?days=7` — cost trend
- `GET /api/observability/audit?agentId=X&from=Y&limit=50` — query audit log
- `GET /api/observability/audit/stats` — audit statistics
- `GET /api/observability/export/sessions` — list exportable sessions
- `POST /api/observability/export/{sessionId}?format=markdown` — export conversation

**Test Status:** All 1154 tests pass (no regressions)

### 2026-07-24 — Phase 4: Store Ops, Margin, Scorecard (Sprints 4.1–4.3)

**Sprint 4.1 — Store Operations + Planogram Agents:**
- Created `StoreOpsAgent` (key: `store-ops`, intent: `store/operations`) — store performance, underperformer detection, stockout prediction.
- Created `PlanogramAgent` (key: `planogram`, intent: `planogram/optimization`) — shelf layout analysis, eye-level optimization, velocity-based facing allocation.
- MCP tools: `GetStorePerformance`, `GetShelfLayout`, `OptimizePlanogram`, `PredictStockout` with matching REST endpoints and API proxy tools.
- DB tables: `StoreMetrics`, `ShelfLayouts`, `SkuVelocity` with deterministic seed data.

**Sprint 4.2 — Margin Agent + Escalation Chain:**
- Created `MarginAgent` (key: `margin`, intent: `margin/analysis`) — P&L analysis, margin drivers, trend tracking, risk detection.
- Created `EscalationOrchestrator` — L1→L2→L3 escalation chain: L1 single specialist (8s timeout), L2 multi-specialist fan-out (15s timeout), L3 flags for human review.
- MCP tools: `GetMarginByBrand`, `GetMarginDrivers`, `GetMarginTrend`, `DetectMarginRisks`.
- DB tables: `BrandFinancials`, `MarginDrivers`.

**Sprint 4.3 — Portfolio Scorecard + Decision Explainability:**
- Created `ScorecardOrchestrator` — fans out per-brand assessments across 5 weighted dimensions (Demand 0.25, Competitive 0.20, Supply 0.20, Store Execution 0.20, Margin 0.15), synthesizes executive brief via LLM.
- Created `ExplainabilityService` — ConcurrentDictionary-backed trace capture with tool steps, reasoning chain, human-readable explanation builder.
- New contract types: `BrandScore`, `PortfolioScorecard`, `ExplanationChain`, `ExplanationStep`.

**Architecture Decisions:**
- All three new agents follow the canonical `DemandForecastAgent` template exactly (constructor, HandleAsync, chart extraction, token usage).
- `RoutingServiceExtensions.AddAgentRouting()` extended with 6 new optional params (storeOpsDef/toolsFactory, planogramDef/toolsFactory, marginDef/toolsFactory).
- Router prompt updated with 4 new intent categories; `AgentIntent.All` updated with `StoreOps`, `Planogram`, `MarginAnalysis`, `Scorecard`.
- SchemaVersion bumped to 7 for re-seeding.

**Key File Paths:**
- `src/RetailPulse.Api/Agents/Specialists/StoreOpsAgent.cs`
- `src/RetailPulse.Api/Agents/Specialists/PlanogramAgent.cs`
- `src/RetailPulse.Api/Agents/Specialists/MarginAgent.cs`
- `src/RetailPulse.Api/Escalation/EscalationOrchestrator.cs`
- `src/RetailPulse.Api/Scorecard/ScorecardOrchestrator.cs`
- `src/RetailPulse.Api/Explainability/ExplainabilityService.cs`
- `src/RetailPulse.McpServer/Tools/StoreOpsTools.cs`
- `src/RetailPulse.McpServer/Tools/MarginTools.cs`
- `src/RetailPulse.Api/Tools/StoreOpsProxyTools.cs`
- `src/RetailPulse.Api/Tools/MarginProxyTools.cs`

**REST Endpoints:**
- `GET /api/stores/performance?region=X` — store performance metrics
- `GET /api/stores/{storeId}/planogram/{aisleId}` — shelf layout
- `POST /api/stores/{storeId}/planogram/{aisleId}/optimize` — planogram optimization
- `GET /api/stores/{storeId}/stockout-risk` — stockout prediction
- `GET /api/margin/{brandId}` — brand P&L
- `GET /api/margin/drivers/{brandId}` — margin drivers
- `GET /api/margin/trend/{brandId}` — margin trend
- `GET /api/margin/risks` — margin risk detection
- `POST /api/escalate` — L1→L2→L3 escalation
- `POST /api/scorecard` — portfolio scorecard generation
- `GET /api/explain/{traceId}` — decision explainability

**Test Status:** All 1264 tests pass (1154 original + 110 new Phase 4 tests, no regressions)

### 2026-05-13 — Documentation Update: Demo Walkthrough + Architecture (Complete)

**Architecture Decisions:**
- Demo walkthrough extended with Acts 6–10 covering multi-agent routing, demand forecasting, promo planning/approval gates, competitive intel/threat detection/escalation, portfolio scorecard/explainability/council consensus, and enterprise features (guardrails, streaming, caching, memory, observability).
- Architecture doc extended with 7 new sections: Multi-Agent Router Architecture, Escalation Chain, Portfolio Health Council, Memory Middleware Pipeline, Guardrails/Cache/Streaming Pipeline, Collaborative Adaptive Cards, Decision Explainability.
- All existing content preserved — new sections are additive only.
- Demo walkthrough total time updated from ~12 min to ~25 min with audience-specific guidance (Acts 1–5 for data teams, Acts 6–10 for enterprise/AI governance).
- Added 15 new "Impressive Queries to Have Ready" covering specialist agents, escalation, scorecard, guardrails, and memory.

**Key File Paths:**
- `docs/demo-walkthrough.md` — Acts 6–10 added after Act 5, new queries section
- `docs/architecture.md` — New sections between Resilience Patterns and Deployment Topology
