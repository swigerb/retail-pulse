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

1. Sign in at the production SWA URL above.
2. Note the deployed app version (git SHA — the first 7 chars of the
   `assets/index-<sha>.js` file name in the page source suffice).
3. Open DevTools → Console.
4. Paste `scripts/browser-chart-acceptance.js` and run
   `await runChartAcceptance()`. Copy the `COPY-TO-DOCS:` JSON into the
   "Chart results" block below.
5. Paste `scripts/browser-prompt-library-acceptance.js` and run
   `await runPromptLibraryAcceptance()`. Copy the `COPY-TO-DOCS:` JSON into
   the "Prose results" block below.
6. Fill in the summary counts and, if any entry fails, keep this file open
   as evidence for the blocking-review workflow.
7. **Never** paste screenshots or console dumps that contain access tokens,
   subscription keys, or the `.auth/me` payload — this file is committed.

## Latest run

> **Status:** pending live sign-off on `squad/61-production-prompt-acceptance`.
> The automated backend matrix (`ProductionPromptAcceptanceTests` + the
> pre-existing `ChartAcceptanceMatrixTests` / `ChartAcceptancePerformanceTests`
> / `ChartAcceptanceManifestContractTests`) already passes on this branch
> against the production prompt source. The reviewer approving the PR must
> record the two live runs below (or link a PR comment that contains them)
> before merge. Any failure is a blocking defect: open a focused GitHub issue,
> report the exact failing prompt(s), and DO NOT merge.

- **Date (UTC):** _pending_
- **App version (git SHA):** _pending_
- **Sign-in identity provider:** _pending_
- **Tester:** _pending_
- **Summary:** _pending — expected 9 CHART PASS + 17 PROSE PASS = 26/26_

### Chart results (9 curated CHART cases)

```json
[]
```

### Prose results (17 curated PROSE cases)

```json
[]
```

## Historical runs

_None yet._
