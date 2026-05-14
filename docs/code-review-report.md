# Retail Pulse Full Code Review Report

**Reviewer:** Kroger — Lead  
**Requested by:** Brian Swiger  
**Review date:** 2026-05-13T20:12:05.840-04:00  
**Scope:** `RetailPulse.slnx`, all `src` projects, `src/RetailPulse.Web/src`, `tests/RetailPulse.Tests`, and `docs`.

## Executive Summary

Retail Pulse has a strong demo foundation: clean solution-level project boundaries, Aspire orchestration is non-containerized, tenant configuration is centralized, SQLite access generally uses parameters, and the agent-router architecture is the right direction. The production readiness gaps are concentrated in the API surface: endpoints and hubs are effectively unauthenticated by default, no rate limiting exists around expensive AI and state-changing routes, and `Program.cs` has become a 2,300+ line composition root plus endpoint implementation file.

The next sprints should harden the boundary first, then split the monolith, then clean up duplication and drift. If this only stays a local demo, some risks are tolerable. If it faces a real tenant network, they are not.

---

## Critical Findings

### [CRITICAL] API and SignalR Surface Has No Production Authorization Boundary
**Location:** `src/RetailPulse.Api/Program.cs:821`, `src/RetailPulse.Api/Program.cs:830`, `src/RetailPulse.Api/Program.cs:835`, `src/RetailPulse.Api/Program.cs:1242`, `src/RetailPulse.Api/Middleware/ApiKeyAuthMiddleware.cs:33`, `src/RetailPulse.Api/Middleware/ApiKeyAuthMiddleware.cs:46`  
**Category:** Security  
**Description:** The API maps every `/api/*` route and both SignalR hubs without `RequireAuthorization()`. The only API gate is optional and disabled by default. That leaves chat, approvals, cards, observability, guardrails, cache mutation, memory, knowledge upload/delete, scorecard, escalation, and SignalR telemetry callable by anyone who can reach the service.  
**Recommendation:** Add real authentication and authorization before production. Put API routes behind route groups with policies, protect hubs, require explicit development-only bypass, and keep health probes separately scoped. API-key middleware can remain a demo shim but must not be the production control.  
**Effort:** Large (4hr+)

---

## High Findings

### [HIGH] No Rate Limiting on AI, State Mutation, or Telemetry Endpoints
**Location:** `src/RetailPulse.Api/Program.cs:835`, `src/RetailPulse.Api/Program.cs:1242`, `src/RetailPulse.Api/Program.cs:1696`, `src/RetailPulse.Api/Program.cs:1762`, `src/RetailPulse.Api/Program.cs:2337`, `src/RetailPulse.Api/Program.cs:2360`  
**Category:** Security | Performance  
**Description:** The API exposes expensive LLM routes and mutable operational routes without `AddRateLimiter`, `UseRateLimiter`, or endpoint rate policies. A single caller can drive AI spend, fill in-memory stores, churn SignalR, or toggle guardrail configuration repeatedly.  
**Recommendation:** Add global and endpoint-specific rate limits. Use stricter policies for `/api/chat`, `/api/chat/stream`, `/api/council/convene`, `/api/scorecard`, `/api/escalate`, upload, and config mutation routes.  
**Effort:** Medium (1-4hr)

### [HIGH] Credentialed CORS Policy Is Too Broad for Production
**Location:** `src/RetailPulse.Api/Program.cs:52`, `src/RetailPulse.Api/Program.cs:59`, `src/RetailPulse.Api/Program.cs:60`, `src/RetailPulse.Api/Program.cs:61`, `src/RetailPulse.Api/Program.cs:62`  
**Category:** Security  
**Description:** CORS allows any method and header with credentials for configured origins. If origin configuration is widened or mis-set, browser clients can make credentialed calls across the entire API surface.  
**Recommendation:** Split development and production CORS policies. In production, allow only exact trusted origins, only required methods/headers, and only enable credentials where the auth model requires them.  
**Effort:** Small (< 1hr)

### [HIGH] Teams SSO Validation Falls Back to Multi-Tenant `common`
**Location:** `src/RetailPulse.TeamsBot/Auth/TeamsSsoHandler.cs:21`, `src/RetailPulse.TeamsBot/Auth/TeamsSsoHandler.cs:46`, `src/RetailPulse.TeamsBot/Auth/TeamsSsoHandler.cs:57`, `src/RetailPulse.TeamsBot/Auth/TeamsSsoHandler.cs:60`  
**Category:** Security  
**Description:** The Teams bot uses `common` when `MicrosoftEntra:TenantId` is missing and includes the common issuer as valid. That makes tenant restriction optional and can accept identities from unintended Entra tenants.  
**Recommendation:** Require explicit tenant configuration outside local development, validate the `tid` claim against an allowlist, and remove `common` from production valid issuers.  
**Effort:** Medium (1-4hr)

### [HIGH] Unbounded In-Memory Observability Stores Can Leak Memory
**Location:** `src/RetailPulse.Api/Observability/InMemoryCostTracker.cs:12`, `src/RetailPulse.Api/Observability/InMemoryCostTracker.cs:24`, `src/RetailPulse.Api/Observability/ConversationExporter.cs:15`, `src/RetailPulse.Api/Observability/ConversationExporter.cs:60`, `src/RetailPulse.Api/Observability/ConversationExporter.cs:66`, `src/RetailPulse.Api/Observability/ConversationExporter.cs:152`  
**Category:** Performance  
**Description:** Cost events and conversation sessions grow without TTL, capacity, or eviction. `ConversationExporter` also stores messages in a regular `List<T>` inside a `ConcurrentDictionary`, which is not safe for concurrent writes to the same session.  
**Recommendation:** Add bounded retention, per-session message limits, eviction, and thread-safe per-session locking or immutable append. For production, persist observability to durable storage instead of singleton memory.  
**Effort:** Medium (1-4hr)

### [HIGH] API Composition Root Has Become a God File
**Location:** `src/RetailPulse.Api/Program.cs:31`, `src/RetailPulse.Api/Program.cs:835`, `src/RetailPulse.Api/Program.cs:2380`  
**Category:** Architecture  
**Description:** `Program.cs` now performs tenant prompt hydration, service registration, chat orchestration, proxy routes, knowledge routes, cards, observability, guardrails, scorecard, escalation, DTOs, and helpers. At more than 2,300 lines, it is difficult to review, test, and safely evolve.  
**Recommendation:** Keep `Program.cs` as composition only. Move endpoint groups into extension classes (`MapChatEndpoints`, `MapObservabilityEndpoints`, etc.), move prompt hydration into a service, and put DTOs in contract/source files.  
**Effort:** Large (4hr+)

### [HIGH] Knowledge Upload and Search Are Unbounded In-Memory Paths
**Location:** `src/RetailPulse.Api/Program.cs:1644`, `src/RetailPulse.Api/Program.cs:1668`, `src/RetailPulse.Api/Rag/InMemoryKnowledgeBase.cs:31`, `src/RetailPulse.Api/Rag/InMemoryKnowledgeBase.cs:45`, `src/RetailPulse.Api/Rag/InMemoryKnowledgeBase.cs:70`, `src/RetailPulse.Api/Rag/InMemoryKnowledgeBase.cs:83`, `src/RetailPulse.Api/Rag/InMemoryKnowledgeBase.cs:89`  
**Category:** Performance | Security  
**Description:** Uploaded knowledge documents are accepted without size/count quotas and stored in process memory. Search scans all chunks and counts matching chunks for each query term. Combined with missing authorization/rate limiting, this is a direct denial-of-service path.  
**Recommendation:** Enforce upload size, document count, chunk count, and per-tenant quotas. Add cancellation checks inside ingestion/search loops, reject unsupported content, and move retrieval to a bounded or external index before production.  
**Effort:** Large (4hr+)

---

## Medium Findings

### [MEDIUM] Duplicate Singleton Registrations Create Ambiguous Service Wiring
**Location:** `src/RetailPulse.Api/Program.cs:466`, `src/RetailPulse.Api/Program.cs:471`, `src/RetailPulse.Api/Program.cs:484`, `src/RetailPulse.Api/Program.cs:491`  
**Category:** Architecture | Performance  
**Description:** Adaptive card state, cost tracking, audit log, and conversation export are each registered twice. The current implementation likely resolves the last registration, but the duplicate blocks make service lifetime and instance identity harder to reason about.  
**Recommendation:** Consolidate each service registration into one canonical block and add a DI smoke test proving interface and concrete resolutions share the intended singleton.  
**Effort:** Small (< 1hr)

### [MEDIUM] Approval Gate Uses Synchronous SQLite Calls in Async Request Paths
**Location:** `src/RetailPulse.Api/Approval/SqliteApprovalGate.cs:74`, `src/RetailPulse.Api/Approval/SqliteApprovalGate.cs:80`, `src/RetailPulse.Api/Approval/SqliteApprovalGate.cs:97`, `src/RetailPulse.Api/Approval/SqliteApprovalGate.cs:122`, `src/RetailPulse.Api/Approval/SqliteApprovalGate.cs:133`, `src/RetailPulse.Api/Approval/SqliteApprovalGate.cs:151`, `src/RetailPulse.Api/Approval/SqliteApprovalGate.cs:192`  
**Category:** Performance  
**Description:** Methods return `Task` but perform synchronous `Open`, `ExecuteNonQuery`, and reader work. `WaitForApprovalAsync` also polls every two seconds and opens a new SQLite connection on every pass.  
**Recommendation:** Use async SQLite APIs consistently, add cancellation checks before DB work, and replace polling with event-driven notification or a backoff strategy.  
**Effort:** Medium (1-4hr)

### [MEDIUM] Fire-and-Forget Memory Work Ignores Request Cancellation
**Location:** `src/RetailPulse.Api/Program.cs:1123`, `src/RetailPulse.Api/Program.cs:1128`, `src/RetailPulse.Api/Program.cs:1134`, `src/RetailPulse.Api/Program.cs:1155`, `src/RetailPulse.Api/Program.cs:1296`, `src/RetailPulse.Api/Program.cs:1299`, `src/RetailPulse.Api/Program.cs:1300`  
**Category:** Performance | Architecture  
**Description:** Chat endpoints use `Task.Run` with `CancellationToken.None` for memory extraction. The streaming endpoint swallows all exceptions. Under load, cancelled requests can still execute LLM/memory extraction work in the background.  
**Recommendation:** Move memory extraction to a bounded background queue with shutdown cancellation, observability, and retry policy. At minimum pass a linked cancellation token and log failures consistently.  
**Effort:** Medium (1-4hr)

### [MEDIUM] SignalR Trace Notifications Can Create Excess Background Work
**Location:** `src/RetailPulse.Api/Tracing/InMemoryTraceCollector.cs:49`, `src/RetailPulse.Api/Tracing/InMemoryTraceCollector.cs:55`, `src/RetailPulse.Api/Tracing/InMemoryTraceCollector.cs:62`, `src/RetailPulse.Api/Tracing/InMemoryTraceCollector.cs:198`, `src/RetailPulse.Api/Tracing/InMemoryTraceCollector.cs:216`  
**Category:** Performance | SignalR  
**Description:** Trace capture starts fire-and-forget work for every new trace and span. SignalR push failures are intentionally swallowed, which is acceptable for telemetry, but there is no bounded queue or backpressure.  
**Recommendation:** Replace per-span fire-and-forget calls with a bounded channel processed by a hosted service. Track dropped telemetry counts so degradation is visible.  
**Effort:** Medium (1-4hr)

### [MEDIUM] Frontend Knowledge API Does Not Match Backend Routes
**Location:** `src/RetailPulse.Web/src/services/knowledgeApi.ts:9`, `src/RetailPulse.Web/src/services/knowledgeApi.ts:13`, `src/RetailPulse.Web/src/services/knowledgeApi.ts:26`, `src/RetailPulse.Web/src/services/knowledgeApi.ts:27`, `src/RetailPulse.Api/Program.cs:1644`, `src/RetailPulse.Api/Program.cs:1668`  
**Category:** Architecture  
**Description:** The frontend posts uploads to `/api/knowledge/documents` with multipart form data and searches via `GET /api/knowledge/search?q=...`. The backend exposes `POST /api/knowledge/upload` with a JSON body and `POST /api/knowledge/search` with a JSON body. Knowledge UI calls will fail or drift further.  
**Recommendation:** Pick one contract and align both sides. Prefer a typed API client generated or tested against backend endpoint contracts.  
**Effort:** Medium (1-4hr)

### [MEDIUM] MCP Server Exposes Duplicate Demand Route Shapes and Names
**Location:** `src/RetailPulse.McpServer/Program.cs:71`, `src/RetailPulse.McpServer/Program.cs:80`, `src/RetailPulse.McpServer/Program.cs:88`, `src/RetailPulse.McpServer/Program.cs:96`, `src/RetailPulse.McpServer/Program.cs:104`, `src/RetailPulse.McpServer/Program.cs:111`, `src/RetailPulse.McpServer/Program.cs:118`, `src/RetailPulse.McpServer/Program.cs:125`  
**Category:** Dead Code | Architecture  
**Description:** Legacy demand endpoints and newer `/api/demand/*` endpoints coexist with the same operation names. That widens the surface area and invites behavior drift.  
**Recommendation:** Mark one route family canonical, deprecate the other behind compatibility tests, then remove the legacy routes when consumers move.  
**Effort:** Medium (1-4hr)

### [MEDIUM] Specialist Agents Duplicate the Same Pipeline Implementation
**Location:** `src/RetailPulse.Api/Agents/Specialists/GeneralAgent.cs:56`, `src/RetailPulse.Api/Agents/Specialists/DemandForecastAgent.cs:53`, `src/RetailPulse.Api/Agents/Specialists/SupplyChainAgent.cs:52`, `src/RetailPulse.Api/Agents/Specialists/StoreOpsAgent.cs:52`, `src/RetailPulse.Api/Agents/Specialists/PlanogramAgent.cs:52`, `src/RetailPulse.Api/Agents/Specialists/MarginAgent.cs:52`  
**Category:** Architecture  
**Description:** Each specialist repeats message construction, history truncation, tool span extraction, chart extraction, token accounting, and error handling. Fixes to one agent can easily miss the others.  
**Recommendation:** Extract a shared `SpecialistAgentBase` or `IAgentExecutionPipeline` that handles common orchestration while leaving each specialist responsible for identity, prompt, tools, and domain-specific post-processing.  
**Effort:** Large (4hr+)

### [MEDIUM] Cost Tracking Uses the Wrong Model Name
**Location:** `src/RetailPulse.Api/Program.cs:1169`, `src/RetailPulse.Api/Program.cs:1170`, `src/RetailPulse.Api/Observability/InMemoryCostTracker.cs:15`, `src/RetailPulse.Api/Observability/InMemoryCostTracker.cs:17`  
**Category:** Architecture  
**Description:** Chat cost tracking records every usage event as `gpt-4o`, even though the configured model can differ. The pricing table also includes demo values only. Cost dashboards will be materially wrong as model routing changes.  
**Recommendation:** Pass the actual model from the selected agent definition into `UsageEvent`, centralize pricing in configuration, and cover it with tests.  
**Effort:** Small (< 1hr)

### [MEDIUM] Teams Bot Telemetry Startup Failure Is Hidden
**Location:** `src/RetailPulse.TeamsBot/Program.cs:67`, `src/RetailPulse.TeamsBot/Program.cs:69`, `src/RetailPulse.TeamsBot/Services/TelemetrySignalRClient.cs:45`, `src/RetailPulse.TeamsBot/Services/TelemetrySignalRClient.cs:55`, `src/RetailPulse.TeamsBot/Services/TelemetrySignalRClient.cs:57`  
**Category:** Architecture  
**Description:** The bot attempts SignalR connection on startup, but `ConnectAsync` catches and logs failures without surfacing degraded health. The process can appear healthy while telemetry is unavailable.  
**Recommendation:** Either fail fast when telemetry is required or expose degraded health and retry state. Make the behavior environment-configurable.  
**Effort:** Small (< 1hr)

### [MEDIUM] Tenant Configuration Defaults Are Demo-Specific
**Location:** `src/RetailPulse.Contracts/TenantConfiguration.cs:11`, `src/RetailPulse.Contracts/TenantConfiguration.cs:12`, `src/RetailPulse.Contracts/TenantConfiguration.cs:63`, `src/RetailPulse.Contracts/TenantConfiguration.cs:71`, `src/RetailPulse.Contracts/TenantConfiguration.cs:82`, `src/RetailPulse.Contracts/TenantConfiguration.cs:86`  
**Category:** Architecture  
**Description:** Shared contract DTOs carry demo defaults such as `Retail Pulse Demo`, `Premium`, and `Three-Tier`. That weakens the generic tenant model by allowing missing tenant config to silently become sample-specific behavior.  
**Recommendation:** Move demo defaults to `tenant.yaml` or sample config. Make required tenant fields explicit and validate tenant configuration on startup.  
**Effort:** Medium (1-4hr)

### [MEDIUM] Bot Critical Paths Are Mostly Manual-Tested
**Location:** `tests/RetailPulse.Tests/RetailPulse.Tests.csproj:25`, `tests/RetailPulse.Tests/RetailPulse.Tests.csproj:28`, `tests/RetailPulse.Tests/bot-test.http:21`, `tests/RetailPulse.Tests/bot-test.http:31`, `tests/RetailPulse.Tests/bot-test.http:58`, `tests/RetailPulse.Tests/bot-test.http:90`  
**Category:** Architecture  
**Description:** The bot project is referenced by tests, but `/api/messages`, welcome, reset/help, card action, and auth-boundary behavior are covered by a manual `.http` harness rather than automated integration tests. That is too thin for a Teams-facing surface.  
**Recommendation:** Add automated bot endpoint integration tests with a test host for health, message, conversation update, card action, reset/help, and auth-required production mode.  
**Effort:** Medium (1-4hr)

---

## Low Findings

### [LOW] Development `demo-key` Can Mask Configuration Errors
**Location:** `src/RetailPulse.Api/Program.cs:509`, `src/RetailPulse.Api/Program.cs:512`, `src/RetailPulse.Api/Program.cs:514`  
**Category:** Security  
**Description:** Development falls back to a hardcoded `demo-key` for OpenAI/API Gateway. This is not a production secret leak, but it can hide misconfiguration and produce confusing downstream authentication failures.  
**Recommendation:** Prefer user secrets or explicit local configuration. If a fake key remains, log a clear warning and ensure it cannot flow outside development.  
**Effort:** Small (< 1hr)

### [LOW] Chart and Alert Parsing Swallows Exceptions Without Diagnostics
**Location:** `src/RetailPulse.Api/Agents/Specialists/GeneralAgent.cs:220`, `src/RetailPulse.Api/Agents/Specialists/GeneralAgent.cs:236`, `src/RetailPulse.Api/Agents/Specialists/DemandForecastAgent.cs:217`, `src/RetailPulse.Api/Agents/Specialists/DemandForecastAgent.cs:233`, `src/RetailPulse.Api/Agents/Specialists/CompetitiveIntelAgent.cs:316`, `src/RetailPulse.Api/Alerts/SqliteAlertService.cs:221`  
**Category:** Architecture  
**Description:** Several JSON parse failures are swallowed silently. Some are expected non-chart tool results, but poisoned or malformed data becomes invisible during debugging.  
**Recommendation:** Use filtered catches (`JsonException`) and debug-level structured logs with safe metadata. Do not log full tool payloads unless explicitly safe.  
**Effort:** Small (< 1hr)

### [LOW] Streaming State in ChatPanel Is Dead Code
**Location:** `src/RetailPulse.Web/src/components/ChatPanel.tsx:467`, `src/RetailPulse.Web/src/components/ChatPanel.tsx:468`, `src/RetailPulse.Web/src/components/ChatPanel.tsx:470`, `src/RetailPulse.Web/src/components/ChatPanel.tsx:471`, `src/RetailPulse.Web/src/components/ChatPanel.tsx:472`  
**Category:** Dead Code  
**Description:** `streamingTokens` and `isStreaming` setters are intentionally no-op'd. That keeps TypeScript quiet but leaves a half-wired streaming feature in the primary chat component.  
**Recommendation:** Remove the unused state until streaming is wired, or complete the SignalR streaming integration.  
**Effort:** Small (< 1hr)

### [LOW] StreamingMessage Timer Churns on Every Reveal Step
**Location:** `src/RetailPulse.Web/src/components/streaming/StreamingMessage.tsx:65`, `src/RetailPulse.Web/src/components/streaming/StreamingMessage.tsx:70`, `src/RetailPulse.Web/src/components/streaming/StreamingMessage.tsx:79`, `src/RetailPulse.Web/src/components/streaming/StreamingMessage.tsx:80`, `src/RetailPulse.Web/src/components/streaming/StreamingMessage.tsx:92`  
**Category:** Performance  
**Description:** The reveal effect depends on `displayedLength`, so it tears down and recreates an interval every 18ms during long responses. This is not catastrophic, but it is unnecessary UI churn.  
**Recommendation:** Use one interval or `requestAnimationFrame` loop per message, or render accumulated text directly and animate only the cursor.  
**Effort:** Small (< 1hr)

### [LOW] Conversation Export Frontend Does Not Abort In-Flight Preview Requests
**Location:** `src/RetailPulse.Web/src/components/observability/ConversationExport.tsx:261`, `src/RetailPulse.Web/src/components/observability/ConversationExport.tsx:275`, `src/RetailPulse.Web/src/components/observability/ConversationExport.tsx:295`, `src/RetailPulse.Web/src/components/observability/ConversationExport.tsx:298`, `src/RetailPulse.Web/src/services/observabilityApi.ts:42`  
**Category:** Performance  
**Description:** Session loading uses a local cancelled flag, but the underlying fetch keeps running. Preview requests have no cancellation guard at all.  
**Recommendation:** Add `AbortController` support to the observability API client and abort pending session/preview requests on unmount or superseding preview selections.  
**Effort:** Small (< 1hr)

### [LOW] Documentation Gives Conflicting Bot SSO Expectations
**Location:** `docs/testing-guide.md:165`, `docs/testing-guide.md:168`, `docs/teams-setup.md:47`, `docs/teams-setup.md:49`, `docs/teams-setup.md:79`, `docs/teams-setup.md:89`  
**Category:** Dead Code | Architecture  
**Description:** The testing guide says the bot should use SSO and pass display name/email, while the Teams setup guide correctly states local emulator/harness mode has no SSO and falls back to anonymous context. This will confuse sprint planning and acceptance criteria.  
**Recommendation:** Split local emulator expectations from real Teams/SSO expectations and add automated tests for the boundary once auth is finalized.  
**Effort:** Small (< 1hr)

---

## Summary Table

| Severity | Performance | Security | Dead Code | Architecture | Total |
|---|---:|---:|---:|---:|---:|
| Critical | 0 | 1 | 0 | 0 | 1 |
| High | 2 | 3 | 0 | 1 | 6 |
| Medium | 3 | 0 | 1 | 7 | 11 |
| Low | 2 | 1 | 2 | 1 | 6 |
| **Total** | **7** | **5** | **3** | **9** | **24** |

> Category columns use each finding's primary category so row totals match the finding count.

---

## Recommended Sprint Breakdown

### Sprint 1 — Production Boundary and Cost Controls
- Add authentication/authorization to API routes and SignalR hubs.
- Add rate limiting for chat, stream, council, scorecard, escalation, upload, config, and cache mutation routes.
- Tighten production CORS.
- Fix Teams SSO tenant validation.
- Add quotas to knowledge upload/search and in-memory observability stores.

### Sprint 2 — API Architecture Reset
- Split `Program.cs` into endpoint group extension files and dedicated orchestration services.
- Consolidate duplicate DI registrations.
- Introduce a shared specialist-agent execution pipeline.
- Align frontend/backend knowledge API contracts.
- Correct cost tracking to use the actual configured model.

### Sprint 3 — Reliability and Test Coverage
- Convert SQLite approval paths to async and remove polling where possible.
- Replace fire-and-forget memory and trace pushes with bounded background queues.
- Add automated Teams bot integration tests.
- Surface telemetry degraded health in the bot.

### Sprint 4 — Cleanup and Documentation
- Deprecate duplicate MCP demand routes.
- Remove dead streaming state or finish streaming integration.
- Reduce frontend timer/fetch churn.
- Resolve docs drift around local bot testing versus real Teams SSO.
- Move demo defaults out of tenant contract DTOs.

---

## Architectural Recommendations for Next Phase

1. **Set the boundary before feature work.** No sprint should add new API surface until auth, rate limits, and CORS are settled. Foundation first.
2. **Keep Aspire as the orchestrator.** The AppHost is clean and non-containerized; preserve that pattern. Add explicit health/degraded states rather than hiding failed service connections.
3. **Make Program.cs composition only.** Endpoint group mapping and orchestration services will give Costco and Target testable seams without compromising Kroger's architecture line.
4. **Codify tenant validation.** Tenant config must fail fast when required fields are absent. Core contracts should not smuggle demo defaults.
5. **Replace singleton memory with bounded stores.** In-memory is fine for the demo, but every in-memory subsystem needs capacity, TTL, and visible degradation behavior.
6. **Standardize agent execution.** A shared specialist pipeline will prevent copy/paste drift as the agent roster grows.
7. **Turn manual bot checks into gates.** The Teams bot is a first-class entry point; it needs automated coverage before sprint velocity increases.
