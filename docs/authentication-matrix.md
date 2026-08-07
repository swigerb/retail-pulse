# Authentication matrix

> The authoritative behavior matrix for Retail Pulse authentication after the
> provider-neutral foundation (Sprint 0), extended with the Sprint 3
> provider-neutral **frontend** build modes (`VITE_AUTH_MODE`). It enumerates every
> mode and environment combination and the exact expected outcome. Each row is
> backed by an automated test — see [Coverage](#coverage).

## Modes

Retail Pulse selects an authentication provider through the `Authentication:Mode`
configuration key (`Authentication__Mode` as an environment variable). The
resolver is deterministic and never auto-detects a provider.

| Mode | Status in this sprint | Production |
|------|-----------------------|------------|
| `Entra` | Implemented (unchanged) | Supported and pinned |
| `GitHub` | Implemented (Sprint 2) — opt-in, fail-closed, not deployed (hosted requires complete validated secret-bearing config); confidential OAuth BFF | Never enabled (Entra pinned) |
| `Anonymous` | Implemented (Sprint 1) — opt-in, fail-closed, not deployed by default (hosted only with `Anonymous:AllowHosted=true`) | Never enabled (Entra pinned) |

## Mode resolution matrix

| `Authentication:Mode` value | Environment | Outcome |
|-----------------------------|-------------|---------|
| `Entra` (any case) | any | Resolves to `Entra`; wires the existing Entra boundary |
| `GitHub` (any case) | Development | Resolves; wires the GitHub confidential OAuth BFF boundary. May use an ephemeral process-local signing key (sessions die on restart), but still requires a client id/secret and an allowlist. |
| `GitHub` (any case) | Production (or any non-Development) | Resolves; requires a complete validated set — client id/secret, ≥ 256-bit signing key, issuer, audience, exact callback + frontend URLs, and a non-empty immutable user-id and/or active-organization allowlist — or startup fails closed. Placeholder (`<...>`) secrets are rejected. |
| `Anonymous` (any case) | Development | Resolves; wires the Anonymous boundary with an ephemeral process-local signing key (sessions die on restart). No hosted guardrails required. |
| `Anonymous` (any case) | Production (or any non-Development) | Resolves; requires `Anonymous:AllowHosted=true` **plus** a strong signing key (≥ 256-bit) **plus** positive daily request/token/cost ceilings, or startup fails closed |
| missing / blank | Development | Defaults to `Entra` (documented Development-only default) |
| missing / blank | Production (or any non-Development) | Startup fails — mode must be pinned explicitly, fails closed |
| unknown string (for example `Okta`) | any | Startup fails — not a recognized mode |
| bare number (for example `1`) | any | Startup fails — numeric selection is rejected |

## Frontend build-mode matrix (`VITE_AUTH_MODE`)

The SPA renders exactly **one** provider's sign-in UX, chosen at build time by
`VITE_AUTH_MODE` (injected by the deployment: `infra/main.bicep` output → azd env →
Vite build). This mirrors the backend `Authentication:Mode` so both halves of a
deployment agree — a deployment contract test proves the parity. Resolution is a pure,
fail-closed function (`src/RetailPulse.Web/src/auth/authMode.ts`).

| `VITE_AUTH_MODE` value | Build | Outcome |
|------------------------|-------|---------|
| `Entra` (any case) | any | Renders the Microsoft sign-in gate (MSAL). Live production build. |
| `GitHub` (any case) | any | Renders the "Continue with GitHub" gate (confidential BFF; provider token never in the browser). |
| `Anonymous` (any case) | any | Renders the "Continue in limited demo" consent gate; all privileged surfaces hidden. |
| missing / blank | SPA already carries Entra config | Resolves to `Entra` (back-compat); mounts the Entra gate. |
| missing / blank | local dev (`import.meta.env.DEV`), no Entra config | Transparent pass-through (no gate) against the API's Development synthetic auth. |
| missing / blank | production build, no Entra config | **Throws at load** — never a silent, insecure default. |
| unknown string (e.g. `Okta`) | any | **Throws** — not a recognized mode. |
| bare number (e.g. `1`) | any | **Throws** — numeric selection rejected. |

### Frontend UX & capability behavior per mode

| Concern | Entra | GitHub | Anonymous |
|---------|-------|--------|-----------|
| Gate label | "Sign in with Microsoft" | "Continue with GitHub" | "Continue in limited demo" |
| Credential in browser | MSAL token (`sessionStorage`, MSAL-owned) | Retail Pulse session token only (memory + `sessionStorage`) | Retail Pulse session token only (memory + `sessionStorage`) |
| Provider token in browser | n/a | **Never** (server-side BFF) | n/a |
| Login start | MSAL redirect | Top-level nav to fixed `GET /api/auth/github/start` (no return URL) | Explicit consent click → `POST /api/auth/anonymous/session` |
| Callback handling | MSAL | One-time `code` stripped via `history.replaceState`, exchanged at `POST /api/auth/github/exchange` | n/a |
| SignalR hubs | yes | yes | **no** (`realtimeHub=false`; hub never started, token factory returns `''`) |
| Telemetry / Observability / Approvals / Memory / Export / write actions / alternate views | shown | shown | **hidden** (all capabilities false) |
| Token cleared on | logout / expiry / 401 / 403 | logout / expiry / 401 / 403 | clear-session / expiry / 401 / 403 |

Capability gating in the UI is a **usability layer only** — the backend remains the
authoritative gate (an anonymous token is still `403`'d on any disallowed route
regardless of what the UI renders).

## Runtime authorization matrix (Entra mode)

Behavior for the `Entra` mode is identical to the pre-foundation implementation.

| Request | Credential | Expected result |
|---------|-----------|-----------------|
| Protected REST endpoint | valid token with `RetailPulse.User` role + `access_as_user` scope | 200 |
| Protected REST endpoint | no token | 401 |
| Protected REST endpoint | valid token missing the role or scope | 403 |
| `/hubs/*` | valid token via `?access_token` query string | connects |
| `/hubs/*` | no token | 401 |
| REST endpoint | token via `?access_token` query string only | 401 (query token is honored only on `/hubs/*`) |
| `/health`, `/alive` | none | 200 (anonymous by design — health-only invariant) |

## Runtime authorization matrix (Anonymous mode)

Anonymous mode is an **opt-in, fail-closed** capability that is **not deployed by
default** (hosted only behind an explicit `Anonymous:AllowHosted=true` opt-in).
Identity is a server-minted, short-lived session token — never a client-supplied
header or body value. The surface is **deny-by-default** and, for Sprint 1, reduced
to exactly **two routes**: the unauthenticated bootstrap and the authenticated
`POST /api/chat`. **The SignalR hubs are NOT part of the anonymous surface** — a
valid anonymous token is denied `403` on both the telemetry and streaming hubs, at
both the connection and negotiate endpoints. Anonymous sessions therefore have **no
real-time telemetry or token streaming**; the Sprint 3 frontend does not start the
hubs in anonymous mode. There is **no blanket GET allowance** — every other route is
`403`.

| Request | Credential | Expected result |
|---------|-----------|-----------------|
| `POST /api/auth/anonymous/session` (bootstrap) | none | 200 — mints a short-lived session token (random subject, `provider=Anonymous`, role `RetailPulse.Anonymous`, scope `chat_limited`, no PII, no refresh) |
| `POST /api/auth/anonymous/session` (bootstrap) | none, over the global bootstrap limit | 429 |
| `POST /api/chat` (within limits) | valid anonymous session token | 200 — the single allowlisted chat capability, output-token-capped, memory + response-cache disabled |
| `POST /api/chat` classified as memory management (e.g. `remember that ...`, `forget everything`) | valid anonymous session token | 200 — standard safe refusal returned **before** the direct-write `MemoryManagementAgent` runs; no `StoreAsync`/`ForgetAsync`, no model call, no memory row written |
| `POST /api/chat` classified as portfolio health / council | valid anonymous session token | 200 — standard safe refusal returned **before** the consensus-council interception; `IConsensusCouncil.ConveneAsync` is never called, so no council model calls and no accounting bypass |
| Any broad GET / observability / admin / export / memory / cards / approvals / guardrail-log route (e.g. `GET /api/scorecard`, `GET /api/sessions`, `GET /api/audit`, `GET /api/memory`) | valid anonymous session token | 403 — not on the allowlist (deny-by-default). Read-only data is reached through the filtered chat tool path, not a direct operator endpoint |
| Alternate LLM / orchestrator routes (`POST /api/chat/stream`, `POST /api/council/convene`) | valid anonymous session token | 403 — removed from the anonymous surface so all model use is accounted through `POST /api/chat` |
| `/hubs/telemetry`, `/hubs/streaming` — connection (`GET ?access_token=`) | valid anonymous token | **403** — the hubs are not on the anonymous allowlist; the deny-by-default guard blocks the request before the hub runs |
| `/hubs/telemetry/negotiate`, `/hubs/streaming/negotiate` — negotiate (`POST`) | valid anonymous token | **403** — negotiate is denied on both hubs, same as the connection endpoint |
| Protected REST/hub | no token | 401 |
| Protected REST/hub | malformed / expired / wrong issuer / wrong audience / wrong signature token | 401 |
| Protected REST/hub | valid-signature token with `provider != Anonymous` (cross-provider) | 403 — the anonymous policy requires `provider=Anonymous` |
| Hub behaviour for **Entra** callers | valid Entra token | unchanged — the anonymous scope reduction does not alter the Entra real-time surface |
| REST endpoint | anonymous token via `?access_token` query string only | 401 (query token honored only on `/hubs/*`) |
| Chat body carrying a spoofed `user.objectId` | valid anonymous token | ignored — identity is the immutable token `sub`; a body `objectId` is never honoured for an authenticated principal |
| Chat tool invocation of a write-capable tool (e.g. `RequestApproval`) | valid anonymous token | Tool is not registered for the anonymous principal — never invoked |
| `POST /api/chat` over per-subject or per-IP minute limit | valid anonymous token | 429 |
| `POST /api/chat` after the daily request/token/cost ceiling trips | valid anonymous token | 503 — circuit breaker, fail-closed (cache hits still consume the request slot) |
| Oversized request body (including chunked / unknown-length without `Content-Length`) | valid anonymous token | 413 — enforced before the body is read and via a length-counting pre-read |
| History over the per-message / aggregate bound | valid anonymous token | 400 — rejected by validation before the model |
| `/health`, `/alive` | none | 200 |

> **Scope & reset limitation.** The rate-limit windows and daily ceilings are
> **replica-local, in-memory**. Hosted Anonymous is therefore pinned to
> `maxReplicas=1`, and all of these counters **reset on restart or replica
> replacement**. Behind the ACA ingress the connection IP is the proxy's, not the
> client's, and `X-Forwarded-For` is not trusted — so the **bootstrap limiter is a
> per-replica global window** (config key `Anonymous:Bootstrap:GlobalPerMinute`,
> conservative default `5`; the legacy `Anonymous:Bootstrap:PerIpPerMinute` key is
> still honoured as a backward-compatible fallback), and the **primary** post-bootstrap
> control is the per-subject chat limit.

## Runtime authorization matrix (GitHub mode)

GitHub mode is an **opt-in, fail-closed** capability that is **not deployed**.
Authentication is a **confidential backend-for-frontend (BFF) OAuth flow**: the
browser talks only to our API; the GitHub client secret stays on the server; the
GitHub **provider token never reaches the SPA** (used transiently server-side to
validate the user and org membership, then discarded). GitHub OAuth Apps do not
support PKCE, so CSRF/fixation is closed by a random `state` in a server-side
one-time store **plus** a separate random secret in an HttpOnly/Secure/
SameSite=Lax cookie (SHA-256 hash stored server-side, constant-time compared).
The SPA receives only a short-lived one-time **redemption code**, which it
exchanges for a short-lived Retail Pulse **session token**. The three BFF
endpoints are the only anonymous surface beyond `/health` + `/alive`, and all are
rate limited.

| Request | Credential | Expected result |
|---------|-----------|-----------------|
| `GET /api/auth/github/start` | none | 302 to `https://github.com/login/oauth/authorize` with the exact registered `redirect_uri`, a random `state`, minimal scope (empty by default; `read:org` only when an org allowlist is configured; **never `repo`**), `allow_signup=false`; sets the HttpOnly/Secure/SameSite=Lax state cookie |
| `GET /api/auth/github/start` | none, over the start limit | 429 |
| `GET /api/auth/github/callback` (valid state + cookie, allowlisted user) | GitHub redirect back | 302 to the **one** configured SPA URL carrying only a one-time redemption `code` (never a provider/app token); state cookie deleted |
| `GET /api/auth/github/callback` | missing/mismatched/expired/replayed `state`, or absent state cookie, or cookie hash mismatch | 400 `invalid_state`, no code exchange performed; state cookie deleted |
| `GET /api/auth/github/callback` | `error=access_denied` (user denied) | 302 to the SPA URL with a generic error code; no token |
| `GET /api/auth/github/callback` | GitHub code exchange or `/user` validation fails | 302 to the SPA URL with a generic `login_failed`; provider errors never leaked |
| `GET /api/auth/github/callback` | user not on the immutable id / active-org allowlist, or org membership absent/inactive, or the allowlist GitHub API errors/rate-limits | 302 to the SPA URL with `not_authorized` — **fail closed** (deny on error) |
| `POST /api/auth/github/exchange` (valid one-time code) | one-time redemption code | 200 — mints a short-lived HS256 session token (`provider=GitHub`, `sub=github:<id>`, role `RetailPulse.User`, scope `access_as_user`, random `jti`, no refresh) |
| `POST /api/auth/github/exchange` | replayed / unknown / expired code | 400 `invalid_code` — the code is one-use and redeemed atomically (replay/race impossible) |
| `POST /api/auth/github/exchange` | over the exchange limit | 429 |
| Protected REST endpoint | valid GitHub session token | 200 — full authenticated capability, acceptable only because it was reached after server-side allowlist verification |
| `/hubs/*` (connection + negotiate) | valid GitHub session token via `?access_token=` | connects — query token honored on `/hubs/*` exactly like Entra |
| REST endpoint | GitHub session token via `?access_token` query string only | 401 (query token honored only on `/hubs/*`) |
| Protected REST/hub | no token | 401 |
| Protected REST/hub | malformed / expired / wrong issuer / wrong audience / wrong signature (algorithm pinned HS256) | 401 |
| Protected REST/hub | valid-signature token with `provider != GitHub` (Entra/Anonymous/cross-provider) | 403 — the GitHub policy requires `provider=GitHub` |
| Any redirect, response body, or log line | — | never contains the GitHub provider token (asserted by integration tests) |
| `/health`, `/alive` | none | 200 |

> **Replica-local limitation.** The state and redemption stores are
> **replica-local, in-memory**, so a callback served by one replica and an
> exchange served by another would not share state. Hosted GitHub is therefore
> pinned to `maxReplicas=1` until the stores are moved to distributed storage; the
> stores are bounded, one-use, and TTL-expiring with opportunistic cleanup.



| Environment | Mode source | Auth handler |
|-------------|-------------|--------------|
| Development (local) | defaults to `Entra` when unset | `DevelopmentAuthHandler` stamps a synthetic identity (`oid` zero-GUID, `RetailPulse.User` role, `access_as_user` scope) |
| Production | `Entra`, pinned in `appsettings.Production.json` and the azd hooks | `JwtBearer` with authority / issuer / audience pinned; `Security:RequireAuth=true` |

The Development default is intentional and documented — it keeps the local demo
running without configuration. It never applies outside Development.

## Fail-closed guarantees

- No missing, unknown, malformed, or unimplemented mode ever falls through to a
  weaker provider. Every such case throws at startup before any authentication
  scheme is registered.
- Production is pinned to `Entra` in three independent artifacts (base config,
  Production config, azd hooks). A deployment contract test proves those
  artifacts never emit `GitHub` or `Anonymous`.
- GitHub is implemented but **opt-in and never deployed**: a hosted
  (non-Development) GitHub deployment fails startup unless a complete, validated
  secret-bearing configuration is present (client id/secret, ≥ 256-bit signing
  key, issuer, audience, exact callback + frontend URLs, an **immutable** allowlist
  — numeric `AllowedUserIds` and/or active-org `AllowedOrgs`, `RequireSecureCookies=true`,
  and `AcknowledgeSingleReplica=true`); placeholder secrets are rejected. The
  mutable login handle never grants access (a login-only allowlist fails startup).
  Cookie `Secure`/`__Host-` semantics come from validated config, not the
  proxy-observed request scheme. The provider token never reaches the SPA, the
  allowlist fails closed on any error, no `repo` scope is requested, genuine
  signing-key rotation is supported, and hosted GitHub is pinned to a single
  replica (`maxReplicas=1`, acknowledged via `AcknowledgeSingleReplica`) because
  the state / redemption stores and login limiters are replica-local.
- Anonymous is implemented but **opt-in and never deployed**: any hosted
  (non-Development) Anonymous deployment fails startup unless a second explicit
  opt-in (`Anonymous:AllowHosted=true`) and a complete, validated guardrail set
  (strong signing key + positive daily request/token/cost ceilings) are present.
  Hosted Anonymous is pinned to a single replica (`maxReplicas=1`) because the
  billable-use ceilings are replica-local; it is **not** equivalent to
  authenticated production.

## Coverage

| Matrix area | Test |
|-------------|------|
| Mode resolution (all rows above) | `tests/RetailPulse.Tests/Security/AuthenticationModeTests.cs` |
| Entra wiring + GitHub/Anonymous factory wiring and hosted fail-closed validation | `tests/RetailPulse.Tests/Security/AuthenticationModeTests.cs` |
| Entra success / 401 / 403 / hubs | `tests/RetailPulse.Tests/Security/EntraAuthenticationTests.cs` |
| GitHub BFF start/callback/exchange happy path + every failure (state/cookie/TTL/replay, denial, exchange/`/user`/org errors, unallowlisted/inactive, code replay/race, wrong redirect, token validation incl. cross-provider, REST + both hubs after session token, query-token-on-REST denied, provider token never in redirect/body/logs, exact scopes/no repo, rate limits, anonymous-exception coverage) | `tests/RetailPulse.Tests/Security/GitHubAuthenticationTests.cs` |
| GitHub options fail-closed validation, one-time stores (TTL/one-use/bounded), session token claims + validation, id/login/active-org allowlist (fail-closed) | `tests/RetailPulse.Tests/Security/GitHubAuthOptionsTests.cs`, `GitHubOneTimeStoreTests.cs`, `GitHubSessionTokenTests.cs`, `GitHubUserAllowlistTests.cs` |
| Anonymous bootstrap, token validation, read-only 403, **both hubs 403 (connect + negotiate)**, budget/rate breakers, isolation, threat cases | `tests/RetailPulse.Tests/Security/AnonymousAuthenticationTests.cs` |
| Anonymous options fail-closed validation, token claims (no PII), capability policy (hubs denied), normalizer | `tests/RetailPulse.Tests/Security/AnonymousCapabilityTests.cs` |
| Chat-internal bypasses closed: memory-management refusal (zero memory mutation), council refusal (zero `ConveneAsync`), single accounted pipeline with truthful cost/audit | `tests/RetailPulse.Tests/Security/AnonymousChatInternalBypassTests.cs` |
| Endpoint graph policy: anonymous surface is bootstrap + `POST /api/chat` only; both hubs denied; REST + hubs carry authorization metadata | `tests/RetailPulse.Tests/Security/EndpointAuthorizationCoverageTests.cs` |
| Entra hub behaviour unchanged by the anonymous scope reduction | `tests/RetailPulse.Tests/Security/AnonymousHubOwnershipTests.cs` |
| Normalized principal mapping | `tests/RetailPulse.Tests/Security/NormalizedPrincipalTests.cs` |
| Production / hooks pinned to Entra, never GitHub/Anonymous; single-replica pin; GitHub example config not auto-loaded and secret-free; **frontend `VITE_AUTH_MODE` ↔ API `Authentication__Mode` parity; web mode templates documented & secret-free** | `tests/RetailPulse.Tests/Deployment/ProviderNeutralDeploymentContractTests.cs` |
| Frontend mode resolver (all `VITE_AUTH_MODE` rows: explicit modes, back-compat, local-dev pass-through, prod-missing throw, unknown/numeric throw) | `src/RetailPulse.Web/src/__tests__/authMode.test.ts` |
| Provider-neutral gate dispatch + Entra gate unchanged; GitHub start/callback/exchange/history-strip/replay/error/logout/401/403; Anonymous consent/bootstrap/limited-nav/no-hub/expiry/banner; token storage (session-only, cleared on expiry/logout/401/403); exact-origin `authorizedFetch`; hub token gated by capabilities; Dashboard hides privileged surfaces & never starts SignalR for anonymous | `src/RetailPulse.Web/src/__tests__/{AuthGate,GitHubAuthGate,AnonymousAuthGate,sessionCredentialStore,tokenService,authorizedFetch,Dashboard.capabilities}.test.ts(x)` |

See [ADR-005](adr/005-provider-neutral-authentication.md) for the design and
threat model, and [Entra authentication](authentication-entra.md) for the
end-to-end Entra flow.
