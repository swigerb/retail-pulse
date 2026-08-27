# Retail Pulse Web (React + TypeScript + Vite)

The Retail Pulse React dashboard: chat panel, real-time SignalR telemetry, chart
rendering, and observability views. Runs on Vite + React 19 + TypeScript against the
`RetailPulse.Api` backend. The Aspire AppHost launches this project automatically
(`AddNpmApp` → `npm run dev`), so day-to-day contributors do not need to start it by
hand.

See also:

- [FRONTEND.md](../../docs/FRONTEND.md) — architecture, component catalog, and provider-neutral sign-in.
- [authentication-matrix.md](../../docs/authentication-matrix.md) — full mode / environment behavior table.
- [ADR-005](../../docs/adr/005-provider-neutral-authentication.md) — provider-neutral authentication foundation.

## Feature flags / Navigation

Retail Pulse keeps the default web navigation focused on **Chat**, **Real-Time Telemetry**, and **Observability**. Real-Time Telemetry is always visible and streams live agent spans, token totals, and cost estimates. Observability is enabled by default because it shows the AI Gateway via Azure APIM story: costs, token usage, and operational metrics.

Secondary demo tabs are configuration-gated and hidden by default. To enable optional tabs locally, copy `.env.example` to `.env.local` and set the matching `VITE_FEATURE_*` flag to `true` or `1`.

Available flags:

| Flag | Default | Tab |
|------|---------|-----|
| `VITE_FEATURE_CAMPAIGN_PLANNER` | `false` | Campaign Planner |
| `VITE_FEATURE_COMPETITIVE` | `false` | Competitive |
| `VITE_FEATURE_KNOWLEDGE_BASE` | `false` | Knowledge Base |
| `VITE_FEATURE_HEALTH_COUNCIL` | `false` | Health Council |
| `VITE_FEATURE_SECURITY` | `false` | Security |
| `VITE_FEATURE_CARDS` | `false` | Cards |
| `VITE_FEATURE_STORES` | `false` | Stores |
| `VITE_FEATURE_FINANCIALS` | `false` | Financials |
| `VITE_FEATURE_PORTFOLIO` | `false` | Portfolio |
| `VITE_FEATURE_OBSERVABILITY` | `true` | Observability |

## Supply chain (npm through the internal proxy)

The official public npm registry (`registry.npmjs.org`) is **unreachable in our environment
and must not be contacted**. All installs are reproducible from the committed
`package-lock.json` and routed through the internal Microsoft package feed proxy, configured in
[`.npmrc`](./.npmrc):

- `registry=https://packagefeedproxy.microsoft.io/npm/` sends every metadata + tarball fetch to
  the proxy.
- `replace-registry-host=always` rewrites the **host** of each canonical `registry.npmjs.org`
  `resolved` URL recorded in the lockfile to the proxy host at fetch time. The committed
  `sha512` `integrity` hashes are still enforced against the downloaded bytes.

Consequences and rules:

- Install with **`npm ci`** only (locally and in CI). Do **not** use `npm install`,
  `--no-package-lock`, or caret re-resolution, and never regenerate the lockfile against
  `registry.npmjs.org`.
- The committed lockfile records **canonical `registry.npmjs.org` `resolved` URLs with `sha512`
  integrity** for every package (never sha1, never unpinned). `npm ci` verifies the tree against
  it and **never rewrites it**; CI additionally runs `git diff --exit-code -- package-lock.json`
  right after the install as an integrity guard proving the install did not mutate the lockfile.
- The lockfile is deliberately kept **cross-platform complete**: it includes the optional peer
  dependencies (`@emnapi/core`, `@emnapi/runtime`, pulled in by the wasm fallback
  `@napi-rs/wasm-runtime`) so `npm ci` reconstructs the exact tree on Linux CI runners as well as
  on Windows. These entries carry canonical `resolved` URLs and `sha512` integrity computed from
  the tarball bytes served by the sanctioned internal feed, pinning that reviewed tree for
  reproducible future installs.
- `src/__tests__/lockfileIntegrity.test.ts` fails the build if any package regresses to
  non-`sha512` integrity or a non-canonical `resolved` URL — no supply-chain weakening can land.

Prove the proxy is the only host contacted (no canonical registry hostname appears):

```bash
npm ci --loglevel=http 2>&1 | grep -Eo 'https?://[^/]+' | sort -u
# → only https://packagefeedproxy.microsoft.io ; zero registry.npmjs.org hits
```

## Authentication provider matrix & production verification

All three sign-in providers (Entra / GitHub / Anonymous) are **statically bundled**, so a
runtime "string absence" scan can never prove which mode a deployed SPA was built for. Instead
the Vite build bakes an **immutable, build-time marker** into `index.html` from
`VITE_AUTH_MODE`:

```html
<meta name="retail-pulse-auth-mode" content="Entra" />
```

- The value is normalized to a fixed enum (`Entra` / `GitHub` / `Anonymous`) at build time, so
  it cannot carry injected markup and cannot be overridden at runtime.
- [`scripts/auth-mode-meta.mjs`](./scripts/auth-mode-meta.mjs) is the single source of truth for
  the parser (`parseAuthModeMeta`) and predicate (`isProductionEntra`). The build plugin, the
  provider matrix, and the PowerShell production verifier all apply the **same** logic.
- `npm run test:provider-matrix` runs the fail-closed config gate for every mode **and** builds
  Entra, GitHub, and Anonymous, asserting on each **emitted** `index.html` that the marker is
  correct and that only Entra satisfies `isProductionEntra` (GitHub/Anonymous/empty/missing
  fail). `src/__tests__/authModeMeta.test.ts` covers the shared parser/predicate behaviorally.
- The read-only [`scripts/Verify-ProductionAuth.ps1`](../../scripts/Verify-ProductionAuth.ps1)
  fetches the live SWA root and asserts the marker is exactly `Entra`; an empty/non-200/missing/
  malformed marker is a failure. It exposes independent `-SkipHttpProbes` (live API status
  probes only) and `-SkipSpaInspection` (SWA marker check only) switches.

## Visual Studio integration

This project appears in the `RetailPulse.slnx` solution via a JavaScript project
file (`RetailPulse.Web.esproj`, using `Microsoft.VisualStudio.JavaScript.SDK`) so
it shows up in Solution Explorer. The project is **built and run with npm/Vite,
not with `dotnet`**:

- At runtime the Aspire AppHost launches it (`AddNpmApp` → `npm run dev`).
- CI installs from the committed lockfile with `npm ci` (through the internal package
  feed proxy — see **Supply chain** above) and then runs `npm run build`.

The `.esproj` is intentionally configured as visibility-only
(`ShouldRunNpmInstall=false`, `ShouldRunBuildScript=false`), so a solution-wide
`dotnet build`/`dotnet restore` (and the .NET CI job) never invokes Node/npm.
Use the npm scripts below (or the VS UI) to install, build, run, and test the app.
VS users need the Node.js workload installed locally.

## npm scripts

| Script | Purpose |
|--------|---------|
| `npm ci` | Reproducible install from `package-lock.json` (routed through the internal proxy). |
| `npm run dev` | Vite dev server on `http://localhost:5173` (started automatically by Aspire). |
| `npm run build` | Production build → `dist/`. Runs `prebuild` (auth config gate) first. |
| `npm test` | Vitest test runner (unit + component + contract). |
| `npm run test:provider-matrix` | Full provider-neutral auth matrix — config gate + Entra/GitHub/Anonymous builds + emitted-`index.html` meta assertions. |
| `npm run test:provider-matrix:gate` | Fast config gate only (skips full builds). |

## Reference

- [Vite](https://vite.dev/) — build tool and dev server.
- [React 19](https://react.dev/) — UI framework.
- [Recharts](https://recharts.org/) — chart rendering.
- [Fluent UI React v9](https://react.fluentui.dev/) — design system.
- [Microsoft SignalR client](https://learn.microsoft.com/aspnet/core/signalr/javascript-client) — real-time telemetry hubs.
- [MSAL React](https://learn.microsoft.com/entra/identity-platform/tutorial-single-page-app-react-prepare-app) — Entra sign-in in `Entra` mode.
