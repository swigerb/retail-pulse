# Decision: Streaming & Guardrails Middleware Architecture

**Author:** Kroger (Lead Architect)  
**Date:** 2026-05-16  
**Sprint:** 3.1 + 3.2  

## Context

Sprint 3.1 (Streaming/Caching) and 3.2 (Guardrails/Content Filtering) required adding middleware layers to the existing chat pipeline without disrupting the multi-agent router architecture from Sprint 1.1.

## Decisions

### 1. Guardrails as Scoped Middleware (not HTTP middleware)

**Decision:** GuardrailsMiddleware is a scoped DI service called explicitly in the chat endpoint, not an ASP.NET Core middleware registered in the HTTP pipeline.

**Rationale:** Guardrails need access to `ChatRequest` typed objects, not raw HTTP requests. Making it a DI service keeps it testable and allows fine-grained control over where in the pipeline it runs (before routing for input, after agent execution for output).

### 2. Pipeline Ordering: Guardrails → Cache → Route → Agent → Cache Store → PII Redact

**Decision:** Input guardrails run first (can reject before any work), then cache check (avoid redundant LLM calls), then normal agent pipeline, then cache store + PII redaction on output.

**Rationale:** This ordering minimizes wasted computation — blocked requests never hit the cache or router, and cached responses skip agent execution entirely.

### 3. Cache Key: Pre-Route SHA256

**Decision:** Cache key is `SHA256("pre-route|normalized_query")` — computed before routing, so the same query always hits cache regardless of which agent would handle it.

**Rationale:** The router is deterministic, so the same query always routes to the same agent. Keying pre-route means cache hits skip both routing and agent execution.

### 4. Deterministic Detection for Cache Eligibility

**Decision:** `CacheHelpers.IsCacheable()` uses a keyword blocklist (forecast, predict, recommend, suggest, etc.) to exclude non-deterministic queries from caching.

**Rationale:** Caching forecasts or recommendations would serve stale/incorrect data. Factual queries ("what are current prices for X") are safe to cache with 5-minute TTL.

### 5. Streaming via SignalR Fallback

**Decision:** The `/api/chat/stream` endpoint uses `StreamResponseFallbackAsync` to push pre-computed responses as word-boundary tokens via SignalR, rather than requiring IChatClient streaming support from agents.

**Rationale:** The specialist agents return full responses via `HandleAsync`. True token-level streaming would require refactoring every agent to expose `IAsyncEnumerable<string>`. The fallback approach provides streaming UX with zero agent changes, and can be upgraded to true streaming per-agent later.

## Impact

- All existing agents are unaffected — guardrails and caching are transparent middleware
- New endpoint `/api/chat/stream` available for streaming-capable clients
- PII redaction applies globally to all agent responses
