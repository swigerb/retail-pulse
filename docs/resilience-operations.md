# Resilience & Operations Guide

> How RetailPulse handles failures, retries, and degraded states.

---

## Middleware Pipeline

Requests flow through this middleware stack (in order):

```
→ CorrelationIdMiddleware    (assigns/propagates X-Correlation-ID)
→ SecurityHeadersMiddleware  (CSP, HSTS, X-Frame-Options)
→ ExceptionHandlingMiddleware (catches unhandled exceptions → RFC 7807)
→ [Rate Limiting]            (ASP.NET Core rate limiter)
→ [Routing]                  (endpoint dispatch)
→ [Validation]               (ChatRequestValidator)
→ [Agent Execution]          (router → specialist → response)
```

---

## Real-time channel resilience (issue #92)

Wave 2 turns a chat "turn" into a multi-step plan that can run far longer than a
single-shot answer. Three configuration surfaces keep the real-time channel and
its UX honest under those long-running conditions.

### SignalR keep-alive + application heartbeat — `RealtimeResilience`

SignalR transport pings satisfy intermediary proxies but are opaque to the
browser layer. RetailPulse layers an application-level heartbeat on top so the
frontend can render "connected / stalled" without probing transport internals.

| Setting                        | Default    | Purpose                                                                 |
|--------------------------------|------------|-------------------------------------------------------------------------|
| `KeepAliveInterval`            | `00:00:15` | Server-to-client transport ping cadence (bound to `HubOptions`).        |
| `ClientTimeoutInterval`        | `00:00:30` | Server-side disconnect threshold. SignalR guidance: `>= 2 x KeepAlive`. |
| `HandshakeTimeout`             | `00:00:15` | Initial protocol negotiation timeout.                                   |
| `ApplicationHeartbeatInterval` | `00:00:15` | Cadence of the observable `heartbeat` event on both hubs.               |
| `ApplicationHeartbeatEnabled`  | `true`     | Master switch for the hosted heartbeat emitter.                         |

The 15-second keep-alive default stays well under the shortest plausible
intermediary idle timeout we see in front of Container Apps (APIM at 240s,
corporate proxies frequently at 60s). The `heartbeat` event is emitted on both
`/hubs/telemetry` and `/hubs/streaming`.

### Chat request timeouts — `ChatTimeout`

Separate ceilings for the single-shot fast path and the long-running plan path.
Preserves the pre-#92 single-shot 90s behavior; the plan ceiling is applied only
when the hybrid execution decider (#95) selects the plan path.

| Setting      | Default    | Purpose                                                                                     |
|--------------|------------|---------------------------------------------------------------------------------------------|
| `SingleShot` | `00:01:30` | Per-request timeout for a fast-path (single-specialist) run.                                |
| `Plan`       | `00:06:00` | Replacement ceiling when the hybrid execution decider selects the plan path. Applied to `/api/chat` only after admission to the plan branch. |

Keep `Plan >= PlanPersistence.PlanTimeout + 60s` so the plan orchestrator's own
per-plan timeout fires first with a plan-specific failure reason rather than the
request seam timing out with the generic 504.

### User-initiated cancellation

`POST /api/chat/{sessionId}/cancel` and `POST /api/plans/{planId}/cancel` end an
in-flight run the caller owns. Ownership is enforced by
`IExecutionCancellationRegistry`: a foreign-subject cancel collapses to `404` so
the endpoints cannot be used to probe another user's live sessions or plan ids.
Anonymous callers cannot cancel plans (`plan_cancel_unavailable`). The
cancellation flows through the same `CancellationToken` the pipeline uses to
invoke specialists and tools, so an in-flight tool call actually observes the
cancellation and ceases work — not merely that the HTTP response returned.

### Reconciliation after reconnect

`GET /api/plans/{planId}/reconcile?afterStepIndex=N` returns durable plan status
plus any step records with `StepIndex > N`, filtered to the caller's subject at
the SQL layer. A cross-subject probe or unknown plan id resolves to `404`.
Mapped only when `PlanPersistence:Enabled` is `true`, matching the rest of the
`/api/plans` surface.

### Hub ownership on reconnect

Every `JoinSession` on both hubs binds the sessionId to the caller's immutable
subject via `ISessionOwnershipRegistry`. This is now enforced for BOTH
authenticated and anonymous callers (previously anonymous-only). A hostile
client that reconnects and attempts to rejoin another subject's session group is
refused with a `HubException`.

### Frontend behavior on top of these contracts

The SPA layers a small set of user-visible affordances on top of the backend
contracts above so a long-running plan or a dropped channel never presents as
a silent spinner. All of these live under `src/RetailPulse.Web/src` and are
covered by Vitest units in `src/__tests__` (see the plan below).

| UI concern | Module | Notes |
|------------|--------|-------|
| Reconnect schedule | `services/reconnectBackoff.ts` | Capped exponential-ish schedule (`1s → 30s` cap, 8 attempts). Returning `null` from the SignalR `IRetryPolicy` triggers `onclose` and the terminal `disconnected` state. |
| Connection status | `services/telemetryHub.ts`, `hooks/useConnectionStatus.ts` | Exposes `connecting / connected / reconnecting / disconnected` plus a `stalled` flag (Connected but no `heartbeat` in `2 × ApplicationHeartbeatInterval`). |
| Visible indicator | `components/ConnectionStatusIndicator.tsx` | Rendered inline in the chat composer next to Send so a dropped hub is visible where the user actually types. |
| Timeout dialog | `components/TimeoutDialog.tsx` | Replaces the pre-#92 hung spinner when `sendMessage` throws `ChatRequestTimeoutError`; offers Retry (replay the same prompt) or Abandon (clear the in-flight state). |
| User cancel | `services/executionControlApi.ts`, `components/ChatPanel.tsx` | The Send button flips to Cancel while a run is in flight. Cancel aborts the local fetch AND posts `/api/chat/{sessionId}/cancel`; when a `planId` is known the plan-owning UI can call `cancelPlan(planId)` from the same module. |
| Reconcile after reconnect | `services/planReconciler.ts`, `services/executionControlApi.ts#reconcilePlan` | Deterministic merge by `stepIndex` with terminal-state monotonicity. Overlap collapses to a single entry; gaps are preserved so streaming can fill them in. |

**Cross-session rejoin safety.** `joinPendingSessions()` in `telemetryHub.ts`
only re-invokes `JoinSession` for sessionIds this client previously joined via
`joinTelemetrySession()`. Server payloads never influence that set, so a
hostile server message cannot trick the client into rejoining a foreign
group. The hub also enforces subject ownership on every join and rejoin.

**Deferred APIM verification.** Local Vitest units cover the schedule, ceiling,
terminal transition, dedupe/overlap/no-gaps, and the timeout / cancel UX.
They do not exercise a real intermediary idle timeout. The multi-minute idle
survival criterion in the issue must be verified end-to-end through APIM
against a deployed environment and the result recorded in the PR.

---

## Circuit Breaker

**Scope:** MCP Server HTTP client (all tool calls)

| Parameter | Value |
|-----------|-------|
| Failure threshold | 5 failures within 30 seconds |
| Break duration | 30 seconds (open state) |
| Sampling duration | 30 seconds |
| Minimum throughput | 2 requests |

**Behavior:**
- **Closed** (normal): Requests flow normally. Failures are counted.
- **Open** (tripped): All requests immediately fail with `BrokenCircuitException`. Returns 503 to caller.
- **Half-Open** (probing): After break duration, one request is allowed through. Success → Closed. Failure → Open again.

**What triggers a failure:** Any HTTP 5xx response from MCP server, request timeout, or connection refused.

---

## Retry Policy

**Scope:** MCP Server HTTP client (composed INSIDE the circuit breaker)

| Parameter | Value |
|-----------|-------|
| Max retries | 3 attempts |
| Backoff | Exponential with jitter |
| Base delay | 1 second |
| Max delay | 30 seconds |

**What gets retried:**
- HTTP 408 (Request Timeout)
- HTTP 429 (Too Many Requests)
- HTTP 5xx (Server Errors)
- Network failures (connection refused, DNS failure)

**What does NOT get retried:**
- HTTP 4xx (client errors, except 408/429)
- Successful responses (2xx)
- Requests that exceed the circuit breaker threshold

---

## Error Classification

All exceptions are classified into categories for appropriate handling:

| Category | Examples | Action |
|----------|----------|--------|
| **Transient** | Timeout, 429, 503, network blip | Retry automatically |
| **User** | Invalid input, message too long, XSS detected | Return 400 with details |
| **System** | Null reference, configuration error | Return 500, alert ops |
| **External** | Azure OpenAI quota, MCP server down | Return 502/503, circuit break |

---

## Dead-Letter Queue

Failed messages that exhaust all retries are placed in the dead-letter queue.

**Implementation:** `Channel<T>` (async producer/consumer) backed by SQLite for durability.

**Replay:** Failed messages can be replayed via the admin endpoint (when enabled).

**Monitoring:** The dead-letter count is exposed as a custom metric: `retailpulse.dead_letter_count`

---

## MCP Fallback Behavior

When the MCP server is unavailable (circuit breaker open):

1. Tools return a graceful error message explaining the data source is temporarily unavailable
2. The agent acknowledges the limitation in its response to the user
3. No partial/stale data is served — fail cleanly rather than mislead

---

## Correlation ID

Every request receives a unique `X-Correlation-ID` (UUID v4):
- If the client sends one, it's preserved
- If not, one is generated at the edge
- Propagated to all downstream calls (MCP, Azure OpenAI)
- Included in all log entries via logger scope
- Returned in every response header

Use correlation IDs to trace requests across all services in Aspire Dashboard or Application Insights.

---

## Health Checks

| Endpoint | Type | What it checks |
|----------|------|---------------|
| `/health` | Readiness | Runs `ready`-tagged health checks (MCP server reachable + Azure OpenAI accessible). 200 healthy / 503 unhealthy. |
| `/alive` | Liveness | Filters out all health checks — 200 whenever the ASP.NET Core pipeline is responsive. |

Both endpoints are anonymous even under `Security:RequireAuth=true` and are wired by
`RetailPulse.ServiceDefaults.MapDefaultEndpoints()` (Aspire convention).

**Kubernetes/Container Apps:** Use `/alive` for liveness probes and `/health` for readiness probes. If readiness fails, the instance is removed from the load balancer until recovery; a failing liveness probe restarts the container.

---

## Alerting Thresholds (Recommended)

| Metric | Warning | Critical |
|--------|---------|----------|
| Error rate | > 1% over 5 min | > 5% over 5 min |
| p95 latency (simple) | > 30s | > 60s |
| p95 latency (complex) | > 60s | > 90s |
| Circuit breaker opens | Any occurrence | Open > 2 min |
| Dead-letter queue depth | > 10 | > 50 |
| Health check failures | > 2 consecutive | > 5 consecutive |

See [SLO Definitions](slo-definitions.md) for formal SLO/SLI targets.
