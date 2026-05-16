# Chick — History

## Notification — 2026-05-16 Timeout Fix from Costco

🔔 **Action Required:** Update `DEFAULT_TIMEOUT_MS` in `src/RetailPulse.Web/src/services/api.ts` from **180s** to **~90s** to align with new backend timeout ceiling.

**Context:** Costco fixed 504 timeouts by tightening request timeout from 150s → 60s and NetworkTimeout from 90s → 30s. The frontend timeout should stay ~90s (≥ backend timeout + network overhead) to avoid premature client-side cancellation.

**See:** `.squad/decisions.md` — "Aggressive fast-fail timeouts for chat endpoints (2026-05-16)"

---

## Summary (May 2026)

**Major Accomplishments:**
- **Telemetry UI Refinements** (May 4-5) — Fixed chart rendering (x-axis label overlap, header button alignment), brand asset standardization across docs and runtime.
- **Agent Routing Visualization** (May 13) — Implemented `AgentRoutingIndicator` (per-message pills) and `AgentRoutingPanel` (telemetry dashboard widget), with centralized color/emoji constants in `agentRouting.ts`.
- **Demand Forecast Visualization** (May 13) — Built ForecastChart (Recharts ComposedChart with confidence bands, seasonal annotations), ForecastSummary KPI strip, and DemandRiskCards with severity sorting.
- **Multi-Feature Sprint Work** (May 13) — Implemented UI for Council voting, Streaming message display, Guardrails detection blocks, Collaborative Adaptive Cards, Observability cost/audit dashboards, Store operations (heatmap, planogram, stockout), Margin waterfall charts, Portfolio scorecards.
- **Fluent UI v9 Migration & Compliance** (May 14) — Refactored CollapsibleSection to native Fluent Accordion; audited entire frontend replacing 5 components' raw HTML (selects, tables, inputs) with Fluent primitives (Dropdown, Table, Button, Input, Badge, Spinner); migrated CSS variables to Fluent tokens.

**Test Coverage:** 249 tests passing. All builds clean.

**Key Learnings:** Fluent Accordion, Dropdown, Table primitives; Recharts composition patterns; token-based theming; React 19 refs; TypeScript strict mode with formatters.

---

## Session Work — 2026-05-14 StoreHeatmap Compact Layout & Demo Store Region Alignment

**Status:** ✅ COMPLETE — StoreHeatmap restructured to horizontal-row layout, demo stores expanded to 10 covering all 6 tenant regions

**Changes:**
- src/components/StoreHeatmap.tsx — Refactored from grid to compact horizontal-row layout for improved responsiveness
- src/Dashboard.tsx — Expanded demo stores from 5 to 10 instances
- **Region Coverage:**
  - Northeast: 2 stores
  - Southeast: 2 stores
  - Midwest: 2 stores
  - Southwest: 1 store (newly added)
  - West Coast: 2 stores (renamed from "West")
  - Pacific Northwest: 1 store (newly added)

**Decision:** Aligned demo store region naming to canonical tenant.yaml:
- Changed all "West" → "West Coast"
- Added Southwest and Pacific Northwest for complete regional coverage
- Merged to decisions.md (2026-05-14)

**Commit:** 618a2d8

---

## Session Work — 2026-05-15 Demo Blocker: Suggested-prompt click spins forever

**Status:** ✅ FIXED — added client-side timeout to /api/chat fetch

**Symptom:** Clicking a default suggested prompt on the initial chat screen showed the user message + `Thinking...` spinner that never resolved. No error surfaced.

**Investigation:**
- Click handler chain in ChatPanel.tsx is correct: `onClick → handleSuggestedClick → sendChatMessage → sendMessage`.
- `setLoading(true)` runs, `setMessages` adds the user bubble, then `await fetch('/api/chat', ...)` runs with no timeout.
- SignalR/telemetry is pre-joined in a useEffect so the hub is wired before the first send. That path is fine.
- Root cause: `services/api.ts` had no client-side timeout. If the backend stalls (cold start, Azure OpenAI slowness, an in-progress backend bug, etc.) the promise never settles → `finally` never runs → `setLoading(false)` never fires → infinite spinner with no error.

**Fix:**
- `services/api.ts`: introduced `DEFAULT_TIMEOUT_MS = 180_000` (matches the backend Azure OpenAI 3-min timeout from commit 02bf3f7) and a `withTimeout` helper that combines the timeout AbortController with any caller-provided signal.
- On timeout the fetch is aborted and a friendly error (`Request timed out after 180s. The server may be busy — please try again.`) is thrown, so ChatPanel's catch block renders an Error: ... bubble and `finally` clears `loading`.
- Caller-provided signals (e.g. ChatPanel's New-Chat unmount abort) are still honored.
- Added 2 vitest cases covering the timeout and caller-abort paths.

**Verification:**
- `npm run build` — clean.
- `npx vitest run` — 30 files, **251 passed**.

**Commit:** 80833e5

**Note:** There are uncommitted in-progress edits in `RetailPulse.Api/Endpoints/ChatEndpoints.cs` and `Program.cs` that look like a parallel backend investigation — left untouched (Costco's domain).

### Learnings
- Always set a client-side timeout on long-running POST endpoints. `fetch` has no built-in timeout — a hung backend = a spinner forever.
- `AbortController` composition pattern: build a wrapper that listens to the caller's signal and forwards aborts to an inner controller, plus a `setTimeout` that aborts with a `TimeoutError` DOMException.
- `vitest` fake timers + `vi.advanceTimersByTimeAsync` is the cleanest way to test timeout paths without slowing the suite.

---

## 2026-05-15 — Frontend Error Handling & Demo Blocker Session

### Session 1: Fix telemetry panel spacing and Live Spans default state
**Status:** ✅ Complete — Commit 04a7dd2

**Changes:**
- Reduced telemetry section margin from 16px to 4px (improved vertical space usage)
- Set "Live Spans" accordion expanded by default (better visibility of active tracing)

**Impact:** Telemetry drawer now shows more useful information at a glance with better visual hierarchy.

---

### Session 2: Fix demo blocker — suggested-prompt click spins forever
**Status:** ✅ Complete — Client-side timeout added

**Issue:** Clicking a default suggested prompt on the initial chat screen showed Thinking... spinner that never resolved. No error message appeared. Hard blocker for executive demo.

**Root Cause:** etch('/api/chat') had no client-side timeout. When backend stalled (even momentarily), the UI spinner ran indefinitely.

**Solution:**
- Added **3-minute (180s) client-side timeout** to all chat fetches in src/services/api.ts
- Used AbortController composition to cleanly abort stalled requests
- Friendly error message displayed when timeout occurs: "Request timed out after 180s. The server may be busy — please try again."
- UI loading state clears immediately upon timeout

**Alignment with Backend:**
- Costco's Azure OpenAI NetworkTimeout: 60s
- Costco's /api/chat request-level CancellationToken: 75s
- Frontend client timeout: 180s (gives backend ample time, prevents infinite spinner)

**Decision Created:**
- "Default 3-minute client timeout on chat fetches" (2026-05-15) — merged to decisions.md
  - Service-layer convention: all chat operations must include AbortController timeout
  - Testable: use i.useFakeTimers() + dvanceTimersByTimeAsync to verify timeout paths
  - Lockstep with backend: if Costco changes backend timeout, update DEFAULT_TIMEOUT_MS constant

**Validation:** Chat fetch timeout behavior now matches backend expectations; demo no longer hangs.

---

## 2026-05-15 — Trace Dashboard & Chat Sanitization Fix

**Status:** ✅ COMPLETE

### Issue 1: Trace Dashboard showing all zeros
**Root Cause:** Three compounding problems:
1. `span_completed` handler never accumulated `totalCostUsd` — only duration/tokens were updated
2. `trace_completed` handler didn't update `intent` or `agentName` — left as "Processing..." / "Unknown"
3. TraceDashboard checked only `s.type === 'tool'` but backend sends `'tool_call'` — tools count always showed 0
4. `meaningfulTraces` filter was too aggressive — excluded traces with spans when trace-level totals were 0

**Fixes:**
- Dashboard.tsx: `span_completed` handler now accumulates `totalCostUsd` from span data
- Dashboard.tsx: `trace_completed` handler now updates `intent` and `agentName` when available
- TraceDashboard.tsx: Tool type matching now includes both `'tool'` and `'tool_call'`
- TraceDashboard.tsx: `meaningfulTraces` filter relaxed — allows traces with spans even if trace-level totals are 0
- TraceDashboard.tsx: Aggregates now compute from span-level data as fallback when trace-level values are 0
- TraceDashboard.tsx: Row display derives agent name from agent spans, shows "Completed" vs "Processing..."
- TraceDashboard.tsx: Badge shows ✓ checkmark for completed traces instead of confusing span count
- TraceCard.tsx: Tool count also matches `'tool_call'` type

### Issue 2: Garbage tool-call text in chat
**Root Cause:** No defense-in-depth filtering on the frontend — raw `to=functions.*` patterns from backend leaked into rendered chat.

**Fix:**
- Created `src/utils/sanitizeMessage.ts` — strips `to=functions.*` prefixes, JSON payloads, and garbled Unicode tool-call artifacts
- Applied in ChatPanel.tsx to both static and streaming message rendering
- 8 unit tests covering edge cases

### Issue 3: Confidence badge
**Finding:** The "Agent ===== 84%" badge is the `AgentRoutingIndicator` component — purely data-driven from `routing.confidence`. Works correctly when backend sends valid data. No frontend fix needed.

**Validation:**
- `npm run build` — clean
- `npx vitest run` — 32 files, **263 passed**

### Learnings
- Trace data flows through two separate systems (Live Spans via `AgentSpan[]` and Trace Dashboard via `Trace[]`) — they are independently populated from different SignalR events
- Always accumulate ALL computed fields in incremental update handlers — missing `totalCostUsd` in `span_completed` was an easy oversight
- Backend span types may differ from frontend type enums — defensive matching (`'tool' || 'tool_call'`) is essential
- Frontend sanitization is defense-in-depth — backend should still fix its output, but the UI should never render raw tool-call internals