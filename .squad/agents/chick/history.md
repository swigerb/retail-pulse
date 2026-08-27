# Chick — History

## ✅ PR #1 Final Review — Memory Type Contract Verification (2026-06-29)

**Task:** Verify frontend memory type contract matches backend changes in `MemoryEndpoints.cs`

**Backend changes (from PR #1):**
- `ConversationSummary` now serializes as `"conversation"` (was `"fact"`)
- `EntityMention` now serializes as `"entity"` (was `"context"`)
- New field: `expiresAt` (optional DateTime)

**Frontend verification:**
- ✅ `src/RetailPulse.Web/src/types/index.ts` — `MemoryType = 'conversation' | 'preference' | 'entity'` (CORRECT)
- ✅ `MemoryEntry` interface includes `expiresAt?: string;` (CORRECT, optional, properly typed)
- ✅ `MemoryPanel.tsx` — `MEMORY_TYPE_CONFIG` references all three types correctly

---

### 2026-08-11T17:04:41Z — Session note (Scribe): P0 issue #67 resolved, PR #64 still held

Recorded by Scribe for your context whenever you're next picked up. You were not spawned
during this P0 incident (correctly held per Brian's directive) — no action was needed or
taken from you.

- **Issue #67 CLOSED, PR #73 MERGED** (squash, commit `463612d`, 2026-08-11T17:04:41Z).
  Root cause was a broken verifier script (`scripts/Verify-ApimAiGateway.ps1` calling
  nonexistent `az apim` CLI subcommands, silently masked), not an actual APIM AI Gateway
  infra/deployment gap. Live backend/policy/token-limit/diagnostics were always correct.
- **PR #64 (26-prompt production acceptance sweep) remains OPEN/HELD** — not merged, not
  touched. It is gated on a full, fresh `azd up --no-prompt` end-to-end dry run exercising
  the new postprovision (mandatory verifier gate) and predeploy (sourcelink-race fix) hooks
  under real azd orchestration, which no sandboxed agent can currently perform. This
  requires Brian or a CI runner with real `azd` access.
- Full incident detail: `.squad/log/2026-08-11T17-04-41Z-p0-apim-hardening-gate-RESOLVED.md`
  and `.squad/decisions.md` ("Issue #67 RESOLVED — PR #73 MERGED").
- ✅ `formatExpiresIn()` helper displays expiration times correctly
- ✅ No `any` type escapes in memory-related code
- ✅ Build passes: `npm run build` completed successfully with 0 TypeScript errors

**Outcome:** CONSISTENT — no changes needed. The frontend types have been correct since initial Sprint 1.3 memory feature implementation (commit 5a060c6). Backend and frontend contracts are fully aligned.

**Impact:** PR #1 is cleared for merge from frontend contract perspective.

---

## 🔔 Notification — 2026-06-04 Memory Store Fix from Costco

Backend memory persistence bug is now fixed. The /api/memory endpoint now receives and returns consistent user identity across write/read paths.

**Frontend impact:** ZERO code changes needed. The Memory Panel will now display stored memories (previously showed empty because write/read paths had divergent identity). Response shape from /api/memory now matches 	ypes/index.ts union.

**See:** .squad/decisions.md — "UserId Resolution Must Go Through UserIdentity.Resolve" (2026-06-04T12:49:32Z)

---

## 🔔 Notification — 2026-06-03 Span Type Telemetry Complete

**Frontend impact:** ZERO — no code changes needed. Trace Dashboard counters/filters will now work correctly.

**See:** .squad/decisions.md — "Span type tags on TraceSpan telemetry" and "Span type telemetry tests"

---

## 🔔 Notification — 2026-05-16 Timeout Fix from Costco

Frontend timeout: **180s client-side timeout** in src/RetailPulse.Web/src/services/api.ts. Aligns with backend timeout ceiling and prevents infinite spinner on stalled requests.

**See:** .squad/decisions.md — "Aggressive fast-fail timeouts for chat endpoints (2026-05-16)"

---

## Summary (May 2026)

**Major Accomplishments:**
- **Telemetry UI Refinements** (May 4-5) — Fixed chart rendering, brand asset standardization
- **Agent Routing Visualization** (May 13) — AgentRoutingIndicator, AgentRoutingPanel with centralized constants
- **Demand Forecast Visualization** (May 13) — ForecastChart, ForecastSummary KPI, DemandRiskCards
- **Multi-Feature Sprint Work** (May 13) — Council voting, Streaming display, Guardrails, Adaptive Cards, Observability dashboards, Store ops, Margin waterfall, Portfolio scorecards
- **Fluent UI v9 Migration** (May 14) — Refactored to native Fluent Accordion; replaced raw HTML with Fluent primitives

**Test Coverage:** 249 tests passing. All builds clean.

**Key Learnings:** Fluent Accordion/Dropdown/Table; Recharts composition; token-based theming; React 19 refs; TypeScript strict mode

---

## Archive

See **history-archive.md** for detailed session work entries from May 14-16, including:
- StoreHeatmap Compact Layout
- Demo Blocker (suggested-prompt timeout)
- Frontend Error Handling
- Trace Dashboard & Chat Sanitization
- Suppress routing metadata on errors
- Frontend Code Review Fixes

---

## ✅ Persistent Prompt Library + Deployed ACA Stack Docs (2026-08-05)

**Task:** Implement persistent, always-available prompt library and update deployment documentation.

**Changes:**
1. **Persistent `PromptLibrary` component** (`src/RetailPulse.Web/src/components/PromptLibrary.tsx`) — Fluent `Popover` with `trapFocus`, always-available panel with categorized, keyboard-accessible prompt suggestions. Works before and during conversation; selecting a prompt reuses safe send path and closes panel.

2. **Single prompt source of truth** — Moved categories and text to `src/RetailPulse.Web/src/constants/prompts.ts` (`PROMPT_CATEGORIES`, `PromptCategory`). Both welcome chips and persistent library import from same module; prevents drift.

3. **Deployment docs corrected** — README Technology Stack now lists Azure Container Apps (backend), Azure Static Web Apps (frontend), Azure Container Registry (managed-identity pulls). Corrected frontend host from "App Service (Node 20 LTS)" to "Static Web Apps". Project Structure and `docs/teams-setup.md` aligned with actual ACA deployment.

**Validation:** `npm run build` clean; 281 tests pass (249 existing + regression coverage).

**Team impact:**
- New prompts go into `constants/prompts.ts` only
- New composer affordances should reuse `PromptLibrary` rather than duplicating
- Docs now match actual `infra/` topology

---

## ✅ PR #16 Revision — Chart Bindability Parity + Inline-Chart Dedup (2026-08-05)

**Task:** Fix two concrete defects in chart-JSON-leak work (Independent revision per reviewer protocol).

**Fixes:**

1. **HIGH — Frontend `sanitizeMessage.ts` over-stripped legitimate prose JSON**
   - Previous `looksLikeChartSpec` deleted prose with empty/null/non-renderable payloads (`data:[]`, `data:null`, `data:{id:1}`)
   - Now mirrors backend `ChartSpecNormalizer` strictness via `chartHasBindableData` requiring ≥1 bindable datapoint
   - Malformed/empty JSON left visible

2. **MEDIUM — Backend dropped distinct inline charts**
   - Both pipeline paths stripped inline JSON but only promoted recovered charts when tool path produced none
   - Replaced `charts.Count == 0` gate with `MergeInlineCharts` helper
   - Deduplication uses `ChartSpecSemanticComparer` (content-based, walks Type/Title/legend/color/points)

**Validation:** Frontend `vitest` 309 passed; `npm run build`, `eslint`, `dotnet format --verify-no-changes` all clean.

**Team impact:**
- Prose with empty/placeholder chart JSON no longer silently deleted
- Real charts still correctly stripped from prose
- Reuse `ChartSpecSemanticComparer` for future chart de-duplication

---

## ✅ Observability Conversation Export Crash Fix (2026-06-30)

**Task:** Fix Observability → Conversation Export crash: `Cannot read properties of undefined (reading 'toLocaleString')`.

**Changes:**
- `fetchExportSessions` now defensively normalizes export session fields in `observabilityApi.ts`.
- `ConversationExport.tsx` guards missing `agentsUsed` and `totalTokens` before rendering.
- Added regression coverage in `ConversationExport.test.tsx` and `observabilityApi.test.ts`.

**Validation:** Frontend suite passed, 281/281 tests; build clean.

**Lesson:** `fetchExportSessions` previously lacked the defensive field normalization that its sibling observability fetchers use; that gap caused the crash.

---

## ✅ Observability Cost Dashboard Endpoint Fan-Out (2026-06-30)

**Task:** Fix blank/non-live Cost Dashboard data and idle empty states.

**Changes:** `fetchCostDashboard` now fans out to `/costs`, `/costs/agents`, `/costs/trend`, and `/costs/tools`; `CostDashboard` refreshes every 10 seconds and renders empty states for idle trend/agents/tools. Chick also fixed Publix's reviewer-found defect where all-zero trend buckets rendered a zero-line chart instead of the empty state.

**Validation:** Frontend suite passed, 285/285 tests.

**Lesson:** The Cost Dashboard broke because the frontend read `trend`, `agentBreakdown`, and `topTools` off the summary-only `/costs` response. Top Tools required the new backend tracing endpoint because `UsageEvent` lacks duration.

---

### 2026-08-11 — Incident context (on hold pending #67)

P0 incident: `azd up` against `retailpulse-demo-eus-001` reported provisioning success but the APIM AI Gateway hardening was incompletely deployed; live `Verify-ApimAiGateway.ps1` failed 9/24 invariants after a manual recovery deploy. Issue #67 filed; Kroger (IC) + Costco (remediation) spawned in worktree `retail-pulse-wt-67-apim-hardening` / branch `squad/67-apim-hardening-gate`. **My PR #64 (26-prompt production acceptance sweep) is intentionally held from merge** until Costco's remediation PR closing #67 passes the live gate at 24/24 invariants — this is a deliberate sequencing hold, not a stall on my end.

### 2026-08-11T12:45:55-04:00 — Session note (Scribe, durable state)

Recorded by Scribe as part of finalizing the production hardening session.
Final scope outcomes durably captured in `.squad/decisions.md` (Active
Decisions, entry "Production hardening session — final outcomes"):
- Issues CLOSED: #60, #61, #62, #68, #71.
- Issues OPEN (intentionally): #59 (umbrella), #63 (QA production sweep — gated
  on authenticated run), #70 (frontend deploy TS7006/TS7016 — not reproducible
  from clean `origin/main`).
- PRs MERGED: #65, #66, #69, #72. PR #64 OPEN and gated (no merge until live
  authenticated production evidence).
- Prompt-ideas acceptance contract: exactly 26 prompts = 9 chart + 17 prose,
  enforced bidirectionally by backend manifest + frontend drift tests.
- APIM live verifier on `rg-retailpulse-demo-eus-001` /
  `apim-5aldk7aotqods`: 25/25 PASS.
- Authenticated production 26-prompt sweep blocker: AADSTS65001, no
  interactive consent granted under the tenant constraints tracked by issue
  #57 (service-principal synthetic monitor is the sanctioned unblock path).
- Deployment completed successfully after PR #66 / PR #65 merges; production
  frontend is serving fresh Static Web App assets from the merged `main`.

No implementation files were modified by Scribe. No secrets, tokens,
`.auth/me` payloads, screenshots, or raw azd/deployment output committed to
tracked state.