# Decision: Promo Campaign Planner UI Architecture

**Date:** 2026-05-14
**Author:** Chick (Frontend Dev)
**Sprint:** 2.1 — Campaign Planner UI
**Status:** Implemented

## Context

Sprint 2.1 adds a promotion planning module to Retail Pulse. The UI needs to let users configure campaign parameters, evaluate them against an AI agent, and visualize results (ROI, timeline conflicts, risk assessment).

## Decisions

### 1. PromoTaskModule as orchestrating form component
The main form component (`PromoTaskModule`) owns form state, validation, API calls, and conditional rendering of result sub-components. This mirrors the pattern where Dashboard orchestrates chat vs. telemetry views.

### 2. Dashboard toggle pattern for view switching
Added `activeView` state ('chat' | 'promo') to Dashboard with a toggle button. This keeps the Campaign Planner accessible without a router, consistent with the single-page telemetry drawer pattern. Future views can extend the union type.

### 3. Custom Gantt calendar instead of library
`PromoCalendar` is a custom CSS-based horizontal timeline rather than a charting library component. Recharts doesn't support Gantt charts natively, and adding a dedicated Gantt library would bloat the bundle. The custom approach uses absolute positioning with calculated pixel offsets (WEEK_PX=80, 26-week window).

### 4. Promo constants in agentRouting.ts
`PROMO_COLORS` and `PROMO_TYPE_CONFIG` were added to `agentRouting.ts` alongside existing agent routing constants. This keeps all agent-related color/config constants co-located. If this file grows too large, consider splitting into `agentRouting.ts` + `promoConfig.ts`.

### 5. Separate promoApi.ts service
API calls for promo evaluation/submission are in `services/promoApi.ts`, separate from `api.ts` and `approvalApi.ts`. This follows the established pattern of one service file per domain.

### 6. Promo types in types/index.ts
All promo types (PromoType, PromoEvaluation, PromoCampaign, etc.) are appended to the shared `types/index.ts`. This keeps the single source of truth for all frontend types.

## Trade-offs

- **Hardcoded brands/regions:** `TENANT_BRANDS` and `TENANT_REGIONS` are hardcoded arrays in PromoTaskModule. In production these should come from tenant config API. Acceptable for demo purposes.
- **No form library:** Uses manual React state instead of react-hook-form or similar. The form is simple enough (6 fields) that a library would be overhead.
- **Bundle size:** Adding Recharts components increases the already-large index chunk (1.27MB). Code-splitting the promo module via lazy loading would help but is deferred.

## Impact

- Frontend: 5 new components + 1 API service + types + constants
- Dashboard: New toggle button and conditional rendering
- Tests: 26 new tests (113 total)
- No backend changes required (API contract defined, not implemented)
