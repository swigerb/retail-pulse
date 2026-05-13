# Chick — History

## 2026-04-30 — Team Initialization

- **Project:** Retail Pulse — a generic pro-code agentic demo for retail & consumer goods organizations (grocers, QSRs, big box retail)
- **Stack:** .NET 10, C#, Aspire (host + OTel, non-containerized), React/Vite/TypeScript, Azure API Management, AI Gateway pattern
- **Owner:** Brian Swiger
- **Context:** Built on Patron Pulse but updated to be generic with tenant configuration, extra organization examples, and corrected diagrams

## Session Work — 2026-05-04 Telemetry Accuracy Session

### Fix: Chart X-Axis Label Overlap (Commit a53657f)
- Fixed x-axis "Brand" label overlapping legend in ChartRenderer.tsx
- Added bottom margin, adjusted label offset, legend padding
- Validation: Build + 12 tests pass

### Fix: Header Buttons Slide on Telemetry Open (Commit 9de13cd)
- Dashboard.tsx header buttons now follow same `margin-right` transition as chat container
- Prevents button overlap when telemetry panel opens
- Validation: Build + 12 tests pass

## Learnings

- 2026-05-04T10:20:27.091-04:00 — `src/RetailPulse.Web/src/components/Dashboard.tsx` owns the telemetry drawer layout, and header action buttons should follow the same `margin-right` transition pattern as `chatContainer` so top-right controls stay visible when the drawer opens.
- 2026-05-04T10:20:27.091-04:00 — Frontend verification for this app uses `cd src/RetailPulse.Web && npm run build` and `cd src/RetailPulse.Web && npx vitest run`.
- 2026-05-04T10:32:17.680-04:00 — `src/RetailPulse.Web/src/components/ChartRenderer.tsx` uses shared chart spacing constants so line and bar charts keep x-axis titles clear of bottom legends via extra bottom margin, a neutral x-axis label offset, and legend top padding.
- 2026-05-04T14:53:22Z — Chart responsiveness improved; dashboard header stability confirmed.
- 2026-05-05T13:21:37.378-04:00 — Official brand assets should be mirrored in `docs\` for README usage and `src\RetailPulse.Web\public\` for runtime usage so docs and UI stay visually aligned.
- 2026-05-05T13:21:37.378-04:00 — `src\RetailPulse.Web\src\components\BrandLogo.tsx` now renders the shipped `/retail-pulse-logo.jpg` asset directly, so future brand updates should replace the public image rather than rebuilding a synthetic wordmark in JSX.
- 2026-05-05T15:50:04.362-04:00 — Keep `src\RetailPulse.Web\src\components\BrandLogo.tsx` as the compact RP gradient mark for general UI chrome, and reserve the full `/retail-pulse-logo.jpg` asset for the ChatPanel welcome state hero treatment.
- 2026-05-05T16:40:32.229-04:00 — `src\RetailPulse.Web\src\components\ChatPanel.tsx` welcome hero can swap to alternate shipped assets in `src\RetailPulse.Web\public\`; keep legacy logos alongside new variants instead of replacing existing files when the UI needs both.
- 2026-05-13T10:41:16-04:00 — Agent routing constants (colors, emojis, labels per intent category) live in `src\RetailPulse.Web\src\constants\agentRouting.ts` — use these everywhere for consistency across the routing indicator, routing panel, and any future routing-related UI.
- 2026-05-13T10:41:16-04:00 — `RoutingInfo` and `IntentCategory` types are defined in `src\RetailPulse.Web\src\types\index.ts`. The `ChatResponse.routing` field is optional for backward compatibility with responses that don't include routing metadata.
- 2026-05-13T10:41:16-04:00 — `AgentRoutingIndicator` is a subtle color-coded pill rendered per-message in ChatPanel; `AgentRoutingPanel` is a prominent statistics widget rendered in the telemetry drawer. The indicator is subtle in chat but the panel is prominent in the dashboard — by design.
- 2026-05-13T10:41:16-04:00 — The `AgentSpan.type` union now includes `'routing'` as a span type, with its own color (#06b6d4 cyan) and icon (🔀) in SpanTimeline. This supports the routing decision as the first span in multi-agent traces.
- 2026-05-13T11:08:14-04:00 — Forecast visualization components live in `src/RetailPulse.Web/src/components/forecast/` — ForecastChart (main composed chart), ForecastSummary (KPI strip), DemandRiskCards (expandable risk list). All export from the barrel `index.ts`.
- 2026-05-13T11:08:14-04:00 — `ForecastData` type in `types/index.ts` defines the API contract for demand forecast responses: historical[], predicted[] (with confidence bounds), seasonality[], risks[]. ChartRenderer detects forecast data via `forecast` property on ChartSpec or via the `forecastData` prop.
- 2026-05-13T11:08:14-04:00 — Demand agent color updated to indigo (#6366f1) in `agentRouting.ts`. Forecast chart theme colors (`FORECAST_COLORS`, `SEASONAL_COLORS`) are also exported from that constants file for cross-component consistency.
- 2026-05-13T11:08:14-04:00 — Recharts v3 Tooltip `labelFormatter` expects `(label: ReactNode, ...) => ReactNode`, so always cast with `String(label)` before formatting — raw typed lambdas like `(iso: string) => ...` fail TypeScript strict mode.
- 2026-05-13T11:56:30-04:00 — Memory types (`MemoryEntry`, `MemoryContext`, `MemoryType`) and approval types (`ApprovalRequest`, `ApprovalResponse`, `ApprovalDecision`, `ApprovalUrgency`) are defined in `types/index.ts`. `ChatResponse.memoryContext` is optional for backward compat.
- 2026-05-13T11:56:30-04:00 — `MemoryIndicator` is a subtle violet-themed chip rendered per-message in chat (below routing pill). `MemoryPanel` is a full dashboard widget in the telemetry drawer with search, type filters, and forget/forget-all actions.
- 2026-05-13T11:56:30-04:00 — `ApprovalCard` renders inline in chat with urgency color-coding (red/yellow/green), countdown timer, and approve/reject/modify buttons. Cards transition to resolved state with decision banner when answered.
- 2026-05-13T11:56:30-04:00 — Approval SignalR events (`approval_requested`, `approval_resolved`) are listened on the same telemetry hub connection in Dashboard. The `PendingApprovals` badge in the header shows count with pulse animation on new arrivals.
- 2026-05-13T11:56:30-04:00 — API services for memory (`memoryApi.ts`) and approvals (`approvalApi.ts`) are separate from the main `api.ts` to keep concerns isolated.
- 2026-05-13T11:56:30-04:00 — React 19 + TypeScript 6 requires `useRef<T>(undefined)` instead of `useRef<T>()` for refs that don't start with a value — the empty-argument overload was removed.

## Session Work — 2026-05-13 Sprint 1.2 Demand Forecast Chart Components (Complete)

**Outcome:** ✅ SUCCESS — Forecast visualization components built, 32 frontend tests passing (11 new), build clean

**Deliverables:**
- `src/components/forecast/ForecastChart.tsx` — Main composed chart: actual line (solid blue), predicted line (dashed violet), confidence band (gradient fill), seasonal ReferenceArea annotations, "Today" divider ReferenceLine. Uses Recharts ComposedChart with smooth animations.
- `src/components/forecast/ForecastSummary.tsx` — KPI strip above chart: current avg, forecast avg, trend direction/percentage, top seasonal factor. Responsive flex layout.
- `src/components/forecast/DemandRiskCards.tsx` — Expandable risk cards below chart: severity-sorted (🔴🟡🟢), click-to-expand detail, accessible keyboard navigation.
- `src/components/forecast/index.ts` — Barrel export for all forecast components.
- `src/types/index.ts` — Added `ForecastData` interface matching backend API contract.
- `src/constants/agentRouting.ts` — Added `FORECAST_COLORS` and `SEASONAL_COLORS` constants; updated demand agent color to #6366f1 (indigo).
- `src/components/ChartRenderer.tsx` — Updated to detect forecast-type data (via `forecast` property or `forecastData` prop) and render ForecastChart instead of generic charts.
- `src/__tests__/ForecastChart.test.tsx` — 11 tests: ForecastChart rendering, summary KPIs, region display, risk cards, severity sorting, expand/collapse, empty states, edge cases.

**Design:**
- Dark theme optimized — all colors chosen for dark backgrounds
- Animated chart rendering (staggered: confidence band 1200ms, actual 1000ms, predicted 1400ms with 400ms delay)
- Confidence band uses vertical gradient fill (violet 18%→4% opacity)
- Seasonal annotations as subtle colored reference areas
- Bridge row connects actual→predicted lines at the transition point
- Mobile-responsive via Recharts ResponsiveContainer + flex-wrap KPI strip

**Test Status:** 32 frontend tests passing (5 files), build clean

## Session Work — 2026-05-13 Sprint 1.1 Multi-Agent Router UI (Complete)

**Outcome:** ✅ SUCCESS — Frontend routing visualization complete, 21 frontend tests passing, build clean

**Deliverables:**
- `src/constants/agentRouting.ts` — centralized agent routing constants: colors (demand=blue, promo=green, supply=orange, competitive=red, sentiment=purple, general=gray), emojis, display labels. Referenced everywhere for UI consistency.
- `src/types/index.ts` — RoutingInfo type definition with agentId, agentName, intentCategory, confidence, reasoning. Matches backend ChatResponse.routing optional field. Backward compatible (routing field null when absent).
- `src/components/chat/AgentRoutingIndicator.tsx` — subtle routing pill rendered per-message in chat. Shows agent emoji, name, confidence, color-coded by intent category.
- `src/components/telemetry/AgentRoutingPanel.tsx` — statistics widget in telemetry drawer. Displays routing decision history, confidence scores, fallback counts, intent distribution.
- `src/components/telemetry/SpanTimeline.tsx` — updated to render 'routing' span type with cyan color and 🔀 icon as the first span in traces. Supports agent routing traces.

**Integration:**
- RoutingInfo flows from backend ChatResponse through SignalR, into frontend ChatPanel, consumed by AgentRoutingIndicator
- Constants referenced in both indicator and panel for visual consistency
- SpanTimeline updated to show routing decision as the first span in multi-agent traces
- All changes are backward compatible — routing field is optional, UI gracefully degrades when routing is null

**Cross-Agent Collaboration:**
- Kroger (Architect): Provided IAgentRouter, RoutingInfo contract
- Costco (Backend): Implemented ChatResponse.routing field with RoutingInfo serialization
- Target (Tester): Verified frontend routing integration through telemetry spans

**Test Status:** 21 frontend routing tests passing, build clean

**Decision Logged:** Agent Routing UI Architecture

## Session Work — 2026-05-13 Sprint 1.3+1.4 Memory Indicator + Approval Cards (Complete)

**Outcome:** ✅ SUCCESS — Memory + Approval UI built, 60 frontend tests passing (28 new), build clean

**Deliverables:**

Sprint 1.3 — Memory UI:
- `src/components/MemoryIndicator.tsx` — Subtle violet-themed chip rendered per-message. Shows "🧠 Remembered: {summary}" with tooltip listing all memory entries by type. Non-distracting, conveys intelligence.
- `src/components/MemoryPanel.tsx` — Full dashboard panel in telemetry drawer. Groups memories by type (Conversations/Preferences/Entities), search/filter, Forget per-entry, Forget All. Shows relative timestamps and expiry info.
- `src/types/index.ts` — Added `MemoryEntry`, `MemoryContext`, `MemoryType` types. `ChatResponse.memoryContext` optional field.
- `src/services/memoryApi.ts` — API service: fetchMemories, deleteMemory, deleteAllMemories.

Sprint 1.4 — Approval UI:
- `src/components/ApprovalCard.tsx` — Inline approval card with urgency color-coding (🔴🟡🟢), countdown timer, approve/reject/modify buttons. Resolved state shows decision banner. Timer creates gentle urgency without alarm.
- `src/components/ApprovalHistory.tsx` — Compact audit trail table with search, decision filter chips. Shows action, agent, decision, who, when.
- `src/components/PendingApprovals.tsx` — Header badge with pending count + pulse animation on new arrivals.
- `src/types/index.ts` — Added `ApprovalRequest`, `ApprovalResponse`, `ApprovalDecision`, `ApprovalUrgency` types.
- `src/services/approvalApi.ts` — API service: fetchPendingApprovals, fetchApprovalHistory, respondToApproval.

Integration:
- `Dashboard.tsx` — Added PendingApprovals badge in header, MemoryPanel + ApprovalHistory in telemetry drawer. SignalR listeners for `approval_requested` and `approval_resolved` events on existing hub connection.
- `ChatPanel.tsx` — Renders MemoryIndicator per-message (after routing pill), ApprovalCard inline in chat for pending approvals. Props for approvals and onApprovalResolved.

Tests (28 new):
- `ApprovalCard.test.tsx` — 10 tests: render, urgency badges, timer, button actions, API calls, resolved/timed-out states, disabled-while-responding.
- `MemoryPanel.test.tsx` — 14 tests: render, grouping, content, relative time, expiry, tags, forget/forget-all, search, type filter, empty state, count badge.
- `MemoryIndicator.test.tsx` — 4 tests: chip render, emoji, null-on-empty, accessible label.

**Test Status:** 60 frontend tests passing (8 files), build clean

## Session Work — 2026-05-13 Sprint 1.5+1.6 Alert Cards + Trace Visualization (Complete)

**Outcome:** ✅ SUCCESS — Alert UI + Trace Visualization built, 87 frontend tests passing (27 new), build clean

**Deliverables:**

Sprint 1.5 — Alert UI:
- `src/components/alerts/AlertCard.tsx` — Severity-coded alert card (🔴 high/red, 🟡 medium/amber, 🟢 low/green). Shows title, brand/region context, % change, recommended action. Actions: View Details (expand), Snooze (1h/4h/24h/1wk dropdown), Dismiss. Auto-dismiss after 30s with progress bar if not interacted. Slide-in animation, pulse on severity badge.
- `src/components/alerts/AlertFeed.tsx` — Real-time alert stream. Groups by severity (high→medium→low), badge count, Clear All button. Empty state when no active alerts.
- `src/components/alerts/AlertHistory.tsx` — Full history table: timestamp, severity, title, brand, region, status. Searchable, filterable by severity. Shows snooze status.
- `src/components/alerts/index.ts` — Barrel export.

Sprint 1.6 — Trace Visualization:
- `src/components/traces/TraceTimeline.tsx` — Chrome DevTools-inspired waterfall chart. Color-coded bars (routing=indigo, agent=purple, tool=blue, memory=green, approval=amber). Hierarchical nesting via parent/child spans. Shows duration, token counts per step, total cost in footer. Legend bar, responsive layout.
- `src/components/traces/TraceCard.tsx` — Compact "How I got this answer" summary: "⚡ 1.8s · 3 tools · $0.003". Collapsed by default. Expands to show step-by-step breakdown + full TraceTimeline waterfall.
- `src/components/traces/TraceDashboard.tsx` — Overview panel: last 20 traces, aggregate stats (avg duration, avg cost, unique tools), tool usage distribution. Click to expand full TraceTimeline.
- `src/components/traces/index.ts` — Barrel export.

Integration:
- `src/types/index.ts` — Added `Alert`, `AlertSeverity`, `AlertStatus`, `SnoozeDuration`, `TraceSpan`, `TraceSpanType`, `Trace` types.
- `Dashboard.tsx` — SignalR listeners for `alert_fired`, `trace_started`, `span_completed`, `trace_completed`. Alert state management (dismiss, snooze, clear all). AlertFeed + AlertHistory + TraceDashboard rendered in telemetry drawer.

Tests (27 new):
- `AlertCard.test.tsx` — 11 tests: render, severity badges, context tags, change percent, expand/collapse, dismiss, snooze menu, ARIA, auto-dismiss bar.
- `TraceTimeline.test.tsx` — 10 tests: render spans, header, waterfall bars, span names, cost footer, legend, empty state, span count, token counts.
- `TraceCard.test.tsx` — 6 tests: compact summary, collapsed default, expand/collapse, step breakdown, multi-tool count, aria-expanded.

**Test Status:** 87 frontend tests passing (11 files), build clean

## Session Work — 2026-05-14 Sprint 2.1 Campaign Planner UI (Complete)

**Outcome:** ✅ SUCCESS — Promotion planning module built, 113 frontend tests passing (26 new), build clean

**Deliverables:**

- `src/components/promo/PromoTypeSelector.tsx` — Visual card picker for 5 promo types (BOGO, % Off, Bundle, Flash Sale, Loyalty Bonus). Shows emoji, description, historical ROI, selected state with green border.
- `src/components/promo/PromoRecommendation.tsx` — Evaluation result display with recommendation badge (🟢 Recommended / 🟡 Conditional / 🔴 Not Recommended), projected ROI with confidence range, timing chips, conflict warnings, expandable risk cards sorted by severity, "Submit for Approval" button for campaigns > $50K.
- `src/components/promo/PromoCalendar.tsx` — Gantt-style horizontal timeline. Region-grouped rows, status-colored bars, proposed campaign dashed bar, overlap detection (red highlighting), hover tooltips with campaign details, scrollable 6-month window (WEEK_PX=80, 26 weeks).
- `src/components/promo/ROIChart.tsx` — Recharts ComposedChart with Bar (proposed vs historical avg), ErrorBar confidence intervals, break-even ReferenceLine, color-coded green/red above/below break-even.
- `src/components/promo/PromoTaskModule.tsx` — Main orchestrating form: brand/region dropdowns, promo type selector, budget input, date pickers, target lift slider, evaluate button. Manages loading/error states, renders PromoRecommendation + ROIChart + PromoCalendar on results.
- `src/components/promo/index.ts` — Barrel export.
- `src/services/promoApi.ts` — API service: evaluatePromo (POST /api/taskmodule/promo), fetchExistingCampaigns (GET /api/campaigns), submitForApproval (POST /api/taskmodule/promo/submit).
- `src/types/index.ts` — Added PromoType, PromoRecommendationLevel, PromoRisk, PromoEvaluation, PromoCampaign, PromoFormData types.
- `src/constants/agentRouting.ts` — Added PROMO_COLORS (recommendation level colors) and PROMO_TYPE_CONFIG (emoji, description, historical ROI per type).

Integration:
- `Dashboard.tsx` — Added "Campaign Planner" toggle button (TargetArrow24Regular icon, green when active). New `activeView` state ('chat'|'promo') conditionally renders PromoTaskModule vs ChatPanel.

Tests (26 new):
- `PromoTaskModule.test.tsx` — 7 tests: render, promo type cards, disabled button, form submission + evaluation, loading state, error state, calendar rendering.
- `PromoRecommendation.test.tsx` — 11 tests: all 3 recommendation states, ROI display, timing details, risk cards sorted by severity, expand/collapse, credibility note, approval button threshold.
- `PromoCalendar.test.tsx` — 8 tests: render, campaign bars, proposed dashed bar, empty state, legend, tooltip on hover, overlap detection, region grouping.

**Test Status:** 113 frontend tests passing (14 files), build clean

## Learnings

- 2026-05-14 — Promo planning components live in `src/components/promo/` — PromoTypeSelector, PromoRecommendation, PromoCalendar, ROIChart, PromoTaskModule. All export from barrel `index.ts`.
- 2026-05-14 — Griffel `makeStyles` does NOT support `:focus` pseudo-selector (causes "Type 'string' is not assignable to type 'undefined'" error). `:hover` works fine. Workaround: skip `:focus` or use inline styles.
- 2026-05-14 — Fluent UI `Input` component wraps the native `<input>` in a way that `querySelector('input')` on the wrapper can return null in jsdom. Use `document.querySelectorAll<HTMLInputElement>('input')` and filter by `type` attribute for reliable test interaction.
- 2026-05-14 — `vi.mock` factories are hoisted to top of file and cannot reference variables defined below. Fix: declare `vi.fn()` mock variables BEFORE `vi.mock()`, then use wrapper functions in the factory: `evaluatePromo: (...args) => mockEvaluatePromo(...args)`.
- 2026-05-14 — High-spend threshold ($50,000) triggers "Submit for Approval" button in PromoRecommendation. PromoCalendar overlap detection compares all campaign pairs in same region for date overlap, storing conflicts in a Set by campaign ID.
- 2026-05-13T12:24:49-04:00 — Alert componentslive in `src/components/alerts/` — AlertCard (per-alert notification), AlertFeed (grouped stream), AlertHistory (filterable table). All export from barrel `index.ts`.
- 2026-05-13T12:24:49-04:00 — Trace visualization components live in `src/components/traces/` — TraceTimeline (waterfall chart), TraceCard (compact collapsible summary), TraceDashboard (overview panel). All export from barrel `index.ts`.
- 2026-05-13T12:24:49-04:00 — Griffel (Fluent UI's CSS-in-JS) does not support shorthand `borderColor` in `makeStyles` — must use `borderTopColor`, `borderRightColor`, `borderBottomColor`, `borderLeftColor` individually.
- 2026-05-13T12:24:49-04:00 — SignalR alert/trace events: `alert_fired` (new alert), `trace_started` (begin trace), `span_completed` (progressive span), `trace_completed` (finalize). All registered on the existing telemetry hub connection in Dashboard.
- 2026-05-13T12:24:49-04:00 — `TraceSpan` type uses `TraceSpanType` union: routing/agent/tool/memory/approval — each with its own color in the waterfall. This is separate from `AgentSpan.type` which maps to the existing telemetry system.

