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

### 2026-06-04T12:49:32Z: UserId Resolution Must Go Through `UserIdentity.Resolve` (Superseded 2026-06-29)

**By:** Costco (Backend Dev)

The Memory Panel was structurally empty for every user because the chat write path and the `/api/memory` read path resolved `userId` from two different sources. In dev mode, `DevelopmentAuthHandler` stamps an `oid="00000000-…"` claim, while the chat endpoint read `request.User?.ObjectId ?? "anonymous"` from the request body (always null). Writes landed under `anonymous`; reads queried `00000000-…`. The two surfaces could not see each other's data.

**Decision (Original):**
Every endpoint, middleware, and agent that needs a `userId` must resolve it through `RetailPulse.Api.Auth.UserIdentity.Resolve(ClaimsPrincipal?, string?)`. Direct reads of claims or hand-rolled fallbacks are no longer acceptable for identity that touches the memory store, audit log, or per-user persistence.

**Resolution priority (original, body-first):**
1. Explicit body `ObjectId` (when present and non-whitespace)
2. `oid` claim — short form or `http://schemas.microsoft.com/identity/claims/objectidentifier`
3. `"anonymous"` (constant `UserIdentity.AnonymousUserId`)

**Files touched:**
- **New:** `src/RetailPulse.Api/Auth/UserIdentity.cs`
- `src/RetailPulse.Api/Endpoints/MemoryEndpoints.cs`
- `src/RetailPulse.Api/Endpoints/ChatEndpoints.cs`
- **New:** `tests/RetailPulse.Tests/Endpoints/UserIdentityTests.cs` (7 tests)

**Verification:** POST `/api/chat` "Remember that …" → GET `/api/memory` returns stored entry. Full suite 1,992 passing (+7 new tests).

**⚠️ SECURITY NOTICE — See 2026-06-29 decision below for critical update**

### 2026-06-29T16:30:27Z: UserIdentity Resolution: Claim-First for Security (Anti-Spoofing Fix)

**By:** Kroger (Lead)  
**Supersedes:** 2026-06-04 "UserId Resolution Must Go Through `UserIdentity.Resolve`" (priority order reversed for security)

**Summary:** Reversed the `UserIdentity.Resolve` priority order to **claim-first** to prevent request-body spoofing attacks. The authenticated `oid` claim is now trusted over request-body values, closing a HIGH-severity identity-spoofing vulnerability.

**New Priority (Security-First):**
1. **`oid` claim** (short form `"oid"` OR full schema `http://schemas.microsoft.com/identity/claims/objectidentifier`)
2. **Explicit request body `ObjectId`** (used only when no claim present)
3. **`"anonymous"` constant** (fallback when neither is available)

**Why This Matters:**
Request-body spoofing risk — the previous body-first priority allowed a malicious request to send an arbitrary `ObjectId` in the request body, which would override the authenticated claim. This is dangerous because:
- An attacker could forge requests claiming to be a different user
- Per-user memory, audit logs, and preferences could be polluted or mixed

**Solution:** Claims are cryptographically signed by the auth provider (AAD in production, dev auth handler in dev). They cannot be forged by the requester. Request-body values are untrusted input.

**Dev Mode Behavior (Unchanged):**
In development mode, the `DevelopmentAuthHandler` stamps the `oid` claim with `"00000000-0000-0000-0000-000000000000"`. Both the chat write path and memory read path now resolve to the same claim, which keeps the original bug fix (Memory Panel showing "0 entries") while closing the security hole.

**Code Changes:**
- **`src/RetailPulse.Api/Auth/UserIdentity.cs`**: Reversed priority in `Resolve()` method; updated docstring to explain anti-spoofing rationale.
- **`tests/RetailPulse.Tests/Endpoints/UserIdentityTests.cs`**:
  - Renamed `Resolve_PrefersBodyObjectId_OverClaim` → `Resolve_PrefersOidClaim_OverBodyObjectId`
  - Updated assertion: claim now wins
  - Added regression test case: spoofed body value is ignored when claim present
  - Renamed fallback test cases to reflect claim priority

**Test Results:** 7/7 UserIdentity tests pass; 16/16 ChatEndpoints + MemoryEndpoints identity tests pass (no regressions).

**Security Property Verified (Publix QA):** An attacker CANNOT spoof identity by injecting a fake `ObjectId` into the request body when an authenticated claim is present. The claim always wins. Anti-spoofing test `Resolve_PrefersOidClaim_OverBodyObjectId` explicitly proves this by passing conflicting values and asserting claim precedence.

**Team Impact:**
- **Backend / API authors:** All new endpoints using `UserIdentity.Resolve()` now benefit from anti-spoofing protection automatically. No behavior change to the resolve signature.
- **QA / Publix:** The security assumption (claims cannot be forged) is now enforced by code. Future auth-related regressions should verify that claim priority is never bypassed.
- **Frontend / Chick:** No impact — the identity resolution happens server-side only.

**Stale Docs Fixed:** The earlier decision noted *"Microsoft.IdentityModel.Protocols.OpenIdConnect pinned at 8.18.0 (latest is 8.19.1)"* — this pin is no longer necessary; build validation confirms dependency constraints resolved. The package can move to 8.19.1; the hard pin should be removed.

**Next Steps:** This decision corrects the security-critical 2026-06-04 decision. The priority change is permanent and reflects the security-first design of the auth subsystem.

---

### 2026-06-29T14:32:01Z: Board Cleanup: Stray Template Duplicates Removed

**By:** Kroger (Lead)

Removed 15 stray untracked `.md` files from `.squad/` root that were byte-identical duplicates of files in `.squad/templates/`. These were artifacts from a bad copy operation. All deletions verified by MD5 hash comparison before removal.

**Validation:**
- All 15 stray files were 100% byte-identical (MD5) to their template counterparts
- No differing files; no missing template matches
- Working tree now shows only legitimate 6-file governance upgrade (Squad v0.9.4)
- Final commit: `61516b8` on `squad/upgrade-deps-and-429-fix`

**Files deleted:**
charter.md, constraint-tracking.md, copilot-instructions.md, fact-checker-charter.md, history.md, issue-lifecycle.md, mcp-config.md, multi-agent-format.md, orchestration-log.md, plugin-marketplace.md, raw-agent-output.md, roster.md, run-output.md, scribe-charter.md, skill.md

**Team impact:**
None. This was pure cleanup of accidental clutter; no functional or architectural changes.

**Template integrity:**
Confirmed: `.squad/templates/` is the single source of truth for all Squad template content. Any future duplicates at `.squad/` root should be treated as erroneous and removed after hash verification.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
