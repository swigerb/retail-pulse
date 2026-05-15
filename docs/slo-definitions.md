# SLO/SLI Definitions — RetailPulse

## Service Level Objectives (SLOs)

| SLO | Target | Measurement Window |
|-----|--------|--------------------|
| **Simple Query Latency** (single agent) | p95 < 30 seconds | Rolling 7 days |
| **Complex Query Latency** (council/multi-agent) | p95 < 60 seconds | Rolling 7 days |
| **Error Rate** | < 1% (excluding HTTP 4xx user validation) | Rolling 7 days |
| **Availability** | > 99.5% | Rolling 30 days |

## Service Level Indicators (SLIs)

### 1. Request Duration (Latency)

**Metric:** `retailpulse.request_duration_ms`

Measured from the moment the HTTP request arrives at the API to the moment the response is fully written. Segmented by query complexity (single-agent vs council).

```
# p95 latency for simple queries (last 7 days)
histogram_quantile(0.95,
  retailpulse_request_duration_ms_bucket{query_type="simple"})

# p95 latency for complex queries
histogram_quantile(0.95,
  retailpulse_request_duration_ms_bucket{query_type="complex"})
```

### 2. Error Rate

**Metric:** `retailpulse.request_total` with `is_error` tag

Errors include 5xx responses, agent execution failures, and timeout errors. Excludes 4xx validation errors (bad input, auth failures).

```
# Error rate (excluding 4xx)
sum(retailpulse_request_total{is_error="true"})
  / sum(retailpulse_request_total) * 100
```

### 3. Availability

**Metric:** Health check success rate from `/health` endpoint

Availability is defined as the percentage of time the service responds to health probes within 5 seconds.

```
# Availability over 30 days
(total_health_checks - failed_health_checks) / total_health_checks * 100
```

### 4. Agent Execution Duration

**Metric:** `retailpulse.agent_execution_duration_ms` (by `agent_key`)

Tracks individual agent performance to identify slow specialists.

### 5. Routing Duration

**Metric:** `retailpulse.routing_duration_ms`

Measures time spent classifying intent — should be < 2s for fast-path and < 5s for LLM classification.

## Burn Rate Alerts

| Alert | Condition | Severity |
|-------|-----------|----------|
| **Latency Budget Burn** | p95 > 30s for 10min (simple) | Warning |
| **Latency Budget Burn** | p95 > 60s for 10min (complex) | Warning |
| **Error Budget Burn (fast)** | Error rate > 5% for 5min | Critical |
| **Error Budget Burn (slow)** | Error rate > 2% for 30min | Warning |
| **Availability Burn** | Health check failures > 3 consecutive | Critical |

## Instrumentation Code

The `RetailPulseMetrics` class (`src/RetailPulse.Api/Telemetry/RetailPulseMetrics.cs`) exposes SLI counters:

- `RecordRequest(durationMs, isError)` — called on every request completion
- `RecordAgentExecutionDuration(agentKey, durationMs)` — per-agent tracking
- `RecordRoutingDuration(durationMs)` — router performance tracking

These metrics are exported via OpenTelemetry to the configured OTLP endpoint and/or Azure Monitor, and are visible in the Aspire dashboard during development.
