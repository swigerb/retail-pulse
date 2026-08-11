# Authenticated synthetic monitor for the AI chat path

Status: **DESIGN — implementation blocked pending tenant credentials**  
Owner: Costco (Backend)  
Tracks: [#57](https://github.com/swigerb/retail-pulse/issues/57)

## Why

During the P0 incident on 2026-08-11 (#55, fixed by PR #56) QA (Publix) could
not personally submit an authenticated chat request or run the curated
grouped-bar acceptance prompt against production end-to-end, because the sandbox
has no human present to complete interactive Entra/MSAL sign-in. Per policy we
do not bypass or impersonate interactive auth to work around this.

The gap did not hide a defect — telemetry and `Verify-ProductionAuth.ps1`
independently confirmed real 200 OK completions routed through the corrected
APIM path — but it meant an on-call responder cannot get direct live evidence
of end-to-end chat behavior without a human at a browser. This document
specifies the non-interactive verification path so a future incident is not
gated on that constraint.

## Contract this monitor validates

For each curated prompt in the smoke set (below), the monitor must prove:

1. A **valid Entra bearer token** was obtained non-interactively (no human).
2. `POST {AZURE_API_APP_URL}/api/chat` returned **200** with a body carrying:
   - an assistant `text` payload,
   - one or more `charts` entries when the prompt expects a chart,
   - a non-empty `traceId` / `sessionId`, so the response can be correlated
     back to Application Insights `requests` + `dependencies`.
3. The App Insights `requests` table records a corresponding `POST /api/chat`
   with `success=true` **and** a dependent AOAI request routed through APIM
   (`name` matching `POST /inference/openai/deployments/*/chat/completions`).
4. The APIM `customMetrics` for the same time window include at least one
   `azure-openai-emit-token-metric` row (proves the token-metric channel is
   still live — the exact indicator Publix flagged as flaky under low-token
   load in the MERGED APIM decision).

Curated smoke set (start narrow, grow with confidence):

| Prompt | Expected chart | Manifest case |
|---|---|---|
| `Show a horizontal bar chart ranking all brands by depletion growth rate` | `horizontalBar`, ≥6 marks | `ChartAcceptanceManifest.Cases[5]` |
| `Show a grouped bar chart comparing FreshMart and Harvest Table across all regions` | `groupedBar`, ≥12 marks | `ChartAcceptanceManifest.Cases[3]` |

Both draw from complete seeded tenant data and exercise the two chart
data-source families most likely to regress independently
(`PortfolioDepletion` and `HistoricalDemand`).

## Implementation shape (once credentials exist)

A single-file PowerShell script `scripts/Invoke-SyntheticChatMonitor.ps1`
(mirrors the shape of the existing `Verify-*Auth.ps1` scripts):

1. Read `AZURE_API_APP_URL`, `MicrosoftEntra__TenantId`, `MicrosoftEntra__ClientId`,
   `MicrosoftEntra__ApiScope` from the current azd env, plus:
   - `RETAIL_PULSE_SYNTHETIC_CLIENT_ID` — a *separate*, dedicated confidential
     application registration in the same tenant with:
       - the RetailPulse API app-role `RetailPulse.User` granted, and
       - the `access_as_user` delegated scope granted with admin consent,
       - a client-credentials flow enabled.
   - `RETAIL_PULSE_SYNTHETIC_CLIENT_SECRET` — pulled from Azure Key Vault
     `kv-<resourceToken>` at run time via `az keyvault secret show`, never
     stored in the repo, never in `.env`, never in azd env.
2. Acquire a token via `az rest` against
   `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token` with the
   `client_credentials` grant and `scope = api://{RetailPulse client-id}/.default`.
3. `POST /api/chat` for each prompt with `Authorization: Bearer <token>`.
4. Assert response shape (200, `charts[].type`, chart mark count) inline;
   emit `Write-Host` per prompt and a final PASS/FAIL summary.
5. Optionally query Application Insights via `az monitor app-insights query`
   for the correlated `requests` and `customMetrics` rows (skipped when the
   caller lacks App Insights Reader — same opportunistic pattern as
   `Verify-ApimAiGateway.ps1`).

Wire it into `.github/workflows/squad-heartbeat.yml` (or a new dedicated
workflow) as a scheduled run and as a `workflow_dispatch` for on-call use.

## Blocker — why this cannot land in this PR

The tenant behind `apim-5aldk7aotqods.azure-api.net` /
`ca-retailpulse-api.*.azurecontainerapps.io` is a **governed customer tenant**
that:

- Requires an administrator to create the confidential application
  registration described above and to admin-consent the `RetailPulse.User`
  app-role + `access_as_user` scope grant. Neither an autonomous Squad
  session nor Costco has interactive administrative access to the tenant
  from this environment.
- Applies conditional-access + client-credentials policy restrictions that
  will refuse a token from any client secret not already issued and
  documented by the tenant admin. This cannot be worked around by minting a
  secret locally.
- Governance policy forbids checking a client secret into the repository or
  into any azd-committed environment; the secret must land in the deployment
  Key Vault and be pulled at run time. The deployment Key Vault does not
  exist in the current `infra/` — `main.bicep` provisions Log Analytics,
  App Insights, ACR, APIM, ACA, SWA, but no Key Vault module. Adding one
  requires a Kroger-level architecture review (the storage-governance work
  from the storage-hotfix worktree explicitly deferred every durable
  policy-scoped resource until a policy-compatible design is signed off).

Together those three constraints mean the client-credentials plumbing this
issue asks for cannot be exercised end-to-end from this session — no client
id, no secret, no vault to store the secret in, and no administrative path
to create any of them.

## What ships in this PR

- This design doc, so the contract and the blocker are captured in-repo
  next to the existing live-test plan.
- `scripts/Verify-ApimAiGateway.ps1` — the closest non-interactive live
  verification we can do today. It exercises the APIM AI Gateway invariants
  (resource + API + policy + backend + diagnostics + RBAC + ACA config)
  against the deployed environment using only read-only `az` calls, so an
  on-call responder can prove the gateway is intact even without a live
  authenticated chat request.

## Follow-through (unblocks #57 fully)

1. Tenant admin (or a delegated Kroger/human) creates the confidential
   application registration, grants scopes, and issues a client secret.
2. Add a Bicep Key Vault module (`infra/modules/key-vault.bicep`) with
   private endpoint + `enableSoftDelete: true` + purge protection, plus a
   `Microsoft.KeyVault/vaults/secrets` resource seeded from a
   `@secure()` param whose value is supplied via `azd env set` at
   provision time (not committed).
3. Grant the deployment identity `Key Vault Secrets User` on the vault, and
   the ACA API's system-assigned identity the same role.
4. Implement `Invoke-SyntheticChatMonitor.ps1` per §"Implementation shape".
5. Wire the scheduled workflow, verify one full run's telemetry, then close
   #57.
