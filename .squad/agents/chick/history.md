# Chick — History

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
