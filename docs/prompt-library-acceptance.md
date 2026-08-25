# Prompt Library Acceptance — Full Curated Matrix

This document is the durable, human-readable index of the **full curated
prompt library acceptance matrix** the codebase enforces automatically for
issue [#63](https://github.com/swigerb/retail-pulse/issues/63). It
complements — and is verified against — the machine sources of truth:

* **Prompt source** — `src/RetailPulse.Web/src/constants/prompts.ts`
  (`PROMPT_CATEGORIES`; every category, every entry).
* **Backend chart manifest** — `src/RetailPulse.Contracts/Charts/ChartAcceptanceManifest.cs`
  (Charts category + the QSR two-brand comparison).
* **Backend prose manifest** — `src/RetailPulse.Contracts/Prompts/ProsePromptAcceptanceManifest.cs`
  (every non-chart entry from every remaining category).

Together the two backend manifests mirror the frontend prompt source
exactly. Contract tests fail CI immediately if either manifest drifts.

## Why a full-library matrix

`ChartAcceptanceManifest` already gates the nine curated chart prompts. But
17 curated PROSE prompts across six domain categories (General, Grocery,
QSR, Home Improvement, Office Supply, Furniture) were only guarded by
generic router unit tests — no acceptance contract asserted their routing,
tool-invocation, or "no chart JSON leak" invariants as a set. Issue #63
closed that gap by adding an **equivalent contract matrix for every prose
prompt** and a browser sweep script that exercises them live.

Each prose case declares the invariants a live invocation MUST satisfy:

| Field | Meaning |
| ----- | ------- |
| `Prompt` | Verbatim prompt text (must exist in `prompts.ts`). |
| `CategoryId` | The `PROMPT_CATEGORIES` id the prompt belongs to. |
| `ExpectedIntent` | The `AgentIntent` the router MUST classify to (never `PortfolioHealth`). |
| `ExpectedAgentKey` | DI key of the specialist that must own the request. Must have ≥1 tool in `prompts.yaml`. |
| `Rationale` | Short note on why this routing is expected. |

## The prose matrix (issue #63)

| # | Category | Prompt | Expected intent | Expected agent |
| - | -------- | ------ | --------------- | -------------- |
| 1 | general | Compare depletion trends across all regions for this quarter | `demand/forecasting` | `demand-forecasting` |
| 2 | general | Which brands are growing fastest year-over-year across the portfolio? | `general/fallback` | `general` |
| 3 | general | Show me field sentiment for our top 3 brands in the Southeast | `sentiment/field` | `field-sentiment` |
| 4 | grocery | How are FreshMart depletions trending in the Northeast this quarter? | `general/fallback` | `general` |
| 5 | grocery | Compare Harvest Table vs FreshMart sell-through rates by region | `demand/forecasting` | `demand-forecasting` |
| 6 | grocery | What is the field sentiment for Harvest Table Meal Kits in the Midwest? | `sentiment/field` | `field-sentiment` |
| 7 | qsr | How is Apex Grill performing in the Southwest this quarter? | `general/fallback` | `general` |
| 8 | qsr | What is the field sentiment for Coastline Tacos in the West Coast? | `sentiment/field` | `field-sentiment` |
| 9 | home-improvement | Show me Pinnacle Hardware depletion stats in the Midwest for Q1 | `general/fallback` | `general` |
| 10 | home-improvement | How is Summit Outdoor performing in the Southeast vs West Coast? | `general/fallback` | `general` |
| 11 | home-improvement | What is the field sentiment for Pinnacle Hardware Power Tools in the Southwest? | `sentiment/field` | `field-sentiment` |
| 12 | office-supply | How are ClearDesk depletions trending in the Northeast this quarter? | `general/fallback` | `general` |
| 13 | office-supply | Compare ClearDesk Technology vs Paper Products sell-through by region | `demand/forecasting` | `demand-forecasting` |
| 14 | office-supply | What is the field sentiment for ClearDesk in the Southeast? | `sentiment/field` | `field-sentiment` |
| 15 | furniture | Show me Urban Living depletion trends across all regions this quarter | `general/fallback` | `general` |
| 16 | furniture | Compare Foundry Home vs Urban Living performance in the West Coast | `demand/forecasting` | `demand-forecasting` |
| 17 | furniture | What is the field sentiment for Urban Living in the Pacific Northwest? | `sentiment/field` | `field-sentiment` |

The QSR two-brand chart comparison prompt (`Compare Coastline Tacos vs Apex
Grill …`) is intentionally excluded from this matrix — it is chart-owned and
covered by `ChartAcceptanceManifest` / `ChartAcceptanceMatrixTests`.

## Backend automation

Two test surfaces guard the prose matrix end-to-end:

1. **`ProsePromptAcceptanceManifestContractTests`** — parses
   `prompts.ts` and asserts the prose manifest mirrors every non-chart
   category prompt exactly (in source order), that the QSR chart comparison
   is not present, and that every manifest case declares coherent semantics
   (non-empty prompt/intent/agent-key, category id in the covered list,
   never `PortfolioHealth`, no duplicate prompts).

2. **`ProsePromptRoutingAcceptanceTests`** — for every case:

   * Routes the prompt through the same `RetailOpsRouter` production uses
     (with the LLM classifier mocked to return the expected intent, so the
     router's fast-path / cache / LLM pipeline stays honest end-to-end) and
     asserts `Intent == ExpectedIntent`, `AgentKey == ExpectedAgentKey`,
     and both are ≠ the council.
   * Asserts `ChartRequestDetector.Detect(prompt).IsExplicitChartRequest`
     is **false** — the chart fast-path must never hijack a prose curated
     prompt (that is the mechanism by which chart JSON could ever land in
     a prose answer).
   * Parses `src/RetailPulse.Api/prompts.yaml` and asserts the expected
     agent key resolves to a specialist with a **non-empty `tools:` list**,
     so a live invocation will always have at least one tool to call and
     cannot produce an empty prose reply for lack of a tool.

Both suites are unit-scale (no I/O beyond reading `prompts.ts` and
`prompts.yaml`) and run in the standard `dotnet test` invocation.

## Live browser verification

The sibling script
[`scripts/browser-prompt-library-acceptance.js`](../scripts/browser-prompt-library-acceptance.js)
walks every prose case in DevTools using the same stable `data-testid`
selectors the chart runner uses (`chat-input`, `chat-send-button`,
`chat-message-assistant`, `chart-card`, `chart-unavailable`). For each
prompt it asserts the assistant produced a non-empty prose reply, that no
`chart-card` was rendered, that no chart JSON leaked as raw text
(defence-in-depth against a specialist that inlines a chart spec in prose),
and that the routing pill — when present — is not the Consensus Council.

To run:

1. Start the API (`RetailPulse.AppHost` or per-project `dotnet run`) and
   the frontend (`cd src/RetailPulse.Web && npm run dev`).
2. Sign in with a chat-capable provider (`Entra` in Production, or
   `Anonymous` / `GitHub` in a dev build).
3. Open DevTools → Console; paste the runner's contents; run
   `await runPromptLibraryAcceptance()`.
4. Copy the `COPY-TO-DOCS:` JSON block into the issue #63 comment on
   parent #59.

## Relationship to the chart matrix

The chart matrix (`docs/chart-acceptance.md`) and this prose matrix are
sibling surfaces. Together they cover **every entry** in
`PROMPT_CATEGORIES`:

* Chart matrix — 8 Charts-category prompts + 1 QSR two-brand comparison.
* Prose matrix — 17 non-chart curated prompts across the six domain
  categories.

Backend contract tests enforce that the union of the two manifests
mirrors `prompts.ts` exactly, so a new curated prompt landed in
`prompts.ts` fails CI until it is added to one manifest or the other.
