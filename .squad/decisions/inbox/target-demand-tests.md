# Decision: Demand Forecasting Test Strategy

**Author:** Target (Tester)  
**Date:** 2026-05-14  
**Sprint:** 1.2  
**Status:** Proposed

## Context

Sprint 1.2 introduces the Demand Forecasting Agent with 4 MCP tools backed by seeded SQLite data (~79K rows). Tests needed to cover the agent contract, tool behavior, data integrity, and routing integration without duplicating Sprint 1.1 patterns.

## Decision

### Test Architecture (4-layer coverage)

1. **Agent contract tests** (`DemandForecastAgentTests.cs`, 28 tests) — validate `ISpecialistAgent` compliance, response shape, and tool isolation using mocked `IChatClient` (same pattern as `GeneralAgentTests`).

2. **Tool/query tests** (`DemandToolTests.cs`, 30 tests) — test the 4 DB query methods directly against real SQLite with seeded data. Validates filtering, aggregation, anomaly detection, and seasonal adjustment logic.

3. **Data integrity tests** (`DemandDataTests.cs`, 46 tests) — validate the seed data itself: brand/region/channel coverage, time span completeness, seasonal patterns, and volume integrity. These catch seed regressions that would silently break tool tests.

4. **Routing integration tests** (5 tests added to existing `RouterIntegrationTests.cs`) — verify demand intents route to `DemandForecastAgent` and coexist with `GeneralAgent`.

### Key Patterns

- **Real DB, not mocks** for tool/data tests — matches Sprint 1.1 precedent (`UpdateMetricsToolTests`). Seeded data is deterministic so assertions are stable.
- **Parameterized brand tests** — `[Theory]` + `[InlineData]` across all 12 brands ensures no brand is silently missing from forecasts.
- **Seasonal pattern validation** — tests verify that multipliers actually vary by month and that known peaks (spirits → Nov/Dec) are correct.

### Bug Fixes Found During Testing

| File | Issue | Fix |
|------|-------|-----|
| `GenerateForecastTool.cs:21` | Extra `channel` param not in DB method | Removed param |
| `RetailPulseDb.cs:~1311` | Extension method on `dynamic` type fails | Explicit `(string)` cast |

### Risk: Duplicate MCP Tool Registration

Both individual tool files (`GetHistoricalDemandTool.cs`, etc.) and `DemandTools.cs` define `[McpServerTool]` attributes with identical names. This compiles but may cause runtime duplicate registration errors. **Costco should resolve which pattern to keep.**

## Impact

- Test count: 237 → 346 (+109 new)
- All tests pass in ~14s
- Regression safety net for demand forecasting feature before Sprint 1.3
