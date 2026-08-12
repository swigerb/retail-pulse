# Chart Acceptance — Live Browser Verification Log

Companion to [`chart-acceptance.md`](./chart-acceptance.md). Every entry in this
log is the observed outcome of pasting the browser runner
([`scripts/browser-chart-acceptance.js`](../scripts/browser-chart-acceptance.js))
into DevTools while the frontend is live and running against the API. The
automated backend + frontend matrix tests already lock in the semantic
invariants — this file is the human sign-off that the wired-up production build
does the same thing end-to-end for each curated prompt.

## How to update this file

1. Start the API (via `RetailPulse.AppHost` or `dotnet run` per project) and
   the frontend (`cd src/RetailPulse.Web && npm run dev`).
2. Sign in (Anonymous is fine for this record) and land on the empty chat.
3. Open DevTools → Console; paste the contents of
   `scripts/browser-chart-acceptance.js`; run `await runChartAcceptance()`.
4. Copy the resulting JSON from the `COPY-TO-DOCS:` log line into the
   "Latest run" section below with the date and the app version (git SHA).

## Latest run

> **Status:** The chart-acceptance matrix landed on `main` via PR #53 ("P0 #50:
> Systemic chart acceptance matrix for all curated prompts") and is enforced on every
> CI run by the backend `ChartAcceptanceMatrixTests` / `ChartAcceptancePerformanceTests`
> and the frontend `chartAcceptance.matrix.test.tsx` suites. This log file remains the
> record of human, in-browser sign-off; the automated matrix is the durable gate.
> Re-run the browser runner (per **How to update this file**) when you want a fresh
> human record and paste the resulting JSON below.

```json
[]
```

## Historical runs

_None yet._
