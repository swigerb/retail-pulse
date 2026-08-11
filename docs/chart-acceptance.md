# Chart Acceptance — Curated Prompt Matrix

This document is the durable, human-readable index of the **curated chart-prompt
acceptance matrix** the codebase enforces automatically for issue
[#50](../.github). It complements — and is verified against — the machine
sources of truth:

* **Prompt source** — `src/RetailPulse.Web/src/constants/prompts.ts` (Charts
  category + the QSR two-brand comparison).
* **Backend manifest** — `src/RetailPulse.Contracts/Charts/ChartAcceptanceManifest.cs`.
* **Frontend manifest** — `src/RetailPulse.Web/src/components/chartAcceptance.ts`.
* **README chart examples** — the "Try These Curated Prompts" block in the
  repository root README.

Two cross-language contract tests fail CI immediately if these four surfaces
drift:

* `ChartAcceptanceManifestContractTests` (backend).
* `chartAcceptance.contract.test.ts` (frontend).

## Why a matrix, not a spot check

The production P0 in #50 showed that fixing one chart prompt at a time is not
enough — a change to the compactors or the deterministic builder can regress a
different prompt while the one you fixed still works. The acceptance matrix
gates every curated prompt end-to-end.

Each case declares the **semantics a rendered chart MUST satisfy**:

| Field | Meaning |
| ----- | ------- |
| `Prompt` | Verbatim prompt text (must exist in `prompts.ts`). |
| `ChartType` | Canonical `ChartSpec.Type` the prompt must yield. |
| `RoutedIntent` | Specialist that must own the request (never the council). |
| `MinSeries` | Minimum legend-bearing series. Grouped/stacked bars require ≥2. |
| `MinMarks` | Minimum finite datapoints across all series. |
| `RequiredEntities` | Brand/variant labels that must appear as legend, category, or in the title. |
| `AxisUnit` | Y-axis unit / semantic description. |
| `DataSource` | Tool/compactor family that must supply a complete aggregate. |
| `PercentAxis` | Y bounded to `[-100, 200]` for share/growth/mix/gauge. |

## The matrix (issue #50)

| # | Prompt | Chart type | Data source | Min series | Min marks | Percent axis | Required entities |
| - | ------ | ---------- | ----------- | ---------- | --------- | ------------ | ----------------- |
| 1 | Create a line chart showing Sierra Gold Tequila depletion trends across all regions | `line` | HistoricalDemand | 1 | 2 | no | Sierra Gold Tequila |
| 2 | Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast | `bar` | HistoricalDemand | 1 | 3 | no | Sierra Gold Tequila, Ridgeline Bourbon, Summit Vodka |
| 3 | Create a pie chart showing market share breakdown for our grocery brands nationally | `pie` | MarketShare | 1 | 2 | yes | FreshMart, Harvest Table |
| 4 | Show a grouped bar chart comparing FreshMart and Harvest Table across all regions | `groupedBar` | HistoricalDemand | 2 | 12 | no | FreshMart, Harvest Table |
| 5 | Create a donut chart of Apex Grill variant mix in the Southwest | `donut` | VariantMix | 1 | 2 | yes | Apex Grill |
| 6 | Show a horizontal bar chart ranking all brands by depletion growth rate | `horizontalBar` | PortfolioDepletion | 1 | 6 | yes | (all portfolio brands) |
| 7 | Create a table showing depletion stats for all home improvement brands by region | `table` | DepletionStats | 1 | 2 | no | Pinnacle Hardware, Summit Outdoor |
| 8 | Show a gauge chart for Pinnacle Hardware inventory health in the Midwest | `gauge` | InventoryLevels | 1 | 1 | yes | Pinnacle Hardware |
| 9 | Compare Coastline Tacos vs Apex Grill depletions across all regions (QSR two-brand) | `groupedBar` | HistoricalDemand | 2 | 4 | no | Coastline Tacos, Apex Grill |

## Backend automation

Three test surfaces guard the matrix end-to-end:

1. **`ChartAcceptanceManifestContractTests`** — parses `prompts.ts` and
   `README.md`, then asserts the backend manifest mirrors the Charts category
   (in source order) plus the QSR two-brand comparison, and that every
   manifest prompt is present in the README chart bullet list.
2. **`ChartAcceptanceMatrixTests`** — for every case, builds a representative
   raw tool payload for its `DataSource`, runs it through the same compactors
   production uses, invokes `DeterministicChartBuilder` with the same
   `requestedType` the router-side `ChartRequestDetector` would produce, and
   asserts the resulting `ChartSpec` matches the manifest's chart type,
   `MinSeries`, `MinMarks`, required entities, and (for percent axes) a
   bounded value range. Also verifies `ChartSpecValidator.TryGetRenderable`
   (with the per-case minimums) agrees the chart is bindable.
3. **`ChartAcceptancePerformanceTests`** — for every case, simulates the
   production per-request budget scope: pushes the representative
   invocations through `ToolResultBudget` with a live `RequestToolContext`
   and asserts:

   * `< 25,000` estimated tool-context tokens cumulatively per prompt, and
   * `≤ 5` distinct tool invocations per prompt.

   These ceilings come directly from the issue #50 acceptance criteria and
   lock in the compactor-driven fix (a bounded portfolio aggregate is one
   call, not a per-brand chain).

## Frontend automation

* **`chartAcceptance.contract.test.ts`** mirrors the backend contract test in
  TypeScript so both languages assert the same three surfaces stay in sync.
* **`chartAcceptance.matrix.test.tsx`** builds a representative `ChartSpec`
  per case and mounts `<ChartRenderer />` against **real Recharts** (with a
  `ResponsiveContainer` shim so jsdom produces real bar/line/pie DOM). Every
  case asserts the DOM shows the manifest's `MinMarks`, its `MinSeries` (for
  multi-series shapes), the required entity labels, and — for percent axes —
  bounded Y values.

## CI gate

`.github/workflows/ci.yml` runs both backend and frontend chart-matrix suites
as explicit named steps after the broad test invocations, so a manifest drift
or an under-populated chart trips CI with a targeted signal instead of being
buried in the full test log.

## Live browser verification

The record of the most recent live browser verification is
[`chart-acceptance-run.md`](./chart-acceptance-run.md). To re-run:

1. Start the API (`src/RetailPulse.AppHost` or the `dotnet run` per-project
   equivalents) and the frontend dev server (`cd src/RetailPulse.Web && npm run dev`).
2. Sign in (Anonymous is fine for this) and open the empty-state dashboard.
3. Paste each prompt from the matrix (they're in the curated prompt library
   popover) and confirm the rendered card matches the manifest row: the
   correct chart type, the required entities as legends or categories, and
   populated finite marks (no "Chart unavailable" diagnostic). The QSR
   two-brand comparison is one grouped bar with both brand legends.

If any prompt fails to render as expected, the matrix has drifted from live
behavior — add or tighten a matrix case and let CI enforce it.
