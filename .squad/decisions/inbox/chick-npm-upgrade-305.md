# Frontend npm dependency upgrade — issue #305

**Author:** Chick (Frontend Dev)
**Branch:** `squad/305-upgrade-frontend`
**Scope:** `src/RetailPulse.Web/package.json`, `src/RetailPulse.Web/package-lock.json`, and one stale test guard

## Summary

Upgraded direct npm dependencies for the Web SPA to current compatible stable
versions while preserving Node 20 compatibility, closing the vitest CVE
`GHSA-82fw-gwwq-j7x9`, and taking no unvalidated majors. Lockfile was
regenerated with npm 10.8.2 on Linux (WSL Ubuntu 24.04, Node 20.20.2) to
preserve the cross-platform install graph. `npm audit` → **0 vulnerabilities**.

## Deferred majors (documented, not applied)

| Package | Current pin | Latest | Reason for defer |
|---|---|---|---|
| `@azure/msal-browser` | `^4.30.0` | `5.22.x` | v5 removes `storeAuthStateInCookie` (used in `src/auth/authConfig.ts`), restructures `Configuration` (`protocolMode` moves to `SystemOptions`), and requires a dedicated redirect bridge page + COOP/COEP headers on Azure Static Web Apps. This is an infra change, not a dep bump. |
| `@azure/msal-react` | `^3.0.29` | `5.7.x` | v4/v5 pair with `msal-browser@5`. Same infra prerequisites. |
| `typescript` | `~6.0.2` | `7.x` | Major TS release requires a validated migration pass and lint config alignment. Out of scope for a dep bump. |
| `jsdom` | `^29.1.1` | `30.x` | Not validated against Vitest 4 + Fluent UI + Recharts test surface in this PR. |
| `@testing-library/jest-dom` | `^6.9.1` | `7.x` | v7 changes matcher signatures; not validated. |
| `@types/node` | `25.9.6` (exact) | `26.x` | See "proxy-lag exact pins" below. |
| `recharts` | `3.8.1` (exact) | `3.10.1` | 3.10.x pie-chart label rendering regresses `ChartRenderer.acceptance` and `chartAcceptance.matrix` (entity labels like `FreshMart` no longer emitted into the pie-chart DOM). Held at 3.8.1 pending a recharts fix or a chart-side migration. |

## Proxy-lag exact pins (three direct deps + `overrides` block)

The workstation's `.npmrc` requires `registry=https://packagefeedproxy.microsoft.io/npm/`
with `replace-registry-host=always`. That proxy trails `registry.npmjs.org` by
hours-to-days on ~30 packages at the time of this PR. When the latest
satisfying version of a caret-ranged dep is on npm but not yet on the proxy,
`npm ci` 404s. Two mitigations were applied:

1. **Three direct devDependencies are exact-pinned** to a proxy-safe version so
   the semver resolver never picks a version the proxy has not synced:
   - `@types/node` → `25.9.6`
   - `eslint` → `10.10.0`
   - `eslint-plugin-react-refresh` → `0.5.6`
2. **A ~30-entry `overrides` block** pins transitive dependencies (zod,
   tldts, @babel/parser, all `@rolldown/binding-*`, browserslist, rolldown,
   etc.) to the highest same-major version present on the proxy.

**Consequence:** future `npm outdated` will always flag these three direct
pins and every override entry. This is a proxy-lag concession, not a
preference. As the proxy catches up, these pins/overrides can be relaxed to
carets in a follow-up PR — the guard is the `.npmrc` rewrite, not the tree.

## Cross-platform lockfile guard

`src/__tests__/lockfileIntegrity.test.ts` retired its `@emnapi/*` assertions.
Rolldown ≥ 1.2.x no longer ships the `@rolldown/binding-wasm32-wasi` optional
binding, so `@napi-rs/wasm-runtime` and its `@emnapi/core` / `@emnapi/runtime`
peer optionals are no longer pulled into the tree. The auth-package (MSAL)
`sha512` / canonical-URL guards and the "no proxy resolves" guard are
preserved and still pass.

## Validation

- `npm audit` → **0 vulnerabilities** (was 6, incl. vitest `GHSA-82fw-gwwq-j7x9`)
- `npm ci` → 483 packages installed, lockfile integrity verified
- `npm run typecheck` → PASS
- `npm run build` → PASS (Vite 8.3.0, bundle size stable)
- `npm test` → **924 / 924 tests pass** across 99 files (baseline 926 → 924
  from the two retired `@emnapi` guards)
- `npm run test:provider-matrix` → PASS
- `npm run test:provider-matrix:full` → PASS

## Files touched (allow-list)

- `src/RetailPulse.Web/package.json`
- `src/RetailPulse.Web/package-lock.json`
- `src/RetailPulse.Web/src/__tests__/lockfileIntegrity.test.ts`
- `.squad/decisions/inbox/chick-npm-upgrade-305.md` (this file)

Nothing in `Directory.Packages.props`, any backend `.csproj`, any
`.github/workflows/*`, or any other `.squad/` file was modified.
