# Squad Decisions

## Active Decisions

### 2026-05-20T08:43:48Z: User directive
**By:** Brian Swiger (via Copilot)
**What:** The project owner's name is Brian Swiger (not "Brady"). Always address them as Brian.
**Why:** User request — captured for team memory

### 2026-06-03T11:22:49Z: Asp.Versioning.Http upgraded 8.1.0 → 10.0.0 (deferral resolved)

**By:** Costco (Backend Dev)

The 2026-06-03 NuGet sweep deferred this bump as "too risky." It turned out to be a drop-in upgrade for our project.

**Outcome:**
- Single line change in `Directory.Packages.props` (8.1.0 → 10.0.0). No code changes.
- `dotnet build`: 0 warnings, 0 errors.
- `dotnet test`: 1,925/1,926 pass (one unrelated flake in `OTelRoutingSpanTests.RoutingSpan_EmitsIntentTag` — passes in isolation; LLM intent routing, not versioning).

**Why the deferral over-estimated risk:**
The original concern was "URL segment/header convention breakage." Those breakages live in `Asp.Versioning.Mvc` and `Asp.Versioning.ApiExplorer`. We ship neither — we only use `Asp.Versioning.Http` for Minimal API endpoint groups with `UrlSegmentApiVersionReader`. The API surface we touch (`AddApiVersioning`, `ApiVersion`, `UrlSegmentApiVersionReader`, `DefaultApiVersion`, `ReportApiVersions`) is unchanged in v10.

NuGet also skipped a public 9.x line for this package, so the "two-major skip" was actually a single release in practice.

**Team impact:**
- **Chick (Frontend):** No client regeneration required. URL convention unchanged: `/api/v{n}/...`. Default version still 1.0. `api-supported-versions` / `api-deprecated-versions` reporter headers unchanged.
- **Publix (QA):** No regression contract sweep needed for this bump; existing `ApiVersioningTests` cover us.
- **Kroger (Lead):** The remaining deferred item from the 2026-06-03 sweep is `coverlet.collector 6 → 10`, which still needs a CI coverage pipeline owner before bumping.

**Heuristic to remember:**
Before deferring a major-version package bump as "too risky," check whether the risk surface (e.g. MVC integration, ApiExplorer integration) is actually consumed by the project. A multi-major skip on a slice you don't use is often a one-line change.

### 2026-06-03T15:29:50Z: Span type tags on TraceSpan telemetry

**By:** Costco (Backend Dev)

Every backend-created `TraceSpan` must populate `Tags["span.type"]` with one of the frontend-supported values: `routing`, `agent`, `tool`, `memory`, or `approval`.

**Why:**
`TelemetryPushBackgroundService` derives the serialized span `type` from `Tags["span.type"]`, defaulting to `generic` when the tag is missing. The Trace Dashboard depends on that normalized `type` field for counters and filtering, so omitted tags silently break UI telemetry features.

**Current mapping:**
- `router.*` → `routing`
- `agent.*` → `agent`
- `tool.*` → `tool`
- `memory.*` → `memory`
- `approval.*` → `approval`

**Team impact:**
- **Costco / backend:** treat `span.type` as required schema, not optional metadata, on all future `TraceSpan` producers.
- **Chick / frontend:** dashboard filters and counters can rely on backend-emitted span types matching the shared union.
- **Publix / QA:** telemetry regressions should verify counts by `type`, not just span presence.

### 2026-06-03T15:35:00Z: Span type telemetry tests

**By:** Publix (QA)

Publix added a mixed test strategy for the span-type regression:
- a static contract test that inspects the production TraceSpan creation sites in `ChatEndpoints.cs` and `MemoryExtractionBackgroundService.cs`
- a runtime test that verifies `TelemetryPushBackgroundService` forwards `Tags["span.type"]` into the frontend payload's `type` field
- dashboard tests that assert unique tool counting and tool distribution rendering from `type: "tool"` spans

**Why:**
Hosting the full production chat endpoint in tests is expensive and tightly coupled to Azure-dependent startup wiring. Static contract coverage keeps the test focused on the exact span-emission lines Costco changed, while the runtime push test proves the frontend-facing payload still depends on that tag.

**Impact:**
If a future edit removes or renames a `span.type` tag on routing, agent, tool, or memory spans, the backend contract suite will fail before the dashboard silently regresses back to `Unique Tools = 0`.

### 2026-06-03T13:04:29Z: coverlet.collector upgraded 6.0.4 → 10.0.1 (deferral resolved)

**By:** Costco (Backend Dev)

The second deferred bump from the 2026-06-03 NuGet sweep is now resolved.
`coverlet.collector` was upgraded **6.0.4 → 10.0.1** in `Directory.Packages.props`.

**Why it's safe:**
- Only one consumer in the repo: `tests/RetailPulse.Tests/RetailPulse.Tests.csproj`.
- No `coverlet.msbuild`, no `coverlet.console`, no `.runsettings` files.
- CI uses the stable VSTest collector contract (`--collect:"XPlat Code Coverage"` + `DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover`), which is **unchanged** in v10.
- v10 release notes do not remove any CLI flag, MSBuild property, or collector config key that we rely on. The "breaking" surface in v8/v10 is in the new `coverlet.MTP` extension and msbuild-integration internals, neither of which we use.

**Validation:**
- `dotnet restore` — clean
- `dotnet build --configuration Release` — 0 warnings, 0 errors
- `dotnet test --collect:"XPlat Code Coverage" ... Format=opencover` — **1,937 tests pass**, `coverage.opencover.xml` produced at the expected path with valid `<CoverageSession>` content.
- The pre-existing "Unable to find a datacollector" warning on `RetailPulse.LoadTests` is unchanged (that project doesn't reference `coverlet.collector` — same behavior on v6).

**Team impact:**
- **Publix (QA):** Coverage pipeline is green end-to-end; no action needed.
- **Kroger (Lead):** Both deferred bumps from the 2026-06-03 sweep are now closed. The "Deferred major-version package bumps" decision can be marked resolved.
- **Chick (Frontend):** No impact.

# Deferred major-version package bumps (2026-06-03)

**Author:** Costco (Backend Dev)
**Date:** 2026-06-03T11:13:12.666-04:00

During the NuGet upgrade sweep, two packages had stable latest versions available but were deliberately **not upgraded** in this pass because they require focused testing beyond a "build + unit tests" gate:

## 1. Asp.Versioning.Http: 8.1.0 → 10.0.0

- Two major-version skip (8 → 10).
- Known breaking changes in URL segment/header conventions and API explorer integration.
- Risk: every versioned endpoint contract could shift; FE callers and OpenAPI consumers need re-validation.
- **Action item:** A separate task should upgrade this with explicit contract regression tests on `/api/v{n}/...` routes, and Chick should be looped in for FE client regeneration.

## 2. coverlet.collector: 6.0.4 → 10.0.1

- Four-major-version skip.
- Collector configuration and output formats have changed; CI coverage reporting (if wired) may need updates.
- **Action item:** Bump only when someone owns validating the coverage pipeline end-to-end.

## What WAS upgraded successfully
See costco/history.md entry for full list. Notable majors that did go through cleanly: `Microsoft.Data.Sqlite 9→10`, `YamlDotNet 17→18`, `Microsoft.NET.Test.Sdk 17→18`. Build is clean (0 warnings) and 1,926 tests pass.

## Compatibility footnotes for the team
- **Microsoft.IdentityModel.Protocols.OpenIdConnect** pinned at 8.18.0 (latest is 8.19.1) because its peer `System.IdentityModel.Tokens.Jwt` only publishes through 8.18.0 — bumping OIDC further causes NU1102.
- **Microsoft.Bcl.Memory** bumped to 10.0.7 to satisfy `Microsoft.Agents.Connector 1.5.184` transitive requirement; future Agents stack bumps may continue pulling Bcl.Memory forward.

### 2026-06-03T16:06:00Z: Memory-management routing must fail closed on destructive intent only

**By:** Costco (Backend Dev)

The `memory/management` router intent and `MemoryManagementAgent` should treat only explicit destructive phrases as clear/reset actions. Any message starting with `remember` must be handled as a store request if it reaches the memory-management agent, even if routing misclassifies it.

**Why:**
This bug showed that a single over-broad keyword or prompt description can turn a benign "remember that..." request into destructive data loss. The specialist now acts as a defense-in-depth layer so misrouting cannot wipe user memory.

**Team impact:**
- **Costco / backend:** Router keyword patterns and prompt wording must stay destructive-only.
- **Publix / QA:** Future memory-management changes should preserve store-vs-clear discrimination in the specialist, not rely solely on routing. Treat "remember ..." as a regression case.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
