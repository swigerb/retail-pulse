# ADR-006: Tool-Context Budget (tool-result compaction boundary)

## Status

Accepted

## Context

Every agent in RetailPulse funnels through `AgentExecutionPipeline`, which uses
`Microsoft.Extensions.AI`'s `FunctionInvokingChatClient` with
`MaximumIterationsPerRequest = 3`. That client re-sends the full accumulated message
history — system prompt plus every prior tool result — on each iteration. A single
verbose tool payload is therefore multiplied across the request.

The dominant amplifier was `GetHistoricalDemand`, which returns an unbounded
`weekly_data` array (region × channel × week). For an all-regions/all-channels/12-month
call it serialized to ~147 KB / ~36,851 estimated tokens **per brand**, re-sent per
iteration. A two-brand "across all regions" comparison thus drove six-figure token
counts and ~40 s latency, with no correctness benefit (the model needs totals and
region-aligned points, not ~1,000 raw weekly rows).

Existing caching (`McpResponseCachingHandler`, ADR-004) reduced network latency on
repeat calls but did **not** reduce the number of tokens entering model context — a
cache hit still injects the same full payload.

## Decision

Introduce a centralized, typed **tool-context budget** applied at the single pipeline
tool-wrap choke point, so it covers the entire tool catalog automatically. Each tool is
wrapped with `BudgetedAIFunction` (outermost, after `TimedAIFunction` /
`InstrumentedAIFunction`). Per result, in order:

1. **Request-scoped dedup** keyed by `principal + tool name + normalized args`
   (`RequestToolContext`, an `AsyncLocal` scope that dies with the request — dedup never
   crosses requests or principals).
2. **Distinct-call cap** (`MaxToolCalls`).
3. **Per-result compaction** (`ToolResultBudget`): tool-specific summarizers
   (`IToolResultCompactor`) → generic array truncation with explicit metadata → a
   guaranteed-valid hard clip. Never malformed JSON, never silent data loss.
4. **Cumulative per-request budget** (`MaxCumulativeChars`).

`CreateChart` is **exempt**: its canonical `ChartSpec` is what the frontend renders, so
it passes through byte-for-byte and does not count toward the cumulative budget.

Prefetched results are run through the same boundary before injection into the system
prompt, closing the prefetch duplication path.

A catalog contract test enumerates every registered agent tool and fails CI if a new
tool is added without a bounded-output classification.

## Consequences

**Positive**
- ~98% reduction in per-occurrence tool-context tokens for the baseline query
  (74,868 → 1,412 est. tokens), with the cumulative cap making six-figure token counts
  structurally impossible.
- Correctness preserved: compacted `GetHistoricalDemand` keeps `summary` totals and
  aligned `by_region` points; the grouped-bar chart still renders two series / six bars.
- Truthful telemetry (sizes/flags only, no payload/PII); cost-dashboard semantics intact.
- New unbounded tools cannot silently regress context (contract gate).

**Negative / trade-offs**
- Compaction is lossy by design. Detail is opt-in: callers re-request a narrower
  region/time window (see `docs/tool-context-budget.md`).
- Per-tool summarizers must be maintained as tool payloads evolve; the generic fallback
  bounds anything not yet given a bespoke projection.

## Alternatives considered

- **Cache-only (warm cache, no injection).** Reduces latency but not tokens — rejected
  as insufficient for the token blow-up.
- **Raising `MaximumIterationsPerRequest` down to 1.** Reduces multiplication but breaks
  multi-tool reasoning; does not bound a single oversized payload.
- **Generic truncation only.** Simple but loses the totals/region alignment the chart
  needs. Kept only as the last-resort fallback beneath tool-specific summarizers.

## Related

- ADR-002 (MCP tool server), ADR-004 (caching strategy)
- `docs/tool-context-budget.md`
- `src/RetailPulse.Api/Budget/*`, `tests/RetailPulse.Tests/Budget/*`
