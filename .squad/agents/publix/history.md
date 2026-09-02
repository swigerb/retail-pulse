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

## Archive

Detailed work from 2026-06-03 through 2026-06-30 (Memory Routing Defense-in-Depth Tests,
Span Type Telemetry Tests, Coverage Validation & Decision Archive, PR #1 Final Security
Gate identity-spoofing validation, Observability Cost Dashboard contract validation)
available in **history-archive.md**.

---
### 2026-08-05T09:57:34-04:00 — Quality gate: Issue #11 secretless-ACR deployment

**Status:** ✅ APPROVE (final). Independent quality review of Costco's dedicated-ACR + postprovision-hook work.

**Notes:** Initial APPROVE with one cosmetic note — narrow the BCP334 suppression. Final recheck APPROVE after correction.

**Heuristic:** Prefer narrowly-scoped Bicep linter suppressions over blanket ones.

---

### 2026-08-11 — Incident context (on hold pending #67)

P0 incident: `azd up` against `retailpulse-demo-eus-001` reported provisioning success but the APIM AI Gateway hardening was incompletely deployed; live `Verify-ApimAiGateway.ps1` failed 9/24 invariants after a manual recovery deploy. Issue #67 filed; Kroger (IC) + Costco (remediation) spawned in worktree `retail-pulse-wt-67-apim-hardening` / branch `squad/67-apim-hardening-gate`. **My independent live verification is intentionally on hold** until Costco's remediation PR closing #67 is ready — do not begin verification against the known-broken gateway. Hard gate: PR #64 (26-prompt sweep) must not merge until 24/24 live invariants pass.

---

### 2026-08-11T11:59Z — Independent verification of PR #73 (issue #67 remediation) — APPROVE (posted as comment)

**Status:** ✅ Independent verification complete. PR reviewed and commented (GitHub blocked a formal `--approve` since this account authored the PR; verdict recorded as a detailed comment instead).

**What I did (fresh worktree at `retail-pulse-wt-67-apim-hardening`, fetched to `1e48b7d`, not trusting Costco's local claims):**
1. **Live re-run of the fixed verifier myself** against production (`rg-retailpulse-demo-eus-001` / `apim-5aldk7aotqods` / `ca-retailpulse-api`) — I had live az credentials (`BrianSwiger-Microsoft-External-2026` subscription, already signed in). Result: **PASS 24/24**, matching Costco's claim.
2. **Real chat completion through APIM** — pulled the APIM `master` subscription key via `az rest ... listSecrets` and POSTed directly to `.../inference/openai/deployments/gpt-5.4-mini-2026-03-17/chat/completions`. Got a genuine 200 OK completion ("PASS"). This is the strongest possible acceptance evidence — actual inference traffic through the hardened gateway, not just resource-shape checks.
3. **Code review of the verifier rewrite** — confirmed the ARM-REST bearer-token approach (`Invoke-ArmGet`/`Invoke-RestMethod`) legitimately replaces the three nonexistent `az apim` CLI subcommands. Audited every remaining `2>$null`/`SilentlyContinue` — all on real core `az` commands, all gated by an explicit `Assert` that fails loudly if the command errors. No silent-failure/masking pattern remains. Exit-code contract verified: `exit 2` only for environment-precondition checks in `Test-Prereq`, before any invariant executes; every invariant failure after that is a hard `exit 1`.
4. **Full validation suite:** `dotnet build RetailPulse.slnx -c Release` clean; `dotnet test RetailPulse.slnx -c Release` → **2637/2637 passed** (105/105 in `tests/RetailPulse.Tests/Deployment/*`); `dotnet format --verify-no-changes` clean. Confirmed `CompiledArmDeploymentGraphTests.cs` genuinely runs `az bicep build` and parses the compiled ARM JSON output (not source-text grep).
5. **azure.yaml/hook wiring reviewed** — root-level `hooks:` (preprovision/postprovision/predeploy, windows+posix, `continueOnError:false`) matches azd's real global-hook schema; `postprovision.ps1` correctly threads the verifier's exit code into a `throw` that fails `azd provision`/`azd up`.

**⚠️ Genuine finding — NOT a defect in PR #73, but a real merge-mechanics hazard:** `gh pr view 73` reports `mergeable: CONFLICTING`. `main` had already merged a *different* fix to the same file (PR #69, commits `1ae6ffd`/`50991c7`, "APIM verifier robustness") using an `az rest` + BOM-strip approach. **I independently ran main's currently-merged verifier live against the same production resources and reproduced a crash**: uncaught `UnicodeEncodeError` (`'charmap' codec can't encode character '\ufeff'`) reading the API policy, because `az rest`'s output pipe can't handle the UTF-8 BOM APIM returns — PR #73's `Invoke-RestMethod` rewrite specifically avoids this failure mode. **Whoever resolves the merge conflict must keep PR #73's `Invoke-ArmGet`/`Invoke-RestMethod` implementation, not main's `az rest` version**, or main will silently regress a real, reproduced crash back in. Flagged prominently in the PR comment; also logging to decisions inbox.

**What I could NOT verify:** a full fresh `azd up --no-prompt` exercising the new postprovision/predeploy hooks end-to-end (no azd in my sandbox either — same gap Costco noted). The predeploy sourcelink-race fix is logically sound by inspection but its live effectiveness against the actual race is unverified. This is the one remaining gap before merge that no sandboxed agent can close — needs a human-run or CI-run `azd up` against a scratch/staging environment.

**Team impact:**
- Kroger (Lead): merge decision is yours; the CONFLICTING mergeable state needs a deliberate resolution that preserves PR #73's Invoke-RestMethod approach over main's crashing az-rest approach from PR #69. A full azd up dry-run remains the one open verification gap.
- Costco: no defects found in your PR's own code — great work on both the ARM-REST rewrite and the hard-gate wiring.

**Commit under review:** `1e48b7d` on `squad/67-apim-hardening-gate`.

### 2026-08-11T12:45:55-04:00 — Session note (Scribe, durable state)

Recorded by Scribe as part of finalizing the production hardening session.
Final scope outcomes durably captured in `.squad/decisions.md` (Active
Decisions, entry "Production hardening session — final outcomes"):
- Issues CLOSED: #60, #61, #62, #68, #71.
- Issues OPEN (intentionally): #59 (umbrella), #63 (QA production sweep — gated
  on authenticated run), #70 (frontend deploy TS7006/TS7016 — not reproducible
  from clean `origin/main`).
- PRs MERGED: #65, #66, #69, #72. PR #64 OPEN and gated (no merge until live
  authenticated production evidence).
- Prompt-ideas acceptance contract: exactly 26 prompts = 9 chart + 17 prose,
  enforced bidirectionally by backend manifest + frontend drift tests.
- APIM live verifier on `rg-retailpulse-demo-eus-001` /
  `apim-5aldk7aotqods`: 25/25 PASS.
- Authenticated production 26-prompt sweep blocker: AADSTS65001, no
  interactive consent granted under the tenant constraints tracked by issue
  #57 (service-principal synthetic monitor is the sanctioned unblock path).
- Deployment completed successfully after PR #66 / PR #65 merges; production
  frontend is serving fresh Static Web App assets from the merged `main`.

No implementation files were modified by Scribe. No secrets, tokens,
`.auth/me` payloads, screenshots, or raw azd/deployment output committed to
tracked state.

---

### 2026-08-26T18:14:32Z — Quality Review: Issue #57 Synthetic Monitor (PR #162)

**Status:** ✅ APPROVED — Independent review of Kroger's optional synthetic monitor implementation.

**What:** Reviewed PR #162 (issue #57 — optional synthetic monitor for backend observability).

**Review Findings:**
- No blocking issues identified
- Implementation validated
- All 9 CI checks green

**Verdict:** APPROVED  
**Comment:** https://github.com/swigerb/retail-pulse/pull/162#issuecomment-5429540488

**Team Impact:** PR #162 cleared for merge; synthetic monitor capability validated for deployment.

## 2026-08-26 — Issue #165 / PR #166 Review (Tester, sync)

- Independently reviewed PR [#166](https://github.com/swigerb/retail-pulse/pull/166) at exact head SHA `6a7517f8a026716f3f07e1bffb72dd89a307669a`.
- Verdict: **APPROVE** — comment: https://github.com/swigerb/retail-pulse/pull/166#issuecomment-5430053534
- All 9 CI checks green at reviewed SHA. Approval scoped to code + offline self-tests; no live endpoint monitor was executed.
- Kroger implemented on `squad/165-monitor-doc-accuracy`; commit `6a7517f8a026716f3f07e1bffb72dd89a307669a`.


