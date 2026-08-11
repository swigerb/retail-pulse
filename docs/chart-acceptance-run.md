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

> **Status:** pending live sign-off on `squad/50-all-chart-prompt-acceptance`
> for the PR opened against `main`. The automated matrix suites (backend
> `ChartAcceptanceMatrixTests`, backend `ChartAcceptancePerformanceTests`,
> frontend `chartAcceptance.matrix.test.tsx`) all pass on this branch, so the
> production build satisfies the same invariants that the browser runner
> checks. The reviewer approving the PR is expected to record the live
> outcome here (or in a PR comment linked here) before merge.

```json
[]
```

## Historical runs

_None yet._
