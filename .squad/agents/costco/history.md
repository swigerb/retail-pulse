# Costco — History

## Recent Work (2026-05-18)

### 2026-05-18 — Fixed Apex Grill Southwest data call (lightweight path)

**Status:** ✅ Complete — Build passed, tests passing

**Issue:** The default demo query "How is Apex Grill performing in the Southwest?" failed silently. Backend returned empty dataset.

**Root Cause:** The lightweight performance lookup path (keyword fast-path in RetailOpsRouter) treated `quarter` and `period` query parameters as mandatory in validation, but the router was omitting them for default queries. Contract mismatch: DepletionStatsTool accepts optional parameters, but the endpoint validation required them.

**Fix (src/RetailPulse.Api/):**
- **Tools/DepletionStatsTool.cs:** Made `quarter` and `period` truly optional (not just nullable) in the parameter schema. When omitted, the tool returns all historical data for the brand/region pair.
- **Added regression test:** `DemandForecastAgentTests.PerformanceLookup_WithoutQuarterOrPeriod_ReturnsValidData` ensures future changes don't reintroduce the requirement.
- **Verified:** Apex Grill + Southwest now returns valid depletion stats (last 52 weeks).

**Build & Tests:** 0 errors. 1,886+ tests pass (including new regression test).

**Decision:** No new decisions created — this was a bug fix, not an architecture change. The four-layer 429 defense and MaxIterations restoration decisions from earlier sessions are the guiding changes.

**Learnings:**
1. **Endpoint default alignment:** Lightweight keyword-matched routes skip the full agent pipeline and go directly to data tools. Their parameter defaults must exactly match the tool definitions in both the proxy layer (API) and the MCP layer (McpServer).
2. **Test-driven discovery:** This failure was surfaced by running the default demo prompts (DemoReadinessTests.cs) from the UI — a pattern Target (Tester) established in Sprint 1.1.

## Recent Work (2026-05-16)

### 2026-05-16 — Fixed 504 timeout demo blocker

**Status:** ✅ Complete — Pushed to main

**Issue:** Executive demo broken by 504 timeouts on default-question buttons.

**Root Cause:** Timeout math was internally inconsistent. `MaxIterations=2 × NetworkTimeout=90s = 180s` exceeded the request-level timeout of `150s`, guaranteeing second-iteration cancellation. Azure SDK retry policy compounded the problem.

**Fix (src/RetailPulse.Api/):**
- **Program.cs:**
  - `NetworkTimeout`: 90s → 30s (single LLM call ceiling)
  - `MaximumIterationsPerRequest`: 2 → 1 (single round-trip)
  - `ClientRetryPolicy(maxRetries: 0)` (disable Azure SDK retries)
- **Endpoints/ChatEndpoints.cs:**
  - Request timeout: 150s → 60s (both `/api/chat` and `/api/chat/stream`)

**Build:** 0 errors. Tests: 1,860 pass.

**Decision:** Documented in `.squad/decisions.md` as "Aggressive fast-fail timeouts for chat endpoints (2026-05-16)"

**Team Notifications:**
- Chick (Frontend): Update `DEFAULT_TIMEOUT_MS` to ~90s in `src/RetailPulse.Web/src/services/api.ts`
- Target (Tests): Update timeout assertions to expect 60s, not 150s
- Kroger (Architecture): Establish endpoint-specific timeout override pattern for future multi-iteration agents

## Recent Work (2026-05-15)

## Learnings

### 2026-05-16 — Four-layer 429 defense architecture

1. **Dual-call pattern demands quota headroom:** Router + agent through one APIM gateway means the effective TPM needed is 2× what a single-call system requires. 20K TPM with ~5500 tokens per query pair means ~3.6 queries/minute max — insufficient for demo scenarios. 80K gives room for parallel users.

2. **Router classification is highly cacheable:** Intent classification for the same query is deterministic within a session. A 5-minute IMemoryCache TTL eliminates all repeat-query LLM calls. SHA256 hash of normalized message → cache key works well (same pattern as InMemoryResponseCache).

3. **Keyed services for model separation:** DI keyed services (`AddKeyedSingleton<IChatClient>("router", ...)`) cleanly separate the router's lighter model from the main agent model. Backward-compatible: if config key is empty, register the same instance under both keys.

4. **Regex keyword expansion reduces LLM dependency:** "How is Apex Grill performing in the Southwest?" was the #1 demo query hitting the LLM router. A simple `BrandPerformingRegex` (excluding portfolio patterns) routes it instantly. Every keyword match = one fewer LLM call = ~500 tokens saved from the quota.

### 2026-05-16 — Rate-limit (429) error handling at endpoint level

1. **Two-layer 429 defense needed:** `AgentExecutionPipeline` catches `ClientResultException(429)` during agent execution, but the router classification call in `ChatEndpoints` is OUTSIDE that try/catch. A 429 during routing crashes to the generic `Exception` handler (503) or the debugger. Always add `ClientResultException` catches at the endpoint level too.

2. **Strip routing metadata from error responses:** When the pipeline returns an error response (⏳/⚠️ prefix), the endpoint was still attaching `RoutingInfo` (showing "78% confidence"). This is misleading — confidence about intent classification is irrelevant when the answer is an error message. Detect error replies by prefix and null out `Routing`.

3. **`ClientResultException.Status` maps to HTTP status codes:** For non-429 errors (500, 503 from APIM), forward the status code rather than always returning 503. This gives the frontend more signal for retry logic.

### 2026-05-16 — Timeout math must be internally consistent

1. **Timeout budget arithmetic:** MaxIterations × NetworkTimeout must fit within the request-level timeout. The old config (2 × 90s = 180s vs 150s request cap) guaranteed the second iteration would always be cancelled by the request CTS. Always validate the math: `MaxIterations * NetworkTimeout < RequestTimeout`.

2. **Disable Azure SDK retries for interactive endpoints:** The SDK's default retry policy retries timed-out HTTP calls, doubling user wait time. For interactive chat endpoints, set `ClientRetryPolicy(maxRetries: 0)` — let the user retry manually rather than silently doubling latency behind the scenes.

3. **CacheWarmingService is safe:** It only does a write/read/delete health probe with AgentId `"startup-probe"`. The `cache-warming` guard in ChatEndpoints (line 74) is a defensive check that's no longer needed since the service was refactored, but harmless to keep.

4. **AgentExecutionPipeline timeout handling is solid:** `HandleTimeoutError` correctly sets `error.type=timeout` telemetry tags, records metrics, and returns a user-friendly message. Both `TaskCanceledException` (SDK internal) and `OperationCanceledException` (request CTS) paths are covered with distinct `when` guards.

### 2026-05-15 — Response sanitization and telemetry accuracy

1. **response.Text leakage:** Microsoft.Extensions.AI's `ChatResponse.Text` returns raw text content from the final assistant message. If the model hallucinates function call syntax as text (e.g., `to=functions.ToolName` with garbled characters), it passes straight through to the user. Always sanitize before returning.

2. **Tool call timing was fabricated:** The old `perToolMs = thoughtDurationMs / toolCount` divided total time evenly across tools — producing identical fake numbers (e.g., both tools showing exactly 34273ms). The SDK's auto-invocation pattern (`GetResponseAsync` with tools) doesn't expose individual tool durations. Report 0ms for individual tool_call spans and rely on the parent "thought" span for real wall-clock.

3. **Routing confidence ≠ answer confidence:** `RoutingInfo.Confidence` (the "84%" badge) is the router LLM's self-reported confidence about intent classification, not data quality. It's derived from the JSON response of the classification prompt. Keyword fast-path matches get a fixed 0.95. LLM-classified intents get whatever the model reports. Frontend should clarify this distinction.

4. **68s with 2 tool calls:** Single `GetResponseAsync` handles the full loop (model → request tools → SDK invokes tools → feeds results back → model synthesizes). No parallelization within the SDK pattern. Improvement requires manual tool orchestration (call model, parse tool requests, invoke tools in parallel, feed back results).

### 2026-05-18T09:55:41.150-04:00 — Keep backend tool defaults aligned end-to-end

1. **Single-brand performance queries route to GeneralAgent:** `RetailOpsRouter.BrandPerformingRegex()` intentionally sends prompts like "How is Apex Grill performing in the Southwest?" down the lightweight GeneralAgent path, where `DepletionStatsTool` is the primary structured lookup (`src\RetailPulse.Api\Agents\Routing\RetailOpsRouter.cs`, `src\RetailPulse.Api\Program.cs`).

2. **Brand and region matching are already flexible:** `RetailPulseDb.GetDepletionStats` uses SQLite `LIKE` queries against `COLLATE NOCASE` columns, so tenant brands/regions such as `Apex Grill` and `Southwest` work with partial and case-insensitive input. The failure mode was not missing simulated data; it was a contract mismatch in the API proxy tool (`src\RetailPulse.McpServer\Data\RetailPulseDb.cs`).

3. **Proxy-tool defaults must match MCP/REST defaults:** `src\RetailPulse.Api\Tools\DepletionStatsTool.cs` must keep its optional parameters aligned with `src\RetailPulse.McpServer\Tools\GetDepletionStatsTool.cs` and `src\RetailPulse.McpServer\Program.cs`. If the proxy marks a parameter required while the MCP tool treats it as optional, model tool invocation can fail before the HTTP call is ever made.

## 2026-05-15 — Demo blocker: chat endpoint infinite spin

**Symptom:** Clicking default-question buttons in the UI spun forever. Backend never returned, so the FE spinner never cleared.

**Root cause:** The /api/chat pipeline (guardrails -> cache -> RAG -> router classify -> specialist HandleAsync) has *two* sequential IChatClient calls (router + agent) and zero overall request timeout. AzureOpenAIClientOptions.NetworkTimeout was set to 3 minutes per attempt, and Azure SDK retries can stack on top. Worst-case a single hung AI Gateway call could keep the request open well past any user's patience -> 'infinite spin'.

**Fix (src/RetailPulse.Api):**
- Endpoints/ChatEndpoints.cs: Wrapped both /api/chat and /api/chat/stream in a linked CancellationTokenSource with CancelAfter(75s). Renamed the lambda parameter to clientCt so the existing 'ct' in the body now points at the linked token, propagating timeout through router.RouteAsync, specialist.HandleAsync, RAG, memory, guardrails and the cache. Added explicit catch arms that distinguish client-abort (499) from request-timeout (504) from generic failure (503). Stream endpoint also fires a streaming:error SignalR event on timeout so any connected client can stop its own spinner.
- Program.cs: Reduced AzureOpenAIClient NetworkTimeout from 3 min to 60 s — interactive UI doesn't tolerate 3-minute hangs and 60s still covers slow tool-using completions.

**Why 75s for the request and 60s for the network:** Network timeout fires first on a single hung HTTP attempt, the Azure SDK can attempt a fast retry, and the request-level cap then ends the whole pipeline cleanly with a 504 instead of letting retries stack.

**Build:** dotnet build RetailPulse.slnx -> 0 errors, 0 warnings.


## 2026-05-15 — Backend Stability & Performance Session

### Session 1: Fix "malformed response payload" error for Apex Grill query
**Status:** ✅ Complete — Commit 995d1d3

**Issue:** Apex Grill query returned HTTP 500 with malformed response payload.

**Root Cause:** Frontend RoutingInfo type guard expected field names gentId/intentCategory, but backend DTO used gentKey/intent. Type mismatch caused deserialization failure in the payload parser.

**Solution:** 
- Standardized field names across 6 files (backend DTO + frontend types)
- Updated RoutingInfo contract to use consistent naming
- Added integration tests to catch payload shape mismatches

**Validation:**
- Build clean
- 1815 backend tests pass
- 249 frontend tests pass

---

### Session 2: Fix 60s+ query time for Apex Grill
**Status:** ✅ Complete — Commit 6fa84be

**Issue:** Apex Grill queries (single brand) took 60+ seconds even with all councils. Unacceptable for demo.

**Root Cause:** PerformingRegex pattern match was evaluated on every brand query, triggering the Consensus Council even for single-brand requests that don't need consensus logic.

**Solution:**
- Narrowed PerformingRegex to only match portfolio-level queries (contains 2+ brands)
- Single-brand queries now skip Consensus Council entirely
- Latency reduced to <2s for single-brand path

**Validation:**
- Apex Grill single-brand queries now return in <2s
- Portfolio queries unaffected
- No breaking changes to other retail operations

**Decision Created:**
- "75s request-level timeout on chat endpoints" (2026-05-15) — merged to decisions.md
  - /api/chat and /api/chat/stream bounded by 75s CancellationTokenSource
  - Azure OpenAI NetworkTimeout lowered from 3 min to 60s
  - Implications documented for Chick (FE error handling) and Target (test expectations)

---

**Archive:** See costco/history-archive.md for prior sessions.