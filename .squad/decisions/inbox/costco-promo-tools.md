# Decision: Promo Planning Tools — Data Layer + MCP Tools + Task Module

**Date:** 2026-05-13
**Author:** Costco (Backend Dev)
**Status:** Implemented

## Context

Sprint 2.1 requires promo campaign planning capabilities: historical analysis, lift prediction, timing evaluation, and ROI estimation. These tools feed into a Task Module endpoint that orchestrates all four and applies approval gates for high-budget or low-ROI scenarios.

## Decision

### Schema Design
- `PromoHistory` table stores 60+ seeded campaigns with full metrics (spend, baseline/actual volume, lift, ROI, rating)
- `LiftCoefficients` table stores 30 rows (6 categories × 5 promo types) with category-adjusted coefficients
- Both tables use `COLLATE NOCASE` for case-insensitive filtering, matching existing pattern

### ROI Calculation Model
- Diminishing returns: spend beyond `MaxEffectiveSpend` incurs 0.7x penalty on marginal lift
- Timing multiplier combines seasonality factor and conflict penalty
- Confidence derived from coefficient standard deviation: `1.0 - (stdDev / avgLift)`
- Breakeven analysis included in output

### Approval Gate Triggers
- Budget > $500K → auto-triggers approval
- ROI < 2.0x AND budget > $100K → auto-triggers approval
- Uses existing `IApprovalGate.RequestApprovalAsync()` contract

### API Pattern
- McpServer owns data + query methods (same as demand tools)
- API project uses HTTP proxy tools calling McpServer REST endpoints
- Task Module endpoint in API orchestrates all 4 tools in sequence

## Consequences
- Promo tools follow identical patterns to demand tools — consistent codebase
- Task Module creates a single orchestration point for the Campaign Planner UI
- Approval gate integration reuses Sprint 1.4 infrastructure without changes
