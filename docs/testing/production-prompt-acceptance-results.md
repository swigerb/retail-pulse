# Production Prompt Ideas — Live Acceptance Results

Companion log to [`production-prompt-acceptance-matrix.md`](./production-prompt-acceptance-matrix.md).

Each entry is the observed outcome of the paste-in browser runners
(`scripts/browser-chart-acceptance.js` for the 9 CHART cases and
`scripts/browser-prompt-library-acceptance.js` for the 17 PROSE cases) run in
DevTools while signed in at
[https://calm-wave-04edb640f.7.azurestaticapps.net/](https://calm-wave-04edb640f.7.azurestaticapps.net/).

The backend + frontend matrix suites already lock in the semantic invariants
per [`production-prompt-acceptance-matrix.md`](./production-prompt-acceptance-matrix.md)
— this file is the human sign-off that the wired-up production build satisfies
all 26 curated entries end-to-end.

## How to update this file

Per issue #63 acceptance criteria, the **per-prompt sweep report** is posted
as a **comment on #59** (markdown table, no screenshots committed) — not
into this file. This file records only the summary counts and app version.

1. Sign in at the production SWA URL above (interactive Entra as Publix, or
   the service-principal replay once #57 lands).
2. Note the deployed app version (git SHA — the first 7 chars of the
   `assets/index-<sha>.js` file name in the page source suffice).
3. Open DevTools → Console.
4. Paste `scripts/browser-chart-acceptance.js` and run
   `await runChartAcceptance()`. Verify 9/9 PASS. Verify G3 (horizontal-bar
   depletion-growth prompt renders `horizontalBar` with ≥6 finite marks).
5. Paste `scripts/browser-prompt-library-acceptance.js` and run
   `await runPromptLibraryAcceptance()`. Verify 17/17 PASS.
6. Post the per-prompt markdown table (both runs) as a **comment on #59**.
   Do NOT commit the `COPY-TO-DOCS:` payloads, screenshots, or console dumps.
7. Fill in the summary block below with the app version, date, identity
   provider, tester, summary counts, G3 verdict, and the link to the #59
   comment.
8. **Never** paste tokens, subscription keys, or the `.auth/me` payload into
   this file, into a PR body, or into a #59 comment — this file is committed
   and the comment is public in the repo.

## Latest run

> **Status:** ⛔ **NOT YET RUN in production.** The automated backend matrix
> (`ProductionPromptAcceptanceTests` + the pre-existing
> `ChartAcceptanceMatrixTests` / `ChartAcceptancePerformanceTests` /
> `ChartAcceptanceManifestContractTests`) already passes on this branch
> against the production prompt source, but an authenticated end-to-end sweep
> against the deployed SWA → ACA → APIM → AOAI stack has **not been
> executed**. **No production PASS is claimed until this block is filled in.**
>
> **Durable blocker:** the CLI environment executing this branch has no
> interactive Entra sign-in, no service-principal token for the SWA, and no
> headless-browser hook to acquire one. Per squad `secret-handling` no token,
> `.auth/me` payload, or subscription key is printed or committed. Issue #63
> notes this work blocks on #57 (service-principal synthetic monitor) for
> automated runs; until #57 lands, the manual path requires a human signed in
> as Publix. Both runners in `scripts/` are wired up and ready — they just
> need an authenticated browser session.
>
> The reviewer approving the PR must complete both live runs (per the
> checklist in the PR body and the acceptance criteria on #63) and post the
> per-prompt report as a **comment on #59** — **NOT** into this file — before
> merge. This file records only the summary counts and the app version.

- **Date (UTC):** _pending live run_
- **App version (git SHA):** _pending live run_
- **Sign-in identity provider:** _pending live run_
- **Tester:** _pending live run_
- **Chart summary:** _pending — expected 9/9 PASS_
- **Prose summary:** _pending — expected 17/17 PASS_
- **G3 (horizontal-bar depletion growth, ≥6 marks):** _pending — required by #63_
- **Report comment on #59:** _pending link_

## Historical runs

_None yet._
