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
- `GitHub` is declared but not implemented in this sprint; selecting it fails
  startup and never falls through to another provider.
- A missing mode defaults to `Entra` **only in Development**. Outside
  Development a missing, unknown, or malformed mode fails startup.

Production pins `Authentication__Mode=Entra` explicitly in
`appsettings.Production.json` and the azd postprovision hooks. See
[ADR-005](adr/005-provider-neutral-authentication.md) and the
[authentication matrix](authentication-matrix.md).

### Anonymous mode (opt-in, fail-closed)

Anonymous mode lets a future self-serve frontend (Sprint 3) reach a **single
chat capability** — authenticated `POST /api/chat` plus the two SignalR hubs that
carry that chat's telemetry/streaming — without an identity provider. It is
additive and fail-closed. Hosted Anonymous **is permitted only behind an explicit
opt-in** (`Anonymous:AllowHosted=true`); by default it is never deployed, and the
**live deployment artifacts stay Entra**, proven so by deployment-contract tests.
It is not deployed this sprint.

- **Smallest useful surface (deny-by-default).** The anonymous surface is exactly
  three things: the unauthenticated bootstrap, authenticated `POST /api/chat`, and
  the `/hubs/telemetry` + `/hubs/streaming` hubs. **Every other (method, route) is
  `403`** — there is **no blanket GET allowance**. Observability, admin, export,
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
- **Hub session ownership (Finding 6).** For an anonymous caller a hub `JoinSession`
  binds/verifies the session against the caller's immutable subject, so an anonymous
  attacker cannot subscribe to another subject's telemetry/stream even with a known
  session id. Card/approval hub groups are refused for Anonymous. Entra hub
  behaviour is unchanged.
- **ACA proxy / per-IP limitation.** Behind the Azure Container Apps ingress the
  connection remote IP is the **proxy's**, not the client's, and `X-Forwarded-For`
  is **not** trusted (it is forgeable and ACA gives no cryptographically verifiable
  client-IP header). Per-IP limits therefore collapse to a global bucket in
  practice; bootstrap is intentionally a single **global** conservative window and
  the **primary** abuse control is the **per-subject** limit applied after bootstrap.
- **Limitation.** Ceilings, rate-limit windows, and the hub session-ownership
  registry are **replica-local, in-memory**, so hosted Anonymous is pinned to
  `maxReplicas=1` with conservative limits and is **not** equivalent to
  authenticated production; **all of these counters/bindings reset on restart or
  replica replacement**.

### Development
- `DevelopmentAuthHandler` bypasses all authentication
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
