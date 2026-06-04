# Costco — History

## Recent Work (2026-06-03)

### 2026-06-03T21:25:19-04:00 — Memory Routing Priority Beats Brand Lookup

**Status:** ✅ Complete — memory commands now short-circuit router keyword classification before portfolio/general brand fast-paths; targeted build + memory/router suites pass

**Issue:** Explicit memory directives like "Remember that ClearDesk depletions are trending in the Northeast this quarter" could be intercepted by lightweight brand/performance shortcuts before the router reached memory keyword handling.

**Root Cause:** `RetailOpsRouter.TryKeywordClassify()` evaluated portfolio and single-brand shortcut checks ahead of memory intent detection, so memory directives did not have absolute routing priority.

**Solution:**
- Added `IsMemoryCommand()` as the first gate in `TryKeywordClassify()`
- Preserved existing behavior for portfolio and simple single-brand lookups after the memory early exit
- Expanded router regressions to cover `remember ...` commands that also contain trend/depletion language

**Impact:**
- Store/forget/reset directives now always route to `MemoryManagementAgent` even when they mention brands, trends, or depletion phrasing
- Ordinary single-brand performance/depletion questions still stay on the lightweight `General` path
- No change to `MemoryExtractionService`: Azure content-filter hits already degrade safely to warning + skip

---

### 2026-06-03T21:45:00Z — Memory Store Routing Restored

**Status:** ✅ Complete — explicit "remember that/this" commands route back to `MemoryManagementAgent`, build clean, 117 memory/prompt/router tests pass

**Issue:** Explicit store commands like "Remember that ClearDesk is trending..." were falling through to `general/fallback`, so the Memory Panel stayed empty even though `MemoryManagementAgent` had a working `IsStoreIntent()` → `StoreAsync()` path.

**Root Cause:** Commit `2810ed0` removed store-intent phrases from both router keyword fast-paths and the router prompt, leaving no route for explicit memory store commands to reach `MemoryManagementAgent`.

**Solution:**
- Restored `remember that` and `remember this` keyword routing to `AgentIntent.MemoryManagement`
- Updated the router prompt to classify both explicit store and clear/forget commands as `memory/management`
- Flipped router regression tests to assert fast-path routing for store commands while keeping non-command preference text on the normal pipeline

**Impact:**
- `Remember that ...` now reaches `MemoryManagementAgent`, where the agent's store-vs-clear discrimination safely chooses `StoreAsync()`
- Destructive commands like `Forget everything` still route to the same specialist and remain handled by the destructive path
- Ambient preference statements like `I'm focused on the Spirits category...` still avoid keyword routing and stay on the general path

---

### 2026-06-03T20:06:00Z — Memory Management Router Defense-in-Depth

**Status:** ✅ Complete — Commit part of `2810ed0`, 1,972 tests pass

**Issue:** Router misclassification could cause benign "remember ..." store requests to trigger destructive clear/reset operations in `MemoryManagementAgent`, causing data loss.

**Root Cause:** `memory/management` router keyword patterns were over-broad, and agent specialist had no fail-closed logic for ambiguous intent.

**Solution:**
- Removed "remember" and store-intent keywords from `memory/management` router patterns
- Updated `MemoryManagementAgent` prompt to validate intent before destructive operations — only explicit clear/reset phrases trigger data loss
- Added store-vs-clear discrimination to specialist agent as defense-in-depth layer against routing errors

**Impact:**
- Router now fail-closed on store intents — misrouting cannot cascade to destructive actions
- Defense-in-depth: specialist agent validates intent independent of routing classification
- Regression protection in place (Publix added 17 memory routing tests)

**Team Impact:**
- **Publix (QA):** Treat "remember ..." as regression case in any memory-management edit; guard store/clear discrimination
- **Router/MemoryManagementAgent maintainers:** Keyword patterns stay destructive-only; specialist must remain store-vs-clear validator

**Decision Documented:** "Memory-management routing must fail closed on destructive intent only" in decisions.md.

---

### 2026-06-03T19:40:00Z — Span Type Tags on TraceSpan Telemetry

**Status:** ✅ Complete — Commit a1787df, 1,926 tests pass

**Issue:** "Unique Tools" counter on Trace Dashboard was always showing 0, preventing tool usage telemetry from being visible.

**Root Cause:** Backend `TraceSpan` objects were not populating `Tags["span.type"]`, so `TelemetryPushBackgroundService` defaulted all spans to `generic`, breaking the frontend's span type filtering and counting logic.

**Solution:** Added `span.type` tag population to all `TraceSpan` creation sites:
- **ChatEndpoints.cs:** routing, agent, tool, memory spans
- **MemoryExtractionBackgroundService.cs:** memory extraction spans

**Impact:** 
- Trace Dashboard now correctly displays unique tool counts and tool distribution
- Backend telemetry now has contractually-required span type coverage
- QA has static + runtime regression protection via decision "Span type telemetry tests"

**Decision Documented:** "Span type tags on TraceSpan telemetry" in decisions.md — backend must treat `span.type` as required schema on all future `TraceSpan` producers.

**Team Coordination:**
- Publix added contract tests to prevent future regressions
- Chick aware of backend telemetry change (no FE code changes required)

---

### 2026-06-03T17:51:01Z — Trace Dashboard Model Name Fix

**Status:** ✅ Complete — Commit 699e2fb, Tests pass

**Issue:** Trace Dashboard displayed "Unknown" for model names despite backend telemetry having the data.

**Root Cause:**
1. Backend never sent `specialist.Model` on `trace_started` event
2. Frontend dedup logic skipped enriched events, preventing model enrichment

**Solution:**
- Threaded `Model` through full stack: `TelemetryPushItem` → SignalR → TypeScript types
- Added `llm.model` span attribute to backend telemetry pipeline
- Rewrote frontend event handler to merge instead of skip enriched events

**Impact:** Model names now correctly propagate from backend instrumentation to frontend display across all trace types.

---

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

### 2026-06-03T13:41:53-04:00 — Trace Dashboard "Unknown" LLM model fix

**Status:** ✅ Complete — Build clean (0 warnings), 27 telemetry tests pass, 31 frontend Dashboard tests pass (including 6 TraceDashboard).

**Symptom:** Trace Dashboard rendered "Unknown" in both the trace row header (`Unknown 47.42s $0.00`) and the span detail (`🤖 Unknown`) instead of the LLM model name (e.g. `gpt-4o-mini`).

**Root cause — three independent gaps in the telemetry pipeline:**

1. **`InMemoryTraceCollector.CaptureSpan`** emits an initial `trace_started` push with `Intent=null, AgentName=null` (before routing classifies the message). The Dashboard `trace_started` SignalR handler was a *dedup-by-traceId noop* on the second event, so the later enriched `EmitTraceStarted` from `ChatEndpoints` (carrying `specialist.DisplayName`) was silently dropped. The trace's `agentName` stayed `'Unknown'` forever.
2. **`TelemetryPushItem` / SignalR `trace_started` payload** never carried the model name at all. `specialist.Model` was available on every `ISpecialistAgent` but nothing read it.
3. **`agent.{specialist.Key}.process` span** had no `llm.model` tag — even if the FE wanted to fall back to span attributes, the data wasn't there.

**Plus a 4th, latent bug:** The backend serializes span attributes as `tags`, but the FE TS `TraceSpan` type uses `attributes`. `Dashboard.tsx`'s `span_completed` handler was dropping them entirely on both nested and flat shapes, so *any* span tag (not just `llm.model`) was invisible to the UI.

**Fix — end-to-end plumbing:**

- `TelemetryPushChannel.cs` — added optional `string? Model = null` to `TelemetryPushItem` record (backward-compatible; tests use named params).
- `InMemoryTraceCollector.EmitTraceStarted` — added `model` parameter, forwarded into the push item.
- `TelemetryPushBackgroundService` — included `model = item.Model` in the SignalR `trace_started` payload.
- `ChatEndpoints` — passes `specialist.Model` to `EmitTraceStarted`; adds `llm.model` tag to both the `Activity` and the `TraceSpan` for `agent.{key}.process`.
- `Trace` TS type — added `model?: string`.
- `Dashboard.tsx` — rewrote `trace_started` handler to **merge** into an existing trace instead of dedup-skip (this was the actual UI bug fix; the missing model field was the second half). Also mapped backend `tags` → FE `attributes` in `span_completed` so span attributes become accessible.
- `TraceDashboard.tsx` row + `TraceTimeline.tsx` header — display `trace.model`, falling back to the `llm.model` attribute on the agent span, then `'Unknown model'` / agentName.

**Key takeaway:** When a SignalR event handler does dedup-by-ID and the producer emits the same event twice (once early, once enriched), the second one is invisible. Either merge (preferred when enrichment is the intent) or move the second emission to a distinct event type. The "Unknown" bug *looked* like a missing field but was 50% a state-update bug — adding the `model` field without fixing the merge wouldn't have rendered it either.

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

### 2026-06-03T17:04:29Z — Session Summary & Decision Archive

**Status:** ✅ Both deferred bumps (Asp.Versioning.Http, coverlet.collector) documented and escalated to decisions.md

**What happened:**
- Scribe merged 2 inbox decision files (`costco-asp-versioning-v10-upgrade.md`, `costco-coverlet-v10-upgrade.md`) into decisions.md
- Both upgrades are now recorded as active decisions with full team impact analysis
- Orchestration logs created for Costco and Publix (session recording)

**Team notifications:**
- Kroger (Lead): "Deferred major-version package bumps (2026-06-03)" decision is now CLOSED — both bumps are done
- Publix (QA): Coverage pipeline validated; no action item
- Chick (Frontend): No client regen required; URL conventions stable

**Commits:** 4e63ebd (Asp.Versioning), dabe9ff (coverlet)