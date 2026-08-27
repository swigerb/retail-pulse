# Kroger — History

## Recent Work (2026-08-11)

### 2026-08-11T12:00:00Z — Root-cause investigation: Issue #67 (APIM hardening "9/24 failures")

**Status:** ✅ Root cause found and documented. Awaiting Costco's remediation PR before final gate review.

**Issue:** #67 (P0) — `Verify-ApimAiGateway.ps1` reported 9/24 live-invariant failures on
`retailpulse-demo-eus-001` immediately after the recovery from the `azd up` deploy-race
incident (PR #66/#65).

**Work done (in worktree `retail-pulse-wt-67-apim-hardening`, branch `squad/67-apim-hardening-gate`):**
- Reviewed `infra/modules/apim-openai-api.bicep`, `apim-openai-policy.xml`, `main.bicep`,
  `azd-hooks/postprovision.ps1` — no conditional gating, no removed resources, no
  unpopulated parameters found across PR #66's diff (which touched zero bicep/policy
  files) or PR #52's original landing.
- Directly queried the live Azure environment (`rg-retailpulse-demo-eus-001`,
  `apim-5aldk7aotqods`) via `az rest` ARM REST calls, bypassing the `az apim` CLI
  command group: confirmed the `retail-pulse-foundry` backend, its MI credentials, the
  full API policy (backend-service/MI-auth/token-limit/emit-token-metric), and the
  `applicationinsights` diagnostic are ALL present and correct live, exactly matching
  the Bicep source.
- Reproduced the verifier's 9 failures directly and traced them to three `az apim`
  subcommands that don't exist in this environment's Azure CLI (2.81.0): `az apim api
  policy show`, `az apim backend show`, `az apim api diagnostic show`. Each is piped
  through `2>$null | ConvertFrom-Json` in the script, which silently swallows the "not
  recognized" CLI error and produces `$null`, causing every dependent assertion to
  report a false FAIL. This exactly accounts for all 9 reported failures.

**Conclusion:** Production APIM AI Gateway hardening from PR #66/#52 is fully intact
and correctly live. Issue #67 is a false alarm caused by a verifier-script defect
shipped in PR #66 (`scripts/Verify-ApimAiGateway.ps1`), not an IaC or deployment-process
regression. No rollback or re-provisioning needed.

**Artifacts:**
- Root-cause report: `docs/incidents/2026-08-11-apim-hardening-gap.md` (worktree)
- Decision log: `.squad/decisions/inbox/kroger-issue-67-apim-verifier-false-positive.md`

**Next:** Costco to fix the verifier script (replace broken `az apim` subcommands with
`az rest` ARM calls, following the pattern already used for the `azuremonitor`
diagnostic check in the same file; add a guard against silently-swallowed CLI errors).
I (Kroger) remain final reviewer/gate — will not approve until real chat completion is
confirmed, a corrected verifier reports genuine 24/24, the deploy-race fix is verified,
and CI is green. PR #64 stays held per Brian's directive.

### 2026-08-11T12:41:00Z — Final reviewer gate: PR #73 (Issue #67 remediation by Costco)

**Status:** ✅ APPROVED (architecture/fix quality) — merge held pending Publix's independent live/e2e verification.

**Reviewed:** `scripts/Verify-ApimAiGateway.ps1` rewrite (ARM REST via bearer token,
replacing the broken `az apim` subcommands I identified), the new mandatory
postprovision hard-fail gate, the new `CompiledArmDeploymentGraphTests.cs` (validates
compiled ARM JSON via `az bicep build`, not source text), and the new `predeploy`
hook fixing the `sourcelink.json` publish race.

**Independent verification performed (not just trusting Costco's report):**
- Re-ran the fixed verifier myself live against `apim-5aldk7aotqods` → 24/24 PASS.
- `dotnet build` clean; `dotnet test --filter FullyQualifiedName~Deployment` → 105/105
  passed, 0 skipped (confirms the compiled-ARM-graph test executed for real, using
  actual `az bicep build` output).
- Confirmed postprovision hooks: exit 2 skip path is unreachable from the invariant
  checks (only from env preconditions); every other non-zero exit hard-fails via
  throw/exit 1 — no exit-2 masking.
- Could not verify GitHub Actions "CI green" — no checks registered on this branch in
  this sandbox; noted as an open item, not treated as a blocker given clean local
  results matching Costco's claims.

**GitHub note:** `gh pr review --approve` was rejected (GitHub blocks self-approval —
the authenticated CLI identity is the PR's own author account in this environment).
Posted the full verdict as a PR comment instead:
https://github.com/swigerb/retail-pulse/pull/73#issuecomment-5256104438

**Gate held:** Did NOT merge. Per Brian's directive, merge requires Publix's
independent live verification (real APIM chat completion + ideally a fresh `azd up
--no-prompt` exercising predeploy+postprovision together, since Costco's sandbox
lacks `azd`). PR #64 remains held until #67 fully closes.

**Decision logged:** `.squad/decisions/inbox/kroger-pr73-approve-hold-merge.md`

### 2026-08-11T13:04:00Z — Final go/no-go: PR #73 MERGED, Issue #67 CLOSED

**Status:** ✅ Merged. Issue #67 closed.

After Publix found #73 conflicting (competing verifier fix #72 landed on main using a
broken `Invoke-WebRequest` approach that crashed live with the same BOM error it claimed
to fix), Costco rebased #73 onto `origin/main` (751feb2), kept the confirmed-correct
`Invoke-RestMethod` implementation, and merged in #72's useful `-SelfTest` CI fence.

**Verified myself before merging:** fetched the rebased branch directly and confirmed
`Invoke-RestMethod` (not `Invoke-WebRequest`) is what's actually present, plus the
`-SelfTest` switch; `gh pr view` showed `mergeable: MERGEABLE` / `mergeStateStatus: CLEAN`;
`gh pr checks 73` showed all 8 CI checks passing.

**Action:** Squash-merged via `gh pr merge 73 --squash` referencing `Closes #67`. Confirmed
merged (`mergedAt: 2026-08-11T17:04:41Z`) and issue #67 auto-closed.

**Remaining gap (explicitly not closed by this merge):** no sandboxed agent has real `azd`
CLI access, so nobody on the team has run a full fresh `azd up --no-prompt` end-to-end dry
run exercising the new predeploy+postprovision hooks together under real azd orchestration.
This is the final acceptance step and requires Brian or a CI runner with azd.

**PR #64 (26-prompt sweep) remains held** until that dry run is done.

**Decision logged:** `.squad/decisions/inbox/kroger-pr73-merged-issue67-closed.md`

## Recent Work (2026-08-10)

### 2026-08-10T14:30:00Z — Architecture Gate Review: APIM #51/PR #52 and Chart Matrix #50

**Status:** ✅ Complete — Verdicts issued; awaiting team action.

**Issues:** #51 (APIM AI Gateway / PR #52), #50 (Chart Matrix P0)

**Review Results:**
- **APIM #51/PR #52:** Architecture APPROVED (meets generic + secret-safe bar), but PR NOT ready to leave draft due to:
  1. `dotnet format` linting failure on OpenAiConnectionSettings.cs (release-blocking)
  2. All six live-acceptance boxes in PR body unchecked (Publix must execute full live test plan)
  
- **Chart Matrix #50:** REJECT for merge readiness. Direction sound but scope materially incomplete (no commits, no PR, validator not wired, no backend 9-prompt suite, no frontend Recharts suite, no trace assertions, no perf gates, no CI gate, no docs, no browser runner). Directed back to Chick to complete scope items 1–8.

**Constraints Honored:**
Architecture review only; no code modifications, no branch changes, read-only analysis.

**Next Actions:**
1. Costco must format + push to fix linting blocker
2. Publix owns live acceptance gate once CI passes
3. Chick picks up Chart Matrix scope completion per Kroger's direction

**Learnings:**
- Secret-safe architecture (subscription key never persisted, cross-RG RBAC isolated, system identity + LLM diagnostics) is necessary but not sufficient — live evidence required before merge.
- P0 acceptance contracts require systemic gates (9-prompt suite, trace assertions, perf tests, CI gate) wired end-to-end; shipping partial builders without acceptance infrastructure reopens the regression the contract is meant to prevent.

---

## Notification — 2026-05-16 Timeout Fix from Costco

🔔 **Pattern Established:** If a future agent genuinely needs multi-iteration tool calling and the 60s request timeout is insufficient, implement **endpoint-specific timeout override** rather than raising the global cap.

**Context:** Costco fixed 504 timeouts by setting global request timeout to 60s (both `/api/chat` and `/api/chat/stream`). This tight budget works for current single-iteration agents. Future complex agents (e.g., council convene orchestrator with nested tool calls) should request their own `/api/agent-name/execute` endpoint with a higher timeout rather than changing the global limit.

**See:** `.squad/decisions.md` — "Aggressive fast-fail timeouts for chat endpoints (2026-05-16)"

---

**Archive:** See kroger/history-archive.md for detailed May 14 session work, the 2026-06-29 board cleanup + PR #1 security review, and the 2026-08-05 secretless-ACR architecture gate (summarized 2026-08-11).

---

### 2026-08-11T04:20:00Z — Re-review after owner updates: APIM (#51 / PR #52) + Chart Matrix (#50)

**PR #52 (APIM AI Gateway):** Architecture APPROVED (prior verdict stands). CI blocker cleared — `dotnet format` now passing on `OpenAiConnectionSettings.cs`; all six CI checks GREEN on HEAD (Build & Test .NET, Frontend, Security, Auth Provider Matrix, Lint, Squad CI). 2,558 automated tests pass. **HOLD approval to leave draft** — six live-acceptance boxes still unchecked. Publix owns independent execution of `docs/testing/apim-ai-gateway-live-test-plan.md` before merge; Costco (author) cannot self-certify the live gate. No lockout applies (architecture not rejected).

**Issue #50 (Chart Matrix):** REJECT for merge readiness (unchanged from 2026-08-10). No PR, zero commits on `squad/50-all-chart-prompt-acceptance`, scope items 1–8 unfulfilled. Relabeled `squad:chick` per prior written direction. Lockout does not apply yet — first pass. Reminder in place: if a subsequent pass still ships without the acceptance gates, Publix + a fresh specialist owns the revision under lockout.

**Actions taken:**
- PR #52 status comment posted (architecture APPROVED, awaiting Publix evidence).
- Issue #51 status comment posted (routing Publix to the live acceptance plan).
- Issue #50 re-review comment posted, relabeled `squad:chick`.
- Decision written to `.squad/decisions/inbox/kroger-review-issue-51-issue-50-followup.md`.

**Discipline:** Did not attempt `gh pr review --approve` — architecture approval is not yet the final merge gate (Publix evidence is), and prior history shows self-review permission issues on this PR (author is repo owner). Used `gh pr comment` / `gh issue comment` instead. Preserved unrelated worktrees; touched only `.squad/decisions/inbox/` and `.squad/agents/kroger/history.md`.

---

### 2026-08-11T06:00:00Z — MERGED: PR #52 (APIM #51) and PR #53 (Chart Matrix #50)

**PR #52 → `61323c7`, issue #51 CLOSED.**
- First live-acceptance pass by Publix REJECTED with §5 (customMetrics empty — logger NamedValue missing) and §7 (Container App on direct AOAI + `k8se/quickstart` placeholder) as hard blockers. Sustained the verdict; called Costco back for one revision under warning-of-lockout.
- Costco's `9fdc2ab`/`3c39ae4` closed both root causes inside Bicep (not the postprovision hook): `apim.bicep` switched `appinsights-logger` to a secretless `credentials.connectionString`; `apim-openai-api.bicep` set `metrics: true` on the API-level applicationinsights diagnostic; `container-apps.bicep` was rewritten to declare the `apim-sub-key` ACA secret and wire the APIM env vars declaratively so a re-provision cannot regress §7 again.
- Publix re-verification: 7/7 sections PASS. Live `AppMetrics` shows Total/Prompt/Completion Tokens with all five dimensions populated; the API Container App is on the real image (`--azd-1786425305`) with `OpenAI__Endpoint` on the APIM inference URL and `Security__RequireAuth=true` gated by Entra.
- Final architecture re-review APPROVED — diff is tight, no unrelated changes, IaC is the single source of truth for the AI Gateway path.

**PR #53 → `8ed3561`, issue #50 CLOSED.**
- Chick's first-pass PR met all ten merge-readiness gates from the 2026-08-10 verdict:
  cross-language `ChartAcceptanceManifest` from the single prompt source; backend
  `ChartAcceptanceMatrixTests` + `ChartAcceptancePerformanceTests` (<25K tokens, ≤5 tool calls);
  frontend `chartAcceptance.matrix.test.tsx` on real Recharts; strengthened
  `ChartSpecValidator.TryGetRenderable(minSeries, minMarks)` + `MinimumMarksForType`;
  new `VizVerbTableWithDataCueRegex` in `ChartRequestDetector` for prompt #7; explicit
  fail-fast CI steps; matrix docs + browser sign-off log + DevTools runner.
- Publix's live browser sweep (Playwright headless against a local stack routed through the live APIM gateway) reported 9/9 curated prompts PASS with per-prompt marks + entity checks + screenshots. No `[role="note"]` diagnostic on any prompt.
- Final architecture / scope review APPROVED — single source of truth preserved, semantic acceptance enforced end-to-end, no council-routing regressions, missing values never coerced.

**Follow-up (non-blocking, tracked):** Publix noted `ApiManagementGatewayLlmLog` capture reliability drops ~30% under low-token load with `metrics: true` on. Not a demo-integrity blocker (the plan's pass criterion is met by marker-tagged calls consistently emitting populated response records). Open a follow-up issue for a deterministic LLM-capture smoke and profile the diagnostic sampling.

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