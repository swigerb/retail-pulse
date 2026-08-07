# Security Hardening

> Security measures implemented in RetailPulse.

> **User authentication:** production access is gated by Microsoft Entra ID
> (single-tenant SPA/API app registration, MSAL PKCE, `RetailPulse.User` app role
> required on every protected endpoint and hub). See
> [authentication-entra.md](./authentication-entra.md) for the full boundary,
> environment contract, and provisioning scripts.

---

## Security Headers

All responses include these security headers (via `SecurityHeadersMiddleware`):

| Header | Value | Purpose |
|--------|-------|---------|
| `Strict-Transport-Security` | `max-age=31536000; includeSubDomains` | Force HTTPS |
| `Content-Security-Policy` | `default-src 'self'; script-src 'self' 'unsafe-inline'...` | Prevent XSS/injection |
| `X-Content-Type-Options` | `nosniff` | Prevent MIME sniffing |
| `X-Frame-Options` | `DENY` | Prevent clickjacking |
| `X-XSS-Protection` | `0` | Disabled (CSP is preferred) |
| `Referrer-Policy` | `strict-origin-when-cross-origin` | Limit referrer leakage |
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=()` | Restrict browser APIs |

---

## Input Validation

All chat requests are validated by `ChatRequestValidator` before reaching the agent:

| Rule | Limit | Error |
|------|-------|-------|
| Message length | Max 2000 characters | 400 — "Message exceeds maximum length" |
| Message format | Non-empty, trimmed | 400 — "Message is required" |
| XSS detection | No `<script>`, `javascript:`, `on*=` patterns | 400 — "Message contains disallowed content" |
| Session ID format | Alphanumeric + hyphens only | 400 — "Invalid session ID format" |

Validation errors return RFC 7807 Problem Details with specific error descriptions.

---

## Audit Log

All chat interactions are recorded in a tamper-evident audit log (`DurableAuditLog`):

**Storage:** SQLite database (local file, production: managed storage)

**Fields per entry:**
- Timestamp (UTC)
- Correlation ID
- User identifier
- Action type
- Request summary (truncated)
- Response metadata
- SHA256 hash (current entry + previous hash)

**Tamper detection:** Each entry's hash incorporates the previous entry's hash, forming a hash chain. Any modification to historical entries breaks the chain and is detectable.

**Querying:** The audit log is append-only. Query by correlation ID, user, or time range for compliance reviews.

---

## Authentication

### Provider selection (mode contract)

The active identity provider is chosen through the `Authentication:Mode`
configuration key (`Authentication__Mode`). Resolution is deterministic and
fails closed — it never auto-detects a provider:

- `Entra` (the production mode) routes to the unchanged Entra boundary.
- `Anonymous` is implemented (Sprint 1) as an **opt-in, fail-closed** capability
  that is **not deployed by default** (hosted only behind an explicit
  `Anonymous:AllowHosted=true` opt-in) — see
  [Anonymous mode](#anonymous-mode-opt-in-fail-closed) below.
- `GitHub` is implemented (Sprint 2) as an **opt-in, fail-closed** confidential
  OAuth backend-for-frontend capability that is **not deployed** — see
  [GitHub mode](#github-mode-confidential-oauth-bff-opt-in-fail-closed) below.
- A missing mode defaults to `Entra` **only in Development**. Outside
  Development a missing, unknown, or malformed mode fails startup.

Production pins `Authentication__Mode=Entra` explicitly in
`appsettings.Production.json` and the azd postprovision hooks. See
[ADR-005](adr/005-provider-neutral-authentication.md) and the
[authentication matrix](authentication-matrix.md).

### Anonymous mode (opt-in, fail-closed)

Anonymous mode lets a future self-serve frontend (Sprint 3) reach a **single
chat capability** — authenticated `POST /api/chat` — without an identity provider.
It is additive and fail-closed. In Sprint 1 the **SignalR hubs are NOT part of the
anonymous surface**: anonymous sessions have no real-time telemetry or token
streaming, and a valid anonymous token is denied `403` on both hubs. Hosted
Anonymous **is permitted only behind an explicit
opt-in** (`Anonymous:AllowHosted=true`); by default it is never deployed, and the
**live deployment artifacts stay Entra**, proven so by deployment-contract tests.
It is not deployed this sprint.

- **Smallest useful surface (deny-by-default).** The anonymous surface is exactly
  **two routes**: the unauthenticated bootstrap and authenticated `POST /api/chat`.
  **Every other (method, route) is
  `403`** — there is **no blanket GET allowance**, and **both SignalR hubs
  (`/hubs/telemetry`, `/hubs/streaming`) are denied** at connection and negotiate.
  Observability, admin, export,
  memory, cards, approvals, guardrail logs, `/api/chat/stream`, council, scorecard,
  escalation and the message-extension endpoints are all forbidden. Read-only data
  (e.g. charts) is reached **through the filtered chat tool path**, never a direct
  operator endpoint. A runtime endpoint-graph test enumerates every mapped route and
  proves deny-by-default.
- **Two-key hosted activation.** `Authentication:Mode=Anonymous` enables it;
  Development may run with an ephemeral process-local signing key (sessions die on
  restart). Any hosted/non-Development Anonymous deployment additionally requires
  `Anonymous:AllowHosted=true` **and** a strong signing key (≥ 256-bit) **and**
  positive daily request/token/cost ceilings, or startup fails closed.
- **Server-minted identity.** A bootstrap endpoint
  (`POST /api/auth/anonymous/session`) issues a short-lived (default 15 min) HS256
  session token with a cryptographically random subject, `provider=Anonymous`,
  role `RetailPulse.Anonymous`, scope `chat_limited`, strict expiry, no PII, and
  no refresh token. It works as a REST bearer and a SignalR `?access_token`
  (query token honored only on `/hubs/*`). The signing key is a secret, never
  committed. Bootstrap is throttled by a **single global conservative** rate limit
  (see the ACA/proxy note below), not per-client-IP.
- **Provider-aware, spoof-proof identity.** Identity is resolved per provider:
  Anonymous → the immutable server-minted `sub`; Entra → the immutable `oid`. A
  request-body `objectId` is **never** honoured for any authenticated principal, so
  two anonymous sessions cannot be conflated or spoofed. **Memory is disabled for
  Anonymous** (no read, write, or extraction) to eliminate stored cross-prompt
  injection, and the **response cache is disabled for Anonymous** so identical
  prompts from different subjects can never share a reply.
- **Constrained authorization.** A dedicated policy requires
  `provider=Anonymous` + the anonymous role + scope, so Entra/cross-provider
  tokens can never satisfy it. Only health and the bootstrap endpoint are
  unauthenticated.
- **Central enforcement + billable-use safeguards.** `AnonymousGuardMiddleware`
  centrally enforces the deny-by-default allowlist (403), request size (413 —
  enforced before the body is read and via a length-counting pre-read, so a
  chunked/unknown-length body without `Content-Length` cannot bypass it),
  per-subject/per-IP minute limits (429), a per-request timeout, and a daily
  request/token/cost circuit breaker (503, fail-closed). Request slots are charged
  before the cache so cache hits cannot bypass the ceiling. Because only the single
  `AgentExecutionPipeline` behind `POST /api/chat` is reachable, the output-token
  cap and the write-capable-tool filter apply to the whole anonymous surface; there
  is no alternate orchestrator or billable route to escape them. History length is
  bounded per-message and in aggregate (validation `400` before the model).
- **Chat-internal intent hard-stops.** Two in-process paths do not use the AI tool
  set, so the write-tool filter cannot reach them; the endpoint refuses them by the
  router's own classification, before specialist selection: a memory-management turn
  (the `MemoryManagementAgent` calls `StoreAsync`/`ForgetAsync` directly) returns a
  safe refusal with **no memory write and no model call**, and a portfolio-health turn
  is refused **before the consensus-council interception**, so `ConveneAsync` is never
  called and no unaccounted council model fan-out occurs.
- **Hubs are not part of the anonymous surface.** A valid anonymous token is denied
  `403` on both hubs (connection + negotiate), so no anonymous caller reaches a hub
  group at all — this retires the global-broadcast and cross-subject group-collision
  risk without touching the Entra hub telemetry path. **Entra hub behaviour is
  unchanged.**
- **ACA proxy / bootstrap limiter.** Behind the Azure Container Apps ingress the
  connection remote IP is the proxy's, not the client's, and `X-Forwarded-For`
  is **not** trusted (it is forgeable and ACA gives no cryptographically verifiable
  client-IP header). The bootstrap limiter is therefore a per-replica **global**
  window (config key `Anonymous:Bootstrap:GlobalPerMinute`, conservative default `5`;
  the legacy `Anonymous:Bootstrap:PerIpPerMinute` key is still honoured as a
  backward-compatible fallback), and the **primary** abuse control is the
  **per-subject** limit applied after bootstrap.
- **Limitation.** Ceilings and rate-limit windows are **replica-local, in-memory**,
  so hosted Anonymous is pinned to
  `maxReplicas=1` with conservative limits and is **not** equivalent to
  authenticated production; **all of these counters reset on restart or
  replica replacement**.

### GitHub mode (confidential OAuth BFF, opt-in, fail-closed)

GitHub mode lets a future self-serve frontend (Sprint 3) sign in with GitHub
without exposing a GitHub provider token to the browser. It is additive and
fail-closed, and the **live deployment artifacts stay Entra** (proven by
deployment-contract tests). It is **not deployed** this sprint.

- **Confidential backend-for-frontend (BFF).** GitHub OAuth Apps **do not support
  PKCE**, so a browser-only exchange is impossible without leaking the client
  secret or the provider token. The browser therefore talks only to our API: the
  API holds the client secret, performs the authorization-code→token exchange
  **server-side**, validates the user by calling `/user`, and the GitHub
  **provider token never reaches the SPA** (it is used transiently server-side,
  then discarded). Integration tests assert it never appears in any redirect,
  response body, or log.
- **Three narrowly-anonymous, rate-limited endpoints** (the only anonymous surface
  besides health): `GET /api/auth/github/start`, `GET /api/auth/github/callback`,
  `POST /api/auth/github/exchange`.
- **Login-CSRF / fixation closed without PKCE.** `start` mints a random `state`
  in a server-side **one-time** store **and** a separate random secret in an
  HttpOnly cookie whose **Secure / `__Host-` attributes come from the validated
  `RequireSecureCookies` option, never from `Request.IsHttps`** — behind a
  TLS-terminating proxy (Azure Container Apps) the in-container request is plain
  HTTP even though the browser↔edge hop is HTTPS, so deriving cookie security from
  the observed scheme would silently emit an insecure cookie in production. In
  hosted/secure mode the cookie is `__Host-` prefixed (Secure, HttpOnly,
  SameSite=Lax, Path=/, no Domain); Development may explicitly opt into an insecure,
  non-`__Host` dev cookie over plain HTTP (rejected at startup outside Development).
  Only the cookie secret's SHA-256 hash is stored server-side. The cookie **name is
  per-state** (`__Host-rp_gh_state_` + a bounded URL-safe suffix derived from the
  state), so **parallel login tabs never clash** — each `callback` reads/deletes
  only its own cookie. `callback` validates the state **format** first, requires
  both signals, consumes the state atomically (one-use), and **constant-time**
  compares the cookie hash **before any code exchange**, then deletes the state
  cookie on every path (success or failure).
- **Open-redirect / SSRF closed.** `start` redirects **only** to the fixed
  `https://github.com/login/oauth/authorize`; the token, `/user`, and org-membership
  calls use fixed GitHub endpoints; the SPA return is the **one** configured,
  validated absolute-HTTPS frontend URL — no user-supplied redirect target is ever
  honored.
- **One-time redemption, no token in the redirect.** On success `callback`
  redirects to the SPA carrying only a random, short-lived, single-use **redemption
  code** (never a provider/app token). `exchange` atomically redeems it (replay/race
  impossible) and returns the session token. There is no refresh token.
- **Server-side immutable allowlist, minimal scopes.** Authorization is decided
  server-side on **immutable** signals only: the **numeric GitHub user id**
  (`AllowedUserIds`, positive, deduped) and/or **active** org membership
  (`/user/memberships/orgs/{org}`, `state==active`). The **mutable login handle
  never grants access** — a renamed/re-created handle cannot inherit access
  (handle-reuse denied), and a login-only allowlist fails startup. Startup **fails
  closed** unless at least one immutable mechanism is configured. **No `repo` scope
  is ever requested**; scope is empty by default and `read:org` only when an org
  allowlist is configured (private membership requires the user's `read:org`
  consent). The allowlist **fails closed** on any GitHub API/rate/transport error.
- **Retail Pulse session token.** A compact short-lived HS256 JWT (algorithm
  pinned) with a **separate issuer/audience**, `provider=GitHub`, subject
  `github:<immutable id>`, the required `RetailPulse.User` role + `access_as_user`
  scope, a random `jti`, strict expiry, no PII beyond the public login, and no
  refresh token. The ≥ 256-bit signing key is a secret; **genuine key rotation** is
  supported via `AdditionalValidationKeys` (validation-only strong keys keep
  pre-rotation tokens valid while the current key signs; each key has a stable
  material-derived `kid`). It works as a REST bearer and a SignalR `?access_token`
  (query token honored only on `/hubs/*`), exactly like Entra. A dedicated policy
  requires `provider=GitHub`, so Entra/Anonymous/cross-provider tokens can never
  satisfy it. `GitHubPrincipalNormalizer` trusts only the numeric subject, never the
  mutable login.
- **Fail-closed hosted config.** `Authentication:Mode=GitHub` enables it;
  Development may run with an ephemeral process-local signing key (sessions die on
  restart) but still needs a client id/secret and an immutable allowlist. Any hosted
  deploy requires a complete validated set (client id/secret, ≥ 256-bit signing key,
  issuer, audience, exact callback + frontend URLs, immutable allowlist,
  `RequireSecureCookies=true`, and `AcknowledgeSingleReplica=true`) — missing,
  malformed, or angle-bracket **placeholder** secrets fail startup. The client id is
  public; the client secret and signing key are secrets, never committed/logged.
- **Limitation.** The state and redemption stores are **replica-local, in-memory**
  (bounded, one-use, TTL with opportunistic cleanup), so hosted GitHub is pinned to
  `maxReplicas=1` until they move to distributed storage; a callback and exchange
  served by different replicas would not share state. Because the runtime cannot
  inspect ACA topology, hosted GitHub requires an explicit
  `AcknowledgeSingleReplica=true` fail-closed acknowledgement of that pin.

### Development
- `Security:RequireAuth` defaults to `false`
- No API key required

### Production
- **API Key:** Required via `x-api-key` header (configured in `ApiKey:Value`)
- **JWT Bearer:** For Teams bot integration (validated by `TeamsSsoHandler`)
- **Managed Identity:** For Azure OpenAI and APIM (no secrets in code)

---

## Rate Limiting

Four tiers of rate limiting (ASP.NET Core Rate Limiter):

| Policy | Limit | Applies To |
|--------|-------|-----------|
| `strict` | 10 req/min | `/api/v1/chat`, AI-intensive routes |
| `standard` | 30 req/min | General API endpoints |
| `relaxed` | 100 req/min | Health checks, static resources |
| `upload` | 5 req/min | File upload endpoints |

Rate limit responses include `Retry-After` header.

---

## OWASP Coverage

The test suite includes OWASP Top 10 validation:

| OWASP | Category | Test Coverage |
|-------|----------|---------------|
| A01 | Broken Access Control | API key validation, auth bypass prevention |
| A03 | Injection | XSS detection, input sanitization |
| A05 | Security Misconfiguration | Header validation, default credentials check |
| A07 | Authentication Failures | Token validation, session management |

See `tests/RetailPulse.Tests/Security/Owasp*.cs` for test implementations.

---

## Secrets Management

- **Never** committed to source control
- User secrets (`dotnet user-secrets`) for local development
- Azure Key Vault references for production deployments
- Managed Identity preferred over connection strings
- `.gitignore` excludes all secret files
