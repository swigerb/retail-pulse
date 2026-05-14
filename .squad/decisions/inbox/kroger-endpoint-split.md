# Decision: Decompose Program.cs into Endpoint Extension Methods

**Author:** Kroger (Lead/Architect)
**Date:** 2025-07-24
**Status:** Implemented

## Context

`src/RetailPulse.Api/Program.cs` had grown to 2,567 lines — a "god file" containing all DI registrations, middleware pipeline setup, and **55+ route handler definitions** with their full lambda bodies, DTOs, and helper methods. This made the file difficult to navigate, review, and maintain.

## Decision

Decompose all endpoint registrations into **13 focused extension method files** under `src/RetailPulse.Api/Endpoints/`, following the `Map{Group}Endpoints(this WebApplication app)` pattern. Program.cs becomes composition-only: DI + middleware + `app.Map*Endpoints()` calls.

## Endpoint Files Created

| File | Routes | Domain |
|------|--------|--------|
| `ChatEndpoints.cs` | `/api/chat`, `/api/chat/stream`, `/api/info`, `/api/council/*` | Chat, streaming, council |
| `AlertEndpoints.cs` | `/api/alerts/*` | Alert management |
| `ApprovalEndpoints.cs` | `/api/approvals/*` | Approval workflows |
| `ObservabilityEndpoints.cs` | `/api/traces/*`, `/api/observability/*`, `/api/cache/*`, `/api/explain/*` | Tracing, costs, audit, cache, explainability |
| `KnowledgeEndpoints.cs` | `/api/knowledge/*`, `/api/message-extension/*` | Knowledge base, Teams message extension |
| `CardEndpoints.cs` | `/api/cards/*` | Adaptive card CRUD |
| `GuardrailEndpoints.cs` | `/api/guardrails/*` | Guardrails config/log |
| `ScorecardEndpoints.cs` | `/api/scorecard` | Scorecard generation |
| `EscalationEndpoints.cs` | `/api/escalate` | Escalation orchestration |
| `PromoEndpoints.cs` | `/api/promo/*`, `/api/taskmodule/promo` | Promo planning |
| `SupplyEndpoints.cs` | `/api/supply/*` | Supply chain |
| `StoreEndpoints.cs` | `/api/stores/*` | Store operations |
| `MarginEndpoints.cs` | `/api/margin/*` | Margin analysis |

## Constraints

- **Zero behavior change** — all routes, rate limiting policies, and authorization requirements are identical
- **DTOs co-located** — each endpoint file owns its request/response DTOs (moved from bottom of Program.cs)
- **SignalR hubs stay in Program.cs** — `TelemetryHub` and `StreamingHub` mappings remain in composition root
- **Program.cs reduced from ~2,567 to ~927 lines** — now purely DI + middleware + Map calls

## Consequences

- Each domain's routes are independently reviewable and testable
- New endpoints are added to the appropriate `*Endpoints.cs` file, not Program.cs
- The `ChatEndpoints.MapChatEndpoints()` method accepts an `AgentDefinition` parameter (needed for `/api/info`)
