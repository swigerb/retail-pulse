# Costco — History

## 2026-04-30 — Team Initialization

- **Project:** Retail Pulse — a generic pro-code agentic demo for retail & consumer goods organizations (grocers, QSRs, big box retail)
- **Stack:** .NET 10, C#, Aspire (host + OTel, non-containerized), React/Vite/TypeScript, Azure API Management, AI Gateway pattern
- **Owner:** Brian Swiger
- **Context:** Built on Patron Pulse but updated to be generic with tenant configuration, extra organization examples, and corrected diagrams

## 2026-05-04 — Performance Optimization Session

Agent sessions completed:
- **fix-1-conversation-history**: Added multi-turn support with 10-turn history cap to RetailPulseAgent
- **fix-2-portfolio-tool**: Integrated GetPortfolioDepletionStats MCP tool with API proxy and tests

Both features enable enhanced retail data analysis workflows without breaking changes.

## Session Work — 2026-05-04 Telemetry Accuracy Session

### Fix: Telemetry Total Duration Double-Counting (Commit acbc3d3)
- Issue: Telemetry panel summed all spans including overlapping thought+tool spans, overstating request duration by ~2x
- Root Cause: Backend `thought` span covers full `GetResponseAsync()` wall-clock time; summing all spans counts this twice
- Decision: Expose `TotalDurationMs` on ChatResponse contract; telemetry drawer prefers response-level value with fallback to summed spans
- Changes: Added TotalDurationMs to ChatResponse, wired through Dashboard→TelemetryPanel
- Impact: Wall-clock accuracy improved from ~130.9s misreport to correct ~65.5s
- Validation: Backend build + frontend build + 12 tests pass

## Session Work — 2026-05-07 Prompt Enforcement Session

### Fix: Tool Enforcement in System Prompt (Commit a21cb48)
- Issue: gpt-5.4-mini responded to data/visualization requests with text-only answers, skipping GetPortfolioDepletionStats and CreateChart tools entirely
- Root Cause: System prompt in `prompts.yaml` described tools but never mandated their use for data questions
- Decision: Added "Critical: Always Use Tools for Data Requests" section to `prompts.yaml`, placed BEFORE visualization guidelines so model encounters mandate early
- Changes: 
  - Concept-to-tool mapping table (market share → GetPortfolioDepletionStats, trends → GetDepletionStats, etc.)
  - Visualization selection rules (proportional breakdown → pie chart, trends → line chart, etc.)
  - "Always Chart Available Data" guidance for estimated breakdowns
- Impact: Model now reliably invokes data tools first, then CreateChart for visualizations
- Validation: All 174 backend + 12 frontend tests pass

## Session Work — 2026-05-08 Sprint 1.1 Multi-Agent Router Infrastructure

### Task: Routing contracts, RetailOpsRouter, GeneralAgent refactor, DI/endpoints, router tests

- **Context:** Sprint 1.1 issue — build multi-agent routing pipeline so messages can be classified by intent and dispatched to specialist agents
- **Parallel work:** Kroger (Architect) independently implemented the same contracts and router during the same session
- **Reconciliation:** Adopted Kroger's implementations (better namespace structure, richer contracts) and deleted my duplicates
  - Kroger's namespace: `RetailPulse.Contracts.Routing` (vs my root-level `RetailPulse.Contracts`)
  - Kroger's `ISpecialistAgent` uses `Key`/`SupportedIntents`/`HandleAsync(ChatRequest)` (vs my `AgentId`/`IntentCategories`/`ChatAsync`)
  - Kroger's `RoutingDecision` record (vs my `AgentRoutingResult`)
  - Kroger's slash-separated intents: `"demand/forecasting"` (vs my simple `"demand"`)
- **My contributions:**
  - Converted `RetailPulseAgent` to thin legacy wrapper delegating to `GeneralAgent` for backward compat
  - Wrote/verified all 4 new test files aligned to Kroger's contracts (63 new tests)
  - Removed duplicate contract files, top-level router prompt key, PromptConfiguration.Router property
- **Validation:** Build clean (0 errors), all 237 tests pass (174 existing + 63 new)
- **Decision:** When two agents build the same feature in parallel, prefer the architect's implementation and reconcile the implementer's work around it

## Learnings

- 2026-05-13T11:08:14-04:00 — Demand forecasting tools: `DemandHistory` table stores daily granularity but `GetHistoricalDemand` aggregates to weekly buckets for manageable LLM output. Forecast algorithm is trailing-30-day avg × seasonal multiplier × (1 + trend_slope × days_ahead) with ±15% confidence bands. Risk detection uses 7-day rolling averages comparing week-over-week changes.
- 2026-05-13T11:08:14-04:00 — `GetCategorySeasonalMultiplier()` is a static helper shared between seeding (to generate realistic data) and forecasting (to project future demand). This ensures the forecast model uses the same seasonal assumptions as the training data.
- 2026-05-13T11:08:14-04:00 — Anomalies are injected at deterministic day offsets (seeded per brand) so `IdentifyDemandRisks` reliably detects them. Each brand gets 1-2 anomalies: one spike and one drop, separated by ≥30 days.

- 2026-05-04T10:32:17.680-04:00 — The telemetry drawer in `src\RetailPulse.Web\src\components\TelemetryPanel.tsx` should use a response-level wall-clock total, not a sum of span durations, because the backend `thought` span in `src\RetailPulse.Api\Agents\RetailPulseAgent.cs` already includes tool time.
- 2026-05-04T10:32:17.680-04:00 — `src\RetailPulse.Web\src\components\Dashboard.tsx` is the right place to own top-level telemetry stats and pass response metadata from `src\RetailPulse.Web\src\components\ChatPanel.tsx` into the telemetry drawer without changing SignalR span flow.
- 2026-05-04T10:32:17.680-04:00 — Shared chat contract changes for telemetry belong in `src\RetailPulse.Contracts\ChatModels.cs`, with matching frontend shape updates in `src\RetailPulse.Web\src\types\index.ts`.
- 2026-05-04T14:53:22Z — Telemetry accuracy achieved via response-level TotalDurationMs with fallback to span summation for backward compatibility.
- 2026-05-07T15:11:15.222-04:00 — Added "Critical: Always Use Tools for Data Requests" section to `src\RetailPulse.Api\prompts.yaml` to fix gpt-5.4-mini skipping tool calls on data/visualization requests. Includes concept-to-tool mapping table (market share → GetPortfolioDepletionStats, trends → GetDepletionStats, etc.) and visualization selection guidance. Root cause was the system prompt described tools but never mandated their use for data questions.
- 2026-05-07T15:33:55-04:00 — Added `VariantMix` table to SQLite schema in `src\RetailPulse.McpServer\Data\RetailPulseDb.cs`. Schema: Brand, Region, Variant (all COLLATE NOCASE), MixPercent REAL, DepletionsYoY REAL. Primary key is (Brand, Region, Variant). Seeded deterministically using GetStableHash("variant|{brand}|{region}") — normalized random weights per brand×region×variant produce mix percentages summing to ~100%.
- 2026-05-07T15:33:55-04:00 — New MCP tool `GetVariantMixTool` lives in `src\RetailPulse.McpServer\Tools\GetVariantMixTool.cs`. Supports region="National" (averages MixPercent/DepletionsYoY across all regions via GROUP BY). Pattern matches existing tools — static class, `[McpServerToolType]`, inject `RetailPulseDb`, return `data.GetVariantMix(brand, region)`.
- 2026-05-07T15:33:55-04:00 — prompts.yaml update strategy for variant data: add to `tools:` array, `## Available Tools` section, `### Concept-to-Tool Mapping` table, and rewrite `### Always Chart Available Data` to call GetVariantMix first (real data, no "Estimated" label) rather than estimating from brand config.
- 2026-05-07T16:45:21-04:00 — Strengthened variant mix prompt in `src\RetailPulse.Api\prompts.yaml` to prevent model from calling GetDepletionStats/GetFieldSentiment for variant queries. Added explicit FAILED/CORRECT examples and a concrete donut ChartSpec mapping showing how to turn GetVariantMix output (mix_percent values) into a working donut chart (each variant as its own series with one value). Root cause: the model ignored weak "call GetVariantMix" instructions and fell back to familiar tools, then couldn't map unfamiliar output to ChartSpec format.

## Session Work — 2026-05-13 Sprint 1.1 Multi-Agent Router (Complete)

**Outcome:** ✅ SUCCESS — Backend implementation role, reconciled with Kroger's parallel work, enhanced ChatResponse, added VariantMix data, 237 tests passing (174 existing + 63 new)

**Deliverables:**
- Contract reconciliation: adopted Kroger's `RetailPulse.Contracts.Routing` namespace design, deleted duplicate root-level contracts
- Legacy wrapper: confirmed `RetailPulseAgent` thin wrapper delegating to `GeneralAgent.HandleAsync()` for backward compatibility
- ChatResponse enhancement: added optional `RoutingInfo` field with agent metadata (agentId, agentName, intentCategory, confidence, reasoning)
- SQLite schema: added `VariantMix` table (Brand, Region, Variant COLLATE NOCASE; MixPercent, DepletionsYoY REAL; PK: Brand/Region/Variant)
- MCP tool: `GetVariantMixTool` in `src\RetailPulse.McpServer\Tools\`, supports region="National" for cross-region averaging
- Seeding: deterministic via `GetStableHash("variant|{brand}|{region}")` normalized random weights
- prompts.yaml: registered GetVariantMix in tools array, Available Tools section, Concept-to-Tool Mapping table, Always Chart Available Data section

**Reconciliation Strategy:** When two agents implement the same feature in parallel, defer to the architect's contracts and rebuild your work around them. This session: verified Kroger's contracts were superior (namespace isolation, richer intents, cleaner interface), deleted my duplicates, rewrote tests to match his interfaces, and validated all 237 tests pass.

**Cross-Agent Collaboration:**
- Kroger (Architect): Lead on contracts and router implementation
- Chick (Frontend): Routing UI visibility and constants
- Target (Tester): Test coverage for reconciled contracts and integration

**Test Status:** All 237 tests passing (174 existing + 63 new router/integration tests)

**Decisions Logged:** Router Contract Reconciliation, Variant-Level Data in SQLite + GetVariantMix Tool

## Session Work — 2026-05-13 Sprint 1.2 Demand Forecasting MCP Tools + Simulated Data

### Task: DemandHistory/SeasonalFactors schema, 4 MCP tools, 365-day seed data with seasonal patterns

- **Context:** Sprint 1.2 — build the data layer and MCP tools that power the Demand Forecasting Agent. Kroger designing the agent in parallel; Costco owns data + tools.
- **Deliverables:**
  - **Schema:** Added `DemandHistory` table (Brand, Region, Channel, Date, Volume, Units) with indexes on Brand/Region and Date. Added `SeasonalFactors` table (Category, Month, Multiplier, EventName, Description) with index on Category.
  - **Seed data:** 365 days × 12 brands × 6 regions × 3 channels = ~78,840 daily demand records. Volumes parameterized from tenant.yaml with category-specific seasonal multipliers, day-of-week patterns, linear trend, ±8% random noise, and 1-2 injected anomalies per brand.
  - **Seasonal factors:** 38 curated rows covering 6 categories (Spirits, Grocery, QSR, Home Improvement, Office Supply, Furniture) with holiday/seasonal event names and descriptions.
  - **MCP Tools (DemandTools.cs):**
    - `GetHistoricalDemand(brand?, region?, channel?, months?)` — weekly-aggregated history with summary stats
    - `GenerateForecast(brand, region?, days?)` — trailing-30-day avg × seasonal multiplier × trend slope, ±15% confidence bands
    - `GetSeasonalityFactors(category?)` — monthly multipliers with event names and impact classification
    - `IdentifyDemandRisks(brand?, region?)` — detects sudden drops (>20%), unusual spikes (>30%), and trend reversals (>15%) over 90-day window
  - **REST endpoints:** 4 new `/api/demand/*` routes in Program.cs matching tool signatures
  - **Schema version bumped** to v3 to force re-seed on next startup
- **Patterns followed:** Same `[McpServerToolType]` + `[McpServerTool]` + `[Description]` pattern as existing tools. Data methods on `RetailPulseDb` class. Deterministic seeding via `GetStableHash()`.
- **Validation:** Build clean (0 errors), all 237 tests pass (no regressions)
- **Seasonal patterns applied:**
  - Spirits: +40% Dec (holidays), +15% Jul (summer entertaining), -15% Jan (post-holiday)
  - Grocery: +30% Dec, +25% Sep (back-to-school), -10% Jan
  - QSR: +20% Jul (summer peak), -12% Jan (winter dip)
  - Home Improvement: +35% May (spring projects), +20% Sep (fall prep), -20% Jan
  - Office Supply: +25% Aug (back-to-school), +15% Jan (new year setup)
  - Furniture: +25% Nov (Black Friday), +20% Aug (dorm/apartment), -20% Jan

## Session Work — Sprint 1.4 Human-in-the-Loop Approval System

### Deliverables
- **IApprovalGate contract** (Contracts/Approval/IApprovalGate.cs): ApprovalContext-based interface with RequestApprovalAsync, RespondAsync (void/idempotent), WaitForApprovalAsync, GetPendingAsync, GetHistoryAsync
- **SqliteApprovalGate** (Api/Approval/SqliteApprovalGate.cs): WAL-mode SQLite, single-table schema, 2s polling, auto-timeout
- **ApprovalTool** (Api/Agents/Tools/ApprovalTool.cs): AI-callable tool with SignalR push notifications (approval_requested/approval_resolved)
- **REST endpoints**: GET /api/approvals/pending, GET /api/approvals/{id}, POST /api/approvals/{id}/respond, GET /api/approvals/history
- **DI wiring**: IApprovalGate singleton + ApprovalTool scoped in Program.cs; tool added to DemandForecastAgent
- **Tests**: 48 passing tests covering CRUD, timeout, concurrency, idempotency, audit trail

### Learnings
- `IClientProxy.SendAsync(string, object)` is an extension method in `Microsoft.AspNetCore.SignalR` namespace — top-level Program.cs needs explicit `using`
- RespondAsync returning void (not ApprovalResult) simplifies idempotency — just UPDATE WHERE Decision='Pending'
- Single-table schema (Decision column on ApprovalRequests) is simpler and faster than two-table request/result split
- Large file rewrites via edit tool can fail on size limits — use PowerShell Set-Content for files >15KB

## Session Work — 2026-05-24 Sprint 1.6 Distributed Tracing Enhancement + Phase 1 Integration

### Task: Enhanced distributed tracing with span hierarchy, trace summaries, SignalR push, token cost tracking, REST endpoints

- **Context:** Sprint 1.6 — enhance the existing OTel tracing infrastructure to support full span hierarchy across the multi-agent pipeline, structured trace summaries for Teams cards, and real-time SignalR push of trace events.
- **Approach:** Built on existing `ITraceCollector`/`InMemoryTraceCollector` infrastructure (created by another agent) rather than creating parallel implementations. Enhanced rather than replaced.
- **Deliverables:**
  - **Enhanced ITraceCollector contract:** Added `TraceStep`, `TraceTokenDetail`, `StructuredTraceSummary` records and `GetStructuredSummary()` method
  - **Enhanced InMemoryTraceCollector:** SignalR push (trace_started, span_completed, trace_completed events), structured summary builder with step extraction, cost calculation with configurable pricing
  - **Full span hierarchy in AgentTelemetry:** 8 new static methods (StartChatRequest, StartRouterClassify, StartRouterSelectAgent, StartAgentProcess, StartMemoryRecall, StartMemoryStore, StartApprovalRequest, StartApprovalWait) providing root-to-leaf span hierarchy
  - **Chat endpoint trace instrumentation:** Full pipeline wrapped in trace spans with ITraceCollector capture
  - **REST endpoints:** GET /api/traces/recent, GET /api/traces/{traceId}/summary, GET /api/traces/{traceId}/spans
  - **DI registration:** InMemoryTraceCollector registered as singleton with SignalR hub context and IConfiguration
  - **Token cost tracking:** Default gpt-5.4-mini pricing (0.15/0.60 per million tokens), configurable via TokenPricing config section

### Learnings
- `IDictionary<string,string>` does NOT have `GetValueOrDefault()` — only `IReadOnlyDictionary` does. Need static helper for tag extraction.
- Lambda parameter `_` conflicts with fire-and-forget discard `_ =` — use named parameter or `Task.Run()` pattern.
- `ChatResponse` ambiguity between `Microsoft.Extensions.AI.ChatResponse` and `RetailPulse.Contracts.ChatResponse` — resolve with `using ChatResponse = RetailPulse.Contracts.ChatResponse;` alias.
- FluentAssertions uses `BeLessThanOrEqualTo()`, not `BeLessOrEqualTo()`.
- When enhancing existing infrastructure created by other agents, always check for pre-existing interfaces and implementations before creating new ones.
- SignalR events should be best-effort (wrapped in try/catch) to never break the span capture pipeline.

**Test Status:** All 540 tests passing (0 failures, 0 skipped)

## Session Work — 2026-05-13 Sprint 2.1 Promo Planning MCP Tools + Simulated Data

### Task: PromoHistory/LiftCoefficients schema, 4 MCP tools, 60+ campaign seed data, Task Module endpoint with approval gate

- **Context:** Sprint 2.1 — build the data layer, MCP tools, and orchestrating Task Module endpoint for promo campaign planning. Follows same patterns as Sprint 1.2 demand tools.
- **Deliverables:**
  - **Schema:** Added `PromoHistory` table (Brand, Region, PromoType, CampaignName, StartDate, EndDate, Spend, BaselineVolume, ActualVolume, LiftPercent, ROI, SuccessRating) with indexes. Added `LiftCoefficients` table (Category, PromoType, AvgLiftPercent, StdDev, MinSpend, MaxEffectiveSpend).
  - **Seed data:** 60+ campaigns across 12 brands (5-6 per brand), 18-month window, 5 promo types (discount 30%, BOGO 20%, display 20%, digital 20%, bundle 10%), ~15% intentionally poor performers. 30 lift coefficient rows (6 categories × 5 types) with category-specific adjustments.
  - **MCP Tools (PromoTools.cs):**
    - `GetPromoHistory(brand?, region?, promoType?, months?)` — filtered campaign history with summary stats
    - `CalculateLift(brand, region, promoType, spend)` — expected lift with diminishing returns and confidence scoring
    - `EvaluateTiming(brand, region, startDate, endDate)` — conflict detection, seasonality analysis, cannibalization risk
    - `EstimateROI(brand, region, promoType, spend, durationWeeks)` — full ROI projection with breakeven analysis
  - **REST endpoints:** 6 new `/api/promo/*` routes in McpServer Program.cs (history, calculate-lift, evaluate-timing, estimate-roi, calendar, types)
  - **API proxy tools:** 4 HTTP proxy tools (PromoHistoryTool, CalculateLiftTool, EvaluateTimingTool, EstimateROITool)
  - **Task Module endpoint:** `POST /api/taskmodule/promo` orchestrating all 4 tools with approval gate triggers (budget > $500K or ROI < 2.0x with budget > $100K)
  - **Schema version bumped** to v4

### Learnings
- PowerShell heredoc strings with backtick escaping corrupt C# files — always use Python for multi-line file operations
- `git checkout HEAD --` on a branch restores from the branch's HEAD commit, not from main — verify the commit content first
- PowerShell `>` redirection adds BOM and may change encoding — use Python subprocess for binary-safe git operations
- For files >1000 lines, the Python insertion approach (read full file → find insertion point → write) is more reliable than PowerShell Set-Content or edit tool for large appends

**Validation:** Build clean (0 errors, 4 pre-existing warnings), all 540 tests pass

## Session Work — 2026-05-13 Sprint 2.3 RAG Knowledge Base + Message Extension Backend

### Task: BM25-based RAG system, knowledge base CRUD endpoints, message extension API, sample docs, agent integration

- **Context:** Sprint 2.3 — build a Retrieval-Augmented Generation system so all specialist agents get grounded context from uploaded documents, plus a Teams message extension endpoint for quick lookups.
- **Deliverables:**
  - **IKnowledgeBase contract** (Contracts/Rag/IKnowledgeBase.cs): `IngestDocumentAsync`, `SearchAsync`, `ListDocumentsAsync`, `DeleteDocumentAsync` + `SearchResult` and `DocumentInfo` records
  - **DocumentChunker** (Api/Rag/DocumentChunker.cs): Static utility. Splits by paragraph, merges short paragraphs up to ~500 tokens, creates overlapping chunks with 50-token overlap, preserves section headers for citations
  - **InMemoryKnowledgeBase** (Api/Rag/InMemoryKnowledgeBase.cs): BM25 scoring (k1=1.2, b=0.75), thread-safe via ConcurrentDictionary, no Azure dependency. Simple whitespace tokenization. Score normalization to 0-1 range, threshold at 0.3
  - **RagContextProvider** (Api/Rag/RagContextProvider.cs): Searches KB for top-3 relevant chunks, formats as "[Source: {title}, chunk {N}]" reference context injected into agent history
  - **KnowledgeBaseSeeder** (Api/Rag/KnowledgeBaseSeeder.cs): 4 embedded sample documents (holiday planning, category management, promo effectiveness, competitive response), idempotent via `HasDocument()` check
  - **Sample docs** (Api/Rag/SampleDocs/): 4 markdown files mirroring the embedded constants
  - **REST endpoints:** POST /api/knowledge/upload, GET /api/knowledge/documents, DELETE /api/knowledge/documents/{id}, POST /api/knowledge/search, GET /api/knowledge/stats
  - **Message extension:** POST /api/message-extension/query (searches KB → injects context → routes to GeneralAgent → returns answer with citations), GET /api/message-extension/manifest (Teams app manifest snippet)
  - **DI wiring:** IKnowledgeBase + InMemoryKnowledgeBase as singleton, RagContextProvider as singleton, auto-seed on startup
  - **RAG middleware:** Injected between memory enrichment and router classification in /api/chat — transparent to all agents

### Technical Decisions
- BM25 over embeddings for local dev — zero Azure dependency, works with `demo-key` config
- RAG context appended as system message in History (not modifying system prompt) — clean separation
- Citation format: `[Source: {title}, chunk {N}] (relevance: {score})`
- Message extension returns confidence: high (3+ citations), medium (1-2), low (0)

### Learnings
- 2026-05-13T13:37:26-04:00 — BM25 scoring with IDF normalization works well for retail domain docs. `Math.Log((N - n + 0.5) / (n + 0.5) + 1.0)` avoids negative IDF for common terms.
- 2026-05-13T13:37:26-04:00 — Score normalization to 0-1 range (dividing by max score) is essential for consistent threshold filtering across different query lengths.
- 2026-05-13T13:37:26-04:00 — When test files pre-exist from another agent but target a different API surface, rewrite them completely rather than trying to patch — saves time and avoids subtle mismatches.
- 2026-05-13T13:37:26-04:00 — RAG context injection point should be AFTER memory enrichment but BEFORE router classification, so all specialist agents benefit transparently.

**Validation:** Build clean (0 errors), 60 new RAG tests pass (786 total pass, 17 pre-existing failures in Competitive/Alert tests)

## 2026-05-13 — Sprint 2.4: Supply Chain Data Layer + Council Endpoints

### Deliverables
- **Schema v6:** 3 new tables (InventoryLevels, SupplyDisruptions, FulfillmentRates) with indexes
- **Seed data:** ~180 inventory records, 18 active disruptions, 6 months fulfillment history per brand/region
- **MCP Tools (SupplyTools.cs):** GetInventoryLevels, GetSupplyDisruptions, GetFulfillmentRate, GetSupplyHealthSummary
- **API Proxy Tools (SupplyChainTools.cs):** InventoryLevelsTool, SupplyDisruptionsTool, FulfillmentRateTool, SupplyHealthTool
- **MCP REST endpoints:** /api/supply/inventory, /api/supply/disruptions, /api/supply/fulfillment, /api/supply/health
- **API endpoints:** Supply proxy endpoints + council endpoints (POST /api/council/convene, GET /api/council/agents)

## Session Work — Phase 4 Implementation (4.1 + 4.2 + 4.3)

### Overview
Full Phase 4 implementation across all three sprints: Store Operations & Planogram (4.1), Margin Analysis & Escalation (4.2), Scorecard & Explainability (4.3).

### Database — Schema v7
- **5 new tables:** StoreMetrics, ShelfLayouts, SkuVelocity, BrandFinancials, MarginDrivers
- **5 seed methods** for realistic retail data (store performance, shelf layouts, SKU velocity, brand financials, margin drivers)
- **8 query methods:** GetStorePerformance, GetShelfLayout, OptimizePlanogram, PredictStockout, GetMarginByBrand, GetMarginDrivers, GetMarginTrend, DetectMarginRisks

### MCP Tools
- **StoreOpsTools.cs:** GetStorePerformance, GetShelfLayout, OptimizePlanogram, PredictStockout
- **MarginTools.cs:** GetMarginByBrand, GetMarginDrivers, GetMarginTrend, DetectMarginRisks

### API Proxy Tools
- **StoreOpsProxyTools.cs:** StorePerformanceTool, ShelfLayoutTool, OptimizePlanogramTool, PredictStockoutTool
- **MarginProxyTools.cs:** MarginByBrandTool, MarginDriversTool, MarginTrendTool, DetectMarginRisksTool

### Specialist Agents
- **StoreOpsAgent** (key: "store-ops", intent: "store/operations")
- **PlanogramAgent** (key: "planogram", intent: "planogram/optimization")
- **MarginAgent** (key: "margin-analysis", intent: "margin/analysis")

### Orchestrators & Services
- **EscalationOrchestrator:** L1→L2→L3 escalation chain with complexity detection
- **ScorecardOrchestrator:** Fan-out brand scoring with 5 weighted dimensions (Demand 0.25, Competitive 0.20, Supply 0.20, Store Execution 0.20, Margin 0.15)
- **ExplainabilityService:** Tool execution trace capture + "why?" handler for decision chains

### Wiring
- **AgentIntent.cs:** Added StoreOps, Planogram, MarginAnalysis, Scorecard constants
- **RoutingServiceExtensions.cs:** 3 new agent registrations with tool factories
- **prompts.yaml:** 4 new router intent categories + 5 new agent definitions (store-ops, planogram, margin, scorecard-synthesis, exec-brief)
- **Program.cs:** Agent def loading, 8 proxy tool DI registrations, orchestrator/service DI, ~15 REST endpoints

### Key Decisions
- Used `$$"""` raw string literal pattern for ScorecardOrchestrator JSON templates (CS9006 fix)
- Kept all agents on ISpecialistAgent interface with DemandForecastAgent as the canonical pattern
- Schema versioning bumped 6→7 for Phase 4

### Validation
- Build: 0 errors (only pre-existing warnings)
- Tests: 1264 pass, 0 failures (55+ new tests added by sub-agents for StoreOps, Planogram, Margin, Routing, Escalation, Scorecard)
- **CouncilConveneRequest DTO** for convene endpoint request body

### Learnings
- Composite health summary queries (GetSupplyHealthSummary) that aggregate across multiple tables are more useful for agents than raw table queries — they reduce multi-tool orchestration overhead.
- Council convene endpoint uses placeholder response pattern (returns participant list + "awaiting_orchestrator" status) — clean contract for parallel development with Kroger's ConsensusOrchestrator.
- Pre-existing duplicate `.WithName()` values in MCP Program.cs (e.g., GetHistoricalDemand appears twice) don't cause build errors but may cause runtime issues — noted for future cleanup.

**Validation:** Build clean (0 errors, 5 pre-existing warnings), all 816 tests pass

## Session Work — Sprint 3.1+3.2 Streaming, Caching & Guardrails Contracts + Wiring

### Task: SignalR streaming hub, response cache contracts, query classifier, guardrails config, PII/jailbreak patterns, REST endpoints

- **Context:** Sprint 3.1 (Streaming + Caching) and Sprint 3.2 (Guardrails Services) — create contract interfaces, implementations, and REST endpoints for real-time streaming, deterministic query caching, and security guardrails.
- **Parallel work:** Kroger (Architect) independently created implementations (InMemoryResponseCache, InMemorySuspiciousRequestLog, JailbreakDetector, PiiRedactor, AccessControlGuard, GuardrailsMiddleware, StreamingMiddleware) in the same worktree, all referencing MY contract namespaces.
- **Reconciliation:** Kept Kroger's implementations, deleted my duplicate `Api/Services/` directory. Fixed namespace references (`Api.Services.Caching` → `Api.Caching`, `Api.Services.Guardrails` → `Api.Guardrails`).

- **My contributions (contracts + wiring):**
  - `Contracts/Caching/IResponseCache.cs` — IResponseCache interface, CachedResponse, CacheStats records
  - `Contracts/Caching/QueryClassifier.cs` — Static IsDeterministic with GeneratedRegex (never-cache: forecasts/recommendations/time-sensitive; always-cache: factual/historical)
  - `Contracts/Guardrails/ISuspiciousRequestLog.cs` — ISuspiciousRequestLog interface, SuspiciousRequest, GuardrailsStats records
  - `Contracts/Guardrails/GuardrailsConfig.cs` — Runtime-toggleable settings (PiiDetection, JailbreakDetection, AutoRedactPii, MaxInputLength)
  - `Api/Hubs/StreamingHub.cs` — SignalR hub at `/hubs/streaming` with JoinSession/LeaveSession + StreamingEvents static helper
  - `Api/Guardrails/GuardrailPatterns.cs` — Compiled regex: 4 PII patterns (SSN, Email, Phone, CreditCard) + 6 jailbreak patterns
  - Program.cs DI: InMemoryResponseCache, InMemorySuspiciousRequestLog, GuardrailsConfig singletons
  - Program.cs endpoints: GET/DELETE /api/cache/*, GET/PUT /api/guardrails/*
  - Fixed GuardrailsMiddleware.cs using statement, removed duplicate JailbreakConfig from JailbreakDetector.cs
  - Fixed StreamingMiddleware.cs using statement (`Api.Services.Caching` → `Api.Caching`)

### Learnings
- Kroger's implementations reference Costco's contract namespaces by design — contracts-first approach works for parallel development
- When deleting duplicate implementations, check ALL files that imported the old namespace (middleware, middleware middleware, Program.cs)
- JailbreakDetector.cs already had a separate JailbreakConfig.cs file from Kroger — adding a duplicate record causes CS0101

**Validation:** Build clean (0 errors, 5 warnings), all 1061 tests pass

## Session Work — 2026-05-13 Card State & Observability Services (Sprint 3.3 + 3.4)

### What Was Done
- **Card State Service**: Enhanced `InMemoryAdaptiveCardState` with `CreateFromVerdictAsync()` for council verdict → voting card integration and `ListAsync()` for type/lifecycle filtering
- **Conversation Exporter**: Created `ConversationExporter.cs` implementing `IConversationExport` with Markdown/JSON export, session tracking via `TrackMessageAsync`
- **TelemetryHub extensions**: Added `JoinCard`/`LeaveCard` methods for per-card SignalR groups (`card:{cardId}` pattern)
- **DI wiring**: Registered all 4 services (IAdaptiveCardState, ICostTracker, IAuditLog, IConversationExport) as singletons in Program.cs
- **REST endpoints**: 5 card endpoints + 7 observability endpoints with query parameter parsing, error handling, and filtering
- **Chat pipeline instrumentation**: Cost tracking, audit logging, and conversation export tracking integrated after each agent response
- **Duplicate cleanup**: Removed Kroger's duplicate endpoint set that caused ASP0022 route conflict warnings

### Key Decisions
- Used `init` properties (not positional records) for `TrackedMessage` to support object-initializer syntax used in chat pipeline
- Escalation reason persists once set — majority vote does NOT auto-clear escalation (matches test expectations)
- Card groups use `card:{cardId}` prefix matching existing `stream:{sessionId}` pattern in StreamingHub
- ConversationExporter tracks messages via concrete `TrackMessageAsync` method (not on IConversationExport interface) for pipeline-specific concerns

### Learnings
- Kroger added both contracts AND duplicate endpoint registrations in a parallel session — always check for route conflicts when integrating parallel work
- The `CreateFromVerdictAsync` maps council agents to card votes: Approve/Conditional → approve, Reject → reject
- TrackedMessage needed `required` init properties to work with both named construction and object initializers

**Validation:** Build clean (0 errors, 5 warnings), all 1154 tests pass
