# Costco — History

## Recent Work (2026-06-04)

### 2026-06-04T08:49:32Z — Memory Panel Empty: UserId Resolution Divergence

**Status:** ✅ Complete — Memory Panel now reflects what the agent stores; 1,992 tests passing (+7 new UserIdentityTests).

**Issue:** Saying "Remember this competitive analysis…" routed correctly to MemoryManagementAgent and trace showed "Stored explicit user memory request", but Memory Panel showed **0 memories**. DB had 34 rows; GET /api/memory returned [].

**Root Cause:** Two different userId resolutions on write/read paths:
- ChatEndpoints (write): request.User?.ObjectId ?? "anonymous" → FE never sends User → always "anonymous"
- MemoryEndpoints (read): HttpContext.User.FindFirst("oid")?.Value → DevelopmentAuthHandler stamps GUID → always "00000000-..."
- Writes under "anonymous"; reads under zero-GUID. Never agreed.

**Solution:**
- New RetailPulse.Api.Auth.UserIdentity.Resolve() — single source of truth. Priority: body → oid claim → "anonymous"
- MemoryEndpoints GET/DELETE use UserIdentity.Resolve(httpContext.User)
- ChatEndpoints /api/chat and /api/chat/stream resolve via helper AND normalize request.User before downstream calls
- Fixed FE/BE contract: response shape now uses storedAt/expiresAt and type ∈ {conversation, preference, entity} (was fact/preference/context) matching types/index.ts
- Added missing DELETE /api/memory collection handler (was 404)
- New UserIdentityTests.cs (7 tests) — pins resolution priority, asserts write/read paths converge

**Impact:**
- Memory Panel now updates immediately after "Remember…" command
- Old "anonymous" rows orphaned (acceptable dev test data)
- Any endpoint/agent/middleware needing userId has single helper; identity drift prevented
- End-to-end verified: POST /api/chat → GET /api/memory returns correct shape

**Learnings:**
- Identity resolution MUST centralize when two surfaces touch same store
- Dev auth stamping deterministic GUID changes failure mode from "all anonymous" (works) to "writes vs reads disagree" (silent)
- FE silently swallowing non-200 hid endpoint-shape mismatch for weeks
- Test gap: zero HTTP-level /api/memory coverage (plugged with helper-level regression net)

---

## Recent Work (2026-06-03)

### 2026-06-03T21:25:19Z — Memory Routing Priority Beats Brand Lookup

**Status:** ✅ Complete — Commit part of work, 1,972 tests pass

**Issue:** Explicit memory directives like "Remember that ClearDesk depletions are trending in the Northeast" could be intercepted by lightweight brand/performance shortcuts before router reached memory keyword handling.

**Solution:**
- Added IsMemoryCommand() as first gate in TryKeywordClassify()
- Preserved existing behavior for portfolio and single-brand lookups after memory early exit
- Expanded router regressions to cover "remember …" commands with trend/depletion language

**Impact:**
- Store/forget/reset directives always route to MemoryManagementAgent even with brand/trend mention
- Ordinary brand performance questions stay on lightweight General path

---

### 2026-06-03T21:45:00Z — Memory Store Routing Restored

**Status:** ✅ Complete — 117 memory/prompt/router tests pass

**Issue:** "Remember that..." commands falling through to general/fallback; Memory Panel stayed empty.

**Solution:**
- Restored "remember that" and "remember this" keyword routing to MemoryManagement
- Updated router prompt to classify both explicit store and clear/forget as memory/management
- Flipped router regression tests for store command routing

**Impact:** "Remember that ..." now reaches MemoryManagementAgent for agent's store-vs-clear discrimination

---

### 2026-06-03T20:06:00Z — Memory Management Router Defense-in-Depth

**Status:** ✅ Complete — Commit part of work, 1,972 tests pass

**Issue:** Router misclassification could cause benign "remember..." store requests to trigger destructive operations, causing data loss.

**Solution:**
- Removed "remember" and store-intent keywords from memory/management router patterns
- Updated MemoryManagementAgent prompt to validate intent before destructive operations
- Added store-vs-clear discrimination to specialist agent

**Impact:**
- Router now fail-closed on store intents
- Defense-in-depth: specialist agent validates intent independent of routing

**Decision:** "Memory-management routing must fail closed on destructive intent only" documented in decisions.md

---

## Archive

Detailed work from June 3, May 18, May 16, and May 15 available in **history-archive.md**. Includes:
- Span Type Tags telemetry work
- Trace Dashboard "Unknown" LLM model fixes
- NuGet upgrade sweep and deferred packages
- Asp.Versioning.Http 8.1.0 → 10.0.0 upgrade analysis
- coverlet.collector 6.0.4 → 10.0.1 upgrade analysis
- Older telemetry, timeout, and API session work

---

### 2026-06-30T16:54:45-04:00 — Observability Cost Dashboard Top Tools Endpoint

**Status:** ✅ Complete — backend suite passed (RetailPulse.Tests 1,998 passed; LoadTests 2 skipped)

**What:** Added `ToolUsageStat`, `ITraceCollector.GetToolStats`, the `InMemoryTraceCollector` aggregation, and `GET /api/observability/costs/tools`.

**Why:** The Cost Dashboard was broken because the frontend read `trend`, `agentBreakdown`, and `topTools` from the summary-only `/costs` response instead of calling the dedicated endpoints. Top Tools needed a new tracing-backed endpoint because `UsageEvent` has tool names but no duration; duration lives on tool trace spans.

**Team impact:** Duration-aware tool usage stats should be sourced from `ITraceCollector`, not `ICostTracker` usage events.

---

### 2026-08-05T09:57:34-04:00 — Issue #11: Secretless ACR image pull for Container Apps

**Status:** ✅ Complete — Bicep validated (no artifact), hooks syntax-checked, deployment-contract tests 39/39. Publix + Kroger both APPROVE.

**What:** Dedicated Basic-SKU ACR (`infra/modules/container-registry.bicep`, `adminUserEnabled: false`) with `AZURE_CONTAINER_REGISTRY_ENDPOINT`/`_NAME`/`_RESOURCE_ID` outputs from `infra/main.bicep`, plus cross-platform postprovision hooks (`azd-hooks/postprovision.ps1`/`.sh`) that idempotently grant `AcrPull` to API/MCP/TeamsBot system identities and set `az containerapp registry set --identity system`.

**Why:** System-identity Container Apps lost registry config after Bicep provisioning (UNAUTHORIZED pull). The self-pull binding is circular in one Bicep pass; a postprovision hook breaks the cycle and re-asserts idempotently on every `azd up`/`provision`.

**Team impact:** New Container Apps must use a system identity AND be added to the app list in BOTH hooks. Decision recorded in decisions.md; guardrails in `DeploymentContractTests.cs`; ops docs in `docs/deployment-azd.md`.
