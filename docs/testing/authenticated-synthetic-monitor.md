# Authenticated synthetic monitor for the AI chat path

Status: **DELIVERED — optional, credential-gated, federation-only**
Owner: Kroger (Lead) — implemented per issue [#57](https://github.com/swigerb/retail-pulse/issues/57)
Original design owner: Costco (Backend)
Superseded assumption: an earlier revision of this document specified a
confidential app registration + client-secret + Key Vault storage
pattern. That plan is REPLACED, in full, by workload-identity federation
— see "Authentication" below. No client secret exists, and no client
secret is required.

## Why

During the P0 incident on 2026-08-11 (#55, fixed by PR #56) QA (Publix)
could not personally submit an authenticated chat request or run the
curated grouped-bar acceptance prompt against production end-to-end,
because the sandbox has no human present to complete interactive
Entra/MSAL sign-in. Per policy we do not bypass or impersonate
interactive auth to work around this.

The gap did not hide a defect — telemetry and `Verify-ProductionAuth.ps1`
independently confirmed real 200 OK completions routed through the
corrected APIM path — but it meant an on-call responder cannot get direct
live evidence of end-to-end chat behavior without a human at a browser.
This monitor closes that gap non-interactively, without introducing any
credential to the repo or to GitHub Actions secrets.

## Optionality contract (matches Azure AI Search, Foundry IQ, Content Safety)

The monitor is a **fully OPTIONAL add-on**. It ships wired into the repo
and works the moment the deployment opts in. When the deployment has not
opted in:

- The scheduled + `workflow_dispatch` workflow reports a clean
  `::notice::` and completes green.
- `scripts/Invoke-SyntheticChatMonitor.ps1` prints an explicit `SKIP:`
  line that names exactly what is missing, and exits `0` — it never
  turns CI red for an unconfigured fork or clean-clone user, and it
  never fabricates a live result.
- The manual interactive alternative (a human running
  `scripts/browser-chart-acceptance.js` /
  `scripts/browser-prompt-library-acceptance.js` with an interactive
  Entra sign-in) stays fully supported for on-call responders who do
  have a browser to hand.

## Authentication — workload-identity federation ONLY

The monitor authenticates via an Entra federated-credential exchange —
there is NO client secret to store, rotate, leak, or grant to a
workflow. This mirrors the principle already applied across the
platform: Azure AI Search, Foundry IQ, and Content Safety all
authenticate with managed identity and no keys; a client secret here
would have been the only credential in the system, and the weakest link.

### Identity (non-secret configuration — safe to commit)

| | |
|---|---|
| App registration | `retail-pulse-synthetic-monitor` |
| Tenant | MCAP sandbox (`MngEnvMCAP617906.onmicrosoft.com` / "Contoso") |
| Application (client) ID | `b8212317-e16d-4f06-996b-955e885ca1ca` |
| Directory (tenant) ID | `48351615-345c-4547-bb6f-8fcc8d6e2568` |
| Sign-in audience | Single tenant (`AzureADMyOrg`) |
| Service principal | Created |

### Federated credential

| | |
|---|---|
| Name | `github-actions-main` |
| Issuer | `https://token.actions.githubusercontent.com` |
| Subject | `repo:swigerb@1630580/retail-pulse@1223914087:ref:refs/heads/main` |
| Audience | `api://AzureADTokenExchange` |

The subject uses the **new immutable numeric-id format** (organisation
`1630580`, repository `1223914087`) which Entra recommends because it
survives a rename and cannot be hijacked by re-registering a deleted
name. The credential is currently scoped to `main` only; a second
federated credential is needed for any other branch or environment.

### How the workflow uses it

`.github/workflows/synthetic-monitor.yml` runs `azure/login@v2` with the
non-secret `client-id` and `tenant-id` inputs and the top-level
`permissions: id-token: write`. GitHub Actions' OIDC token is exchanged
directly for an Entra token — nothing sensitive goes into GitHub Actions
secrets, into the repo, or into any log. The `client-secret` input is
NEVER passed.

Locally, on-call responders run the same script under their own
`az login` / `DefaultAzureCredential` context; no credential is ever
stored.

## Contract this monitor validates

For each curated prompt in the smoke set, the monitor proves:

1. A **valid Entra bearer token** was obtained non-interactively (no
   human, no secret) via
   `az account get-access-token --resource <ApiResource>`.
2. `POST {ApiOrigin}/api/chat` returned **200** with a body carrying:
   - an assistant `text` payload,
   - one or more `charts[]` entries with the expected chart type,
   - the expected minimum mark count per the acceptance manifest,
   - a non-empty `traceId` / `sessionId` / `correlationId`, so the
     response can be correlated back to Application Insights `requests`
     + `dependencies`.

The token itself is never printed or logged; the correlation id is
redacted to its last 8 characters in the run output.

Curated smoke set (matches
`src/RetailPulse.Contracts/Charts/ChartAcceptanceManifest.cs` — the
offline self-test asserts this synchronisation):

| Prompt | Expected chart | Manifest case |
|---|---|---|
| `Show a horizontal bar chart ranking all brands by depletion growth rate` | `horizontalBar`, ≥6 marks | `ChartAcceptanceManifest.Cases[5]` |
| `Show a grouped bar chart comparing FreshMart and Harvest Table across all regions` | `groupedBar`, ≥12 marks | `ChartAcceptanceManifest.Cases[3]` |

Both draw from complete seeded tenant data and exercise the two chart
data-source families most likely to regress independently
(`PortfolioDepletion` and `HistoricalDemand`).

## Implementation

`scripts/Invoke-SyntheticChatMonitor.ps1` — a single-file PowerShell 7
script that mirrors the shape of `scripts/Verify-ApimAiGateway.ps1` and
`scripts/Verify-ProductionAuth.ps1`:

- `-SelfTest` (offline, no Azure signin) exercises the smoke-set
  synchronisation with the contracts manifest, the response-contract
  validator on well-formed and malformed synthetic payloads, the
  config-missing SKIP path, and a convention guard that refuses to let
  any client-secret code path be reintroduced. Wired into CI as a
  signin-free regression fence (`synthetic-monitor-selftest` job in
  `.github/workflows/ci.yml`).
- Live run reads the following optional configuration:
  - `RETAIL_PULSE_SYNTHETIC_API_ORIGIN` (fallback: `AZURE_API_APP_URL`)
    — deployed API base URL.
  - `RETAIL_PULSE_SYNTHETIC_API_RESOURCE` — App ID URI / scope for the
    API app registration, e.g. `api://<api-client-id>/.default`.
  - `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` — set automatically by
    `azure/login@v2` in CI; overridable locally.
- With any required value missing, the script prints
  `SKIP: optional synthetic monitor is not configured — missing …`
  and exits 0. It never turns an unconfigured fork red.
- With config present but token acquisition failing, the script emits an
  actionable `SKIP:` naming the likely causes (wrong `--resource` /
  App ID URI, wrong tenant, or a principal with no `RetailPulse.User`
  assignment) and exits 0. It never fabricates a live result.
- With a token acquired but the API rejecting it `403`, the run reports
  a `FAIL` for that prompt naming the most likely cause: the deployed
  API has not opted in to app-only tokens (see "Remaining condition for
  a live PASS" below).

`.github/workflows/synthetic-monitor.yml` — `workflow_dispatch` and a
daily `06:15 UTC` schedule. The job is gated on the repository variable
`RETAIL_PULSE_SYNTHETIC_ENABLED == 'true'`; when unset, every real step
short-circuits and the workflow completes green with a `::notice::`
explaining the outcome.

## Entra authorisation — COMPLETE

The identity is fully provisioned **and authorised**. The app-role grant
and admin consent that an earlier revision of this document listed as
outstanding are both in place. Do not go looking for them.

| Item | Value |
|---|---|
| Monitor app | `retail-pulse-synthetic-monitor` |
| Client ID | `b8212317-e16d-4f06-996b-955e885ca1ca` |
| Tenant ID | `48351615-345c-4547-bb6f-8fcc8d6e2568` |
| Credential | Federated `github-actions-main` — **no client secret exists** |
| Subject | `repo:swigerb@1630580/retail-pulse@1223914087:ref:refs/heads/main` |
| Audience | `api://AzureADTokenExchange` |
| API app | `Retail Pulse` — `b03317ab-a407-49cc-8769-0a15062777b1` |
| App role member types | `Users/Groups` → **`Users/Groups,Applications`** |
| Application permission | `RetailPulse.User` |
| Admin consent | **Granted for Contoso** |

All values above are non-secret identifiers, safe to commit.

### Why the app role allows "Both", not "Applications"

`RetailPulse.User` was originally declared with `allowedMemberTypes:
["User"]`, which Entra will not let an application be assigned to. It was
widened to `["User", "Application"]` — the "Both" option in the portal.

"Applications" **alone** was rejected deliberately: it would have removed
users' ability to hold `RetailPulse.User`, and since the API requires that
same role on every authenticated request, every interactive human sign-in
would have started failing `403`. "Both" is purely additive — existing user
assignments keep working untouched, and applications become assignable in
addition.

## Remaining condition for a live PASS

The remaining condition is **configuration on the API**, not a grant in
Entra.

PR [#164](https://github.com/swigerb/retail-pulse/pull/164) (issue
[#163](https://github.com/swigerb/retail-pulse/issues/163)) added opt-in
app-only (client-credentials) token acceptance. Before it, the API's
authorization policy required *both* the `RetailPulse.User` app role **and**
a delegated `scp` scope claim. An app-only token carries `roles` but no
`scp`, so it satisfied the role check and still failed the scope check —
`403`, no matter how complete the Entra configuration was.

The monitor therefore produces a live PASS only against a deployment that
has opted in:

| Key | Default | Effect |
|---|---|---|
| `MicrosoftEntra:AllowAppOnlyTokens` | **`false`** | Master opt-in. While `false` — the shipped default, and what the current Bicep deploys — every app-only token is rejected `403`. |
| `MicrosoftEntra:AllowedAppClientIds` | **empty** | Optional allow-list. Empty means the app role alone gates access. When populated, the token's `azp`/`appid` must match a listed GUID — set it to the monitor's client ID to keep the grant narrow. |

Environment-variable form:
`MicrosoftEntra__AllowAppOnlyTokens=true`,
`MicrosoftEntra__AllowedAppClientIds__0=b8212317-e16d-4f06-996b-955e885ca1ca`.

The opt-in is fail-closed and validated at startup in every environment —
see `docs/security.md` §"App-only (client-credentials) tokens".

**With the opt-in left at its default, the monitor still behaves
correctly:** it acquires a token and reports the `403` as a clean,
actionable failure rather than fabricating a result. Nothing about the
default deployment is broken; it simply does not accept machine callers.

## Manual interactive alternative (unchanged)

For on-call responders in front of a browser, the same smoke set can be
run manually via
`scripts/browser-chart-acceptance.js` and
`scripts/browser-prompt-library-acceptance.js` (PR #148) with an
interactive Entra sign-in. This remains the supported alternative when
the automated monitor is not enabled, or when the target deployment has
not opted in to app-only tokens.

## Historical note

An earlier revision of this document specified a confidential app
registration + client secret + Key Vault storage pattern. That plan is
REPLACED, in full, by workload-identity federation as described above:

- No `RETAIL_PULSE_SYNTHETIC_CLIENT_ID` / `_SECRET` GitHub Actions
  secrets.
- No `infra/modules/key-vault.bicep` module is required for this
  monitor — the whole client-secret storage sub-tree is gone.
- No `az keyvault secret show` at run time — token acquisition is
  a single `az account get-access-token` against the federated
  identity.

The federated model matches the platform's existing "managed identity,
no keys" pattern and is what actually shipped.
