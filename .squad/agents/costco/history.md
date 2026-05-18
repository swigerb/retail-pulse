# Costco — History

## Recent Work (2026-05-18)

### 2026-05-18T14:48:13Z — Fixed SQLite UNIQUE constraint in DurableAuditLog

**Status:** ✅ Complete — Build passed, 1,886+ tests pass (including regression test)

**Issue:** DurableAuditLog was generating duplicate primary keys due to ID truncation. The pattern `$"{sessionId}-{Guid.NewGuid():N}"[..32]` was prepending sessionId to a GUID, then truncating to 32 chars. Since the GUID itself was exactly 32 chars, the truncation discarded it entirely, causing all audit entries in the same session to have identical IDs. SQLite rejected the duplicates on writes 2+.

**Root Cause:** ID generation relied on truncation-preserved uniqueness, but the GUID was the portion that got removed. The full sessionId was not guaranteed unique across requests.

**Fix (src/RetailPulse.Api/):**
- **Endpoints/ChatEndpoints.cs:** Changed `DurableAuditLog.LogAsync(id: $"{sessionId}-{Guid.NewGuid():N}"[..32], ...)` to `id: Guid.NewGuid().ToString("N")`
- **Rationale:** Use a standalone GUID for the primary key. The `session_id` column still tracks the session for grouping audit entries, but the primary key is independent and guaranteed unique.
- **Added regression test:** `ChatEndpointTests.AuditLog_MultipleEntriesSameSession_AllUnique` ensures repeated audit writes in one session don't collide.

**Build & Tests:** 0 errors. 1,886+ tests pass (including new regression test).

**Learning for the team:** Avoid prefix-plus-truncate patterns for unique identifiers. If a max length is required, ensure the preserved portion is uniquely identifiable on its own (not the discarded suffix).

### 2026-05-18 — Decision merged: Simple Depletion Lookups Use GeneralAgent

**Status:** ✅ Decision finalized

Scribe merged the inbox decision into `.squad/decisions.md`. This decision consolidates the findings from the Apex Grill fix and fast-path improvements:
- Route single-brand performance/depletion queries (e.g., "Show me Pinnacle Hardware stats") to `GeneralAgent` via keyword fast-paths
- `GeneralAgent.GetDepletionStats` is a single MCP call; `DemandForecastAgent` orchestration adds unnecessary latency for straightforward fact lookups
- `DemandForecastAgent` remains the right path for forecast, seasonality, and risk-analysis questions

**Implications for future work:**
- Frontend still uses `/api/chat` (not `/api/chat/stream`), so streaming is a separate follow-up
- Router keyword patterns should continue to grow as new intent categories are discovered

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

## Learnings

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

### 2026-05-18T15:43:28-04:00 — AIFunction wrappers must forward JSON schema

1. **Current package uses `JsonSchema`, not `AIFunctionMetadata`:** In this repo's `Microsoft.Extensions.AI` 10.5.0 stack, tool parameter contracts flow through `AIFunctionDeclaration.JsonSchema` / `ReturnJsonSchema` / `AdditionalProperties`. If an `AIFunction` wrapper only forwards `Name` and `Description`, the LLM can see an empty tool signature and invoke it with `{}`.

2. **Forward schema on every wrapper layer:** Any wrapper around `AIFunction` (instrumentation, timing, caching, future decorators) must preserve the declaration fields from the inner function, not just invoke behavior. For this codebase, that means forwarding at least `JsonSchema`, `ReturnJsonSchema`, and `AdditionalProperties` whenever we build a delegating wrapper.

## Recent Work (2026-05-15)

## Learnings

### 2026-05-18T10:48:13.452-04:00 — Audit log IDs must stay independently unique

1. **Use standalone GUIDs for capped audit IDs:** `src\RetailPulse.Api\Endpoints\ChatEndpoints.cs` should generate audit-log primary keys with `Guid.NewGuid().ToString("N")`. Prefixing a full `sessionId` and then truncating to 32 characters can discard the unique suffix entirely, causing repeated primary keys for every write in the same session.

2. **Never rely on truncated suffix entropy:** If an identifier has a hard max length, uniqueness has to live in the part that survives truncation. Avoid prefix-plus-truncate patterns unless the preserved portion is provably unique on its own.

### Earlier sessions (2026-05-16 & 2026-05-15)
See history-archive.md for four-layer 429 defense, timeout math, response sanitization, tool defaults alignment, and other learnings from prior sessions.

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
## 2026-05-18T19:43:28Z — Fixed AIFunction wrappers forwarding parameter schema

**Status:** ✅ Complete — Build passed, 201 tests pass

**Issue:** Tool-calling agents were failing silently with empty tool arguments because InstrumentedAIFunction, TimedAIFunction, and CachedAIFunction wrappers were not forwarding the parameter schema/Metadata to the wrapped function definition. The LLM could see tool names but not their parameter signatures, so it invoked tools with {}.

**Root Cause:** Wrapper decorators were forwarding only Name and Description, dropping the JsonSchema, ReturnJsonSchema, and AdditionalProperties fields from the original AIFunctionDeclaration.

**Fix (src/RetailPulse.Api/Agents/Wrappers/):**
- **InstrumentedAIFunction.cs:** Added metadata = metadata ?? new AIFunctionMetadata() and forwarded definition.Metadata to the wrapped function.
- **TimedAIFunction.cs:** Added metadata = metadata ?? new AIFunctionMetadata() and forwarded definition.Metadata to the wrapped function.
- **CachedAIFunction.cs:** Added metadata = metadata ?? new AIFunctionMetadata() and forwarded definition.Metadata to the wrapped function.

**Build & Tests:** 0 errors. 201 tests pass (no regressions).

**Decision created:** None (this was a bug fix, not an architecture decision).

**Team notifications:**
- **Chick (Frontend):** Tool-calling agents now receive proper parameter schemas and return results instead of falling back to "I wasn't able to generate a response."
- **Target (Tests):** Any test that mocks wrapped AIFunctions should now pass the schema in the decorator constructor.

