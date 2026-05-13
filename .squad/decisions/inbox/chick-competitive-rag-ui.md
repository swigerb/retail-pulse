# Decision: Competitive Intelligence + RAG Knowledge Base UI Architecture

**Date:** 2026-05-13
**Author:** Chick (Frontend Dev)
**Sprint:** 2.2 + 2.3

## Context

Sprint 2.2 required a competitive intelligence dashboard and Sprint 2.3 required a RAG knowledge base management UI. Both needed to integrate into the existing Dashboard shell alongside chat, promo planner, and telemetry views.

## Decisions

### 1. Two new component directories (`competitive/`, `knowledge/`)

Each sprint gets its own directory under `src/components/` with a barrel `index.ts`, matching the pattern established by `forecast/`, `alerts/`, `traces/`, and `promo/`.

### 2. Separate API services per domain

Created `competitiveApi.ts` and `knowledgeApi.ts` as standalone service modules (matching `promoApi.ts`, `memoryApi.ts`, `approvalApi.ts` pattern). Each owns its endpoint paths and response typing.

### 3. Dashboard activeView extension

Extended the `activeView` union type with `'competitive' | 'knowledge'` and added header nav buttons (Shield icon, Library icon). This keeps the single-view-at-a-time pattern rather than introducing tabs or nested routing.

### 4. Inline styles for Griffel-incompatible CSS properties

Griffel's `makeStyles` doesn't accept `borderColor` shorthand or dynamic color values from constants in pseudo-selectors. Solution: keep layout/transform properties in `makeStyles`, apply dynamic colors via inline `style` prop. This is consistent with existing patterns (AlertCard uses inline `style` for `borderLeftColor`).

### 5. Color constants in agentRouting.ts

Added `COMPETITIVE_COLORS` and `KB_COLORS` to the shared constants file, following the established `FORECAST_COLORS`, `PROMO_COLORS` pattern. Components reference these for consistency.

## Impact

- Dashboard now supports 5 views: chat, promo, competitive, knowledge, (plus telemetry drawer)
- 22 new tests bring total to 135 passing
- No breaking changes to existing components
