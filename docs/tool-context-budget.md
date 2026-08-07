# Tool-Context Budget (tool-result compaction boundary)

## Why this exists

Agent tool results are re-sent to the model on **every** function-invocation
iteration (`FunctionInvokingChatClient`, `MaximumIterationsPerRequest = 3`). A single
verbose tool payload therefore multiplies across the request. For the depletion
comparison query

> *Compare Coastline Tacos vs Apex Grill depletions across all regions*

the dominant amplifier was `GetHistoricalDemand`, whose unbounded `weekly_data`
array (region × channel × week) serialized to **~147 KB / ~36,851 est. tokens** for a
single all-regions/all-channels/12-month call — per brand, re-sent per iteration. That
is the root of the six-figure token counts observed live.

## Design

A single, typed compaction boundary is applied at the **one** place every specialist
agent funnels through: `AgentExecutionPipeline` wraps each tool with
`BudgetedAIFunction` (outermost, after the existing `TimedAIFunction` /
`InstrumentedAIFunction`). Wrapping there covers the entire tool catalog automatically.

```
raw AIFunction → Timed/Instrumented → Budgeted (outermost) → ChatOptions.Tools
```

For each tool result, in order:

1. **Request-scoped dedup** — an identical `(principal + tool name + normalized args)`
   call within the same request returns the earlier compact result without
   re-executing. Keyed via `RequestToolContext` (an `AsyncLocal` scope that begins per
   request and dies with it, so dedup **never** crosses requests or principals).
2. **Distinct-call cap** (`MaxToolCalls`) — beyond the cap a compact diagnostic is
   returned instead of invoking, so runaway tool loops cannot explode context.
3. **Per-result compaction** (`ToolResultBudget`):
   - **Tool-specific summarizers** first (`IToolResultCompactor`) — faithful
     projections that preserve totals/units/averages and enough aligned points for a
     chart, rather than blunt truncation.
   - **Generic array truncation** as fallback — trims the largest JSON array to
     `MaxArrayItems` and attaches explicit `_truncation` metadata (`truncated`,
     `original_count`, `returned_count`, drill-down `hint`).
   - **Hard clip** as last resort — a guaranteed-valid JSON envelope with a bounded
     preview and explicit `_budget` metadata. Never malformed JSON, never silent loss.
4. **Cumulative per-request budget** (`MaxCumulativeChars`) — once the running total of
   returned characters would exceed the cap, further results are replaced by a compact
   diagnostic that tells the model to synthesize from what it already has.

Exempt tools (`CreateChart`) carry a canonical payload the frontend renders
(`ChartSpec`), so they pass through **byte-for-byte** and do **not** count toward the
cumulative budget. The canonical `ChartSpec` is never compacted or truncated.

## Configuration

`appsettings.json` → `ToolResultBudget` section (all values have safe defaults, so the
boundary is active even without configuration):

| Setting | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Master switch. When false, results pass through unchanged. |
| `MaxResultChars` | `6000` | Max serialized chars for a single result before compaction. |
| `MaxCumulativeChars` | `24000` | Max cumulative chars of all tool results in one request. |
| `MaxToolCalls` | `8` | Max distinct (non-deduplicated) tool invocations per request. |
| `CharsPerToken` | `4` | Divisor for the estimated-token telemetry. |
| `MaxArrayItems` | `24` | Max elements kept by the generic array compactor. |
| `ExemptTools` | `["CreateChart"]` | Tools that pass through unchanged (canonical payloads). |
| `PerToolMaxResultChars` | `{}` | Optional per-tool overrides of `MaxResultChars`. |

## How to request more detail (opt-in drill-down)

Compaction is deliberately lossy in a **recoverable** way. Every compacted result
carries an explicit hint describing how to retrieve detail:

- **`GetHistoricalDemand`** — the weekly rows are rolled up per region into `by_region`
  (region, volume, units, avg_weekly_volume, weeks). To get week-level detail, call the
  tool again with an explicit **single region** and a **smaller months** window; the
  narrower result fits the per-result budget without compaction.
- **`GetPortfolioDepletionStats`** — the per-brand sentiment narrative is dropped while
  all numeric metrics are kept. Call `GetDepletionStats` for a single brand to retrieve
  its full sentiment summary.
- **Generic truncation** — re-call the tool with a narrower filter to page past the
  first `MaxArrayItems` items (`_truncation.original_count` reports the full size).

## Prefetch contract

`ToolPrefetchService` (DemandForecasting intent only) pre-fetches
`GetHistoricalDemand` / `GetSeasonalityFactors`. Prefetched results are now run through
the **same** compaction boundary (`CompactPrefetch`) before being injected into the
system prompt, so prefetch can never smuggle an un-budgeted raw payload into context (or
re-send it on every iteration). Identical subsequent tool calls the model makes are
independently compacted and bounded by request dedup and the cumulative budget.

## Telemetry

`BudgetedAIFunction` emits one structured log per tool result carrying **sizes/flags
only — never payload content or PII**: `originalChars`, `returnedChars`, `estTokens`,
`origItems`/`retItems`, and the `compacted` / `truncated` / `dedup` / `exempt` /
`budgetExceeded` flags plus `durationMs`. Cost-dashboard semantics remain truthful.

## Measured impact (deterministic, single occurrence)

For the baseline query's tool payloads, measured directly from the seeded database and
run through the boundary (`ToolContextAfterMeasurement`):

| Tool | Before (est. tokens) | After (est. tokens) |
| --- | ---: | ---: |
| GetDepletionStats (Coastline Tacos) | 168 | 168 |
| GetDepletionStats (Apex Grill) | 169 | 169 |
| GetPortfolioDepletionStats | 2,012 | 526 |
| GetHistoricalDemand (Coastline Tacos, 12mo) | 36,851 | 275 |
| GetHistoricalDemand (Apex Grill, 12mo) | 35,668 | 274 |
| **Total** | **74,868** | **1,412** |

**≈98% reduction** per occurrence — before the additional multiplier removed by request
dedup and the cumulative cap across iterations. The compacted `GetHistoricalDemand`
still carries the `summary` totals and aligned `by_region` points a two-brand grouped-bar
comparison needs.

## Adding a new tool

The catalog contract test (`ToolCatalogContractTests`) reflects over every
model-callable agent tool and asserts each has an explicit bounding classification
(`Exempt` / `ToolSpecificSummarizer` / `GenericBudget`). **Adding a new tool without
classifying it fails CI**, so an unbounded tool can never silently enter model context.
If a new tool can return large/unbounded output, add a dedicated `IToolResultCompactor`
projection and classify it `ToolSpecificSummarizer`.
