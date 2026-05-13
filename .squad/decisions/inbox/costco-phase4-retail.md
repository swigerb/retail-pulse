# Phase 4 Architectural Decisions

**Author:** Costco (Backend Dev)  
**Date:** Session work  
**Scope:** Phase 4 sprints 4.1, 4.2, 4.3

## Decisions

### 1. Schema v7 — Five New Tables
Added StoreMetrics, ShelfLayouts, SkuVelocity, BrandFinancials, MarginDrivers. Follows existing seeded-SQLite pattern with deterministic hash-based data generation.

### 2. Escalation Levels (L1/L2/L3)
EscalationOrchestrator uses keyword-based complexity detection to route queries through escalation levels. L1 = simple single-dimension, L2 = multi-dimensional cross-analysis, L3 = strategic/executive with exec brief format.

### 3. Scorecard Weighted Dimensions
ScorecardOrchestrator scores brands across 5 dimensions with these weights: Demand Momentum (0.25), Competitive Position (0.20), Supply Reliability (0.20), Store Execution (0.20), Margin Health (0.15). Weights are constants, not yet configurable per tenant.

### 4. Raw String Literal Pattern
Used `$$"""` with `{{variable}}` interpolation for JSON templates in ScorecardOrchestrator to avoid CS9006 errors with literal braces. This pattern should be used for any future agent that builds JSON in raw string literals.

### 5. New AgentIntent Constants
Added slash-separated intents following Kroger's contract convention: `store/operations`, `planogram/optimization`, `margin/analysis`, `scorecard/portfolio`. All added to `AgentIntent.All` for router validation.

### 6. ExplainabilityService Pattern
Captures tool execution traces as structured data (tool name, input, output, duration) and chains them into human-readable explanation narratives. Registered as singleton, not per-request.
