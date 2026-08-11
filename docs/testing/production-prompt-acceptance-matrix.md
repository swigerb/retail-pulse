# Production Prompt Ideas — Acceptance Matrix

Companion to [`chart-acceptance.md`](../chart-acceptance.md) and
[`chart-acceptance-run.md`](../chart-acceptance-run.md). Where the chart
acceptance suite locks in the 9-prompt chart matrix, this document locks in
the **full curated "Prompt ideas" library** — every entry the SPA popover
exposes across every category and every tenant/domain variant — with the
response-class contract each one must satisfy in production.

Single source of truth for the prompt text: `src/RetailPulse.Web/src/constants/prompts.ts`.
This document is generated once from that source and drift-guarded by
`ProductionPromptAcceptanceTests` (backend, xUnit).

## Enumeration

**Category count:** 7 · **Total curated prompts:** 26

| # | Category | Label | Prompts |
|---|---|---|---|
| 1 | `general` | General Retail | 3 |
| 2 | `grocery` | Grocery | 3 |
| 3 | `qsr` | Quick-Serve Restaurants | 3 |
| 4 | `home-improvement` | Home Improvement | 3 |
| 5 | `office-supply` | Office Supply | 3 |
| 6 | `furniture` | Furniture | 3 |
| 7 | `charts` | Charts | 8 |

## Response classes

Each curated prompt has one of two contract-tested response classes:

- **CHART** — the request MUST produce a rendered `ChartSpec` (real Recharts
  canvas / real table) that satisfies the semantics in
  `RetailPulse.Contracts.Charts.ChartAcceptanceManifest`.
- **PROSE** — the request MUST produce a narrative response only. No chart
  card, no `[role="note"]` diagnostic, no leaked chart JSON, no assistant
  fallback error.

## Acceptance matrix

| # | Category | Prompt | Class | Chart type | Notes / expected entities |
|---|---|---|---|---|---|
| 1  | general          | Compare depletion trends across all regions for this quarter | PROSE | — | Narrative comparison across seeded regions |
| 2  | general          | Which brands are growing fastest year-over-year across the portfolio? | PROSE | — | Portfolio ranking narrative |
| 3  | general          | Show me field sentiment for our top 3 brands in the Southeast | PROSE | — | Sentiment narrative, top-3 brands |
| 4  | grocery          | How are FreshMart depletions trending in the Northeast this quarter? | PROSE | — | Single-brand trend narrative |
| 5  | grocery          | Compare Harvest Table vs FreshMart sell-through rates by region | PROSE | — | Two-brand narrative comparison (no explicit chart noun) |
| 6  | grocery          | What is the field sentiment for Harvest Table Meal Kits in the Midwest? | PROSE | — | Variant-level sentiment narrative |
| 7  | qsr              | How is Apex Grill performing in the Southwest this quarter? | PROSE | — | Single-brand performance narrative |
| 8  | qsr              | Compare Coastline Tacos vs Apex Grill depletions across all regions | **CHART** | groupedBar | Implicit comparison → grouped bar (Coastline Tacos, Apex Grill) |
| 9  | qsr              | What is the field sentiment for Coastline Tacos in the West Coast? | PROSE | — | Sentiment narrative |
| 10 | home-improvement | Show me Pinnacle Hardware depletion stats in the Midwest for Q1 | PROSE | — | Single-brand stats narrative |
| 11 | home-improvement | How is Summit Outdoor performing in the Southeast vs West Coast? | PROSE | — | Two-region comparison narrative |
| 12 | home-improvement | What is the field sentiment for Pinnacle Hardware Power Tools in the Southwest? | PROSE | — | Category-level sentiment narrative |
| 13 | office-supply    | How are ClearDesk depletions trending in the Northeast this quarter? | PROSE | — | Single-brand trend narrative |
| 14 | office-supply    | Compare ClearDesk Technology vs Paper Products sell-through by region | PROSE | — | Category-level comparison narrative |
| 15 | office-supply    | What is the field sentiment for ClearDesk in the Southeast? | PROSE | — | Sentiment narrative |
| 16 | furniture        | Show me Urban Living depletion trends across all regions this quarter | PROSE | — | Single-brand trend narrative |
| 17 | furniture        | Compare Foundry Home vs Urban Living performance in the West Coast | PROSE | — | Two-brand narrative comparison |
| 18 | furniture        | What is the field sentiment for Urban Living in the Pacific Northwest? | PROSE | — | Sentiment narrative |
| 19 | charts           | Create a line chart showing Sierra Gold Tequila depletion trends across all regions | **CHART** | line | ≥1 series, ≥2 marks, entity: Sierra Gold Tequila |
| 20 | charts           | Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast | **CHART** | bar | ≥1 series, ≥3 marks, entities: Sierra Gold Tequila, Ridgeline Bourbon, Summit Vodka |
| 21 | charts           | Create a pie chart showing market share breakdown for our grocery brands nationally | **CHART** | pie | ≥2 marks, percent axis, entities: FreshMart, Harvest Table |
| 22 | charts           | Show a grouped bar chart comparing FreshMart and Harvest Table across all regions | **CHART** | groupedBar | ≥2 series, ≥12 marks, entities: FreshMart, Harvest Table |
| 23 | charts           | Create a donut chart of Apex Grill variant mix in the Southwest | **CHART** | donut | ≥2 marks, percent axis, entity: Apex Grill |
| 24 | charts           | Show a horizontal bar chart ranking all brands by depletion growth rate | **CHART** | horizontalBar | ≥6 marks, percent axis |
| 25 | charts           | Create a table showing depletion stats for all home improvement brands by region | **CHART** | table | ≥2 rows, entities: Pinnacle Hardware, Summit Outdoor |
| 26 | charts           | Show a gauge chart for Pinnacle Hardware inventory health in the Midwest | **CHART** | gauge | entity: Pinnacle Hardware |

**Counts:** 9 CHART · 17 PROSE · 26 total.

## Universal invariants (all classes)

Every curated prompt — regardless of response class — must additionally satisfy:

- **No JSON leakage:** the assistant response text MUST NOT contain a raw
  `{"type":"…","title":"…","data":…}` chart JSON payload
  (the sanitizer parity across `RetailPulse.Api/Charts/ChartSpecNormalizer.cs`
  and `RetailPulse.Web/src/utils/sanitizeMessage.ts` guarantees this).
- **No unhandled fallback error:** the assistant text MUST NOT surface any of
  "I couldn't complete that request", "internal server error",
  "unhandled error", "something went wrong".
- **Bounded tool calls:** ≤ 5 distinct tool calls per prompt
  (`ChartAcceptancePerformanceTests` enforces this for chart cases; the
  general `ToolResultBudgetOptions` cap applies to prose cases via the same
  compactor path).
- **Bounded tool-context tokens:** < 25,000 estimated tool-context tokens per
  prompt (same performance suite; see also `docs/tool-context-budget.md`).
- **Polling & selectors:** browser sweeps poll `[class*="chartCard"]`,
  `[role="note"]`, and streaming assistant bubbles — the runners under
  `scripts/browser-chart-acceptance.js` and
  `scripts/browser-prompt-library-acceptance.js` are the canonical polling +
  stable-selector implementation.

## Independent test coverage

| Layer | Suite | Covers |
|---|---|---|
| Backend (xUnit) | `ChartAcceptanceMatrixTests` | Render invariants for the 9 CHART cases (owned by Chick/Costco) |
| Backend (xUnit) | `ChartAcceptancePerformanceTests` | Tool-call ≤5 and tool-context <25K token ceilings per CHART case |
| Backend (xUnit) | `ChartAcceptanceManifestContractTests` | Chart manifest / prompt source / README drift guard |
| Backend (xUnit) | **`ProductionPromptAcceptanceTests`** (this branch) | 26-prompt enumeration + response-class classification for **every** curated entry via `ChartRequestDetector` |
| Frontend (vitest) | `chartAcceptance.matrix.test.tsx` | Real-Recharts render suite for the 9 CHART cases |
| Frontend (vitest) | `chartAcceptance.contract.test.ts` | Chart manifest / prompt source drift guard |
| Frontend (vitest) | `PromptLibrary.test.tsx` | UI enumeration / open-close / keyboard contract |
| Live sweep | `scripts/browser-chart-acceptance.js` | 9 CHART prompts against a live browser |
| Live sweep | `scripts/browser-prompt-library-acceptance.js` (this branch) | 17 PROSE prompts against a live browser |

## Production sweep procedure

Per issue #63, the per-prompt sweep report is posted as a **comment on
issue #59** (markdown table, no screenshots committed). The summary counts
and app version are recorded in
[`production-prompt-acceptance-results.md`](./production-prompt-acceptance-results.md).

1. Sign in at [https://calm-wave-04edb640f.7.azurestaticapps.net/](https://calm-wave-04edb640f.7.azurestaticapps.net/)
   as Publix (interactive Entra) or via service-principal replay once #57 lands.
2. Open DevTools → Console.
3. Paste `scripts/browser-chart-acceptance.js`, run
   `await runChartAcceptance()`. Verify 9/9 PASS.
4. Verify **G3** explicitly: the horizontal-bar depletion-growth prompt
   renders `horizontalBar` with ≥ 6 finite marks. Regression = P0.
5. Paste `scripts/browser-prompt-library-acceptance.js`, run
   `await runPromptLibraryAcceptance()`. Verify 17/17 PASS.
6. Post the per-prompt markdown table (both runs) as a comment on **#59**.
   Do NOT commit `COPY-TO-DOCS:` payloads, screenshots, or console dumps.
7. Update `production-prompt-acceptance-results.md` with the summary block
   only (app version, date, identity provider, tester, counts, G3 verdict,
   link to the #59 comment).
8. Any failure blocks merge. Report the exact failing prompt(s), the
   observed `failures` array, and the app version. Never print or commit
   tokens, subscription keys, or the `.auth/me` payload.
