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

## Content Safety (optional second layer)

> **Status:** disabled by default. See
> [ADR-010](adr/010-content-safety-layering.md) for the full design rationale.

The regex-based guardrails
([`GuardrailsMiddleware`](../src/RetailPulse.Api/Middleware/GuardrailsMiddleware.cs))
can be layered with Azure AI Content Safety and Prompt Shields for defence in
depth. The layer is opt-in; when disabled, the runtime is byte-for-byte
identical to the regex-only baseline (no `ContentSafetyClient` is
constructed, no HTTP handler is registered, `DefaultAzureCredential` is not
resolved).

### Coverage

All four trust boundaries are covered:

| Stage                | Where                                      | Prompt Shields | Categories |
| -------------------- | ------------------------------------------ | -------------- | ---------- |
| Input                | `GuardrailsMiddleware.CheckInputAsync`     | Yes (user)     | Yes        |
| Output               | `GuardrailsMiddleware.FilterOutputAsync`   | No             | Yes        |
| Retrieved knowledge  | `RagContextProvider.FilterByContentSafety` | Yes (document) | Yes        |
| Tool result          | `Budget/BudgetedAIFunction` (ambient hook) | No             | Yes        |

PII redaction runs before Content Safety on the output path so raw PII is
never included in a moderation call.

### Configuration

`Guardrails:ContentSafety` in `appsettings.json` (all values shown are
defaults):

```jsonc
{
  "Guardrails": {
    "ContentSafety": {
      "Enabled": false,
      "Endpoint": "",              // https://<account>.cognitiveservices.azure.com
      "TimeoutMs": 1500,
      "OnUnavailable": "FailOpen",  // or "FailClosed"
      "PromptShieldsEnabled": true,
      "CheckInput": true,
      "CheckOutput": true,
      "CheckRetrievedKnowledge": true,
      "CheckToolResults": true,
      "Thresholds": { "Hate": 4, "Sexual": 4, "Violence": 4, "SelfHarm": 4 }
    }
  }
}
```

There is deliberately **no** key / secret field. Authentication is via
`DefaultAzureCredential` — locally that resolves to the developer's Azure CLI
context; in production it resolves to the container app's system-assigned
identity, which is granted `Cognitive Services User` on the resource by the
postprovision hook. The `/api/guardrails/config` endpoint never returns the
`Endpoint` value so an operator cannot leak it through the audit surface.

### Fail-open vs fail-closed

`OnUnavailable` is explicit. When the circuit breaker opens or the timeout
trips:

* `FailOpen` (default) — the request continues and a `content-safety-
  unavailable` audit row is written with `Action = failopen-passed`. Use for
  general-purpose or exploratory deployments where availability is more
  important than a temporary softening of the second layer.
* `FailClosed` — the request is refused with a distinct "Content Safety
  layer is temporarily unavailable" message and a `failclosed-blocked` audit
  row. Use for regulated deployments where the second layer is part of the
  compliance posture.

**Runbook — Content Safety unavailable.** Check the guardrails dashboard for
`content-safety-unavailable` blocks (fail-closed) or a spike of
`failopen-passed` rows (fail-open). The circuit breaker state is exposed by
the existing readiness health check (`contentSafetyCircuitState`); a
persistent `Open` value means the Azure region is degraded. Recovery is
automatic once the breaker's sampling window sees successes again — no
restart or configuration change is required.

### Language support

Prompt Shields is language-tuned for English at GA. Non-English payloads
are still analysed by the text-moderation categories (Hate, Sexual,
Violence, SelfHarm), but jailbreak detection quality varies. Regulated
deployments should treat Prompt Shields as one signal alongside the regex
jailbreak patterns rather than the sole defence, and review
`content-safety-prompt-shield` audit rows to calibrate.

### Telemetry

Every evaluator call emits a dedicated span:

| Stage               | Span name                                    |
| ------------------- | -------------------------------------------- |
| Input               | `guardrails.contentsafety.input`             |
| Output              | `guardrails.contentsafety.output`            |
| Retrieved knowledge | `guardrails.contentsafety.retrieved_knowledge`|
| Tool result         | `guardrails.contentsafety.tool_result`       |

Tag names include `guardrails.contentsafety.stage`,
`guardrails.contentsafety.decision`,
`guardrails.contentsafety.latency_ms`,
`guardrails.contentsafety.prompt_shield.jailbreak`,
`guardrails.contentsafety.prompt_shield.indirect`, and one
`guardrails.contentsafety.category.<hate|sexual|violence|selfharm>` tag per
category hit (value is the severity). Payload content is never included in a
span tag.

### Provisioning

`infra/modules/content-safety.bicep` provisions a `ContentSafety`
Cognitive Services account with `disableLocalAuth = true` and a
system-assigned managed identity. `main.bicep` includes the module only
when `contentSafetyEnabled = true`, so the default `azd up` is unchanged.
The postprovision hook (`azd-hooks/postprovision.{ps1,sh}`) grants each
container app's system identity the `Cognitive Services User` role on the
resource idempotently — a re-provision never duplicates the assignment.

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

### Frontend build mode & session-token lifecycle (Sprint 3)

The SPA renders exactly **one** provider's sign-in UX, selected at build time by
`VITE_AUTH_MODE` (`Entra` / `GitHub` / `Anonymous`), which mirrors the backend
`Authentication__Mode`; a deployment contract test proves the parity. Resolution is a
pure, **fail-closed** function (`src/RetailPulse.Web/src/auth/authMode.ts`): a missing
mode resolves to `Entra` only when safe (the SPA already carries Entra config, or an
explicit local-dev pass-through); a missing mode in a production build, or any unknown/
numeric value, **throws at load** — never a silent, insecure default. A single-mode
deployment renders only its configured provider (no provider chooser), minimizing
attack surface. The live build stays `Entra`.

- **One centralized credential path.** A single `SessionProvider` interface, active
  selector (`activeProvider.ts`), and token service feed both the global REST
  `authorizedFetch` and the SignalR `accessTokenFactory`; provider logic is never
  duplicated in components. `authorizedFetch` attaches the token **only** to our own
  `/api` paths on an **exact-origin** match (same-origin or the exact configured
  `VITE_API_ORIGIN`), rejecting userinfo-smuggling, lookalike hosts, scheme/port
  mismatches, `/hubs`, assets, and third parties — so a token is never sent to a
  lookalike or redirect target.
- **Narrow session-token storage.** The Retail Pulse session tokens minted for GitHub
  and Anonymous live in memory (source of truth) mirrored to `sessionStorage` for
  same-tab reload only — **never `localStorage`, never a broadly-readable cookie,
  never cross-tab**. They are cleared on logout / clear-session, on expiry, and on a
  401/403 from the API. The **GitHub provider token never reaches the browser** (the
  confidential BFF holds it server-side); the SPA only ever handles the one-time
  redemption code (stripped from the URL immediately via `history.replaceState`) and
  the resulting Retail Pulse session token.
- **SignalR gated by capability.** A build-time capability object disables the real-time
  hubs for Anonymous (`realtimeHub=false`): the Dashboard never starts SignalR and the
  hub token factory returns `''`. Entra and GitHub retain full hub access. This is a
  defense-in-depth usability layer — the backend remains authoritative and still `403`s
  any disallowed route regardless of what the UI renders.
- **Login start cannot be redirected.** GitHub login is a top-level navigation to the
  fixed same-origin `GET /api/auth/github/start`; no return/redirect URL is ever read
  from the query string or user input. Logout clears only our session token (no
  github.com provider-logout assumption).

### Development
- `Security:RequireAuth` defaults to `false`
- `Authentication:Mode` defaults to `Entra`, whose `DevelopmentAuthHandler`
  stamps a synthetic local identity. This bypass exists only in Development.
- No API key required.

### Production
- **Entra Bearer (primary):** `Authentication:Mode=Entra` and `Security:RequireAuth=true` are
  pinned by `infra/modules/container-apps.bicep`. All `/api/**` calls require an
  `Authorization: Bearer <token>` JWT issued by the tenant's Entra app registration; SignalR
  hubs accept the same token via `?access_token=...`. See
  [`authentication-entra.md`](authentication-entra.md) and
  [ADR-005](adr/005-provider-neutral-authentication.md) for the full flow.
- **JWT Bearer (Teams bot channel):** Teams-to-bot activity is validated by `TeamsSsoHandler`
  in the bot pipeline (independent of the SPA/API auth above).
- **Optional API key gate (MCP profile):** `ApiKeyAuthMiddleware` remains available as a pre-auth
  header check (`ApiKey:Enabled=true` + `ApiKey:Value=<secret>`, header name defaults to
  `X-Api-Key`). It is **disabled by default** on the API and is **not** enabled by the shipped
  Bicep — Entra bearer + `Security:RequireAuth=true` is the Production gate for the REST/SPA
  surface. The middleware exists specifically for the MCP server profile (`src/RetailPulse.McpServer`),
  where server-to-server callers pin a rotating shared secret in front of the tool endpoints; do
  not enable it on the public API.
- **Managed Identity:** For Azure OpenAI (via APIM) and other Azure resources — no client
  secrets in code.

---

## Rate Limiting

Four tiers of rate limiting (ASP.NET Core Rate Limiter):

| Policy | Limit | Applies To |
|--------|-------|-----------|
| `strict` | 10 req/min | `POST /api/chat`, `POST /api/chat/stream`, `POST /api/council/convene`, `POST /api/escalate` — AI-intensive routes |
| `moderate` | 30 req/min | State-changing endpoints (approvals, alerts, cards, guardrails config, knowledge deletes) |
| `relaxed` | 100 req/min | Read-only reporting endpoints (health, margin, observability lists, planogram gets) |
| `upload` | 5 req/min | `POST /api/knowledge/upload` — file / large-body upload endpoint |

Rate limit responses include `Retry-After` header. Policy names are declared in
`src/RetailPulse.Api/Program.cs` and referenced by `RequireRateLimiting(...)` on
each endpoint group in `src/RetailPulse.Api/Endpoints/`.

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
