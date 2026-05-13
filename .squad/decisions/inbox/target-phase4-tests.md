# Decision: Phase 4 Test Contracts

**Author:** Target (Tester)
**Date:** 2026-05-15
**Status:** Proposed

## Context

Phase 4 tests (Sprints 4.1/4.2/4.3) define test-first contracts for features not yet fully implemented. The tests serve as executable specifications for the backend team.

## Decisions

1. **Test-first interfaces for Escalation, Scorecard, Explainability** — `IEscalationService`, `IScorecardService`, and `InMemoryExplanationStore` are defined inside test files with mock implementations. When backend implements real versions, move interfaces to `RetailPulse.Contracts` and delete the mocks.

2. **BrandFinancials is the margin table** — The table is `BrandFinancials` (PascalCase columns: `BrandId`, `Period`, `Revenue`, `Cogs`, `Marketing`, `Distribution`, `NetMargin`). There is no stored `GrossMargin` column — it's computed as `Revenue - Cogs`.

3. **Phase 4 AgentIntents already registered** — `StoreOps`, `Planogram`, `MarginAnalysis`, `Scorecard` are already in `AgentIntent.cs`. Router tests validate these route correctly.

4. **Aisle ID format is compound** — Format: `AISLE-{StoreId}-{NN}`. Tests use direct SQLite queries via `GetFirstAisleId()` helper instead of hardcoded aisle names.

## Impact

- Backend team implementing Escalation/Scorecard/Explainability should match the test-first contracts
- 110 new tests serve as regression guard for Phase 4 features
