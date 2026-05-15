# Chick — History

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
