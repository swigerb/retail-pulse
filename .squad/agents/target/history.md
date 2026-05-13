# Target — History

## 2026-04-30 — Team Initialization

- **Project:** Retail Pulse — a generic pro-code agentic demo for retail & consumer goods organizations (grocers, QSRs, big box retail)
- **Stack:** .NET 10, C#, Aspire (host + OTel, non-containerized), React/Vite/TypeScript, Azure API Management, AI Gateway pattern
- **Owner:** Brian Swiger
- **Context:** Built on Patron Pulse but updated to be generic with tenant configuration, extra organization examples, and corrected diagrams

## Learnings

### 2026-05-13 — Sprint 1.1 Router Test Infrastructure

- **Multi-agent routing interfaces** live in `RetailPulse.Contracts.Routing` namespace (not base Contracts). Two key interfaces: `IAgentRouter` (returns `RoutingDecision`) and `ISpecialistAgent` (with `Key`, `SupportedIntents`, `HandleAsync(ChatRequest)`).
- **RetailOpsRouter** (`Api.Agents.Routing`) uses `ParseClassification` (internal, testable) to parse LLM JSON. Falls back to `AgentIntent.General` on malformed JSON or unknown intents. Confidence threshold is 0.6.
- **GeneralAgent** implements `ISpecialistAgent` and supports ALL six intent categories as fallback. It preserves full backward compatibility with the original `RetailPulseAgent` (same ChatResponse shape, spans, token usage).
- **Test pattern:** Mock `IChatClient` with fixed JSON response text, pass `IEnumerable<ISpecialistAgent>` to router constructor. TelemetryHub mock needs `IHubClients` → `Group()` → `IClientProxy` chain.
- **Codebase is actively being modified by multiple agents** — files can be deleted/moved between reads. Always re-verify file existence before writing tests against a specific API surface.
- **63 new tests** added across 3 test files + 1 fixture file (Router: 33 tests, GeneralAgent: 21 tests, Integration: 9 tests). All 237 total tests pass.

## Session Work — 2026-05-13 Sprint 1.1 Multi-Agent Router Tests (Complete)

**Outcome:** ✅ SUCCESS — Test infrastructure complete, 63 new tests added, all 237 tests passing (174 existing + 63 new)

**Deliverables:**
- `tests/RetailPulse.Tests/Agents/Router/RetailOpsRouterTests.cs` — 33 tests covering:
  - Intent classification for all 6 categories (demand, promo, supply, competitive, sentiment, general)
  - Confidence threshold validation (0.6 boundary, below/above cases)
  - Fallback to General agent on low confidence or classification error
  - ParseClassification edge cases (malformed JSON, unknown intents, multi-intent, intent normalization)
  - History propagation and conversation context
  - Error handling and telemetry span emission

- `tests/RetailPulse.Tests/Agents/Specialists/GeneralAgentTests.cs` — 21 tests covering:
  - ISpecialistAgent interface compliance (Key, DisplayName, SupportedIntents identity)
  - HandleAsync contract and response shape
  - Backward compatibility with original RetailPulseAgent expectations
  - Token usage and cost calculation preservation
  - Error handling and edge cases

- `tests/RetailPulse.Tests/Integration/RouterIntegrationTests.cs` — 9 tests covering:
  - Full pipeline: intent classification → specialist dispatch → response
  - DI registration smoke tests (router discovery, specialist enumeration)
  - Telemetry span verification (agent.routing OTel spans with intent, confidence, fallback tags)
  - Multi-tenant routing scenarios
  - Backward compatibility with existing `/api/chat` endpoint

- `tests/RetailPulse.Tests/Fixtures/AgentTestFixtures.cs` — shared factory methods:
  - Mock IChatClient builders (fixed JSON responses for deterministic classification)
  - Mock specialist agents
  - TelemetryHub mock with IHubClients → Group() → IClientProxy chain setup

**Test Strategy:** Target `Contracts.Routing` interface surface exclusively (IAgentRouter, ISpecialistAgent, RoutingDecision, AgentIntent). ParseClassification is internal but testable via InternalsVisibleTo pattern. Mocks use deterministic JSON so tests are stable and fast.

**Cross-Agent Collaboration:**
- Kroger (Architect): Provided contract surface, router implementation details
- Costco (Backend): Reconciled parallel test work, verified against refactored GeneralAgent
- Chick (Frontend): Integration tests include telemetry span verification for UI consumption

**Validation:** All 237 tests pass (0 failures). Build clean. Test coverage now includes routing layer as regression safety net for ongoing specialist agent work.

**Decision Logged:** Router Test Infrastructure Decision

## Session Work — 2026-05-14 Sprint 1.2 Demand Forecasting Tests (Complete)

**Outcome:** ✅ SUCCESS — 109 new tests added, all 346 tests passing (237 existing + 109 new)

### Learnings

- **Backend team (Costco) was further along than expected** — `DemandForecastAgent.cs`, all 4 MCP tools, DB schema, seed methods, and query methods were already implemented. Always check existing implementations before writing stubs.
- **Duplicate method trap:** Adding seed/query methods without scrolling the full `RetailPulseDb.cs` (1770+ lines) caused duplicate definitions. Backend team's implementations lived at lines 808+. Lesson: always search the entire file for a method name before adding it.
- **Dynamic dispatch + extension methods don't mix:** `severityOrder.GetValueOrDefault(((dynamic)r).severity, 3)` fails because extension methods can't be resolved on `dynamic`. Fix: explicit cast `(string)((dynamic)r).severity`.
- **`GenerateForecast` signature mismatch:** Tool passed 3 args (`brand, region, channel`) but DB method only takes 2 (`brand, region`). Fixed by removing `channel` parameter from tool call.
- **FluentAssertions `BeOneOf` with `because` string:** `BeOneOf(11, 12, "reason")` fails because the string arg gets interpreted as a `params int[]` element. Use `BeOneOf(new[] { 11, 12 }, "reason")` instead.
- **Duplicate MCP tool registration risk:** Both individual tool files (e.g., `GetHistoricalDemandTool.cs`) AND `DemandTools.cs` define `[McpServerTool]` with identical names. Builds fine but may cause runtime duplicate registration errors.
- **Seeded data is deterministic but large:** 12 brands × 6 regions × 3 channels × 365 days = ~79,000 rows. Data integrity tests still run in ~14s total including all 346 tests.

### Deliverables

- `tests/RetailPulse.Tests/Agents/Specialists/DemandForecastAgentTests.cs` — 28 tests:
  - ISpecialistAgent interface compliance (Key, DisplayName, SupportedIntents)
  - Response shape validation (JSON structure, forecast fields)
  - Tool isolation (only claims DemandForecasting intent, not General)
  - History propagation and conversation context
  - Error handling (null/empty prompts, LLM failures)
  - Parameterized brand tests across all 12 brands

- `tests/RetailPulse.Tests/Tools/DemandToolTests.cs` — 30 tests:
  - GetHistoricalDemand: brand/region/channel filtering, monthly aggregation, null-brand returns all
  - GenerateForecast: confidence bands (±15%), seasonal adjustment, date ranges
  - GetSeasonalityFactors: category filtering, impact classification, null returns all
  - IdentifyDemandRisks: anomaly detection (spike/drop), severity sorting, brand/region filtering

- `tests/RetailPulse.Tests/Data/DemandDataTests.cs` — 46 tests:
  - Brand coverage (all 12 brands present in seed data)
  - Region coverage (all 6 regions)
  - Channel coverage (all 3 channels)
  - Time span validation (365 days, no gaps, correct date range)
  - Seasonal patterns (multipliers vary by month, spirits peak Nov/Dec)
  - Volume integrity (positive values, reasonable ranges)

- `tests/RetailPulse.Tests/Integration/RouterIntegrationTests.cs` — 5 new tests added:
  - Demand routing: router dispatches demand intents to DemandForecastAgent
  - Backward compatibility: demand routing doesn't break existing general routing
  - Multi-specialist coexistence: DemandForecastAgent + GeneralAgent in same pipeline

### Bug Fixes (pre-existing backend issues)

- `GenerateForecastTool.cs` line 21: removed extra `channel` parameter not in DB method signature
- `RetailPulseDb.cs` ~line 1311: explicit `(string)` cast on dynamic severity to fix extension method dispatch
- Both fixes are in backend team's code — flagged for Costco review

**Validation:** All 346 tests pass (0 failures, 0 skipped). Build clean (0 errors, 0 warnings).

**Decision Logged:** Demand Forecasting Test Strategy

## Session Work — Sprint 1.3/1.4 Memory & Approval Tests (Complete)

**Outcome:** ✅ SUCCESS — 97 new tests added, all 443 tests passing (346 existing + 97 new)

### Learnings

- **Backend team (Costco) built implementations in parallel** — Contract interfaces and implementations for both Conversation Memory and Approval Gate were already complete. Always read real implementations before writing test stubs.
- **SqliteConversationMemory.ParseKeywords includes full phrase** — Inserts trimmed query as first keyword for exact-match boosting, then individual tokens (≥3 chars, no stop words, max 7). Total keywords = phrase + tokens, max 8.
- **SqliteApprovalGate.RespondAsync is idempotent** — Silently ignores re-respond on already-resolved requests (no exception). First decision wins.
- **ConversationMemoryMiddleware is a standalone class** — Does NOT implement IMemoryMiddleware interface. Has `BuildMemoryContextAsync` and `ExtractAndStoreAsync` methods. MaxContextChars = 2000.
- **MemoryExtractionService.ParseExtraction is internal static** — Testable via InternalsVisibleTo. Returns ExtractionResult with Summary, Entities[], Preference?.
- **ConversationMemoryMiddleware.FormatAge is internal static** — Formats TimeSpan as human-readable ("just now", "2h ago", "1d ago", "1w ago").
- **ApprovalTool returns JSON strings** — Not ApprovalResult objects. Catches OperationCanceledException and general exceptions, returns JSON error objects.
- **GetHistoryAsync is global** — Returns ALL users' history (no user filter), ordered by RespondedAt DESC.

### Deliverables

- `tests/RetailPulse.Tests/Memory/ConversationMemoryTests.cs` — 24 tests:
  - StoreAsync: entry creation, TTL validation (30d/90d), entity key persistence, unique IDs
  - RecallAsync: privacy scoping (no cross-user leaks), maxResults limit, query relevance ranking, entity key matching, empty state
  - TTL enforcement: expired entries pruned, valid entries returned
  - ForgetAsync: full purge, cross-user isolation, empty user no-op
  - ForgetEntryAsync: single entry removal, nonexistent ID no-op
  - Concurrency: parallel stores across users and same user
  - ParseKeywords (internal): count validation, 8-keyword limit, phrase inclusion

- `tests/RetailPulse.Tests/Memory/MemoryMiddlewareTests.cs` — 15 tests:
  - BuildMemoryContextAsync: context block generation, token budget (~2000 chars), first-time user null, preference labels
  - ExtractAndStoreAsync: summary extraction, entity mentions, user preferences, short exchange handling, extraction failure resilience
  - ParseExtraction (internal): valid JSON parsing, malformed JSON handling, null preference, empty entities
  - FormatAge (internal): human-readable time formatting

- `tests/RetailPulse.Tests/Approval/ApprovalGateTests.cs` — 26 tests:
  - RequestApprovalAsync: unique IDs, timestamps, 5-min default timeout, urgency/impact, pending initial state
  - GetResultAsync: pending/approved/rejected states, nonexistent ID throws
  - RespondAsync: approved/rejected/modified decisions, optional comments, idempotent re-respond
  - WaitForApprovalAsync: blocking wait, timeout, already-resolved immediate return
  - Concurrency: parallel requests, independent resolution
  - Audit trail: request/response persistence, history ordering
  - GetPendingAsync/GetHistoryAsync: user filtering, resolved exclusion, limit

- `tests/RetailPulse.Tests/Approval/ApprovalToolTests.cs` — 8 tests:
  - Tool creates approval request with agent context
  - JSON return format validation
  - Timeout/rejection/modified decision handling
  - Error handling (gate throws, cancellation)

- `tests/RetailPulse.Tests/Approval/ApprovalApiTests.cs` — 13 tests:
  - GET pending: user filtering, empty state, urgency/impact inclusion
  - POST respond: approved/rejected/modified decisions, invalid ID, idempotent re-respond
  - GET history: resolved requests, ordering, empty state, pending exclusion, global audit trail
  - Full flow: request → pending → respond → history

- `tests/RetailPulse.Tests/Integration/RouterIntegrationTests.cs` — 3 new integration tests:
  - Memory persistence across conversations (uses real SqliteConversationMemory)
  - Approval end-to-end flow (uses real SqliteApprovalGate)
  - MemoryManagement intent routing

**Validation:** All 443 tests pass (0 failures, 0 skipped). Build clean.
