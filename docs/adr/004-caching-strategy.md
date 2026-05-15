# ADR-004: Dual-Layer Caching Strategy

## Status

Accepted

## Context

The RetailPulse chat pipeline involves expensive operations: LLM inference (~1-3s), MCP tool calls (~200-500ms each), and RAG context retrieval (~100-300ms). Many user queries are repetitive (e.g., "What are Sierra Gold sales this month?" asked by multiple users or the same user in different sessions). Without caching, each request incurs full pipeline cost.

## Decision

We implement a **dual-layer caching strategy**:

### Layer 1: HTTP Response Caching Handler (`McpResponseCachingHandler`)
- Acts as a delegating handler on the MCP `HttpClient`.
- Caches deterministic tool responses (depletion stats, shipment data, etc.) using `IMemoryCache`.
- Cache key is derived from the tool name + serialized parameters.
- TTL is configurable per tool type (real-time data: 30s, historical data: 5min, reference data: 1hr).
- Reduces redundant MCP server calls for the same data within a session.

### Layer 2: Pre-Route Response Cache (`IResponseCache`)
- Caches full chat responses before routing.
- Cache key is built from the normalized user message.
- Only cacheable queries (deterministic, non-personalized) are eligible.
- Cache hit short-circuits the entire pipeline (no routing, no agent execution).
- Returns the cached response with a `cache.hit` span for observability.

### Cache Eligibility
The `CacheHelpers.IsCacheable()` function determines if a query is cache-eligible based on:
- No personal pronouns or user-specific context
- Deterministic questions (facts, not opinions)
- Not time-sensitive below the cache TTL

## Consequences

**Positive:**
- Dramatically reduces latency for repeated queries (~5ms vs. ~2000ms).
- Reduces token consumption and API costs for common questions.
- Layer 1 (tool-level) helps even novel queries that reuse the same underlying data.
- Observability via cache.hit spans allows monitoring cache effectiveness.

**Negative:**
- Stale data risk if TTLs are too long (mitigated by short default TTLs).
- Memory pressure from in-memory cache (mitigated by size limits).
- Cache key collisions could serve wrong responses (mitigated by full message normalization).
- Cache invalidation is passive (TTL-based) — no active invalidation on data changes.
