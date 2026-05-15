# Costco — History

## Recent Work (2026-05-15)

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