# Decision: Sprint 1.5/1.6 Test Strategy — Alerts, Tracing & Phase 1 Regression

**Author:** Target (Tester)
**Date:** 2026-05-15
**Status:** Accepted

## Context

Sprints 1.5 (Proactive Alerts) and 1.6 (Distributed Tracing) introduced new subsystems requiring comprehensive test coverage. Additionally, with all Phase 1 features complete, a regression suite was needed to validate cross-feature interactions.

## Decision

### Alert Testing (45 tests across 4 files)

- **InMemoryAlertService** created as testable implementation of `IAlertService` with deterministic anomaly detection, configurable throttle windows, and snooze/dismiss support.
- Tests validate deviation thresholds (>40% = high, >20% = medium), throttle key specificity (brand|region), and cross-user isolation.
- Method naming: `SnoozeAsync` implements the interface contract (3 params); `SnoozeWithDetailsAsync` adds optional brand/region specificity (avoids C# overload ambiguity).

### Tracing Testing (25 tests across 2 files)

- Tests target `InMemoryTraceCollector` (backend team's implementation with SignalR).
- Ring buffer eviction, concurrent capture, and structured summary generation validated.
- `CapturedSpan` bridge record created to fix pre-existing build error in OTelAgentMiddleware.

### Phase 1 Regression (15 tests)

- Integration tests exercise cross-feature flows: router → memory → approval → alerts → tracing.
- DI registration smoke tests ensure all Sprint 1.x services resolve correctly.
- Backward compatibility tests confirm existing `/api/chat` pipeline still works.

## Consequences

- Total test count: **540** (443 existing + 97 new). All passing.
- Alert service uses string-based Type/Severity (matching backend team's contract choice), not enums.
- `SnoozeWithDetailsAsync` naming convention established for extended interface methods.
