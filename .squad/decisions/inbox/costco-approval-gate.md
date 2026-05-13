# Decision: Approval Gate Architecture

**Author:** Costco (Backend Dev)  
**Date:** 2025-07-17  
**Status:** Implemented

## Context

Sprint 1.4 required a human-in-the-loop approval system to pause agent execution for high-impact recommendations.

## Decisions

1. **Single-table schema** — Decision column lives on ApprovalRequests row (not a separate approval_results table). Simpler queries, fewer joins, natural idempotency via `UPDATE WHERE Decision='Pending'`.

2. **RespondAsync returns void** — Idempotent by design; second respond on same request is a no-op. Callers check state via GetResultAsync if needed.

3. **ApprovalContext record pattern** — All request metadata (agentId, userId, action, impact, urgency, reasoning) bundled into a single immutable record passed to RequestApprovalAsync. Cleaner than 6+ parameters.

4. **SignalR push + SQLite polling hybrid** — ApprovalTool pushes `approval_requested` via SignalR for real-time UI, then polls SQLite every 2 seconds for the decision. Simple, no complex pub/sub needed.

5. **5-minute default timeout** — Requests auto-expire to TimedOut if no human responds within 5 minutes. Prevents agents from blocking indefinitely.

## Alternatives Considered

- Two-table design (requests + results): More normalized but added complexity for no real benefit
- Event-driven (no polling): Would require more infrastructure; polling at 2s is acceptable for approval latency
- RespondAsync returning ApprovalResult: Adds complexity; void + idempotent UPDATE is simpler
