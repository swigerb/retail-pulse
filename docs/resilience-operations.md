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
