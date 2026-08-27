# Squad Decisions

## Active Decisions

### 2026-08-11: Production hardening session — final outcomes

**By:** Scribe (session logger), on behalf of Brian Swiger

**Scope closed this session (post PR #56 P0 closeout, umbrella #59):**

- **Issues — CLOSED:** #60 (APIM AI Gateway hardening: Bicep contract tests + runtime startup
  guard + live post-provision verification), #61 (backend `PromptIdeaAcceptanceManifest` +
  APIM-only guardrail), #62 (frontend prompt-ideas manifest mirror + `data-testid`s), #68
  (`Verify-ApimAiGateway.ps1` false failures when `az apim` extension not installed on
  Windows), #71 (verifier: replace `az rest` with `Invoke-WebRequest` to eliminate the
  Windows UTF-8 BOM codepage crash).
- **Issues — OPEN (intentionally):** #59 (umbrella — remains open pending production sweep
  evidence), #63 (QA production sweep of all Prompt-ideas + Charts entries — actionable only
  when an authenticated 26-prompt production run is executable), #70 (frontend deploy build
  TS7006/TS7016 — not reproducible from clean `origin/main`, awaiting raw azd/SWA build log).
- **PRs — MERGED:** #65 (Prompt-ideas frontend acceptance contract + drift tests, 26 prompts),
  #66 (APIM AI Gateway hardening — runtime guard + static contract + live verify + CI Bicep
  gate), #69 (`fix(verify-apim)`: ARM REST fallback + BOM-safe policy read, #68), #72
  (`fix(verify-apim)`: PowerShell-native ARM path + expanded self-test — fixes the Windows
  BOM crash on live policy fetch).
- **PRs — OPEN, gated (do NOT merge):** #64 (`test(#63)`: full 26-prompt production
  acceptance matrix + prose runner) — gated on live authenticated production evidence.

**Prompt-ideas acceptance contract:** exactly **26 prompts** total — **9 chart** entries
+ **17 prose** entries — enforced bidirectionally by the backend `PromptIdeaAcceptanceManifest`
and the frontend drift tests (per PR #65 and #61/#62 closures).

**APIM AI Gateway live verifier:** on production (`rg-retailpulse-demo-eus-001` /
`apim-5aldk7aotqods`), the fixed `scripts/Verify-ApimAiGateway.ps1` (post PR #69/#72)
reports **25/25 PASS** invariants live. This is the current authoritative gate; nothing
that would break these invariants may merge.

**Authenticated production sweep — blocker recorded:** an interactive authenticated
26-prompt run against production is blocked by `AADSTS65001` (user/administrator has not
consented to the application). Under the tenant constraints established for issue **#57**,
no interactive consent will be granted in this session — an unattended service-principal
synthetic monitor (tracked by #57) is the sanctioned path. This is why PR #64 stays open
and #63 stays open despite all their code-side work being complete and green.

**Deployment status:** the session's deployment to
`retailpulse-demo-eus-001` completed successfully after the PR #66 / PR #65 merges, and
the frontend at the production origin is serving the fresh Static Web App assets built
from the merged `main` (verified by fresh asset presence — no raw build output or secrets
retained in `.squad/` per secret-handling rules).

**Team impact / non-goals for future sessions:**
- Do NOT merge PR #64 until an authenticated production sweep is executable and the
  evidence bundle for #63 is produced (blocked on #57's service-principal monitor).
- Do NOT close #59 until the umbrella sweep evidence exists.
- Do NOT ship a "fix" for #70 without a real reproducer from the azd/SWA build log —
  clean `origin/main` builds green (Chick's inbox decision, folded into this session).

**Evidence:** Live verifier output, chart-run screenshots, APIM policy/backend/diagnostic
JSON captures, and deployment-hook logs live under
`.squad/evidence/publix-apim-2026-08-11/` (uncommitted, coordinator-owned per source-of-truth
rules; no tokens, no `.auth/me` payloads, no screenshots or raw azd output committed to
tracked state by Scribe).

### 2026-08-11: Issue #67 — root cause was a broken verifier script, not an APIM AI Gateway infra regression

**By:** Costco (Backend Dev), corroborated independently by Kroger (Lead/Architecture) via
`docs/incidents/2026-08-11-apim-hardening-gap.md`

**What:** Live ARM inspection of production (`rg-retailpulse-demo-eus-001` /
`apim-5aldk7aotqods`) confirmed the `retail-pulse-foundry` backend, its managed-identity
credentials, the API policy (`set-backend-service`, `authentication-managed-identity`,
`azure-openai-token-limit`, `azure-openai-emit-token-metric`), and the API-level
`applicationinsights` / `azuremonitor` diagnostics were already fully and correctly
provisioned by PR #66 / #52. The `9/24` invariant failures reported by
`scripts/Verify-ApimAiGateway.ps1` immediately after the deploy-race recovery were **false
positives**: the script called three `az apim` subcommands that do not exist in the pinned
Azure CLI 2.81.0 (`az apim api policy show`, `az apim backend show`, `az apim api
diagnostic show`), and each call was piped through `2>$null | ConvertFrom-Json`, silently
swallowing the "command not recognized" error and producing null-derived assertion
failures.

**Remediation (delivered on `squad/67-apim-hardening-gate`, PR #73, Costco author):**
1. Rewrote `scripts/Verify-ApimAiGateway.ps1` to use bearer-token + `Invoke-RestMethod`
   ARM GETs (`Get-ArmAccessToken` / `Invoke-ArmGet`) — sidesteps both the nonexistent CLI
   subcommands and `az rest`'s Windows UTF-8 BOM `UnicodeEncodeError`. 404/403 raise real
   failures; only exit 2 (genuine env precondition, e.g. not signed into `az`) is a soft
   skip.
2. Wired the verifier into `azd-hooks/postprovision.ps1` / `.sh` as a **mandatory**,
   non-bypassable gate — any exit code other than 0 or 2 throws and fails
   `azd provision` / `azd up`.
3. Added `azd-hooks/predeploy.ps1` / `.sh` (wired via `azure.yaml`) that runs a single
   sequential `dotnet restore` + `dotnet build RetailPulse.slnx` before azd's parallel
   per-service publish, fixing the `RetailPulse.ServiceDefaults.sourcelink.json`
   concurrent-write race that caused the packaging failure during the incident.
4. Added `tests/RetailPulse.Tests/Deployment/CompiledArmDeploymentGraphTests.cs` — asserts
   on the actual compiled ARM JSON (`az bicep build` output), not Bicep source text — so a
   future module-boundary or conditional-gating regression cannot pass a source-text-only
   contract test while silently dropping the resource.

**Team-wide implications (Kroger, tooling policy):**
- Any future "verifier reports N failures" incident must first confirm the verifier's own
  tooling assumptions (CLI subcommand existence, error-swallowing patterns) before assuming
  IaC drift — this is now the second time in this repo where the infra was correct and the
  diagnostic tooling was the defect.
- `az rest` has a Windows-specific crash (`UnicodeEncodeError` on APIM's UTF-8-BOM policy
  response bodies) — future scripts needing raw ARM REST must use `Invoke-RestMethod` /
  `Invoke-WebRequest` + a bearer token from `az account get-access-token` instead.
- Never let `2>$null` mask a genuinely broken `az` command as "resource not found." A
  verification script that silently misreports true state is more dangerous than no
  verification at all.
- Follow-up hardening for the same class of defect landed in PR #69 and PR #72 (closed
  issues #68 and #71) and is now the shipped verifier on `main`.

### 2026-08-11T17:04:41Z: Issue #67 RESOLVED — PR #73 MERGED (squash, `463612d`); merge-conflict resolution kept `Invoke-RestMethod` over competing `Invoke-WebRequest` fix

**By:** Scribe, consolidating decisions from Kroger (Lead/merge), Costco (rebase/conflict
resolution), and Publix (independent verification) for the final resolution batch of the
P0 APIM hardening-gate incident.

**Outcome:** Issue #67 CLOSED (auto-closed by merge). PR #73 squash-merged to `main` at
`463612d`, `mergedAt: 2026-08-11T17:04:41Z`.

**Mid-flight complication and resolution:** While PR #73 was open, two competing verifier
fixes (#68/#69, #72) landed on `main`, putting PR #73 into `mergeable: CONFLICTING`. Publix
independently reproduced main's competing fix (#72, `Invoke-WebRequest`-based, commit
`50991c7`) **crashing live** with the same `UnicodeEncodeError` BOM defect it claimed to
fix, while confirming PR #73's `Invoke-RestMethod` approach has no such crash (24/24 PASS +
live chat completion through the gateway). Costco rebased `squad/67-apim-hardening-gate`
onto `main` @ `751feb2`, resolved the conflict in `scripts/Verify-ApimAiGateway.ps1` by
**keeping the `Invoke-RestMethod` bearer-token implementation** and merging in the useful
`-SelfTest` offline regression fence from #72 (reworked to validate against
`Invoke-RestMethod`'s own decode behavior). Kroger independently re-verified the rebased
branch before merging: `gh pr view 73` → `mergeable: MERGEABLE`, `mergeStateStatus: CLEAN`;
all 8 GitHub Actions checks passing; squash-merged referencing `Closes #67`.

**Standing team guidance (from Costco/Publix):**
- Never assume an HTTP-client swap "fix" is validated live — #72 looked plausible but was
  never re-run against the real ARM response shape before merging, and reintroduced the
  exact BOM crash it claimed to close. Future PRs fixing a live-verification crash should
  include live re-run output in the PR description.
- When two agents converge on the same root cause with different implementations, prefer
  whichever is **proven against the real live resource**, not whichever landed on `main`
  first — merge order is not evidence of correctness.
- `Invoke-RestMethod` (direct JSON deserialization) is the team's confirmed-safe pattern for
  reading ARM resources with BOM-prefixed response bodies from PowerShell on Windows.
  `Invoke-WebRequest` with a manual byte-array BOM strip is NOT safe here — do not
  reintroduce it.
- The `-SelfTest` / `verify-script-selftest` CI job pattern (signin-free regression fence)
  is worth keeping regardless of which HTTP client won.

**Verification (independently confirmed 3x total: Costco x2, Kroger x1, Publix x1):**
`dotnet build` clean, 2637/2637 tests pass, `dotnet format --verify-no-changes` clean, live
verifier 24/24 PASS against `rg-retailpulse-demo-eus-001` / `apim-5aldk7aotqods`, all 8 CI
checks SUCCESS, real APIM gateway chat completion confirmed live (200 OK) by Publix.

**Reviewer gate:** Kroger (Lead) reviewed, approved (posted as PR comment since the
authenticated `gh` account is the PR's own author identity), and personally executed the
merge after independent re-verification — per Reviewer Protocol.

**REMAINING GENUINE BLOCKER — explicitly not closed by this merge:** no sandboxed agent on
this team has real `azd` CLI access. A full, fresh `azd up --no-prompt` end-to-end dry run
exercising the new `postprovision` (mandatory verifier gate) and `predeploy`
(sourcelink-race fix) hooks together under azd's actual orchestration, from a clean state,
has NOT been performed by anyone. This requires Brian or a CI runner with `azd` to execute.
It is the final acceptance step before PR #64 (26-prompt production acceptance sweep) can
resume. **PR #64 remains OPEN/HELD** — not merged, not touched this session. Chick was not
spawned this incident — correctly held per Brian's directive; no action needed until PR #64
resumes.

### 2026-08-11: PR #73 (Issue #67 remediation) — APPROVED on architecture, merge held pending Publix

**By:** Kroger (Lead / final reviewer)

**Verdict:** APPROVE on architecture and fix quality. Formal `gh pr review --approve` was
blocked because the authenticated `gh` account in this environment is the PR's own author
identity; the equivalent approval was posted as PR comment
`#issuecomment-5256104438` on PR #73 and treated as the gating approval for Squad
purposes.

**Independently verified by Kroger (not just re-reading Costco's claims):**
1. Verifier fix uses `Get-ArmAccessToken` + `Invoke-ArmGet` (also avoids `az rest`'s BOM
   crash); 404/403 raise; any other exception rethrows.
2. Re-ran the fixed verifier against live prod
   (`rg-retailpulse-demo-eus-001` / `apim-5aldk7aotqods` / `ca-retailpulse-api`) —
   **24/24 PASS**, matching Costco's report.
3. `azd-hooks/postprovision.{ps1,sh}` invoke the verifier as a hard gate — exit 0 = pass,
   exit 2 = env-precondition skip (unreachable from the invariant path); any other exit
   throws and fails `azd`.
4. `CompiledArmDeploymentGraphTests.cs` inspects the actual compiled ARM JSON via
   `az bicep build`, materially closing the "conditional `if()` / module-wiring drop" gap
   a source-grep test cannot catch.
5. `azd-hooks/predeploy.{ps1,sh}` correctly serialize a single restore+build before azd's
   parallel per-service publish, fixing the `sourcelink.json` writer race.
6. Local build clean; `dotnet test --filter FullyQualifiedName~Deployment` → 105/105 pass,
   0 skipped — confirms the compiled-ARM-graph test executed for real.

**Merge is NOT authorized yet.** Per Brian's directive and the Reviewer Protocol, merge
requires Publix's independent live verification (real APIM chat completion end-to-end +
ideally one clean `azd up --no-prompt` exercising the new predeploy + postprovision hooks
together, since Costco's sandbox does not have `azd`). PR #64 (26-prompt sweep) remains
held regardless until #67 is fully closed. CI check registration could not be independently
confirmed from the reviewer's sandbox and is an open item for whoever merges.

### 2026-08-11: Issue #70 (frontend deploy TS7006/TS7016) — not reproducible from clean `origin/main`; no code fix ships until real evidence surfaces

**By:** Chick (Frontend Dev)

**What:** The reported production frontend build failure (`TS7006` / `TS7016` in
`PromptLibrary.tsx`, `CompetitiveDashboard.tsx`, `telemetryHub.ts`, `DocumentUpload.tsx`)
does **not** reproduce from a clean worktree of `origin/main` @ `47b94a2` (the merge of
PR #65):
- `npm ci` — 477 packages, clean
- `npm run build` (`tsc -b && vite build`) — **0 TypeScript errors**, `dist/` emitted
- `npx tsc -b --force` — exit 0, no errors
- `npm test -- --run` — **541 / 541 tests pass** across 60 files (26-prompt acceptance
  manifest included and green)
- `npm run lint` — only pre-existing errors on `main`; none of type `TS7006` / `TS7016`,
  none in the four reported files (one pre-existing `react-hooks/set-state-in-effect`
  warning in `CompetitiveDashboard.tsx:120` predates this branch)

Static inspection: all four files have explicitly typed props/state/callbacks and their
imports resolve on POSIX case-sensitivity.

**Decision:**
- **No code fix ships** on `squad/67-fix-frontend-deploy-build`; nothing on `main` is
  broken. `azd deploy frontend` against current `main` (`47b94a2`) can proceed — the build
  produces a valid `dist/`.
- **Blocked on real evidence.** A code change requires the raw azd / SWA build log with
  actual failing lines and commit SHA. If the SWA managed build container re-fails, the
  fix is almost certainly to clear its build cache (Oryx `.oryx-cache` / regenerate
  `node_modules` inside the SWA build image), not to modify code.
- **Kroger (architecture) follow-up:** if this recurs, consider pinning the SWA build
  image's Node/TS version explicitly in `azure.yaml` or the SWA workflow so deploy-time
  toolchain drift cannot diverge from CI.
- **Publix (QA):** the 26-prompt acceptance manifest and 541-test suite remain green on
  `main` — no regression to gate.
- **Ralph:** #70 remains the open follow-up; awaiting the deploy log to decide whether to
  reopen this as real work or close as environmental.

### 2026-08-05: Dedicated Basic ACR + postprovision hook for secretless Container Apps image pull

**By:** Costco (Backend Dev)

**What:** Retail Pulse now provisions its own **Basic-SKU Azure Container Registry**
(`infra/modules/container-registry.bicep`, `adminUserEnabled: false`) and emits
`AZURE_CONTAINER_REGISTRY_ENDPOINT` / `_NAME` / `_RESOURCE_ID` from `infra/main.bicep`.
A cross-platform **postprovision hook** (`azd-hooks/postprovision.ps1` + `.sh`, wired in
`azure.yaml`, `continueOnError: false`) idempotently grants `AcrPull` to each of the three
Container Apps' system-assigned identities and runs `az containerapp registry set --identity
system` — no registry secrets, no admin user.

**Why:** After Bicep provisioning during `azd up`, the three system-identity Container Apps
lost their registry configuration and the API failed pulling from `*.azurecr.io` with
`UNAUTHORIZED`. Binding an app to pull from ACR via its *own* system identity is circular in
a single Bicep pass (principalId doesn't exist until the app is created; the AcrPull grant
the registry binding needs depends on that principalId), and a re-provision can strip the
registry block azd set during deploy. Doing it in a postprovision hook breaks the cycle and
re-asserts state on every `azd up`/`azd provision`, so clean and repeated deploys are
self-contained and idempotent.

**Team impact:**
- azd now pushes images to the dedicated ACR (via `AZURE_CONTAINER_REGISTRY_ENDPOINT`), not
  an implicit one. `azd up` / `azd provision` / `azd deploy` require no manual registry steps.
- Any new Container App added to `infra/modules/container-apps.bicep` must (a) use a
  system-assigned identity and (b) be added to the app list in **both** postprovision hooks so
  it gets `AcrPull` + system-identity registry auth.
- Deployment identity/RBAC sequencing, outputs, and operational notes are documented in
  `docs/deployment-azd.md` ("Container images & secretless registry pull").
- Guardrails: `tests/RetailPulse.Tests/Deployment/DeploymentContractTests.cs`.

### 2026-08-05T09:00:00Z: Inline chart-JSON extraction + shared chart-spec normalizer

**By:** Costco (Backend Dev), with Chick (Frontend) defense-in-depth

**Issue:** #15 — live app rendered raw chart JSON as an assistant bubble instead of a chart for `Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast`.

**What:**
- New shared `ChartSpecNormalizer` (`src/RetailPulse.Api/Charts/ChartSpecNormalizer.cs`) maps realistic LLM chart-JSON variations onto the canonical `ChartSpec`:
  - alternate Chart.js-style schema `data:{labels,series:[{name,values}]}` (in addition to canonical `data:[{legend,values:[{x,y}]}]`)
  - axis titles under `options.xAxisLabel`/`yAxisLabel`
  - `options.orientation:"horizontal"` on a `bar` → `horizontalBar`
- New pipeline pass `AgentExecutionPipeline.ExtractInlineCharts` (`AgentExecutionPipeline.ChartExtraction.cs`): balanced-brace, string/escape-aware scan that promotes chart-spec JSON to structured `charts` — only when tool path produced no charts.
- `ChartDataTool.TryRecover` now tries normalizer first, recovering non-canonical but well-formed payloads.
- Frontend `sanitizeMessage` gained guard stripping leaked chart-spec JSON blocks.

**Why:** Telemetry showed demand agent + CreateChart ran, but the model emitted the chart in its text using non-canonical schema. Root cause was response-contract/parsing gap, not inference failure.

**Team impact:**
- **Chick (Frontend):** `charts` now reliably populated for inline-narrated charts; prose no longer contains chart JSON.
- **Publix (QA):** Regression coverage added — `AgentPipelineTests.ExtractInlineCharts_*`, `ChartDataToolTests` alternate-schema, `sanitizeMessage.test.ts` chart-strip cases.
- **Guardrail:** Never use enum `switch` on `JsonValueKind` in dotnet format–governed code (IDE0010 mangle); use `if`/`else` or `==` ternary chains.

**Validation:** backend `dotnet test` 2034 passed; frontend `vitest` 298 passed; `npm run build` and `dotnet format --verify-no-changes` clean.

### 2026-08-05T10:30:00Z: Persistent prompt library + explicit deployed ACA stack in docs

**By:** Chick (Frontend Dev) — implementation owner for issue #17

**What:**
1. **Persistent, discoverable prompt library.** Curated prompts now live in always-available `Prompt ideas` control (`src/RetailPulse.Web/src/components/PromptLibrary.tsx`), built on Fluent `Popover` with `trapFocus`. Opens categorized panel before and during a conversation. Selecting a prompt reuses existing safe send path and closes the panel.
2. **Single source of truth for prompts.** Categories and text moved to `src/RetailPulse.Web/src/constants/prompts.ts` (`PROMPT_CATEGORIES`, `PromptCategory`). Both welcome chips and persistent library import from that module — no duplication.
3. **Docs: deployed Azure stack made explicit.** README Technology Stack now lists Azure Container Apps (backend), Azure Static Web Apps (frontend), and Azure Container Registry (managed-identity pulls). Corrected frontend host from "App Service (Node 20 LTS)" to "Static Web Apps". Project Structure and `docs/teams-setup.md` corrected to match actual ACA deployment.

**Why:**
- Prompt discoverability should not disappear after first message.
- Single prompt module prevents drift between welcome state and persistent library.
- README omitted ACA and misdescribed frontend host, contradicting actual `infra/`.

**Team impact:**
- **Chick / frontend:** Add new prompts in `constants/prompts.ts` only. Reuse `PromptLibrary` for new composer affordances.
- **Publix / QA:** Frontend regression coverage in `PromptLibrary.test.tsx` (open/close, category filter, selection, roles/names, keyboard Enter/Escape) and ChatPanel cases.
- **Kroger / lead + Costco / backend:** No backend changes. Docs now match `infra/` and `docs/deployment-azd.md`.

### 2026-08-05T14:00:00Z: PR #16 revision — frontend chart bindability parity + inline-chart dedup

**By:** Revision owner for PR #16 (`squad/15-fix-chart-json-leak`), independent of original author (reviewer protocol: rejected author cannot revise own branch).

**Issue:** Independent reviewer found two concrete defects in #15 chart-JSON-leak work.

**What changed:**

1. **HIGH — Frontend `sanitizeMessage.ts` over-stripped legitimate prose JSON.** Previous `looksLikeChartSpec` deleted prose containing empty/null/non-renderable payloads (`data:[]`, `data:null`, `data:{id:1}`) that backend normalizer correctly rejects. Frontend now mirrors `ChartSpecNormalizer` strictness via `chartHasBindableData` that requires ≥1 actual bindable datapoint across supported schemas. Malformed/empty JSON left visible.

2. **MEDIUM — Backend `AgentExecutionPipeline` dropped distinct inline charts.** Both paths stripped all recognizable inline chart JSON but only promoted recovered charts when tool path produced none; a distinct valid chart narrated in prose was lost. Replaced `charts.Count == 0` gate with new `MergeInlineCharts` helper that appends only non-duplicate inline charts. Deduplication uses new `ChartSpecSemanticComparer` (`src/RetailPulse.Api/Charts/ChartSpecComparer.cs`) that walks Type, Title/axis titles, legend, color, and points (X ordinal, Y within 1e-9 epsilon).

**Why:** Root-cause corrections, not masking or broad catches. Streaming and non-streaming paths use identical helper, keeping behavior consistent.

**Team impact:**
- **Chick (Frontend):** Prose with empty/placeholder chart JSON no longer silently deleted; real charts still stripped.
- **Costco (Backend):** Inline-chart recovery now merges distinct charts instead of dropping them; reuse `ChartSpecSemanticComparer` for future de-duplication.
- **Publix (QA):** Regression coverage added — `sanitizeMessage.test.ts` bindability suites, `AgentPipelineTests` `MergeInlineCharts_*`, `ChartSpecSemanticComparer_*`.

**Validation:** backend `dotnet test` 2046 passed; frontend `vitest` 309 passed; `npm run build`, `eslint`, `dotnet format --verify-no-changes` clean.

### 2026-08-10T14:30:00Z: Architecture review verdicts — APIM #51/PR #52 and Chart Matrix #50

**By:** Kroger (Lead)

**Issues:** #51 (APIM AI Gateway / PR #52), #50 (Chart Matrix P0)

**Verdict:**

1. **APIM #51 / PR #52 — Architecture APPROVED, not ready to leave draft:**
   - ✅ Architecture design meets generic + secret-safe bar (subscription key never persisted, cross-RG RBAC isolated, Developer-tier + system identity + LLM diagnostics)
   - ❌ `dotnet format` fails on `src/RetailPulse.Api/OpenAI/OpenAiConnectionSettings.cs` (imports ordering) — release-blocking per squad linting gates.
   - ❌ All six live-acceptance boxes in PR body unchecked. Publix must execute `docs/testing/apim-ai-gateway-live-test-plan.md` end-to-end (APIM provisioning, direct APIM inference, MI backend auth, deployed-app traversal, 429/Retry-After, App Insights token metrics + LLM diagnostics). No merge until both linting and acceptance evidence are green.

2. **Chart Matrix #50 — REJECT for merge readiness:**
   - Direction sound (`ChartAcceptanceManifest` on both surfaces, type-led `SelectBuilder`, grouped-region/growth-ranking/pie-mix/demand-line builders, `MinimumMarksForType` validator overload).
   - Scope materially incomplete: no commits on branch, no PR opened, validator overload not wired into fulfillment, no backend 9-prompt acceptance suite, no frontend real-Recharts render suite, no router→prefetch→budget→tools→fulfillment→ChartSpec trace assertions, no performance gate tests (<25K tool-context tokens, ≤5 tool calls), no CI gate, no chart-rendering guide/acceptance-matrix docs, no browser acceptance runner.
   - **Direction:** Return to Chick to complete scope items 1–8. If subsequent pass still ships without those gates, Publix (acceptance) + fresh specialist own revision under lockout.

**Why:** APIM meets landable bar but the demo must prove it live before leaving draft. Chart Matrix P0 is a systemic acceptance gate whose whole point is contract-tested determinism across every curated prompt; shipping builders without acceptance wired into tests and CI would re-open the regression #50 is meant to close.

**Team impact:**
- **Costco (Backend):** Format corrections are release-blocking. Rerun `dotnet format`, commit, push. Once CI passes, architecture approved (subject to Publix acceptance evidence).
- **Publix (QA):** Acceptance gate awaits linting fix + Kroger's pre-approved architecture. Once CI passes, own full live test plan execution and attach PASS/FAIL evidence to PR body before merge.
- **Chick (Frontend):** Chart Matrix work returned to you per Kroger review. Complete scope items 1–8 (acceptance manifest, validators, 9-prompt backend suite, frontend Recharts, CI gates, docs).

### 2026-08-11T02:10:00Z: APIM Gateway linting blocker + Chart Matrix P0 escalation (Overnight)

**By:** Ralph (Work Monitor)

**Issues:** #51 (APIM AI Gateway / PR #52), #50 (Chart Matrix P0)

**What:**

1. **APIM Gateway (PR #52) CI linting blocker — Kroger-flagged issue confirmed:** `dotnet format --verify-no-changes` failed in overnight CI run #31451145085. Formatting violations detected in `src/RetailPulse.Api/OpenAI/OpenAiConnectionSettings.cs` (imports ordering) — exact issue Kroger's architecture review flagged as release-blocking. Costco must run local format correction, commit, and push. Once CI passes, Publix executes live acceptance plan (`docs/testing/apim-ai-gateway-live-test-plan.md`). PR cannot leave draft until both linting passes AND live evidence attached.

2. **Chart Matrix P0 (Issue #50) follow-up on Kroger's direction:** Kroger's review (2026-08-10) rejected Issue #50 for merge readiness and directed "Return to Chick to complete scope items 1–8". Issue remains unassigned overnight. Ralph notes: Kroger's verdict requires Chick's action; if no assignment by next sync, Kroger may need escalate or directly assign.

**Dependency Chain:**
```
Costco: Format fix + push
  ↓
CI re-runs (full suite passes)
  ↓
Publix: Execute live acceptance plan
  ↓
Publix: Attach PASS/FAIL evidence to PR
  ↓
Kroger: Approve evidence + sign off
  ↓
Ready for merge to main

Parallel: Kroger's Chart Matrix direction
  ↓
Chick: Pick up scope items 1–8
  ↓
Chick: Open PR with full acceptance contract
  ↓
Publix + Kroger: Re-review for merge readiness
```

**Decision:**
1. Linting blocker is hard-stop (Kroger review binding). Costco must format + push immediately.
2. Chart Matrix assignment follows Kroger's written direction ("Return to Chick"). Ralph notes Chick assignment pending.


### 2026-08-05: Inline chart-JSON extraction + shared chart-spec normalizer

**By:** Costco (Backend Dev), with Chick (Frontend) defense-in-depth

**Issue:** #15 — live app rendered raw chart JSON as an assistant bubble instead of a chart
for `Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast`.

**What:**
- New shared `ChartSpecNormalizer` (`src/RetailPulse.Api/Charts/ChartSpecNormalizer.cs`)
  maps realistic LLM chart-JSON variations onto the canonical `ChartSpec`:
  - alternate Chart.js-style schema `data:{labels,series:[{name,values}]}` (in addition to the
    canonical `data:[{legend,values:[{x,y}]}]`),
  - axis titles under `options.xAxisLabel`/`yAxisLabel`,
  - `options.orientation:"horizontal"` on a `bar` → `horizontalBar`.
  It is strict: a recognized chart `type` + non-empty `title` + ≥1 bindable datapoint are all
  required, so non-chart/unusable JSON is rejected (left visible), never silently discarded.
- New pipeline pass `AgentExecutionPipeline.ExtractInlineCharts`
  (`AgentExecutionPipeline.ChartExtraction.cs`): balanced-brace, string/escape-aware scan of the
  reply text that strips any chart-spec JSON the model narrated as prose and promotes it to
  structured `charts` — but only when the tool path produced no charts (guards against duplicate
  renders). Wired into **both** the non-streaming and streaming pipeline paths (streaming runs it
  before `StreamReplyAsync`, so streamed tokens are clean too).
- `ChartDataTool.TryRecover` now tries the normalizer first, so a well-formed but non-canonical
  CreateChart payload is bound (`recovered:true`) instead of failing.
- Frontend `sanitizeMessage` gained a last-line guard that strips a leaked chart-spec JSON block
  (same strictness) so raw chart JSON never renders as prose even against a stale backend.

**Why:** Telemetry showed the demand agent + CreateChart ran, but the model emitted the chart in
its assistant *text* using a non-canonical schema, so `ExtractChartSpecs`/`ChartDataTool` couldn't
bind it (`charts` count = 0) and `SanitizeReplyText` didn't strip it. Root cause was a
response-contract/parsing gap, not inference failure.

**Team impact:**
- **Chick (Frontend):** `charts` is now reliably populated for inline-narrated charts; prose no
  longer contains chart JSON. `sanitizeMessage` strips recognized chart JSON defensively.
- **Publix (QA):** Regression coverage added — `AgentPipelineTests.ExtractInlineCharts_*`,
  `ChartDataToolTests` alternate-schema cases, and `sanitizeMessage.test.ts` chart-strip cases.
  The screenshot payload (alternate schema + prose) is asserted to strip to clean prose and yield
  one `horizontalBar` `ChartSpec`.
- **Guardrail — dotnet format landmine:** the `dotnet format` populate-switch fixer (IDE0010/
  IDE0072) mangles `switch`/switch-expressions on the `JsonValueKind` enum (injects
  `throw new NotImplementedException()` / bare `break` → CS0177/CS0161). Use `if`/`else` or `==`
  ternary chains on `JsonValueKind`, never an enum `switch`, in code that must pass the CI lint
  gate (`dotnet format --verify-no-changes`).

**Validation:** backend `dotnet test` 2034 passed; frontend `vitest` 298 passed; `npm run build`
and `dotnet format --verify-no-changes` clean.

### 2026-08-05: persistent prompt library + explicit deployed ACA stack in docs

**By:** Chick (Frontend Dev) — implementation owner for issue #17

**Date:** 2026-08-05

## What

Two related changes landed on `swigerb/17-aca-docs-prompt-library` (PR for issue #17):

1. **Persistent, discoverable prompt library.** The curated prompts used to appear
   only on the empty New Chat welcome state and vanished once a conversation
   started. There is now an always-available `Prompt ideas` control next to the
   composer (`src/RetailPulse.Web/src/components/PromptLibrary.tsx`), built on the
   Fluent `Popover` with `trapFocus`. It opens a categorized, keyboard-accessible,
   responsive panel and works both before and during a conversation. Selecting a
   prompt reuses the existing safe send path (`handleSuggestedClick` →
   `sendChatMessage`) and closes the panel.

2. **Single source of truth for prompts.** Prompt categories and text moved out of
   `ChatPanel.tsx` into `src/RetailPulse.Web/src/constants/prompts.ts`
   (`PROMPT_CATEGORIES`, `PromptCategory`). Both the welcome chips and the
   persistent library import from that module — no duplicated prompt arrays.

3. **Docs: deployed Azure stack made explicit.** The README Technology Stack now
   lists Azure Container Apps (backend hosting, scale-to-zero), Azure Static Web
   Apps (frontend), and Azure Container Registry (managed-identity image pulls).
   The Azure Deployment section no longer claims the frontend runs on Azure App
   Service (Node 20 LTS); it is Azure Static Web Apps. The Project Structure infra
   note and a `docs/teams-setup.md` backend-hosting line were corrected to match
   the actual ACA deployment. `docs/deployment-azd.md` was already accurate and is
   the source of truth.

## Why

- Prompt discoverability should not disappear after the first message.
- Keeping prompts in one module prevents drift between the welcome state and the
  persistent library.
- The README omitted ACA and misdescribed the frontend host, contradicting the
  real `infra/` (three Container Apps with `minReplicas: 0`, a Static Web App, and
  a dedicated Basic ACR with secretless managed-identity pulls).

## Team impact

- **Chick / frontend:** add new prompts in `constants/prompts.ts` only. Any new
  composer-adjacent affordance should reuse `PromptLibrary` rather than
  re-implementing prompt lists.
- **Publix / QA:** frontend regression coverage lives in
  `src/RetailPulse.Web/src/__tests__/PromptLibrary.test.tsx` (open/close, category
  filter, prompt selection, roles/names/focus, keyboard Enter/Escape) and new
  ChatPanel cases (availability before/after a message, library send path). Note:
  Fluent's trap-focus popover applies `aria-hidden` to its surface across repeated
  jsdom renders in a single file, so the ChatPanel integration test queries popover
  content with `{ hidden: true }`; the a11y contract is asserted in the clean
  `PromptLibrary.test.tsx`.
- **Kroger / lead + Costco / backend:** no backend or infra changes. Docs now match
  `infra/` and `docs/deployment-azd.md`; keep README hosting rows in sync if the
  deployment topology changes.

### 2026-08-05: PR #16 revision — frontend chart bindability parity + inline-chart dedup

**By:** Revision owner for PR #16 (`squad/15-fix-chart-json-leak`), independent of the original
author (reviewer protocol: rejected author cannot revise their own branch).

**Issue:** Independent reviewer found two concrete defects in the #15 chart-JSON-leak work.

**What changed:**

1. **HIGH — Frontend `sanitizeMessage.ts` over-stripped legitimate prose JSON.**
   `looksLikeChartSpec` previously treated any recognized `type` + `title` + (a `data` key OR an
   array `series`) as a chart, so it deleted prose containing empty/null/non-renderable payloads
   (`data:[]`, `data:null`, `data:{id:1}`) that the backend normalizer correctly rejects — silently
   erasing text with no chart rendered. Frontend now mirrors `ChartSpecNormalizer` strictness via a
   faithful TS port (`chartHasBindableData`) that requires ≥1 actual bindable datapoint across the
   supported schemas (canonical `data:[{legend,values}]`, labels/series object
   `data:{labels,series}` / single-series `data:{labels,values}`, and full-config top-level
   `series:[{name,data|values}]`). Malformed/empty/unrelated JSON is left visible.

2. **MEDIUM — Backend `AgentExecutionPipeline` dropped distinct inline charts.**
   Both pipeline paths stripped all recognizable inline chart JSON but only promoted recovered
   charts when the tool path produced none, so a distinct valid chart narrated in prose was lost
   whenever a tool chart already existed. Replaced the `charts.Count == 0` gate in **both** the
   non-streaming and streaming paths with a new `MergeInlineCharts` helper
   (`AgentExecutionPipeline.ChartExtraction.cs`) that appends only non-duplicate inline charts.
   Deduplication uses a new content-based `ChartSpecSemanticComparer`
   (`src/RetailPulse.Api/Charts/ChartSpecComparer.cs`) — record equality compares the `List<>`
   `Data`/`Values` members by reference, so it could never detect a structural duplicate; the
   comparer walks Type (ordinal-ignore-case), Title/axis titles (ordinal), and each series' legend,
   color, and points (X ordinal, Y within a 1e-9 epsilon).

**Why:** Root-cause corrections. No CSS masking, broad catches, or success-shaped fallback.
Streaming and non-streaming paths use the identical helper, keeping behavior consistent.

**Team impact:**
- **Chick (Frontend):** prose with empty/placeholder chart JSON is no longer silently deleted;
  real charts are still stripped from prose.
- **Costco (Backend):** inline-chart recovery now merges distinct charts instead of dropping them;
  reuse `ChartSpecSemanticComparer` for any future chart de-duplication.
- **Publix (QA):** regression coverage added — `sanitizeMessage.test.ts` bindability suites (the 3
  proven leak examples + empty canonical/full-config negatives + positive canonical/labels-series/
  single-series/full-config/numeric-string cases); `AgentPipelineTests` `MergeInlineCharts_*`
  (duplicate echo suppression, distinct chart preservation, mixed, inline-dupe collapse,
  same-title-different-data) and `ChartSpecSemanticComparer_*` (content equality + the record-
  reference-equality guard).

**Guardrail reminder:** `dotnet format`'s IDE0046 fixer collapsed the comparer's `Equals` guards
into one expression and then IDE0048 demanded parentheses on the mixed `||`/`&&`. Resolved by
writing `Equals` as an explicit parenthesized expression body. Private const followed the repo
`_camelCase` field convention (`_valueEpsilon`) to satisfy IDE1006.

**Validation:** backend `dotnet test` 2046 passed (0 failed); frontend `vitest` 309 passed;
`npm run build`, `eslint`, and `dotnet format --verify-no-changes` all clean.

### 2026-08-11T06:00:00Z: MERGED — APIM (#51 / PR #52 → `61323c7`) and Chart Matrix (#50 / PR #53 → `8ed3561`)

**By:** Kroger

**What:**

- **APIM AI Gateway (#51 / PR #52)** — MERGED to `main` as squash commit `61323c7`; issue #51
  CLOSED. HEAD before merge: `3c39ae4`. Publix's initial live-acceptance REJECT on §5
  (dead token-metrics sink — App Insights logger's instrumentation-key NamedValue missing)
  and §7 (deployed API on direct AOAI + placeholder `k8se/quickstart` image) was resolved
  entirely inside the provisioning path: `apim.bicep` switches `appinsights-logger` to a
  secretless `credentials.connectionString`; `apim-openai-api.bicep` adds `metrics: true`
  on the API-level `applicationinsights` diagnostic; `container-apps.bicep` was rewritten
  to declare the `apim-sub-key` ACA secret (from Bicep-time `listSecrets()` on the APIM
  subscription) and wire `OpenAI__Endpoint` → APIM inference URL,
  `OpenAI__ApimSubscriptionKey` → `secretRef:apim-sub-key`, `OpenAI__UseManagedIdentity=false`,
  Entra config, `Security__RequireAuth=true` declaratively — so a re-provision cannot regress
  the AI Gateway wiring. `azd-hooks/postprovision.{ps1,sh}` lose 102/75 lines of hand-stitched
  `az containerapp update` / APIM-key fetching. Publix re-verification against `9fdc2ab`:
  all 7 sections PASS, with live evidence of populated `Total Tokens` / `Prompt Tokens` /
  `Completion Tokens` rows in App Insights `AppMetrics` (dimensions:
  `API ID` / `Operation ID` / `Subscription ID` / `Region` / `Service ID` / `Service Type`),
  and the API container app on the real image (`--azd-1786425305`, healthy, replicas=1)
  serving `/healthz` behind Entra JWT.

- **Chart Matrix (#50 / PR #53)** — MERGED to `main` as squash commit `8ed3561`; issue #50
  CLOSED. HEAD before merge: `755f2ec`. All ten merge-readiness gates from the 2026-08-10
  verdict are satisfied: canonical `ChartAcceptanceManifest` on both surfaces (single-sourced
  from `src/RetailPulse.Web/src/constants/prompts.ts` via a cross-language contract test);
  9-prompt backend acceptance suite (`ChartAcceptanceMatrixTests`) that runs each prompt
  through the production compactors + `DeterministicChartBuilder` +
  `ChartSpecValidator.TryGetRenderable(minSeries, minMarks)`; performance-budget suite
  (`ChartAcceptancePerformanceTests`) enforcing the #50 numerical ceilings (<25,000
  estimated tool-context tokens/prompt, ≤5 distinct tool calls/prompt); frontend
  real-Recharts render suite (`chartAcceptance.matrix.test.tsx`); `ChartSpecValidator`
  strengthened with `TryGetRenderable(minSeries, minMarks)` and `MinimumMarksForType`;
  `ChartRequestDetector` extended with `VizVerbTableWithDataCueRegex` to catch
  "Create a table showing …" without misclassifying "book a table for two"; explicit
  fail-fast CI steps for both matrices; `docs/chart-acceptance.md` matrix reference +
  `docs/chart-acceptance-run.md` browser sign-off log + `scripts/browser-chart-acceptance.js`
  DevTools runner. Publix's independent live browser sweep (Playwright headless against
  a local stack routed through the live APIM gateway) reported 9/9 curated prompts PASS
  with per-prompt marks, entity checks, and screenshots — no `[role="note"]` diagnostic,
  no prose-only responses, correct chart type per prompt.

**Why:** Both PRs met the demo-integrity, security/auth, and architecture bars set by
issue #51 (generic + secret-safe APIM AI Gateway with MI backend, token cap +
`Retry-After`, token metrics + LLM diagnostics visible in App Insights, deployed app
routes through APIM) and issue #50 (systemic, contract-tested acceptance gate across
every curated chart prompt; missing values dropped, not coerced; performance ceilings
enforced; CI gate). CI was fully green on both HEADs, and both received explicit
independent Publix APPROVE — Kroger's charter requires the acceptance authority to be
someone other than the author, satisfied for both PRs.

**Team impact:**
- **Costco (Backend):** APIM AI Gateway is now the deployment default. All future
  container-app/env changes go through `container-apps.bicep`, not `az containerapp
  update` in postprovision. `apim.bicep`'s `appinsights-logger` uses
  `credentials.connectionString`, not a NamedValue — do NOT re-introduce
  instrumentation-key NamedValues. Publix flagged a non-blocking follow-up:
  `ApiManagementGatewayLlmLog` capture reliability drops ~30% under low-token load with
  `metrics: true`. Open a follow-up issue for a deterministic LLM-capture smoke and
  profile the diagnostic sampling.
- **Chick (Frontend):** `PROMPT_CATEGORIES` is the single source of truth for curated
  prompts. Adding a new curated chart prompt requires an entry in `ChartAcceptanceManifest`
  and `chartAcceptance.ts` — the contract test will fail CI otherwise. Do not duplicate
  prompt text in manifests.
- **Publix (QA):** New live-run evidence pattern established: `.squad/evidence/{agent}-{issue}-{date}/`
  for artifacts. Fold PR #53's `chart-acceptance-run.json` into `docs/chart-acceptance-run.md`
  in a follow-up commit.
- **Kroger (Lead):** APIM AI Gateway architecture landed and validated end-to-end.
  Chart-acceptance gate closes the regression class that #32/#48 kept re-opening —
  future single-prompt fixes must extend the manifest, not patch in isolation.
- **Ralph (Monitor):** Both #50 and #51 closed; no open squad-labeled issues from this
  batch. Continue to watch for the LLM-capture follow-up issue **#54**
  (https://github.com/swigerb/retail-pulse/issues/54) — filed by Kroger post-merge to capture
  both non-blocking observations Publix noted: (a) `ApiManagementGatewayLlmLog` capture
  reliability drops ~30% under low-token load with `metrics: true`, and (b) the browser
  chart-acceptance runner (`scripts/browser-chart-acceptance.js`) uses Griffel-hashed
  substring selectors that don't match at runtime plus a case-7 scrape-before-render race.
  Suggested owners: (a) Costco (APIM smoke), (b) Chick (`data-testid` hooks + runner script).
- **Scribe:** Publix's chart-acceptance-run JSON at
  `.squad/evidence/publix-apim-2026-08-11/chart-acceptance-run.json` should be folded into
  `docs/chart-acceptance-run.md`.

### 2026-08-26T14:07-04:00: Merge gate — never merge on CI alone; verdicts live on the PR

**By:** Kroger (Lead)

**Rule (operational, binds every PR including this one):**

1. **Never merge on CI status alone.** Confirm an explicit **APPROVE** verdict for the **current head SHA** before merging. Green checks mean the code compiled and the tests that ran passed — nothing more.
2. **A REJECT verdict blocks merge**, however green the checks are. Merge only after the reviewer posts a fresh APPROVE against the current head.
3. **Verdicts must be posted as PR comments** (APPROVE and REJECT alike). A verdict that exists only in an agent session transcript did not happen for gate purposes.
4. **Read the diff before merging** any change that claims to fix a defect. Confirm the change actually does what the PR says.
5. **Re-measure stability claims on the merge target** (post-merge `main`, or the PR's mergeable head with the target merged in), not on the working branch under different load. Report **raw per-run output**. If any run fails during a stability sequence — target or not — **name the failing test** rather than reporting a clean sweep.

**Why (both incidents, preserved so the reasoning survives the rule):**

- **Incident 1 — REJECT was ignored and merged.** A PR was merged while its reviewer verdict was REJECT. The merge gate checked `state=OPEN`, `mergeable=MERGEABLE`, and `0 failing checks` — but never looked at the verdict. Three reviewer blockers reached `main`, including a P0 cross-session data leak where plan completion broadcast `subject`, `reply`, and `charts` to `Clients.All`.
- **Incident 2 — stability claims failed independent re-measurement, twice.** PR #155 reported "15 consecutive full-suite passes, 0 failed"; independent measurement on merged `main` showed that exact test still failing. A separate PR reported a clean 10-run sweep that also did not reproduce.

**Root cause of Incident 1 — the single-identity constraint (call it out, it is the whole reason the gate misses):**

Author and reviewer share the same GitHub identity, **`swigerb`**. GitHub blocks formal self-approval, so a reviewer running under `swigerb` **cannot** submit a Files-changed → Approve review on a PR authored by `swigerb`. The squad therefore records verdicts as **PR comments**. Any merge gate that only reads formal GitHub review state (`APPROVED` / `CHANGES_REQUESTED`) will see zero verdicts on every PR and will let REJECTs through. The gate must read the **verdict comment for the current head**, not the formal review state.

**Team impact:**
- **All authors:** Do not merge your own PR on CI status alone. Wait for the reviewer's APPROVE comment against the current head SHA. If you push a new commit after APPROVE, the prior approval is stale — request a fresh one.
- **All reviewers:** Post the verdict — APPROVE or REJECT — as a **PR comment**, and name the head SHA it applies to. Do not leave the verdict in your session transcript. Re-measure stability claims on the merge target with raw per-run output; if any run fails, name the test.
- **Coordinator / gate tooling:** Treat a REJECT comment as a hard merge block regardless of CI. Treat an APPROVE comment as stale once a new commit is pushed. Never infer a verdict from CI or from formal review state alone.
- **This PR:** Docs-only, but the rule it records applies to itself — it merges only after an APPROVE comment against its current head.

**Lockout status:** Neither PR entered a second rejection cycle. No lockout was triggered.
Costco used their one revision on PR #52 and delivered a clean Bicep-first fix; Chick
delivered PR #53 first-pass with green CI and 9/9 Publix live browser PASS.

**Discipline notes:** Unrelated worktrees preserved (branch deletions on merge skipped for
both because worktrees `C:/src/worktrees/retail-pulse-apim-gateway` and
`C:/src/worktrees/retail-pulse-chart-matrix` still have those branches checked out — those
worktrees can be pruned separately if desired). No product-file modifications by Kroger
during either review; only `.squad/agents/kroger/history.md`, `.squad/decisions/inbox/`,
and the two PR bodies were touched. Used `gh pr comment` + `gh pr edit --body` +
`gh pr ready` + `gh pr merge --squash` — no direct `gh pr review --approve` (prior-history
EMU/self-review issue on this repo; the merge itself is the final approval).

### 2026-08-11T08:20:00-04:00: P0 incident — production AI failing fast after PR #52/#53 (APIM double-`/openai` segment)

**By:** Kroger (Lead, acting Incident Commander)

**Issue:** #55 (incident) — durable fix PR #56. References PR #52 (61323c7), PR #53 (8ed3561), issues #51/#50.

**Symptom:** Immediately after PR #52 (APIM AI Gateway) and PR #53 (chart acceptance matrix) merged to `main`, production (`https://calm-wave-04edb640f.7.azurestaticapps.net/`) failed every AI prompt with "Something went wrong while contacting the AI service." Telemetry: 0 tokens, 0 spans, 0 tool calls, ~299ms — failure before agent execution started.

**Diagnosis (evidence-based, read-only Azure CLI + App Insights KQL):**
- `az containerapp show ca-retailpulse-api` confirmed the deployed revision (`ca-retailpulse-api--azd-1786425305`) was current/healthy, correct image, correct secrets (`apim-sub-key` present) — **not** a stale-deployment or missing-secretRef problem as originally suspected.
- Application Insights `exceptions` table (App Insights `appi-5aldk7aotqods`) showed repeated `OperationNotFound` / "Unable to match incoming request to an operation." from APIM.
- `requests` table showed the actual outbound URL: `https://apim-5aldk7aotqods.azure-api.net/inference/openai/openai/deployments/gpt-5.4-mini-2026-03-17/chat/completions` — a **doubled `/openai` path segment**.
- Root cause: `infra/modules/apim-openai-api.bicep` emitted `inferenceEndpoint` as `${gatewayUrl}/${api.properties.path}`, where `api.properties.path = 'inference/openai'` (the APIM API's registered path includes a trailing `/openai` so its OpenAPI spec import matches AOAI's real route shape). The deployed API's `Azure.AI.OpenAI` `AzureOpenAIClient`, however, independently appends `/openai/deployments/{id}/chat/completions` to whatever endpoint it's given. PR #52's live acceptance testing exercised APIM directly with a manually-correct URL and never exercised the deployed API's actual constructed endpoint end-to-end, so this combination was never caught pre-merge.
- Verified live directly against APIM: broken path → `404`; corrected path (`.../inference/openai/deployments/...`) → `200` with a real chat completion.

**Decision — mitigation (applied directly by IC, live prod, documented per delegated authority since restoring service was time-critical):**
```
az containerapp update -n ca-retailpulse-api -g rg-retailpulse-demo-eus-001 \
  --set-env-vars "OpenAI__Endpoint=https://apim-5aldk7aotqods.azure-api.net/inference"
```
This is the smallest reversible action available: a single env-var correction on the existing healthy ACA revision, no code rollback, no bypass of APIM, no image change. New revision `ca-retailpulse-api--0000018` came up Healthy at 100% traffic within ~30s (ACA `Single` revision mode). Old revision `ca-retailpulse-api--azd-1786425305` retained at 0% traffic as an instant rollback target if needed.

**Why not an emergency code rollback:** The defect was a one-line Bicep output expression, not a systemic APIM/auth/RBAC failure. APIM itself, the managed identity, RBAC, and the AOAI backend were all functioning correctly — only the endpoint string handed to the API was wrong. A full rollback of PR #52 would have discarded the (working, tested) APIM gateway, MI auth, and Entra hardening for no benefit, and would not by itself have fixed the underlying Bicep bug for the next `azd provision`.

**Durable fix:** PR #56 (branch `squad/54-fix-apim-inference-endpoint-double-openai-segment`) changes `apim-openai-api.bicep`'s `inferenceEndpoint` output to derive from the base `inferenceApiPath` param instead of `api.properties.path`, and adds regression test `DeploymentContractTests.ApimOpenAiApiBicep_InferenceEndpointOutputDoesNotDoubleAppendOpenAiSegment`. `dotnet test --filter DeploymentContractTests` 53/53 passed; `dotnet format --verify-no-changes` clean.

**Team impact:**
- **Costco (Backend):** Live-acceptance test plans for anything wrapping AI Gateway/AOAI endpoints must exercise the *deployed API's actual constructed request*, not just a manually-assembled equivalent URL against APIM — the SDK's own path-construction behavior is part of the contract.
- **Publix (QA):** Must execute a full live acceptance run against production with PR #56 applied and attach PASS/FAIL evidence to the PR before merge; incident issue #55 stays open until that evidence lands and Kroger signs off.
- **Ralph:** Overnight monitoring should watch App Insights `exceptions`/`requests` for `OperationNotFound` specifically as a fast, cheap signal for APIM path-contract regressions.

**Status at time of writing:** Mitigation live and verified (200 OK path confirmed against APIM). Durable fix PR #56 open, CI running. Incident issue #55 remains open pending Publix's full live acceptance evidence and Kroger's final merge approval.

**Final closeout (2026-08-11, same incident window):**
- Publix Phase 2: CONDITIONAL GO. Auth plumbing (OAuth redirect, PKCE, scopes, Entra-only fail-closed) verified end-to-end via `Verify-ProductionAuth.ps1` + Playwright. Telemetry independently confirmed a genuine post-fix 200 OK chat completion (12:13:12 UTC) routed through APIM with the corrected single-`/openai` path. Publix could **not** personally submit an authenticated chat request or run the curated grouped-bar-chart prompt end-to-end, because completing interactive Entra/MSAL sign-in requires a human present in this sandbox — a genuine environmental/tooling limitation, not a product defect. Per policy we do not bypass or impersonate interactive auth.
- Kroger (IC) independently re-verified before merge: PR #56 CI fully green (Build & Test .NET, Frontend, Lint, Security, Auth Provider Matrix, test), PR clean/mergeable, and App Insights telemetry showing real 200 OK completions at 12:10:47 and 12:13:12 UTC with no further doubled-path 404s.
- **Decision: approved and merged PR #56** (squash merge, `Closes #55`) — evidence bar (root cause fully identified + fixed at code/config level, live mitigation proven, CI green including security/auth-matrix, telemetry proving the exact failure mode is gone and replaced by real 200s) was judged sufficient despite the interactive-auth verification gap, since that gap is an unresolvable sandbox limitation rather than a signal of residual product risk.
- Opened follow-up issue #57 (service-principal-based synthetic monitor for authenticated AI chat path, `priority:p2`/`enhancement`) so future incidents aren't blocked by the same interactive-auth-requires-a-human constraint.
- Posted full closeout comment and confirmed issue #55 closed (auto-closed by the PR #56 merge's `Closes #55` reference; closeout narrative added as a follow-up comment since the auto-close event preceded it).

**Incident status: CLOSED. Production confirmed healthy and serving real AI chat completions through the corrected APIM path.**

### 2026-08-11: Production hardening + Prompt-ideas acceptance — umbrella coordination

**By:** Kroger (Lead)

**Context:** Post-merge of PR #56 (APIM double-`/openai` fix), the demo stack is functionally green but two production-quality invariants are not yet enforced end-to-end:

1. **APIM is always the AI inference plane.** After PR #52 made it first-class in `azd`, no code path should bypass it or fall back to direct AOAI. There is no `useApim=false` toggle today (verified in `infra/main.bicep`), but that must remain true for every future change — no reintroduction of an "APIM optional" branch.
2. **Every "Prompt ideas" popover entry must have a live acceptance contract.** `ChartAcceptanceManifest.Cases` currently covers all eight Charts-category prompts plus the two-brand comparison. The other **fifteen** non-chart prompts across General / Grocery / QSR / Home Improvement / Office Supply / Furniture in `src/RetailPulse.Web/src/constants/prompts.ts` have no equivalent contract test. They render text/tables through tools that already exist, but nothing today asserts that every prompt returns a routed, tool-backed, non-empty response.

Additionally, the "Show a horizontal bar chart ranking all brands by depletion growth rate" prompt is a **product bug** whenever it does not render a real horizontalBar with ≥ 6 brands ordered by `depletions_yoy` — `DeterministicChartBuilder.TryBuildGrowthRanking` exists to guarantee this and any regression is P0.

**Acceptance gates (non-negotiable):**
- **G1 — APIM never optional.** No PR may add a config switch, feature flag, appsetting, environment variable, or code path that lets the API talk to AOAI without going through APIM. Guardrail: `DeploymentContractTests` must keep asserting the API's inference endpoint resolves to the APIM gateway.
- **G2 — Every actually-featured Prompt-ideas entry has a contract.** A single manifest (extension of `ChartAcceptanceManifest` or a new `PromptIdeaAcceptanceManifest`) must enumerate every string in `PROMPT_CATEGORIES` and, per entry, assert (a) it routes to a specialist not the council, (b) at least one data-fetching tool is invoked, (c) the response is non-empty and free of leaked chart JSON. A contract test must fail CI if `prompts.ts` gains a new entry with no manifest row.
- **G3 — Horizontal-bar depletion-growth ranking is a chart, not prose.** The chart-acceptance runner must render a `horizontalBar` with ≥ 6 finite marks for that prompt; any regression to a text refusal or empty spec is treated as a P0 product bug.
- **G4 — Production sweep before we call this done.** A live end-to-end sweep against the deployed stack (APIM → Container Apps → AOAI, all Prompt-ideas entries, all Charts entries) must pass, gated by the service-principal synthetic monitor from #57 once available; until then, Publix runs it manually.

**Ownership decomposition (do not overlap):**
- **Costco (Backend):** G1 guardrails, PromptIdeaAcceptanceManifest backend + contract tests, keep the existing chart-acceptance matrix green.
- **Chick (Frontend):** Frontend mirror of the prompt-idea manifest so `prompts.ts` cannot drift, plus the Griffel/data-testid selector work already scoped in #54.
- **Publix (QA):** Production sweep script covering all Prompt-ideas + Charts entries, execution report attached to the umbrella issue.
- **Kroger (Lead):** This umbrella, the decision log, incident-quality review + merge gate on all three PRs.

**Team impact:**
- No implementation is being merged under Kroger's name in this coordination pass; three scoped issues will be filed and assigned per the decomposition above.
- Costco/Chick/Publix must open small, single-concern PRs and are not permitted to bundle work across the ownership lines above.
- Kroger reserves the right to reject any PR that (a) reintroduces APIM optionality, (b) adds a Prompt-ideas entry with no manifest row, or (c) lands the horizontal-bar ranking as text.
- On rejection, Kroger will name a different revision owner (never the original author on the same defect) per the reviewer-protocol skill.

### 2026-08-11T15:20:00Z: Chick — Prompt-ideas frontend acceptance contract (Issue #58, PR #65)

**By:** Chick (Frontend)

**What:** Extended the chart-only manifest (issue #50 / PR #53) into a canonical acceptance contract covering every one of the **26 curated Prompt Ideas**.

- New frontend module `src/RetailPulse.Web/src/components/promptAcceptance.ts` is derived from the single source (`constants/prompts.ts`) — prompt text is never duplicated. 9 chart cases inherit from `CHART_ACCEPTANCE_CASES`; 17 prose cases declare expected entities, minimum mentions, and the same ≤5 tool-call / <25K token ceilings enforced on the backend.
- Bidirectional drift is a hard CI gate: every featured prompt has an acceptance case AND every acceptance case is still featured. README chart bullets, frontend chart manifest, and prompt manifest all cross-mirror.
- Chart render acceptance now keys off stable `data-testid` selectors on `ChartRenderer` (`chart-card`, `chart-title`, `chart-gauge`, `chart-table`, `chart-unavailable`) and a `data-chart-type` attribute — never Griffel classes. Same for `PromptLibrary` (`prompt-library-trigger`, `-panel`, `-item`, per-category chip testids).
- Production-style browser test (`PromptLibrary.browser.test.tsx`) polls the popover UX for every prompt via `data-testid` anchors and would translate cleanly to Playwright / Cypress if wired up later.

**Why:** Kroger's 2026-08-10 review rejected #50 for scope completeness. #50 shipped the chart half (PR #53); this PR extends the same acceptance-contract shape to the remaining 17 prose prompts so no featured prompt is un-contracted.

**Team impact:**
- **Costco (Backend):** No backend changes. Existing `ChartAcceptancePerformanceTests` still owns chart-prompt ceilings; prose prompts do not go through deterministic fulfillment.
- **Publix (QA):** New test files add 4 files / ~35 cases to the frontend suite. `data-testid` anchors are the sanctioned selector strategy going forward — prefer them over Fluent class substrings in new tests.
- **Kroger (Lead):** PR #65 (26-prompt production acceptance sweep) is **HELD from merge** pending #67 (P0 APIM hardening gate incident) live verification — must not merge until 24/24 live invariants pass per Brian's directive.

**Status note (2026-08-11, P0 incident #67):** PR #65's live production sweep and Publix's independent verification are explicitly on hold until Costco's #67 remediation PR passes `Verify-ApimAiGateway.ps1` at 24/24. Not an omission — a deliberate sequencing decision to avoid validating against a known-broken gateway.
