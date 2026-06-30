# Publix — History

## Project Context

- **Project:** Retail Pulse — AI-powered retail analytics on .NET Aspire + React
- **Stack:** .NET 9, Aspire, Azure OpenAI, React/TypeScript/Vite, SignalR, xUnit, Vitest
- **User:** Brian Swiger
- **Joined:** 2026-05-16 (replacing Target)

## Context from predecessor

- 1,915 tests exist (xUnit backend + Vitest frontend)
- Build: `dotnet build RetailPulse.slnx`
- Frontend: `cd src/RetailPulse.Web && npm run build && npx vitest run`
- Lint: `dotnet format RetailPulse.slnx --verify-no-changes`
- Key test files: `tests/RetailPulse.Tests/`
- Demo smoke tests: `tests/RetailPulse.Tests/Agents/DemoQuerySmokeTests.cs`
- Critical demo query: "How is Apex Grill performing in the Southwest this quarter?"

## Learnings

- MaximumIterationsPerRequest=1 was set in Program.cs line 597 and caused ALL tool-using queries to return empty text (the LLM never got a second turn to synthesize after calling tools). This defect was live for multiple sessions before being caught.
- The FallbackReply mechanism works correctly — it fires when response.Text is empty/whitespace. But the ROOT cause was the model never getting to synthesize.
- Smoke tests in DemoQuerySmokeTests.cs test routing but did NOT test actual LLM response generation end-to-end. That gap let this bug through.
- Added `MaxIterationsSynthesisTests.cs` (7 tests) to guard the MaxIterations boundary: uses real `FunctionInvokingChatClient` to prove MaxIterations=1 breaks synthesis and MaxIterations≥2 allows it. These tests will fail immediately if someone regresses MaxIterations back to 1.
- `FunctionInvokingChatClient` in Microsoft.Extensions.AI v10.5.0 takes `(IChatClient, ILoggerFactory?, IServiceProvider?)` — `MaximumIterationsPerRequest` is a settable property, not a constructor options class.
- API versioning tests (`Endpoints/ApiVersioningTests.cs`, 13 tests, 2026-06-03): mirror the exact `ApiVersioningOptions` from `Program.cs` inside a `TestServer` and attach versioned endpoints via `endpoints.NewApiVersionSet().HasApiVersion(...).Build()` + `WithApiVersionSet(versionSet).MapToApiVersion(...)`. This pattern avoids the project's Azure-credential startup cost while still exercising the real `Asp.Versioning.Http` middleware. Suite passes on both 8.1.0 and 10.0.0 — used to gate Costco's upgrade.
- Asp.Versioning gotcha: with `UrlSegmentApiVersionReader`, an unsupported version (`/api/v99/...`) returns **404**, not 400, because the version is part of the route template and no route matches. The 400-from-middleware shape only applies to header/query readers. The `AssumeDefaultVersionWhenUnspecified=true` option also cannot rescue a missing URL segment — a request to `/api/health` (no `v{n}`) still 404s. Tests must be written with that asymmetry in mind, or they'll false-fail.
- Asp.Versioning route token `{version:apiVersion}` is consumed by middleware, not bound by the minimal-API handler, so ASP0018 ("unused route parameter") fires unless suppressed with `#pragma warning disable ASP0018` at the top of the test file.
- Production endpoints in RetailPulse.Api are **not** versioned at the route level (all are `/api/...` with no `v{n}` segment) even though `AddApiVersioning` is configured. The versioning service is wired but inert at the route layer. When future versioned routes are added, the contract tests in `ApiVersioningTests.cs` give us the template.
- Coverlet upgrade validation pattern (`tests/RetailPulse.Tests/Tooling/CoverletCollectorConfigurationTests.cs` + `tests/verify-coverage-collection.ps1`, 2026-06-03): xUnit-only tests must be *static* — they can inspect `Directory.Packages.props`, the test `.csproj`, the CI workflow, and any pre-existing TestResults artifacts, but they must NOT shell out to `dotnet test` themselves or you re-enter the test runner. The end-to-end exercise lives in a separate PowerShell script that runs `dotnet test --collect "XPlat Code Coverage"` (cobertura), then again with `Format=opencover` (CI parity), then once more with `ExcludeByAttribute=GeneratedCodeAttribute,CompilerGeneratedAttribute` to prove filter config compatibility. Parsing the XML for `<package>/<class>/<method>/<line>` counts (cobertura) and `<Summary numClasses/numMethods>` (OpenCover) catches "report exists but is empty" regressions.
- PowerShell + `dotnet test` quoting gotcha: passing `--collect:"XPlat Code Coverage"` as a single splatted array element strips the embedded quotes and MSBuild then sees `XPlat Code Coverage` as a property name (MSB4177 invalid character " "). Use the space-separated form `"--collect", "XPlat Code Coverage"` so PowerShell quotes the value automatically when invoking the native process.
- coverlet.collector v6.0.4 → v10.0.1 was a clean upgrade for our pipeline: cobertura and opencover outputs both parse, `ExcludeByAttribute` still consumes the same wire format, CI's `--collect "XPlat Code Coverage" -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover` invocation still produces `coverage.opencover.xml`. One observable difference worth noting: v10's OpenCover summary reports the full class count (582) where v6 only reported a subset (136) for the same scope — downstream consumers comparing absolute coverage numbers across the upgrade should expect higher class/method counts in v10.
- Span-type telemetry regression guard (2026-06-03): the safest QA coverage is a mixed strategy — static contract tests inspect the exact `TraceSpan` creation sites in `ChatEndpoints.cs` / `MemoryExtractionBackgroundService.cs` for `Tags["span.type"]`, and runtime tests on `TelemetryPushBackgroundService` prove that tag becomes the frontend-facing `span.type`. This catches both silent backend tag removals and payload-shape regressions that would drive TraceDashboard's "Unique Tools" counter back to 0.

## Recent Work (2026-06-03, continued)

### 2026-06-03T20:06:00Z — Memory Routing Defense-in-Depth Tests

**Status:** ✅ Complete — Commit part of `2810ed0`, 1,972 tests pass (includes 17 new memory routing tests)

**What:** Wrote 17 regression tests covering memory router + `MemoryManagementAgent` store-vs-clear discrimination:
- Router keyword pattern validation (confirms "remember" excluded from `memory/management` intents)
- Agent specialist behavior: benign "remember" → store, explicit clear/reset → destruct
- Edge cases: ambiguous routing followed by specialist validation

**Why:** Defense-in-depth test strategy — guard both router classification AND specialist validation to prevent benign requests from triggering data loss.

**Team Impact:**
- Memory routing regression cases are now contractually protected
- Future router or specialist changes will fail immediately if store-vs-clear logic degrades
- Misclassification by router cannot silently cascade (specialist acts as gatekeeper)

**Commit:** Part of `2810ed0`

---

### 2026-06-03T19:40:00Z — Span Type Telemetry Tests

**Status:** ✅ Complete — Commit 0f4111c, 1,949 backend + 278 frontend tests pass

**What:** Added comprehensive test coverage for span type telemetry end-to-end:
- **TraceSpanTypeContractTests.cs** (8 xUnit tests): Static contract verification inspecting exact `TraceSpan` creation sites in `ChatEndpoints.cs` and `MemoryExtractionBackgroundService.cs` for `Tags["span.type"]` population
- **Dashboard telemetry tests** (3 Vitest tests): Runtime validation that `TelemetryPushBackgroundService` correctly forwards `Tags["span.type"]` into frontend payload's `type` field, then asserts tool counters and distribution rendering work correctly

**Why mixed strategy:**
Hosting full production chat endpoint in tests is expensive and tightly coupled to Azure startup wiring. Static contract tests keep focus on exact span-emission lines Costco changed. Runtime push tests prove frontend-facing payload still depends on that tag.

**Team Impact:**
- If future edit removes or renames `span.type` tag on routing/agent/tool/memory spans, backend contract suite will fail immediately
- Dashboard telemetry regression (Unique Tools counter → 0) is now contractually protected
- Future tool telemetry changes caught before reaching UI

**Decision Documented:** "Span type telemetry tests" in decisions.md.

---

### 2026-06-03T17:04:29Z — Coverage Validation & Decision Archive

**Status:** ✅ All guardrail tests pass on v10.0.1; coverage pipeline green end-to-end

**What happened:**
- Scribe merged 2 inbox decision files (ASP.Versioning, coverlet upgrades) into decisions.md
- Publix's guardrail test suite (`CoverletCollectorConfigurationTests.cs`, 4 tests) validated v10.0.1 compatibility
- verify-coverage-collection.ps1 script confirmed OpenCover XML format, cobertura format, and ExcludeByAttribute filter config

**Team notifications:**
- Costco (Backend): Coverage pipeline is green; safe to ship v10.0.1
- Kroger (Lead): Both deferred bumps from 2026-06-03 sweep are CLOSED

**Commit:** dabe9ff

---

### 2026-06-29T16:35:00Z — PR #1 Final Security Gate: Identity-Spoofing Fix Validation

**Status:** ✅ APPROVED — commit cc4a28e passes independent review

**What:** Independent validation of Kroger's anti-spoofing fix (HIGH severity). He identified and fixed an identity-spoofing vulnerability where request-body `ObjectId` could override authenticated claims, potentially letting unauthenticated users impersonate others.

**The Fix (commit cc4a28e):**
- `src/RetailPulse.Api/Auth/UserIdentity.cs`: Reversed resolution priority to CLAIM-FIRST: (1) authenticated `oid` claim (short + MS schema form), (2) request-body `ObjectId` as fallback only when no claim, (3) `AnonymousUserId`
- `tests/RetailPulse.Tests/Endpoints/UserIdentityTests.cs`: Added anti-spoofing regression test (`Resolve_PrefersOidClaim_OverBodyObjectId`) proving claim with DIFFERENT body value → claim wins

**Validation performed:**
1. **Code review** — Confirmed `UserIdentity.Resolve()` checks `!string.IsNullOrWhiteSpace(oid)` BEFORE examining body. No bypass detected (empty-claim handling cannot let body win).
2. **Test scrutiny** — Anti-spoofing test EXPLICITLY passes claim=`"claim-oid"` + body=`"spoofed-user-from-body"`, then asserts `id.Should().Be("claim-oid")`. Test rationale explicitly states: "authenticated claim must take priority to prevent request-body spoofing."
3. **Suite validation** — All 7 UserIdentity tests PASS (21 ms); full suite 1992/1992 PASS (75s).
4. **Original bug check** — Confirmed dev-mode consistency: ChatEndpoints (write path) and MemoryEndpoints (read path) both resolve to the same `oid` claim when body is null. Original memory-store bug stays fixed.

**Security property verified:** An attacker cannot spoof identity by injecting a fake `ObjectId` into the request body when an authenticated claim is present. The claim always wins.

**Team Impact:**
- PR #1 cleared for merge to main
- Identity-spoofing vector is closed
- Regression test ensures future edits cannot silently revert to body-first priority

**Reviewer:** Publix (Tester) — independent review required because Kroger (Lead) is the fix author and cannot self-certify security patches.

**Commit:** cc4a28e

---

### 2026-06-30T16:54:45-04:00 — Observability Cost Dashboard Contract Validation

**Status:** ✅ PASS after targeted frontend fix

**What:** Validated the cross-boundary contract for `/costs`, `/costs/agents`, `/costs/trend?days=`, and `/costs/tools?period=` plus full backend/frontend suites. First pass failed because idle all-zero trend buckets rendered a chart instead of the empty state. After Chick fixed trend presence detection, re-review passed.

**Validation:** backend suite 1,998 passed / 2 skipped; frontend suite 285 passed; frontend build passed.

**Lesson:** Observability dashboard tests must verify that the frontend calls dedicated endpoints for trend/agent/tool data and that idle all-zero trend data renders an empty state, not a misleading zero-line chart.
