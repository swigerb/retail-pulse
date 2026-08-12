# Aspire Metrics Dashboard — RetailPulse

## Key Metrics to Monitor

### Business Metrics (Custom)

| Metric | Description | Dimensions |
|--------|-------------|------------|
| `retailpulse.intent_classification_total` | Count of intent classifications | `intent`, `fast_path_hit` |
| `retailpulse.cache_hit_total` | MCP response cache hits | — |
| `retailpulse.cache_miss_total` | MCP response cache misses | — |
| `retailpulse.error_total` | Errors by category | `category` |
| `retailpulse.tool_call_duration_ms` | Tool call latency | `tool_name` |
| `retailpulse.agent_execution_duration_ms` | Agent execution time | `agent_key` |
| `retailpulse.routing_duration_ms` | Routing/classification time | — |
| `retailpulse.request_total` | Total requests (SLI) | `is_error` |
| `retailpulse.request_duration_ms` | End-to-end request latency (SLI) | — |

### Infrastructure Metrics (auto-collected)

| Metric | Source |
|--------|--------|
| `http.server.request.duration` | ASP.NET Core instrumentation |
| `http.client.request.duration` | HttpClient instrumentation |
| `process.runtime.dotnet.gc.collections.count` | .NET Runtime instrumentation |
| `process.runtime.dotnet.thread_pool.threads.count` | .NET Runtime instrumentation |

## Aspire Dashboard Queries

### Agent Latency Percentiles (p50, p95, p99)

```
retailpulse.agent_execution_duration_ms
  | group by agent_key
  | percentile(50, 95, 99)
```

### Cache Hit Rate

```
rate(retailpulse.cache_hit_total)
  / (rate(retailpulse.cache_hit_total) + rate(retailpulse.cache_miss_total)) * 100
```

### Error Rate by Category

```
rate(retailpulse.error_total)
  | group by category
```

### Intent Distribution

```
retailpulse.intent_classification_total
  | group by intent
  | top 10
```

### Routing Fast-Path Efficiency

```
sum(retailpulse.intent_classification_total{fast_path_hit="true"})
  / sum(retailpulse.intent_classification_total) * 100
```

### Tool Call Duration Heatmap

```
retailpulse.tool_call_duration_ms
  | group by tool_name
  | percentile(50, 95)
```

## Recommended Alert Thresholds

| Alert | Condition | Action |
|-------|-----------|--------|
| **High Error Rate** | `error_total` rate > 10/min for 5 min | Page on-call |
| **Agent Latency Spike** | p95 `agent_execution_duration_ms` > 45s for 5 min | Warn team |
| **Cache Degradation** | Cache hit rate < 30% for 15 min | Investigate MCP server |
| **Routing Slowdown** | p95 `routing_duration_ms` > 5s for 5 min | Check OpenAI quota |
| **Health Check Failure** | `/health` returns non-200 for 3 consecutive checks | Auto-restart / page |
| **Request Volume Drop** | `request_total` rate drops > 80% vs 1h avg | Investigate ingress |
| **Tool Timeout** | `tool_call_duration_ms` p99 > 30s | Check MCP server health |

## Aspire Dashboard Setup

The RetailPulse metrics are automatically exported via OpenTelemetry when running under Aspire:

1. **Local development**: Metrics appear in the Aspire dashboard. The AppHost HTTPS profile pins the dashboard at `https://localhost:17152` (`src/RetailPulse.AppHost/Properties/launchSettings.json`); Aspire prints the exact `Login to the dashboard at https://localhost:XXXXX/login?t=<token>` URL to the terminal on every launch — use the printed URL if you have overridden the profile.
2. **Azure Monitor**: Set `APPLICATIONINSIGHTS_CONNECTION_STRING` for production telemetry
3. **Custom OTLP**: Set `OTEL_EXPORTER_OTLP_ENDPOINT` for Grafana/Prometheus/Jaeger

The `RetailPulse` meter is registered in the OTel pipeline via:
```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(RetailPulseMetrics.MeterName));
```
