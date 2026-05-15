# ADR-002: MCP Tool Server as Separate Process

## Status

Accepted

## Context

The RetailPulse API orchestrates multiple AI agents that need access to business data (depletion stats, shipments, competitor pricing, etc.). Running these data tools in-process with the API would:
- Couple tool lifecycle to the API process.
- Make it difficult to scale tools independently.
- Risk one slow/failing tool blocking the entire API.
- Complicate tool versioning and deployment.

## Decision

We implement tools as an **MCP (Model Context Protocol) server** running as a separate process (`RetailPulse.McpServer`). The API communicates with it via HTTP using a resilient client with:

1. **Circuit breaker** — prevents cascading failures when the MCP server is unhealthy.
2. **Response caching handler** — caches deterministic tool responses to reduce redundant calls.
3. **Retry with exponential backoff** — handles transient network failures.
4. **Dead-letter queue** — captures failed tool invocations for later inspection and replay.

The MCP server is registered as a named `HttpClient` ("McpServer") with resilience policies applied via `AddMcpResilienceHandler()`.

## Consequences

**Positive:**
- Tool failures are isolated — a crashing tool doesn't take down the API.
- Tools can be scaled independently (e.g., more instances for heavy forecast queries).
- Clear separation allows different teams to own API orchestration vs. data tools.
- Circuit breaker provides graceful degradation with health check integration.
- Caching reduces latency for repeated queries within a session.

**Negative:**
- Network latency between API and MCP server (~5-10ms per call).
- Deployment complexity increases (two processes to manage).
- Need to maintain an HTTP API contract between the two services.
- Cache invalidation must be considered for time-sensitive data.
