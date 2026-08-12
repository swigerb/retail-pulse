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
2. **Sign in with a provider that permits chat** (`Entra` in Production, or
   `Anonymous` / `GitHub` in a non-live dev build with the matching example env
   template). `Anonymous` disables the SignalR realtime hub by capability, so
   `progress`/`SpanReceived`/`trace_*` events won't fire during the run — the
   chart-acceptance runner still passes because it only inspects the rendered
   `ChartRenderer` output, but note it in the entry if you want the telemetry
   pane populated. Do **not** run this in an unauthenticated build: the API's
   `RequireAuth=true` production posture returns 401 for `/api/chat` and no
   chart will ever render.
3. Land on the empty chat.
4. Open DevTools → Console; paste the contents of
   `scripts/browser-chart-acceptance.js`; run `await runChartAcceptance()`.
5. Copy the resulting JSON from the `COPY-TO-DOCS:` log line into the
   "Latest run" section below with the date, provider mode, and app version
   (git SHA).

## Latest run

> **Status:** The chart-acceptance matrix landed on `main` via PR #53 ("P0 #50:
> Systemic chart acceptance matrix for all curated prompts") and is enforced on every
> CI run by the backend `ChartAcceptanceMatrixTests` / `ChartAcceptancePerformanceTests`
> and the frontend `chartAcceptance.matrix.test.tsx` suites. Those tests are the
> durable gate — CI will fail before merge if any curated prompt regresses.
>
> The JSON block below is an intentional **empty-log placeholder**, not a failed run.
> Live browser sign-off is optional and only needs to be captured when an operator
> exercises the DevTools runner against a deployed build; the automated matrix
> guards the invariants regardless. When you do run the browser runner, replace
> the `[]` below with the JSON emitted by the `COPY-TO-DOCS:` log line and record
> the date, provider mode, and app version (git SHA) alongside it.

```json
[]
```

## Historical runs

_None yet._
