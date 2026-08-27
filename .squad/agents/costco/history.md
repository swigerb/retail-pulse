# Costco — History

## Recent Work (2026-08-11)

### 2026-08-11T11:59Z — Issue #67 (P0): APIM AI Gateway verifier root cause + mandatory gate + publish-race fix

**Status:** ✅ PR opened — https://github.com/swigerb/retail-pulse/pull/73 (branch `squad/67-apim-hardening-gate`, worktree `retail-pulse-wt-67-apim-hardening`).

**Issue:** #67 — live post-`azd up` run of `scripts/Verify-ApimAiGateway.ps1` reported 9/24 invariant failures against production.

**Key finding (root cause):** Not an infra regression. Direct ARM REST inspection of `rg-retailpulse-demo-eus-001` (bearer token + `Invoke-RestMethod`, bypassing both the nonexistent `az apim api policy/backend/diagnostic show` subcommands and `az rest`'s Windows UTF-8-BOM crash) confirmed the `retail-pulse-foundry` backend, the API policy (MI auth, token-limit, emit-token-metric), and both API diagnostics (applicationinsights metrics=true, azuremonitor LLM logs) were already fully and correctly provisioned live. The verifier script called three `az apim` subcommands that don't exist in az CLI 2.81.0; each was piped through `2>$null`, swallowing the "not recognized" error and producing 9 false `[FAIL]`s. This independently corroborates Kroger's parallel finding in `docs/incidents/2026-08-11-apim-hardening-gap.md`.

**Fix (4 commits on `squad/67-apim-hardening-gate`):**
1. Rewrote `scripts/Verify-ApimAiGateway.ps1` to use ARM REST (`Get-ArmAccessToken`/`Invoke-ArmGet`) instead of the broken CLI calls — verified live 24/24 PASS (twice) against production.
2. Wired the verifier into `azd-hooks/postprovision.ps1`/`.sh` as a mandatory gate — any exit code other than 0 (pass) or 2 (genuine env precondition) throws and fails `azd provision`/`azd up`.
3. Added `azd-hooks/predeploy.ps1`/`.sh` (wired into `azure.yaml`) that runs a sequential `dotnet restore`+`build RetailPulse.slnx` before azd's parallel per-service publish, fixing the `RetailPulse.ServiceDefaults.sourcelink.json` concurrent-write race that forced the manual sequential redeploy during the incident.
4. Added `tests/RetailPulse.Tests/Deployment/CompiledArmDeploymentGraphTests.cs` — asserts on the actual compiled ARM JSON (`az bicep build` output), not Bicep source text, for backend/policy/diagnostics/RBAC/ACA wiring (issue #67 explicit requirement). Extended `DeploymentContractTests.cs` with predeploy/postprovision-gate hook-wiring coverage.

**Verified locally:** `dotnet build` (0 errors), full test suite 2637/2637 passed, `dotnet format --verify-no-changes` clean, live 24/24 PASS against real Azure.

**NOT verified (needs Publix/Brian, tracked as follow-up in PR):** a fresh full end-to-end `azd up --no-prompt` run with the new predeploy/mandatory-gate hooks in place — this sandbox has `az`/`bicep` CLI access but not `azd` itself.

**2026-08-11T12:51Z — PR #73 conflict resolution:** Publix found PR #73 had gone CONFLICTING with `main` — two competing verifier fixes (#68/#69 "ARM REST fallback + BOM-safe policy read", #72 "live APIM verifier hardening" using `Invoke-WebRequest`) landed on main while this PR was in flight. Publix independently reproduced main's `Invoke-WebRequest` fix (#72) still crashing live with the same `UnicodeEncodeError` on the ARM BOM response, and confirmed my `Invoke-RestMethod` bearer-token approach has no such crash (24/24 PASS + live chat completion through the gateway). Rebased `squad/67-apim-hardening-gate` onto `origin/main` @ `751feb2`; resolved the single conflict in `scripts/Verify-ApimAiGateway.ps1` by keeping my `Invoke-RestMethod` implementation and merging in main's useful `-SelfTest` offline regression fence (reworked to validate against the confirmed-working HTTP client instead of `Invoke-WebRequest`), which also keeps main's new `verify-script-selftest` CI job passing. No Bicep/IaC or `DeploymentContractTests.cs` conflicts occurred. Re-ran full local suite (2637/2637 pass, `dotnet format --verify-no-changes` clean) and re-verified live 24/24 PASS against production post-rebase. Force-pushed; PR #73 now reports `mergeable: MERGEABLE`, `mergeStateStatus: CLEAN`, all 8 CI checks green. Updated PR description to document the rebase and cite Publix's live BOM-crash repro as the evidence for keeping my implementation over main's competing fix.

### 2026-08-11T00:56:21Z — APIM AI Gateway IaC + Postprovision Wiring (Overnight Session)

**Status:** 🔴 Blocker — CI linting failure; awaiting format correction.

**Issue:** #51 — Make APIM AI Gateway first-class in primary azd deployment

**Commits (4 overnight):**
1. `de57de5` — Add APIM gateway live test plan (docs/testing/apim-ai-gateway-live-test-plan.md)
2. `5ccff59` — Add first-class APIM gateway IaC (Developer-tier APIM, inference API, policy, diagnostics, role assignment in primary Bicep)
3. `ca36976` — Route API OpenAI through APIM (postprovision hooks, config wiring, secret references)
4. `2363f14` — Clean up stale APIM references in docs/scripts

**CI Results (Run #31451145085):**
- Security: ✅ PASS
- Build & Test: ✅ PASS (2,558 tests)
- Frontend: ✅ PASS
- Auth Matrix: ✅ PASS
- **Lint (dotnet format): ❌ FAIL** — Formatting violations detected in backend files

**Blocker:**
`dotnet format --verify-no-changes` failed. Must rerun format locally, commit, and push before Kroger (lead review) and Publix (QA acceptance) can proceed.

**Next Action:**
1. Run `dotnet format` locally
2. Stage + commit formatted changes
3. Push to squad/apim-ai-gateway-demo-eus-001
4. CI re-runs automatically

**Learnings:**
- Bicep + .NET formatting rules must pass on all APIM commits before architectural review.
- Postprovision hooks require careful wiring to avoid stale config; test plan captures live acceptance gates.
- PR remains DRAFT until format + Kroger approval.

---

## Recent Work (2026-08-05)

### 2026-08-05T09:00:00Z — Inline chart-JSON extraction + shared chart-spec normalizer (Issue #15)

**Status:** ✅ Complete — Chart JSON properly extracted/normalized; inline charts now render instead of appearing as raw text bubbles.

**Issue:** #15 — Live app rendered raw chart JSON as assistant bubble instead of chart for "Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast".

**Solution:**
- New `ChartSpecNormalizer` (`src/RetailPulse.Api/Charts/ChartSpecNormalizer.cs`) maps LLM chart-JSON variations onto canonical `ChartSpec`:
  - Alternate Chart.js schema `data:{labels,series:[{name,values}]}`
  - Axis titles under `options.xAxisLabel`/`yAxisLabel`
  - `options.orientation:"horizontal"` on bar → `horizontalBar`
  - Strict validation: requires `type` + non-empty `title` + ≥1 bindable datapoint
- New pipeline pass `AgentExecutionPipeline.ExtractInlineCharts` (balanced-brace, string/escape-aware scan) promotes chart-spec JSON to structured `charts` when tool path produced no charts
- `ChartDataTool.TryRecover` tries normalizer first for non-canonical but well-formed payloads
- Frontend `sanitizeMessage` gained guard stripping leaked chart JSON blocks

**Why:** Telemetry showed demand agent + CreateChart ran but model emitted chart in text using non-canonical schema. Root cause was response-contract/parsing gap, not inference failure.

**Impact:**
- `charts` now reliably populated for inline-narrated charts
- Prose no longer contains raw chart JSON
- Defense-in-depth: frontend guards against stale backend behavior

**Validation:** backend `dotnet test` 2034 passed; frontend `vitest` 298 passed; `npm run build` and `dotnet format --verify-no-changes` clean.

**Learnings:**
- LLM chart schemas vary; normalizer must handle realistic variations (Chart.js vs canonical)
- Pipeline must guard against duplicates: only promote inline charts when tool path produced none
- Frontend defensive stripping catches schema JSON even against stale backend

**Guardrail:** Never use enum `switch` on `JsonValueKind` in dotnet format–governed code (IDE0010/IDE0072 mangle); use `if`/`else` or `==` ternary chains.

---

## Archive

Detailed work from June 2026 (Memory Panel UserId divergence, memory routing
defense-in-depth, Observability Cost Dashboard Top Tools endpoint), June 3, May 18, May 16,
and May 15 available in **history-archive.md**. Includes:
- Memory Panel/routing fixes (June 3-4)
- Observability Cost Dashboard Top Tools endpoint (June 30)
- Span Type Tags telemetry work
- Trace Dashboard "Unknown" LLM model fixes
- NuGet upgrade sweep and deferred packages
- Asp.Versioning.Http 8.1.0 -> 10.0.0 upgrade analysis
- coverlet.collector 6.0.4 -> 10.0.1 upgrade analysis
- Older telemetry, timeout, and API session work

---
### 2026-08-05T09:57:34-04:00 — Issue #11: Secretless ACR image pull for Container Apps

**Status:** ✅ Complete — Bicep validated (no artifact), hooks syntax-checked, deployment-contract tests 39/39. Publix + Kroger both APPROVE.

**What:** Dedicated Basic-SKU ACR (`infra/modules/container-registry.bicep`, `adminUserEnabled: false`) with `AZURE_CONTAINER_REGISTRY_ENDPOINT`/`_NAME`/`_RESOURCE_ID` outputs from `infra/main.bicep`, plus cross-platform postprovision hooks (`azd-hooks/postprovision.ps1`/`.sh`) that idempotently grant `AcrPull` to API/MCP/TeamsBot system identities and set `az containerapp registry set --identity system`.

**Why:** System-identity Container Apps lost registry config after Bicep provisioning (UNAUTHORIZED pull). The self-pull binding is circular in one Bicep pass; a postprovision hook breaks the cycle and re-asserts idempotently on every `azd up`/`provision`.

**Team impact:** New Container Apps must use a system identity AND be added to the app list in BOTH hooks. Decision recorded in decisions.md; guardrails in `DeploymentContractTests.cs`; ops docs in `docs/deployment-azd.md`.

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