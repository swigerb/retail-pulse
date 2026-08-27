# Security Hardening

> Security measures implemented in RetailPulse.

> **User authentication:** production access is gated by Microsoft Entra ID
> (single-tenant SPA/API app registration, MSAL PKCE, `RetailPulse.User` app role
> required on every protected endpoint and hub). See
> [authentication-entra.md](./authentication-entra.md) for the full boundary,
> environment contract, and provisioning scripts.

---

## Security Headers

`SecurityHeadersMiddleware`
([`src/RetailPulse.Api/Middleware/SecurityHeadersMiddleware.cs`](../src/RetailPulse.Api/Middleware/SecurityHeadersMiddleware.cs))
adds the following headers to every response:

| Header | Value | Purpose |
|--------|-------|---------|
| `Content-Security-Policy` | `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; img-src 'self' data:; connect-src 'self'` | Same-origin lockdown. `'unsafe-inline'` is granted to **styles only** — scripts are same-origin with no inline allowance. |
| `X-Content-Type-Options` | `nosniff` | Prevent MIME sniffing |
| `X-Frame-Options` | `DENY` | Prevent clickjacking |
| `Referrer-Policy` | `strict-origin-when-cross-origin` | Limit referrer leakage |
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=()` | Restrict browser APIs |

One header is conditional:

| Header | Value | Condition |
|--------|-------|-----------|
| `Strict-Transport-Security` | `max-age=31536000; includeSubDomains` | Emitted only when `Request.IsHttps`. Behind the Azure Container Apps ingress the in-container request is plain HTTP, so HSTS is asserted at the edge rather than by this middleware. |

Every header is written with `TryAdd`, so a value already set by an upstream
component or the hosting platform is preserved rather than overwritten.

`X-XSS-Protection` is deliberately **not** emitted. The header is deprecated,
and its legacy auditor is a known XSS-filter side-channel; the CSP above is the
control that matters. Do not add it back.

---

## Input Validation

All chat requests pass through `ChatRequestValidator`
([`src/RetailPulse.Api/Validation/ChatRequestValidator.cs`](../src/RetailPulse.Api/Validation/ChatRequestValidator.cs))
before the agent pipeline runs, so an oversized or malformed request is refused
**before** it is billed against a model:

| Rule | Limit | Error key |
|------|-------|-----------|
| Message present | Non-null, non-whitespace | `message` |
| Message length | Max 4000 characters | `message` |
| Session ID format | 1–64 characters, alphanumeric or hyphen (when supplied) | `sessionId` |
| History count | Max 50 prior turns | `history` |
| History entry length | Max 4000 characters per entry | `history[i]` |
| History aggregate | Max 100,000 characters across all entries | `history.aggregate` |
| Forced execution path | `fast` or `plan` only — `council` is router-controlled and never user-forceable | `forceExecutionPath` |

Validation failures return an RFC 7807 Problem Details payload via
`Results.ValidationProblem`, keyed by the field names above.

### What this layer does *not* do

This validator is a **shape and size** gate, not a content filter. Injection-style
payloads — `<script>`, `javascript:`, `'; DROP TABLE ...` — are **accepted** here
by design and are handled downstream by the guardrails layer described below.
`OwaspTests.A03_XssAndSqlInjection_DoNotCrashValidator` pins that contract
explicitly, so do not read the size caps as an anti-XSS control.

The three bounds on `history` exist because the array is caller-supplied prompt
context. Capping count, per-entry size, and aggregate size independently stops a
caller from smuggling a very large context past the per-message cap.

---

## Content Safety (optional second layer)

> **Status:** opt-in, and **enabled in the deployed demo environment**
> (`contentSafetyEnabled = true` in `infra/main.bicep` provisions the account and
> injects `Guardrails__ContentSafety__Endpoint`). It remains **off by default in
> code**, so a build with no endpoint configured runs the regex-only baseline
> unchanged. See
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

## Agent-definition validation gate

Agent and tenant configuration is validated at host startup, *before* any
agent or tool is constructed. The gate lives in
`RetailPulse.Api.Guardrails.AgentDefinition.AgentDefinitionValidator` and
runs against the hydrated `PromptConfiguration` while `Program.cs` is still
wiring services, in the same trust posture as ADR-008: `prompts.yaml` is
**trusted-at-load-time** deployment input, and this gate exists to catch
drift, mis-configuration, and a hostile prompt file that slipped in through
a bad deploy, not to open a new path for untrusted config to reach the
loader.

### Layered checks

1. **Structural.** Required fields, known tool references, models against
   the deployment-permitted list, temperature and other numeric bounds, and
   duplicate agent names.
2. **Policy.** Agent definitions cannot grant tools outside the deployment
   allow-list. Privileged write tools — currently `RequestApproval` and any
   future write tool such as `UpdateMetrics` — require an explicit
   `Guardrails:AgentDefinition:PrivilegedTools` grant that names the agent
   keys allowed to hold them. Definitions cannot self-assert them.
3. **Pattern layer.** Every system prompt and description is first run
   through `GuardrailPatterns` / `JailbreakDetector` so cheap regex hits
   short-circuit before we call an external service.
4. **Content Safety.** Anything that survived the pattern layer is sent
   through the configured `IContentSafetyEvaluator` on the
   `AgentDefinition` stage. Prompt-shield jailbreak and indirect-injection
   verdicts count as rejections. Text already blocked by the pattern layer
   is *not* forwarded — no double-billing, no leak to the third-party
   service.

### Failure policies

`Guardrails:AgentDefinition:OnValidationFailure` is `RefuseStartup`
(default) or `QuarantineOffender`. `RefuseStartup` collects every violation
across every agent and then throws a single
`AgentDefinitionValidationException`, so the container refuses to serve
rather than serve a partially trusted roster. `QuarantineOffender` removes
the offending agent keys from `PromptConfiguration.Agents`, emits a loud
`LogWarning` per removed key, and continues startup with the surviving
roster. Silent acceptance is impossible: every code path either throws or
quarantines.

### Audit contract

Every rejection writes a `SuspiciousRequest` row through the same
`ISuspiciousRequestLog` used at runtime, with:

- `UserContext = "startup-validator"` — operators can filter for load-time
  events distinct from user traffic;
- `DetectionType` one of `agent-definition-structural`,
  `agent-definition-policy`, `agent-definition-jailbreak`,
  `agent-definition-content-safety`, `agent-definition-privileged-grant`,
  `agent-definition-content-safety-unavailable`;
- `Action` = `blocked`, `quarantined`, or `failopen-passed`;
- diagnostics name the agent key, field, and rule id — the raw offending
  text is **never** written to the audit row or to logs.

Fail-open Content Safety unavailability is treated as an event of its own
(`agent-definition-content-safety-unavailable` / `failopen-passed`) so
operators can distinguish "we accepted this definition because the safety
service was down" from "we accepted this definition because it passed".
`FailClosed` rejects the definition and emits the same event as an
`agent-definition-content-safety` block.

### Content-Safety-disabled path — honest limits

With `Guardrails:ContentSafety:Enabled = false` (or with
`Guardrails:AgentDefinition:SafetyChecksEnabled = false`), structural,
policy, and pattern checks all still run — the load-time gate never has a
hard dependency on Azure Content Safety. The disabled path is documented
as pattern-only: plain-text `ignore previous instructions` still rejects,
but arbitrary encoded payloads (base64, homoglyph) that need a model to
decode will pass. Deployments that need the second-pass jailbreak coverage
must keep Content Safety enabled.

### Public projection

`/api/guardrails/config` exposes only the operator-facing knobs — the
failure policy, the safety toggle, and the temperature bounds. The
deployment allow-lists (models, tools) and the privileged-tool grants are
never surfaced through the API. Enforced by
`AgentDefinitionPolicyEndpointContractTests`.

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

## Session persistence

Durable server-side conversation history is available as an **opt-in** feature
behind the `SessionPersistence:Enabled` configuration switch. It follows the
same storage model as the other durable stores (`SqliteConversationMemory`,
`SqliteApprovalGate`, `SqliteAlertService`): a SQLite database in the shared
writable data directory, opened through the centralized `SqliteMount` helper
with SMB-safe pragmas (`busy_timeout=10000`, `journal_mode=DELETE`,
`synchronous=FULL`).

Conversation content is more sensitive than the memory facts we already
store, so the surface is deliberately narrow and every privacy control below
is a hard gate — none of them can be turned off with a config flag alone.

### Non-negotiable privacy controls

| Control | Where it lives | What it does |
|---------|---------------|--------------|
| **Anonymous callers never persist** | `ChatEndpoints` short-circuits writes via `IAnonymousChatPolicy`; `SessionEndpoints` refuse anonymous callers at entry with `403 session_persistence_unavailable`. | Matches the existing cache/memory-disabled rule for `Anonymous` mode. Proved by `AnonymousChatDoesNotPersistTests` using a strict-mock `ISessionStore`. |
| **Subject-scoped SQL** | Every read/delete WHERE-clause in `SqliteSessionStore` filters on the resolved subject. | A cross-subject read resolves to `null`, which the endpoint layer surfaces as `404`. The endpoint intentionally responds the same way for "unknown id" and "wrong owner" so it cannot be used as a probing oracle. |
| **Ownership pinned at first write** | `INSERT ... ON CONFLICT(SessionId) DO UPDATE ... WHERE Sessions.Subject = excluded.Subject`, plus a post-upsert ownership check inside the same transaction. | A replayed session id cannot change owners; a turn from a foreign subject is rolled back, not dropped as an orphan. |
| **Complete deletion** | `DELETE /api/sessions/{id}` removes `SessionTurns` rows before the parent `Sessions` row in a single transaction. | An interrupted purge cannot leave orphans, and the row is really gone — no soft-delete flag. |
| **PII redaction on write** | `SessionPersistence:RedactPiiOnWrite` (default `true`) routes each turn's `Content` through the shared `PiiRedactor`. | Redaction on write and redaction on display stay in lock-step because both use the same seam the output guardrail uses. |
| **Config kill-switch** | `SessionPersistence:Enabled=false` means no store singleton, no cleanup service, no DB file, and no `/api/sessions/*` routes. | Restores Wave 1 behaviour bit-for-bit. Verified by `SessionPersistenceServiceExtensionsTests.Disabled_DoesNotRegisterStore_OrCleanupService_OrTouchDisk`. |
| **Bounded retention** | `SessionCleanupBackgroundService` uses `TimeProvider` to purge sessions inactive for `RetentionTtl` (default 30 days) on a `CleanupInterval` cadence (default 1 hour). | Emits per-sweep row counts at `Information` so retention is observable in production logs. |

### Authorization

`/api/sessions`, `/api/sessions/{id}` (GET and DELETE) all require the
authenticated `RetailPulse.User` app role (via `RequireAuthorization()`).
They are not on the anonymous allowlist in
`AnonymousCapabilityPolicy._allowedRoutes`, so the deny-by-default anonymous
guard rejects them before the endpoint ever runs; the endpoint additionally
re-checks anonymity in case the mode ever changes. Rate-limiting policies
(`relaxed` for reads, `moderate` for delete) follow the existing endpoint
conventions.

### Storage lifetime

The deployed demo now sets `SessionPersistence__Enabled=true` (alongside
`PlanPersistence__Enabled` and `PlanReview__Enabled`) so the full capability
surface is exercisable end to end.

That environment still runs on **ephemeral per-replica storage**
(`RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE=true`; see the deployment note in
`docs/architecture.md`), so this is a deliberate, bounded trade: session and plan
rows survive within a warm replica and **reset on revision change, replica
replacement, or scale-to-zero**. For a demo the store is a live view of the
current session, not a system of record — do not present it as durable history.

Once a policy-compatible durable volume is available, the same flag joins
`sessions.db` to the other durable stores under that mount with no code change.

---

## Authentication

### Provider selection (mode contract)

The active identity provider is chosen through the `Authentication:Mode`
configuration key (`Authentication__Mode`). Resolution is deterministic and
fails closed — it never auto-detects a provider:

- `Entra` (the production mode) routes to the unchanged Entra boundary.
- `Anonymous` is an **opt-in, fail-closed** capability that is **not deployed by
  default** (hosted only behind an explicit `Anonymous:AllowHosted=true` opt-in) — see
  [Anonymous mode](#anonymous-mode-opt-in-fail-closed) below.
- `GitHub` is an **opt-in, fail-closed** confidential OAuth
  backend-for-frontend capability that is **not deployed** — see
  [GitHub mode](#github-mode-confidential-oauth-bff-opt-in-fail-closed) below.
- A missing mode defaults to `Entra` **only in Development**. Outside
  Development a missing, unknown, or malformed mode fails startup.

Production pins `Authentication__Mode=Entra` explicitly in
`appsettings.Production.json` and the azd postprovision hooks. See
[ADR-005](adr/005-provider-neutral-authentication.md) and the
[authentication matrix](authentication-matrix.md).

### Anonymous mode (opt-in, fail-closed)

Anonymous mode lets a self-serve frontend reach a **single chat capability** —
authenticated `POST /api/chat` — without an identity provider.
It is additive and fail-closed. The **SignalR hubs are NOT part of the
anonymous surface**: anonymous sessions have no real-time telemetry or token
streaming, and a valid anonymous token is denied `403` on both hubs. Hosted
Anonymous **is permitted only behind an explicit
opt-in** (`Anonymous:AllowHosted=true`); by default it is never deployed, and the
**live deployment artifacts stay Entra**, proven so by deployment-contract tests.

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

GitHub mode lets a self-serve frontend sign in with GitHub
without exposing a GitHub provider token to the browser. It is additive and
fail-closed, and the **live deployment artifacts stay Entra** (proven by
deployment-contract tests). It is **not deployed**.

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

### Frontend build mode & session-token lifecycle

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
- **API key gate (MCP profile only):** `ApiKeyAuthMiddleware` is a pre-auth header check
  (`ApiKey:Enabled=true` + `ApiKey:Value=<secret>`, header name defaults to `X-Api-Key`).
  It is **disabled by default on the API** and is **not** enabled there by the shipped
  Bicep — Entra bearer + `Security:RequireAuth=true` is the Production gate for the
  REST/SPA surface. Do not enable it on the public API.

  It **is** enabled on the MCP server (`src/RetailPulse.McpServer`), where
  server-to-server callers present a shared secret in front of the tool endpoints.
  See [MCP server boundary](#mcp-server-boundary) below.
- **Managed Identity:** For Azure OpenAI (via APIM) and other Azure resources — no client
  secrets in code.

### App-only (client-credentials) tokens — opt-in, fail-closed

By default the API accepts only **delegated** (user) tokens — a token bearing the
`RetailPulse.User` app role AND the `access_as_user` scope. An **app-only**
(client-credentials) token — which carries `roles` but no `scp` — is rejected
`403`. That is the default posture and is preserved bit-for-bit for every
unset deployment. It matches the ADR-005 guardrails: **"Live
production stays Entra only and fails closed"**, **"No silent downgrade"**.

Machine callers — for example the optional authenticated synthetic chat monitor
described in
[`testing/authenticated-synthetic-monitor.md`](testing/authenticated-synthetic-monitor.md)
— need an app-only token, so the Entra boundary supports an **opt-in** app-only
path. It is additive, narrow, and fail-closed:

- **Disabled by default.** Unset configuration behaves exactly as it did
  before this feature existed.
- **Delegated behaviour unchanged.** Enabling the flag does not alter the
  delegated (user) path in any way — same role check, same scope check, same
  claim shapes.
- **Narrow scope.** Only tokens bearing the configured app role are accepted,
  and both SignalR hubs plus every REST endpoint remain protected. The
  anonymous-session hardening is untouched. There is no reusable general
  scope-bypass path that a future endpoint could inherit unintentionally.
- **Fail closed.** A malformed or incomplete opt-in configuration (placeholder
  or non-GUID entry in the allow-list, blank app role) fails startup — never a
  warning-and-continue, never a fallback to a weaker policy.
- **Client-ID allow-list is optional but supported.** Populating
  `MicrosoftEntra:AllowedAppClientIds` requires the token's `azp` (v2) or
  `appid` (v1) claim to match one of the listed GUIDs, so consenting an
  unrelated application to `RetailPulse.User` does not automatically grant it
  API access.

#### Configuration keys

All keys live in the `MicrosoftEntra` section (env-var form
`MicrosoftEntra__...`). Existing `AppRole` and `ApiScope` values are unchanged.

| Key | Type | Default | Purpose |
|-----|------|---------|---------|
| `MicrosoftEntra:AllowAppOnlyTokens` | bool | **`false`** | Master opt-in. When `false`, every app-only token is rejected `403` (this matches the shipped Bicep and is the default posture). When `true`, an app-only token bearing the required app role is accepted — subject to the optional allow-list below. |
| `MicrosoftEntra:AllowedAppClientIds` | string[] (config-array) | **empty** | Optional allow-list of application (client) IDs (Entra service-principal client IDs) permitted to authenticate via app-only tokens. Empty means the app role alone gates access. When populated, the token's `azp` (v2) or `appid` (v1) claim MUST match one of the listed GUIDs. |
| `MicrosoftEntra:AppRole` | string | `RetailPulse.User` | The role required on **every** authenticated request. In the app-only path this is the primary gate — a blank or placeholder value fails startup when the opt-in is on. |

#### Startup validation (fail-closed)

`EntraAuthOptions.FromConfiguration` runs `ValidateAppOnlyOptIn()` whenever
`AllowAppOnlyTokens=true`, in **every** environment (Development included). Any
of the following throws `InvalidOperationException` before authentication is
registered:

- A blank `AppRole` on the resolved options (a documentation placeholder is
  scrubbed and the default `RetailPulse.User` kicks in — this catches the
  direct-construction case).
- Any entry in `AllowedAppClientIds` that is blank or looks like a
  documentation placeholder (`<...>`).
- Any entry in `AllowedAppClientIds` that is not a valid GUID.

Development is not exempt: a typo that slips past a local build would otherwise
propagate to a deployed environment. The opt-in must be spelled right or the
process refuses to start.

#### How each token shape is evaluated

The dual-mode assertion (`AuthenticationSetup.IsAuthorizedPrincipal`) is a
strict branch on token type — there is no fallback and no "or":

- Token carries `scp` (delegated) → require the configured API scope
  (`HasRequiredScope`). This branch is unaffected by the app-only opt-in.
- Token carries `roles` and NO `scp` (app-only) → require
  `AllowAppOnlyTokens=true` AND, if the allow-list is populated, the token's
  `azp`/`appid` matches an allow-listed GUID. The `RequireRole` requirement
  on the policy handles the role check for both branches.
- Token carries neither `scp` nor `roles` → deny.

#### Client-ID / object-ID allow-list decision

**Decision:** support an **optional** `MicrosoftEntra:AllowedAppClientIds`
allow-list keyed on the token's `azp` (v2) / `appid` (v1) claim, with an empty
list meaning "no client-ID restriction". Object-ID (`oid`) allow-listing was
considered and deliberately not adopted.

**Reasoning:**

- Entra emits `azp` on v2 tokens and `appid` on v1 tokens; both are the
  Microsoft-documented "authorized party" identifier — the client ID of the
  application that obtained the token. Matching on this claim maps directly to
  the identity a tenant admin registers and consents in Entra, so operators can
  reason about who is on the allow-list without decoding token internals.
- The service-principal object ID (`oid`) also uniquely identifies the caller
  within a tenant, but it is one indirection removed from the identity operators
  actually manage (the app registration). Using `azp`/`appid` keeps the
  configuration and audit trail aligned with the Entra admin experience.
- Making the list optional preserves the single-tenant default (role assignment
  + admin consent is already tenant-admin-gated), while giving deployments
  defense-in-depth when they want it — critical because the `RetailPulse.User`
  app role allows `Users/Groups,Applications` and any admin-consented app in
  the tenant could otherwise gain API access silently.
- Any entry that is not a valid GUID fails startup, so a typo can never silently
  disable the restriction. Comparison is GUID-normalized so case/format
  differences cannot bypass the check.

---

## Rate Limiting

Four general tiers apply to the always-on API surface (ASP.NET Core Rate
Limiter, fixed window, `QueueLimit = 0` so an over-limit request is rejected
immediately rather than queued):

| Policy | Limit | Applies To |
|--------|-------|-----------|
| `strict` | 10 req/min | `POST /api/chat`, `POST /api/chat/stream`, `POST /api/council/convene`, `POST /api/escalate` — AI-intensive routes |
| `moderate` | 30 req/min | State-changing endpoints (approvals, alerts, cards, guardrails config, knowledge deletes) |
| `relaxed` | 100 req/min | Read-only reporting endpoints (health, margin, observability lists, planogram gets) |
| `upload` | 5 req/min | `POST /api/knowledge/upload` — file / large-body upload endpoint |

Three further policies exist for the opt-in providers. They are always
registered so the limiter graph stays stable and testable, but they only bind to
routes when the matching provider mode is active:

| Policy | Default | Config key |
|--------|---------|-----------|
| `anonymous-bootstrap` | 5 req/min, **global per replica** (not per IP — see the ACA proxy note above) | `Anonymous:Bootstrap:GlobalPerMinute` |
| `github-start` | 10 req/min | `GitHub:RateLimits:StartPerMinute` |
| `github-exchange` | 20 req/min | `GitHub:RateLimits:ExchangePerMinute` |

Rejections return `429` with a `Retry-After` header. Policies are declared in
`src/RetailPulse.Api/Security/RateLimitingSetup.cs` and referenced by
`RequireRateLimiting(...)` on each endpoint group in
`src/RetailPulse.Api/Endpoints/`. They live in that setup class rather than inline
in `Program.cs` specifically so `RateLimitingConfigTests` can exercise the real
limits — do not inline them back.

---

## OWASP Coverage

OWASP behaviour is pinned by tests tagged `[Trait("OWASP", ...)]`, so a category can
be run in isolation with `dotnet test --filter "OWASP=A01-BrokenAccessControl"`.
The tags sit on the suite that genuinely exercises each control rather than on a
single summary file, so the filter runs real coverage:

| OWASP | Category | What is actually asserted | Where |
|-------|----------|---------------------------|-------|
| A01 | Broken Access Control | The real `EndpointDataSource` is walked and every `/api` and `/hubs` route must carry authorization metadata, with a deny-by-default fallback policy and a fixture proving the detector catches an unannotated endpoint. Deployment-side: the MCP server must not be publicly exposed and its API-key gate must be on. | `EndpointAuthorizationCoverageTests`, `ContainerAppDeploymentContractTests` |
| A02 | Cryptographic Failures | Shared secrets are `@secure()` Bicep parameters delivered by `secretRef`, never literals. | `ContainerAppDeploymentContractTests` |
| A03 | Injection | Oversized messages and malformed session IDs are rejected; XSS/SQL payloads in the message body are *accepted* by the validator and deferred to guardrails (see [What this layer does *not* do](#what-this-layer-does-not-do)). | `OwaspTests` |
| A05 | Security Misconfiguration | The security headers above are present on a real response and HSTS appears only on HTTPS. No container app runs as `Development`, and no ingress accepts plaintext HTTP. | `OwaspTests`, `ContainerAppDeploymentContractTests` |
| A07 | Authentication Failures | Real traffic is driven through the production rate-limiter registration: each policy admits exactly its permit limit and then returns `429`. The `anonymous-bootstrap` and `github-start` windows are proven global — rotating a forged `X-Forwarded-For` grants no extra capacity. | `RateLimitingConfigTests` |

### Why the tags moved

The A01 and A07 entries previously pointed at tests that asserted over locally
declared literals — one reduced to `10 <= 20` — and therefore passed no matter how
the application behaved. Raising the chat rate limit from 10/min to 999,999/min
left the entire suite green. Those tests were removed and the tags moved to the
suites above, which fail on that mutation.

A test that restates its own expectations is worse than no test: it advertises
coverage that does not exist. Any new OWASP-tagged test must exercise production
code or a real deployment artifact.

### Not covered

A04, A06, A08, A09 and A10 have no dedicated automated tests. They were assessed
manually in a full-repository security review and found clean — parameterised SQL
throughout, no vulnerable NuGet packages, CI workflows free of
`pull_request_target` and script injection, no secrets or PII reaching logs or
span tags, and no user-controlled outbound hosts — but that assessment is a
point-in-time judgement, not a regression gate.

---

## MCP server boundary

The MCP server hosts the tool transport (`/mcp`) and the REST data endpoints the
API's tools call. It is a **server-to-server dependency of the API and never a
browser-facing surface**. Three controls keep it that way, and all three are
asserted by `ContainerAppDeploymentContractTests`:

| Control | Where | Effect |
|---------|-------|--------|
| Internal ingress | `external: false` in `infra/modules/container-apps.bicep` | Addressable only from inside the Container Apps environment. Not resolvable or reachable from the public internet. |
| Production environment | `ASPNETCORE_ENVIRONMENT=Production` | Enables the API-key gate and suppresses the OpenAPI document. |
| API-key gate | `ApiKey__Enabled=true` + `ApiKey__Value` via `secretRef` | Every `/api` and `/mcp` request must present a matching `X-Api-Key`, compared with `CryptographicOperations.FixedTimeEquals`. |

The API presents the key on its named `McpServer` `HttpClient` and **fails closed at
startup** outside Development if `McpServer:ApiKey` is missing, so a
misconfiguration surfaces as a boot failure rather than as `401`s at request time.

> **Do not set the MCP server's `ASPNETCORE_ENVIRONMENT` to `Development` in any
> deployed environment.** Its gate is written as
> `apiKeyRequired = !IsDevelopment() || ApiKey:Enabled`, so Development silently
> disables authentication entirely. The same pattern applies to the Teams bot, whose
> messaging endpoints are mapped with
> `MapAgentApplicationEndpoints(requireAuth: !IsDevelopment())`. Both were once
> deployed as `Development` behind a public ingress, which left the full MCP REST and
> tool surface callable from the internet with no credential. That is the specific
> regression the deployment-contract tests exist to prevent.

---

## Secrets Management

- **Never** committed to source control; `.gitignore` excludes all secret files.
- **Local development:** user secrets (`dotnet user-secrets`).
- **Azure resources** (Azure OpenAI via APIM, Content Safety, ACR pulls): a
  **system-assigned managed identity** per container app, with role assignments
  granted by the azd postprovision hook. No connection strings, no client secrets.
- **The shared secrets** — the APIM subscription key, and the key the API presents
  to the MCP server as `X-Api-Key` — are stored as **Container Apps secrets** and
  injected by `secretRef` in `infra/modules/container-apps.bicep`. Neither is baked
  into an image or a plain environment variable.

There is **no Azure Key Vault** in this deployment. The Container Apps secret
store plus managed identity covers the current secret surface, so a Key Vault
would add a resource, a network dependency, and a second access-control plane
without removing a secret. If a future secret cannot be replaced by managed
identity, revisit this — but do not document Key Vault as present until a module
actually provisions it.
