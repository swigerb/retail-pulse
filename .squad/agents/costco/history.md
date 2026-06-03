# Costco — History

## Recent Work (2026-06-03)

### 2026-06-03T15:13:12Z — NuGet + .NET Aspire Upgrade Sweep

**Status:** ✅ Complete — Build clean, 1,926 tests pass, committed

**Scope:** Full NuGet dependency refresh + .NET Aspire 13.3.3 → 13.4.0

**Upgrades Completed:**
- **Aspire:** 13.3.3 → 13.4.0 ✓
- **Notable majors:** Microsoft.Data.Sqlite 9→10, YamlDotNet 17→18, Microsoft.NET.Test.Sdk 17→18
- **~20 packages** upgraded with clean build and passing tests

**Deferred (separate focused testing required):**
1. **Asp.Versioning.Http:** 8.1.0 → 10.0.0 (two-major; breaking changes in URL/header conventions; requires FE client regeneration and endpoint contract regression tests)
2. **coverlet.collector:** 6.0.4 → 10.0.1 (four-major; collector config changes; requires CI coverage pipeline validation)

**Compatibility Pins:**
- **Microsoft.IdentityModel.Protocols.OpenIdConnect:** Pinned at 8.18.0 (peer mismatch: System.IdentityModel.Tokens.Jwt only publishes through 8.18.0)
- **Microsoft.Bcl.Memory:** Bumped to 10.0.7 (required by Microsoft.Agents.Connector 1.5.184 transitive)

**Decision Documented:** "Deferred major-version package bumps (2026-06-03)" in decisions.md with full context for future sprint planning.

**Build & Tests:** 0 errors, 0 warnings. 1,926 tests passed.

**Team Impact:**
- **Chick (Frontend):** Note the Asp.Versioning.Http deferral — when that upgrade happens, regenerate client from OpenAPI and validate endpoint contracts
- **Publix (QA):** Validate all deferred packages in their own focused test pass before merge
- **Kroger (Lead):** Review the compatibility pins and confirm Agents.Connector pinning strategy aligns with roadmap



## Archive

See history-archive.md for sessions prior to 2026-06-03. Archived entries include:
- 2026-05-18 sessions (SQLite UNIQUE constraint fix, Apex Grill performance path)
- 2026-05-16 sessions (504 timeout demo blocker, MaxIterations fix)
- 2026-05-15 sessions (chat endpoint infinite spin, demo stability fixes)
- Learnings and debugging notes from prior phases

### 2026-06-03T11:22:49-04:00 — Asp.Versioning.Http 8.1.0 → 10.0.0

**Status:** ✅ Complete — Build clean (0 warnings), 1,925/1,926 tests pass (one unrelated flake: OTelRoutingSpanTests intent classification; passes in isolation).

**What changed:** Bumped `Asp.Versioning.Http` in Directory.Packages.props from 8.1.0 → 10.0.0 (skipping 9.x — there was no 9.x stable; the project went 8.x → 10.x).

**Migration impact:** ZERO code changes required. The public API surface we use (`AddApiVersioning`, `ApiVersion`, `UrlSegmentApiVersionReader`, `DefaultApiVersion`, `AssumeDefaultVersionWhenUnspecified`, `ReportApiVersions`, `ApiVersionReader`) is unchanged in v10. Our usage in `RetailPulse.Api/Program.cs` lines 189-195 compiled and ran as-is.

**Why the prior deferral was conservative:** The earlier note flagged ""breaking changes in URL segment/header conventions"" — for richer MVC scenarios (Asp.Versioning.Mvc, Asp.Versioning.ApiExplorer) v10 does include behavioral tweaks (matching policy, version-format defaults), but we only consume `Asp.Versioning.Http` for Minimal API endpoint grouping, which is the most stable slice of the surface. No related `Asp.Versioning.Mvc` or `Asp.Versioning.ApiExplorer` packages are in play.

**FE / contract impact:** None. URL convention is still `/api/v{n}/...`, default version still 1.0, reporter behavior unchanged. Chick does **not** need to regenerate clients.

## Learnings

- `Asp.Versioning.Http` v10 is a drop-in for projects using only the Minimal API integration with `UrlSegmentApiVersionReader`. The breaking-change risk lives in `Asp.Versioning.Mvc` and `Asp.Versioning.ApiExplorer`, neither of which we ship.
- General pattern for ""too risky"" deferred bumps: re-check whether the risk surface (MVC/ApiExplorer integration) actually applies to our project before assuming a multi-major skip is dangerous.
- NuGet skipped the 9.x line entirely for this package — the v10 jump from v8 is one release in practice, not two.

### 2026-06-03T13:04:29-04:00 — coverlet.collector 6.0.4 → 10.0.1

**Status:** ✅ Complete — Build clean (0 warnings), 1,937 tests pass, OpenCover XML produced; new guardrail tests (4) + verify-coverage-collection.ps1 (cobertura, opencover, ExcludeByAttribute filter) all green.

**What changed:** Bumped `coverlet.collector` in `Directory.Packages.props` from 6.0.4 → 10.0.1. Only one consumer: `tests/RetailPulse.Tests/RetailPulse.Tests.csproj`. No `coverlet.msbuild`, no `coverlet.console`, no `.runsettings` files in the repo.

**Migration impact:** ZERO config changes required. The VSTest data collector contract (`--collect:"XPlat Code Coverage"` with `DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover`) is unchanged in v10. CI workflow (.github/workflows/ci.yml lines 40-62) and the `coverage.opencover.xml` artifact path still work as-is. ExcludeByAttribute filter syntax is also unchanged.

**What v10 actually changed (per release notes v8.0.0 → v10.0.1):**
- New MTP (Microsoft Testing Platform) integration extension (coverlet.MTP) — opt-in, not relevant for our VSTest-based xunit setup.
- New --coverlet-file-prefix option for unique report filenames in AzDO/MTP scenarios.
- .NET 9 / .NET 10 target support added (this is why we need v10 — older coverlet would fail to instrument net10.0 cleanly going forward).
- Bug fixes for async/IAsyncEnumerable branch coverage, is/and pattern-matching branches, LibraryImport/DllImport bodies, and CompilerGenerated tracker attributes.
- No removed CLI flags, no removed MSBuild properties, no removed configuration keys for the VSTest collector path.

**Why the prior "four-major-skip" framing overstated risk:** Coverlet's version jumps (3→6→8→10) have repeatedly been release-numbering bumps rather than breaking-API events for the *collector* package on the VSTest path. The breaking surface for v8/v10 is centered on the new MTP extension and msbuild-integration internals, neither of which we consume.

**Stray-file note:** The working tree had two untracked files prepared for this upgrade (`tests/RetailPulse.Tests/Tooling/CoverletCollectorConfigurationTests.cs` and `tests/verify-coverage-collection.ps1`). They are high-quality guardrails — kept, used to validate the upgrade, and committed alongside the bump. The PS1 script tolerates PowerShell's array-splat quoting on the `--collect` argument (no embedded quotes needed).