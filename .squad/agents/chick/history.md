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
- 2026-05-13T14:25:10-04:00 — Council visualization components live in `src/RetailPulse.Web/src/components/council/` — CouncilPanel (orchestrator), CouncilVoting (vote cards row), VoteCard (individual agent vote), CouncilVerdict (synthesis result), DisagreementHighlight (split decision detail), CouncilHistory (past sessions). All export from barrel `index.ts`.
- 2026-05-13T14:25:10-04:00 — Council types (`HealthRating`, `CouncilAgentVote`, `CouncilDisagreement`, `CouncilVerdict`, `CouncilSession`, `CouncilConveneRequest`, `CouncilConveneResponse`) are defined in `types/index.ts`. API calls via `services/councilApi.ts` (conveneCouncil POST, fetchCouncilHistory GET).
- 2026-05-13T14:25:10-04:00 — `COUNCIL_COLORS` and `COUNCIL_DOMAIN_CONFIG` in `agentRouting.ts` define health indicator colors (green=#0f7b0f, yellow=#d4a017, red=#d32f2f) and domain icon/label mappings. These vivid colors are chosen for dark background visibility.
- 2026-05-13T14:25:10-04:00 — Dashboard `activeView` union now includes `'council'` alongside chat/promo/competitive/knowledge. The Health Council button uses `HeartPulse24Regular` icon from Fluent UI.
- 2026-05-13T14:53:43-04:00 — Streaming components live in `src/RetailPulse.Web/src/components/streaming/` — StreamingMessage (progressive token display with typing cursor, markdown rendering) and CacheIndicator (⚡ Cached pill badge with tooltip and time-saved display). All export from barrel `index.ts`.
- 2026-05-13T14:53:43-04:00 — Guardrails components live in `src/RetailPulse.Web/src/components/guardrails/` — BlockedRequestMessage (friendly amber shield UI), GuardrailsDashboard (stats cards + trend chart + filtered list), PiiRedactionBadge (inline [REDACTED:type] badges with renderWithRedactions parser), GuardrailsConfig (admin toggle/pattern panel). All export from barrel `index.ts`.
- 2026-05-13T14:53:43-04:00 — Streaming/guardrails types (`StreamingToken`, `CacheInfo`, `GuardrailDetectionType`, `BlockedRequest`, `GuardrailsStats`, `GuardrailsConfigData`, `PiiRedactionType`) are defined in `types/index.ts`. Guardrails API service in `services/guardrailsApi.ts`.
- 2026-05-13T15:27:25-04:00 — Collaborative Adaptive Cards components live in `src/RetailPulse.Web/src/components/cards/` — AdaptiveCardPanel (main container with fetch + SignalR), VotingCard (multi-user voting with tally bar, split-vote detection, escalation), DrillDownCard (expandable detail with breadcrumbs), CardComments (inline thread), CardLifecycleIndicator (horizontal stepper), EscalationBanner (amber notification). All export from barrel `index.ts`.
- 2026-05-13T15:27:25-04:00 — Observability components live in `src/RetailPulse.Web/src/components/observability/` — ObservabilityPanel (tab container), CostDashboard (period selector + metric cards + Recharts trend/bar charts + tools table), AuditLogViewer (filterable paginated table with expandable rows), ConversationExport (session list + MD/JSON export + preview modal). All export from barrel `index.ts`.
- 2026-05-13T15:27:25-04:00 — Card types (`CardType`, `CardLifecycleState`, `VoteChoice`, `CardComment`, `UserVote`, `AdaptiveCard`, `DrillDownLevel`) and Observability types (`ObservabilityPeriod`, `CostSummary`, `CostTrendPoint`, `AgentCostBreakdown`, `ToolUsageEntry`, `CostDashboardData`, `AuditLogEntry`, `AuditLogFilters`, `AuditLogPage`, `ExportSession`, `ExportPreview`) defined in `types/index.ts`.
- 2026-05-13T15:27:25-04:00 — `CARD_COLORS`, `CARD_TYPE_CONFIG`, `CARD_LIFECYCLE_CONFIG` and `OBSERVABILITY_COLORS` constants in `agentRouting.ts`. Card colors use blue/amber/green/gray for lifecycle states. Observability colors use cyan (#06b6d4) as primary.
- 2026-05-13T15:27:25-04:00 — API services: `services/cardsApi.ts` (fetchActiveCards, submitVote, addComment) and `services/observabilityApi.ts` (fetchCostDashboard, fetchAuditLog, fetchExportSessions, fetchExportPreview, exportSession with Blob download).
- 2026-05-13T15:27:25-04:00 — Dashboard `activeView` union now includes `'cards'` and `'observability'`. Cards button uses `CardUi24Regular` icon (blue), Observability uses `Eye24Regular` icon (cyan).
- 2026-05-13T14:53:43-04:00 — ChatPanel now supports streaming mode (StreamingMessage component), cache indicators (CacheIndicator inline), and blocked request rendering (BlockedRequestMessage). The loading state shows "Thinking..." before tokens arrive.
- 2026-05-13T14:53:43-04:00 — Dashboard `activeView` union now includes `'security'` for the guardrails admin view. The Security button uses `ShieldCheckmark24Regular` icon with amber (#f59e0b) accent.
- 2026-05-13T14:53:43-04:00 — Fluent UI `makeStyles` does not support `borderColor` or `resize` directly as string properties — use `as unknown as undefined` cast workaround for non-standard CSS properties.
- 2026-05-13T16:16:48-04:00 — Store operations components live in `src/RetailPulse.Web/src/components/stores/` — StoreHeatmap (region-grouped performance grid), PlanogramDiagram (shelf layout with eye-level highlights and comparison mode), StockoutAlert (urgency-sorted risk cards), StorePerformanceTable (sortable ranked store list). All export from barrel `index.ts`.
- 2026-05-13T16:16:48-04:00 — Margin/escalation components live in `src/RetailPulse.Web/src/components/margin/` — MarginWaterfall (Recharts stacked bar waterfall with comparison overlay), MarginDrivers (horizontal impact bars with trend arrows), EscalationPath (collapsible vertical timeline with pulse animation). All export from barrel `index.ts`.
- 2026-05-13T16:16:48-04:00 — Scorecard/explainability components live in `src/RetailPulse.Web/src/components/scorecard/` — PortfolioScorecard (brand grid with SVG score rings and skeleton loading), BrandScoreCard (RadarChart detail with dimension progress bars), ExplanationPanel (slide-out with staggered step reveal animation), WhyButton (reusable purple "?" trigger). All export from barrel `index.ts`.
- 2026-05-13T16:16:48-04:00 — Phase 4 types: `StorePerformance`, `PlanogramSlot`, `PlanogramLayout`, `StockoutRisk`, `MarginWaterfallStep`, `MarginDriver`, `EscalationStep`, `BrandScore`, `ExplanationStep`, `ExplanationData` defined in `types/index.ts`. Color constants: `STORE_COLORS`, `MARGIN_COLORS`, `SCORECARD_COLORS` in `agentRouting.ts`.
- 2026-05-13T16:16:48-04:00 — Dashboard `activeView` union now includes `'stores'`, `'financials'`, and `'portfolio'`. Stores uses `Building24Regular` icon (green), Financials uses `Money24Regular` (blue), Portfolio uses `Star24Regular` (purple).
- 2026-05-13T16:16:48-04:00 — API services: `services/storeApi.ts` (fetchStorePerformance, fetchPlanogram, fetchStockoutRisks), `services/marginApi.ts` (fetchMarginWaterfall, fetchMarginDrivers, fetchEscalationPath), `services/scorecardApi.ts` (fetchPortfolioScorecard, fetchBrandScore, fetchExplanation).
- 2026-05-13T16:16:48-04:00 — Recharts waterfall chart pattern: compute running totals, use stacked bars with transparent `base` bar + colored `value` bar. For comparison overlay, render second bar set at 40% opacity.
- 2026-05-14T11:00:26-04:00 — `StoreDetailDialog` component (`src/RetailPulse.Web/src/components/stores/StoreDetailDialog.tsx`) is a Fluent UI v9 Dialog that displays store details (name, region, revenue, target, performance, issues, recommendations). Opened by clicking store names in StoreHeatmap or StorePerformanceTable. State lives in Dashboard.tsx (`selectedStore`).
- 2026-05-14T11:00:26-04:00 — Planogram section removed from Stores page rendering (PlanogramDiagram.tsx file kept for future use). Layout order: Heatmap → Performance Table → Stockout Risks. This was a Brian decision to declutter the demo flow.
- 2026-05-14T11:00:26-04:00 — StoreHeatmap uses compact cell sizing (80px min, 8px padding, 6px gap) to avoid vertical scroll in the viewport. Original 110px/14px/8px sizing was too spacious.

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

## Session Work — 2026-05-13 Sprint 2.2+2.3 Competitive Intelligence + RAG Knowledge Base UI (Complete)

**Outcome:** ✅ SUCCESS — Competitive dashboard + Knowledge Base UI built, 135 frontend tests passing (22 new), build clean

**Deliverables:**

Sprint 2.2 — Competitive Intelligence Dashboard:
- `src/components/competitive/CompetitiveDashboard.tsx` — Main container with category/region filters and 4 tabs (Overview, Pricing, Market Share, Threats). Loads data from 3 API endpoints on mount.
- `src/components/competitive/PricingGrid.tsx` — Competitor pricing table with sparkline trend charts (Recharts LineChart), price difference indicators (▲/▼ with color coding), and "our price" baseline comparison.
- `src/components/competitive/MarketShareChart.tsx` — Stacked area chart (Recharts AreaChart) showing market share trends over time. Animated gradient fills per competitor, custom tooltip.
- `src/components/competitive/ThreatCards.tsx` — Threat assessment cards sorted by severity (🔴🟡🟢). Expand/collapse detail, response plan generation with loading state, recommendation badges.
- `src/components/competitive/CompetitorProfile.tsx` — Modal competitor detail view with strengths/weaknesses/recentMoves lists and market share stat.
- `src/components/competitive/index.ts` — Barrel export.
- `src/services/competitiveApi.ts` — API service: fetchCompetitorPricing, fetchMarketShare, fetchThreats, generateResponsePlan.

Sprint 2.3 — RAG Knowledge Base UI:
- `src/components/knowledge/KnowledgeBasePanel.tsx` — Main KB management panel: document list with status badges, search input, upload integration, stats sidebar. Loads documents + stats on mount.
- `src/components/knowledge/DocumentUpload.tsx` — Drag-and-drop file upload with progress bar, title input, accepted file types (.pdf/.docx/.md/.txt). Drag-over visual feedback via inline styles (Griffel workaround).
- `src/components/knowledge/CitationBadge.tsx` — Inline citation pill: shows [N] badge, hover tooltip with source info, click to expand full citation content with relevance score.
- `src/components/knowledge/SearchResults.tsx` — KB search results display with relevance score bars, document source, snippet highlighting, expandable full content.
- `src/components/knowledge/KnowledgeStats.tsx` — KB health widget: total docs, indexed count, avg relevance, top cited doc. Horizontal bar chart (Recharts BarChart) showing citation distribution.
- `src/components/knowledge/index.ts` — Barrel export.
- `src/services/knowledgeApi.ts` — API service: fetchDocuments, searchKnowledge, uploadDocument, fetchStats, deleteDocument.

Integration:
- `src/types/index.ts` — Added CompetitorPricing, PricePoint, MarketShareEntry, CompetitiveThreat, CompetitorOverview, KBDocument, KBSearchResult, KBStats, Citation types. ThreatSeverity/ThreatRecommendation unions.
- `src/constants/agentRouting.ts` — Added COMPETITIVE_COLORS and KB_COLORS constant objects.
- `Dashboard.tsx` — Added "Competitive" (Shield24Regular) and "Knowledge Base" (Library24Regular) header buttons. Extended activeView union with 'competitive'|'knowledge'. Conditional rendering for both new views.

Tests (22 new):
- `CompetitiveDashboard.test.tsx` — 11 tests: render, loading state, overview tab, pricing/threats tab switching, error state; ThreatCards severity sorting, expand/collapse, response plan generation, severity badge colors, empty state, recommendation display.
- `KnowledgeBase.test.tsx` — 11 tests: KnowledgeBasePanel document loading/display, search, upload trigger, stats display; DocumentUpload render, file type display, title input; CitationBadge render, hover tooltip, click expand, relevance score.

**Test Status:** 135 frontend tests passing (16 files), build clean

## Session Work — 2026-05-13 Sprint 3.1+3.2 Streaming Chat + Guardrails UI (Complete)

**Outcome:** ✅ SUCCESS — Streaming chat, cache indicators, and guardrails UI built. 185 frontend tests passing (21 files), build clean.

**Deliverables:**

Sprint 3.1 — Streaming Chat UI:
- `src/components/streaming/StreamingMessage.tsx` — Progressive token display with typing cursor, animated dots "Generating..." state, progressive markdown rendering via ReactMarkdown, cursor disappears on completion.
- `src/components/streaming/CacheIndicator.tsx` — ⚡ Cached pill badge with lightning bolt animation, time-saved display (e.g. "Saved ~2.3s"), tooltip with TTL info.
- `src/components/streaming/index.ts` — Barrel export.
- `ChatPanel.tsx` updated: streaming state tracking, StreamingMessage integration for streaming responses, CacheIndicator inline on cached responses, BlockedRequestMessage for guardrail blocks, "Thinking..." state before tokens arrive.

Sprint 3.2 — Guardrails UI:
- `src/components/guardrails/BlockedRequestMessage.tsx` — Friendly amber-bordered message with 🛡️ shield icon, reason display, optional rephrasing suggestion. Accessible (role="alert").
- `src/components/guardrails/GuardrailsDashboard.tsx` — Admin stats cards (total blocked, jailbreak, PII, access), trend bar chart (blocks/hour last 24h via Recharts), recent blocked requests list (last 50), type filter chips.
- `src/components/guardrails/PiiRedactionBadge.tsx` — Inline styled badge for [REDACTED:type] markers with tooltip. `renderWithRedactions()` parser function for message content.
- `src/components/guardrails/GuardrailsConfig.tsx` — Admin configuration with toggle switches per guardrail type, blocked patterns textarea, save/reset buttons calling PUT /api/guardrails/config.
- `src/components/guardrails/index.ts` — Barrel export.
- `src/services/guardrailsApi.ts` — API service for guardrails stats, config CRUD, and reset.
- `src/types/index.ts` — Added StreamingToken, CacheInfo, GuardrailDetectionType, BlockedRequest, GuardrailsStats, GuardrailsConfigData, PiiRedactionType types.
- `Dashboard.tsx` — Added "Security" tab (ShieldCheckmark24Regular icon, amber accent) rendering GuardrailsDashboard + GuardrailsConfig.

Tests (25 new):
- `StreamingMessage.test.tsx` — 6 tests: generating state, streaming cursor, cursor removal, onComplete callback, progressive reveal, markdown rendering.
- `CacheIndicator.test.tsx` — 5 tests: hidden when not cached, badge display, time saved, no time saved, TTL tooltip.
- `BlockedRequestMessage.test.tsx` — 7 tests: shield icon, reason display, prefix text, suggestion display, no suggestion, accessibility role, amber styling.
- `GuardrailsDashboard.test.tsx` — 7 tests: loading state, stats display, stat labels, recent requests, error state, filter chips, title, trend chart.

**Test Status:** 185 frontend tests passing (21 files), build clean

## Learnings

- 2026-05-13T13:37:00-04:00 — Competitive intelligence components live in `src/components/competitive/` — CompetitiveDashboard, PricingGrid, MarketShareChart, ThreatCards, CompetitorProfile. All export from barrel `index.ts`.
- 2026-05-13T13:37:00-04:00 — Knowledge base components live in `src/components/knowledge/` — KnowledgeBasePanel, DocumentUpload, CitationBadge, SearchResults, KnowledgeStats. All export from barrel `index.ts`.
- 2026-05-13T13:37:00-04:00 — Griffel `makeStyles` does NOT accept `borderColor` shorthand (TS2322 error). Use inline `style` prop for dynamic border colors, or use longhand `borderTopColor`/etc. Same issue applies to `backgroundColor` in some contexts. Pattern: keep `transform`/`transition` in makeStyles, put color overrides in inline style.
- 2026-05-13T13:37:00-04:00 — Recharts v3 Tooltip `formatter` expects `(value: ValueType | undefined, ...)` — cast first arg with `Number(v)` before formatting. Pattern: `formatter={(v) => [\`${Number(v).toFixed(1)}%\`]}`.
- 2026-05-13T13:37:00-04:00 — Testing-library "Found multiple elements" errors are common when tab text appears both in the tab bar and in rendered content. Use `getAllByText()[0]` to click the first (tab) instance.


## Session Work — 2026-05-14 Memory Panel 404 Fix

### Fix: Memory Panel 404 Error Graceful Degradation
- Fixed MemoryPanel component failing when memory API returns 404
- Updated memoryApi.ts to return empty array on 404 instead of throwing error
- Panel now displays graceful empty state instead of error banner
- Validation: Build + 249 tests pass

- 2026-05-14T10:09:00-04:00 — `CollapsibleSection` component at `src/RetailPulse.Web/src/components/CollapsibleSection.tsx` provides reusable accordion/twist UI for the telemetry drawer. Uses CSS `max-height` transition and a rotated ▶ chevron. Default state: collapsed.
- 2026-05-14T10:09:00-04:00 — Empty trace filtering in TraceDashboard: traces with 0 spans or (0 duration AND 0 tokens) are excluded from display and aggregates to prevent "Invalid Date" / "Unknown intent" noise.

## Session Work — 2026-05-14 CollapsibleSection Fluent Accordion Refactor

### Refactor: CollapsibleSection to Fluent UI v2 Accordion
- Replaced hand-rolled accordion logic with Fluent UI v9 `Accordion`/`AccordionItem`/`AccordionHeader`/`AccordionPanel`
- Removed: `▶` text chevron, `maxHeight: 5000px` CSS hack, manual ARIA, manual keyboard handling, `useState`, custom CSS variables
- Fluent AccordionHeader handles chevron icon, keyboard nav, and ARIA natively
- Used `tokens.colorNeutralForeground2` for header text color (respects teamsDarkTheme)
- Preserved existing API (`title`, `defaultExpanded`, `children`) — zero changes needed in Dashboard.tsx
- Validation: Build passes, 249/249 tests pass (30 test files)

## Learnings

- 2026-05-14T10:25:37-04:00 — `CollapsibleSection` now wraps Fluent UI v9 `Accordion`/`AccordionItem`/`AccordionHeader`/`AccordionPanel`. No hand-rolled expand/collapse logic. The `collapsible` prop enables independent toggle, `defaultOpenItems` controls initial state.
- 2026-05-14T10:25:37-04:00 — Prefer Fluent UI primitives over hand-rolled interactive patterns. AccordionHeader handles chevron, keyboard, and ARIA automatically — no need for manual `role="button"`, `tabIndex`, `onKeyDown`, or `aria-expanded`.
- 2026-05-14T10:25:37-04:00 — Use `tokens.colorNeutralForeground2` from `@fluentui/react-components` for subtle text in dark theme instead of custom CSS variables like `var(--color-text-subtle)`.

## Session Work — 2026-05-14 Fluent UI v9 Compliance Audit

### Audit: Full component scan for Fluent UI v9 anti-patterns
Scanned all components under `src/RetailPulse.Web/src/components/` for violations of Brian's Fluent UI v9 directive.

### Fixed — Raw `<select>/<option>` → Fluent `Dropdown`/`Option`:
- `competitive/CompetitiveDashboard.tsx` — 2 selects (category, region filters)
- `observability/AuditLogViewer.tsx` — 2 selects (agent, action type filters)
- `promo/PromoTaskModule.tsx` — 2 selects (brand, region) + updated test `fillForm` helper for Fluent Dropdown interaction

### Fixed — Raw `<table>` → Fluent `Table`/`TableHeader`/`TableRow`/`TableCell`:
- `ApprovalHistory.tsx` — approval history table
- `observability/AuditLogViewer.tsx` — audit log table
- `stores/StorePerformanceTable.tsx` — store performance table with sortable headers

### Fixed — `var(--color-*)` CSS variables → Fluent tokens:
- `ApprovalHistory.tsx` — 13 occurrences → `tokens.colorNeutral*`
- `competitive/CompetitiveDashboard.tsx` — 8 occurrences
- `observability/AuditLogViewer.tsx` — 11 occurrences
- `stores/StorePerformanceTable.tsx` — 5 occurrences

### Fixed — Other anti-patterns:
- `CompetitiveDashboard.tsx` — loading text `⏳` → Fluent `Spinner`
- `StorePerformanceTable.tsx` — `▲`/`▼` sort glyphs → Fluent `ArrowUp16Filled`/`ArrowDown16Filled` icons
- `StorePerformanceTable.tsx` — custom issues badge pill → Fluent `Badge`
- `AuditLogViewer.tsx` — raw `<button>` pagination → Fluent `Button`
- `AuditLogViewer.tsx` — raw `<input type="text">` search → Fluent `Input`

### Validation: Build passes, 249/249 tests pass (30 test files)

### Remaining tech debt (not fixed — too large/risky for this pass):
- `cards/DrillDownCard.tsx` — custom breadcrumb, expander chevron, 8 CSS variable instances
- `margin/EscalationPath.tsx` — `▾`/`▸` expand-collapse, custom badge for levels
- `AgentRoutingIndicator.tsx` — custom pill/badge, rotated chevron, 5 CSS variable instances
- `cards/CardLifecycleIndicator.tsx`, `guardrails/PiiRedactionBadge.tsx`, `knowledge/CitationBadge.tsx` — custom badge spans
- `promo/PromoTaskModule.tsx` — `var(--color-surface-alt)` still present (1 instance)
- Various files still use hardcoded hex colors (e.g., `#22c55e`, `#ef4444`) — these are intentional brand/semantic colors, not theme violations

## Learnings

- 2026-05-14T10:25:37-04:00 — Fluent UI v9 `Dropdown` requires `value` to always be a string (never `undefined`) to avoid "uncontrolled to controlled" React warnings. Use `value={state || ''}` not `value={state || undefined}`.
- 2026-05-14T10:25:37-04:00 — When replacing `<select>` with Fluent `Dropdown`, tests that used `fireEvent.change(element, { target: { value } })` must be rewritten to use `userEvent.click(trigger)` then `userEvent.click(optionText)`.
- 2026-05-14T10:25:37-04:00 — Fluent `Table` from `@fluentui/react-components` uses `Table`, `TableHeader`, `TableHeaderCell`, `TableBody`, `TableRow`, `TableCell` — no `<thead>`/`<tbody>` wrappers needed, but raw `<tr>`/`<td>` can still be mixed in for expanded detail rows.
- 2026-05-14T10:25:37-04:00 — Common Fluent token mappings for dark theme: `var(--color-text)` → `tokens.colorNeutralForeground1`, `var(--color-text-muted)` → `tokens.colorNeutralForeground3`, `var(--color-border)` → `tokens.colorNeutralStroke1`, `var(--color-surface)` → `tokens.colorNeutralBackground2`, `var(--color-bg)` → `tokens.colorNeutralBackground1`.


## 2026-05-14 — Stores Page UX Overhaul (b6d69d9)

- Compacted heatmap: cell min-width 110px → 80px, padding 14px → 8px, gap 8px → 6px
- Planogram section removed from render; component/tests kept for future reuse
- Store click opens Fluent UI v9 Dialog showing store details
- Layout reordered: Heatmap → Performance Table → Stockout Risks
- All 249 tests passing, build clean