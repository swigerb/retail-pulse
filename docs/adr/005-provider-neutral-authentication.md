# ADR-005: Provider-neutral authentication foundation

## Status

Accepted (Sprint 0 — foundation only; no live behavior change).
Extended by the **Sprint 1 addendum** below (Anonymous mode implemented; still
opt-in, fail-closed, and never deployed — Production stays Entra).
Extended by the **Sprint 2 addendum** below (GitHub confidential OAuth BFF mode
implemented; still opt-in, fail-closed, and never deployed — Production stays
Entra).

## Context

Retail Pulse authenticates every protected REST endpoint and both SignalR hubs
with Microsoft Entra ID. The wiring is hard-coded: `Program.cs` calls
`AddRetailPulseAuthentication` / `AddRetailPulseAuthorization` directly, and the
only branch is the local `DevelopmentAuthHandler` used when the API runs in the
Development environment. This is secure and well-tested, but it assumes a single
identity provider forever.

The squad wants to run Retail Pulse in environments where Entra is not the right
fit — for example a public GitHub-authenticated demo, or a fully anonymous
read-only sandbox — without forking the codebase or rewriting the security stack
each time. That requires a **configurable, provider-neutral** authentication
foundation.

The hard constraint is that **live production behavior must not change**.
Production is Entra-only and fails closed. GitHub and Anonymous are opt-in
capabilities for other environments; they are declared in this sprint but must
not be selectable in production, and must not exist as a runnable code path yet.

### Current Entra architecture

The security boundary lives in `src/RetailPulse.Api/Security/`:

- `AuthenticationSetup.AddRetailPulseAuthentication(configuration, environment)`
  registers the `JwtBearer` handler (Production) or the `DevelopmentAuthHandler`
  (Development) and returns a validated `EntraAuthOptions`.
- `AddRetailPulseAuthorization(options)` sets both the default and fallback
  authorization policy to deny-by-default. The policy requires an authenticated
  user **plus** the `RetailPulse.User` app role (`roles` claim) **plus** the
  `access_as_user` scope (`scp` claim).
- `EntraAuthOptions.FromConfiguration` fails closed outside Development:
  `Security:RequireAuth` cannot be `false`, tenant and audience are required, and
  angle-bracket placeholder values are rejected.
- The `access_token` query-string parameter is honored **only** on `/hubs/*`
  requests; REST requires the `Authorization: Bearer` header.
- `UserIdentity.Resolve` derives the caller's immutable subject from the token's
  `oid` claim (claim-first, never a client-supplied header), which is the
  anti-spoofing rule recorded in `.squad/decisions.md`.
- Health probes (`/health`, `/alive`) stay anonymous through explicit
  `AllowAnonymous` metadata. The app sets both `DefaultPolicy` and
  `FallbackPolicy`; a runtime endpoint-graph test guards every `/api` and
  `/hubs` route against missing authorization metadata.

## Decision

Introduce a thin, strongly typed **mode contract** and a single **factory
boundary** in front of the existing Entra wiring. Route the current Entra
implementation through an explicit `Entra` mode without changing any of its
security semantics.

### Proposed provider strategy

Three pieces, all additive:

1. **`AuthenticationMode` enum** — `Entra = 0`, `GitHub = 1`, `Anonymous = 2`.
   The named, documented selector. Numeric values are never used for selection
   (see the resolver).

2. **`AuthenticationModeOptions.Resolve(configuration, environment)`** — the
   deterministic, fail-closed resolver. It reads the `Authentication:Mode`
   configuration key (`Authentication__Mode` as an environment variable) and:
   - honors an explicit recognized mode (case-insensitive `Entra`, `GitHub`,
     `Anonymous`);
   - defaults a missing/blank mode to `Entra` **only in Development**, preserving
     the existing local demo experience — a documented default, not inference;
   - throws at startup for a missing/blank mode outside Development;
   - throws at startup for an unknown value **or a bare number** (so `"1"` can
     never select GitHub).

3. **`ProviderNeutralAuthentication.AddProviderNeutralAuthentication(...)`** — the
   factory boundary and the single entry point `Program.cs` calls. It resolves
   the mode and dispatches:
   - `Entra` → registers the resolved mode for diagnostics, calls the unchanged
     `AddRetailPulseAuthentication`, registers the `IPrincipalNormalizer`, and
     returns `EntraAuthOptions` for the caller to wire the authorization policy;
   - `Anonymous` → wires the anonymous session scheme, the constrained
     authorization policy, and all billable-use guardrails internally, then
     returns `null` (implemented in Sprint 1 — see the addendum below);
   - `GitHub` → throws `NotSupportedException` **before any authentication scheme
     is registered**, so the app can never fall through to Entra, Development, or
     anonymous access.

This is a deliberate two-layer split: the resolver classifies *unknown/malformed*
input as a fail-closed error, while the factory classifies a *known-but-
unimplemented* mode (GitHub) as a distinct fail-closed error. The two failure
classes carry different, precise messages.

### Normalized identity and claims model

A provider-neutral principal contract lets later providers map their native
claims into one shape without weakening the Entra requirements:

- `NormalizedPrincipal` — an immutable record: `Provider`, `Subject` (immutable),
  `DisplayName`, `Roles`, `Scopes`.
- `IPrincipalNormalizer` — `{ AuthenticationMode Mode; NormalizedPrincipal
  Normalize(ClaimsPrincipal); }`.
- `EntraPrincipalNormalizer` — maps Entra claims: `Subject` via
  `UserIdentity.Resolve` (the `oid`, claim-first), `DisplayName` from `name` /
  `ClaimTypes.Name`, `Roles` from `roles` + `ClaimTypes.Role` (deduped), `Scopes`
  from `scp` + the long-schema scope claim split on spaces.

The normalizer is **registered and exercised by tests today** — not a dead
abstraction. It does not participate in the authorization decision; the existing
role + scope policy remains the sole gate.

### REST and SignalR enforcement

Unchanged. The `Entra` path produces the same `EntraAuthOptions` the app has
always used, so the same deny-by-default policy applies to every REST endpoint
and to both hubs, and `?access_token` remains hub-only. The foundation adds a
selector in front of this wiring; it does not touch the wiring itself.

### Live-production invariant

> In Production, the only supported authentication mode is `Entra`, and it
> behaves exactly as it did before this foundation existed. Production pins
> `Authentication__Mode=Entra` explicitly in three independent places, and any
> missing, unknown, malformed, or unimplemented mode fails startup. No production
> code path enables GitHub or Anonymous.

The three explicit pins are belt-and-suspenders:

1. `src/RetailPulse.Api/appsettings.json` — base `Authentication:Mode = Entra`
   (deterministic default for local runs).
2. `src/RetailPulse.Api/appsettings.Production.json` — explicit Production pin.
3. `azd-hooks/postprovision.ps1` / `.sh` — deploy-time `Authentication__Mode=Entra`
   re-assertion on the container app, alongside `Security__RequireAuth=true`.

### What changes and what stays

**Changes (all additive / non-behavioral):**

- New `AuthenticationMode`, `AuthenticationModeOptions`,
  `ProviderNeutralAuthentication`, `NormalizedPrincipal`, `IPrincipalNormalizer`,
  `EntraPrincipalNormalizer`.
- `Program.cs` calls `AddProviderNeutralAuthentication` instead of
  `AddRetailPulseAuthentication` directly. The Entra path returns the identical
  `EntraAuthOptions`.
- `appsettings.json`, `appsettings.Production.json`, and both azd hooks pin
  `Authentication__Mode=Entra`.

**Stays exactly the same:**

- `AuthenticationSetup`, `EntraAuthOptions`, `UserIdentity`,
  `DevelopmentAuthHandler`, and the authorization policy — untouched. The
  signature of `AddRetailPulseAuthentication` is preserved so existing tests keep
  calling it directly.
- REST + hub enforcement, `?access_token` hub-only rule, health-only anonymous
  invariant, and Production fail-closed configuration.

## Sprint 1 addendum: Anonymous mode (implemented)

Sprint 1 implements the `Anonymous` mode dispatched by the factory above. It is
additive, opt-in, and fail-closed; the live/deployed configuration stays `Entra`
and is proven to stay `Entra` by the deployment-contract tests. Anonymous mode is
**not deployed** this sprint. Child issue #30, epic #27.

### Explicit activation and fail-closed hosted opt-in

- `Authentication:Mode=Anonymous` is the only switch; no auto-detection/fallback.
- **Development** may enable it with an ephemeral, process-local HMAC signing key
  (generated once per process). Sessions do not survive a restart — this is
  documented, intentional dev behavior, never a hosted fallback.
- **Any hosted (non-Development) Anonymous deployment** additionally requires a
  SECOND explicit opt-in, `Anonymous:AllowHosted=true`, PLUS a complete validated
  guardrail set: a strong configured signing key (≥ 256-bit) and positive daily
  request/token/cost ceilings. Missing/malformed/unsafe values throw at startup
  (`AnonymousAuthOptions.FromConfiguration` / `Validate`), so a misconfigured
  hosted deploy never serves traffic.

### Server-minted session identity (no trusted client header)

- Identity is never taken from a caller-supplied user id. A single, narrowly
  scoped, globally rate-limited bootstrap endpoint
  (`POST /api/auth/anonymous/session`) mints a fresh, cryptographically random
  per-session subject (`anon-<base64url>`) SERVER-SIDE.
- The credential is a compact, short-lived (default 15 min) **HS256 session JWT**
  with issuer, audience, `provider=Anonymous`, subject, role
  (`RetailPulse.Anonymous`), scope (`chat_limited`), strict expiry, and a random
  `jti` — **no PII and no refresh token**. The client re-bootstraps when the TTL
  elapses, which bounds the replay window to the TTL.
- The same token is usable as a REST `Authorization: Bearer` and as a SignalR
  `?access_token` (query token honored **only** on `/hubs/*`). Genuine signing-key
  rotation is supported (`AnonymousSigningKeyProvider.ValidationKeys` /
  `Anonymous:AdditionalValidationKeys`): validation-only keys keep pre-rotation
  tokens valid while the current key signs, parity with GitHub mode. The signing
  key is a secret and is never committed.

### Authorization and normalized principal

- A dedicated authorization policy (registered as both default and fallback)
  requires an authenticated user, the anonymous role, the anonymous scope, and
  `provider==Anonymous`. An Entra or any cross-provider token can therefore never
  satisfy the anonymous policy (authenticated-but-unauthorized → 403).
- `AnonymousPrincipalNormalizer` projects the token to `provider=Anonymous`, the
  immutable random subject, and the constrained role/scope — the same
  `IPrincipalNormalizer` seam Entra uses.
- Only `/health`, `/alive`, and the bootstrap endpoint are unauthenticated.
- Sessions are isolated by their random subject; no identity state is shared
  across sessions.

### Deny-by-default capability allowlist (single source of truth)

- `AnonymousCapabilityPolicy` is data, not UI hiding, and it is **deny-by-default
  over every (method, route)** — there is no read/GET shortcut. For Sprint 1 the
  allowlist is reduced to exactly **two routes**: the unauthenticated bootstrap and
  authenticated `POST /api/chat` (plus `OPTIONS` preflight). Everything
  else — all observability/admin/export/memory/cards/approvals/guardrail-log routes,
  every broad GET, **both SignalR hubs** (`/hubs/telemetry`, `/hubs/streaming` — at
  connection and negotiate), and every **alternate LLM/orchestrator route**
  (`/api/chat/stream`, `/api/council/*`, message-extension, scorecard, escalation) —
  returns `403`. A newly added endpoint is denied to anonymous callers unless it is
  explicitly allowlisted, and a runtime endpoint-graph test enumerates the compiled
  route table to prove it.
- **The hubs are removed from the anonymous surface (Sprint 1 scope reduction).**
  Anonymous sessions get **no real-time telemetry or token streaming**; a valid
  anonymous token is denied `403` on both hubs by the guard, and the Sprint 3
  frontend does not start the hubs in anonymous mode. This retires the whole class of
  hub exposures (global `Clients.All` broadcast reach and cross-subject group
  namespace collisions) without touching the Entra hub telemetry path. Entra hub
  behaviour is unchanged.
- Read-only data (e.g. charts) is reached **through the filtered chat tool path**,
  not a direct operator endpoint, so subject-scoping and the output/budget controls
  always apply.
- Because only the single `AgentExecutionPipeline` behind `POST /api/chat` is
  reachable, **all model use is accounted through one metered chokepoint** — there is
  no alternate billable route or orchestrator to escape the output-token cap, the
  cost/token metering, or the write-capable-tool filter.
- The chat tool set is filtered so write-capable tools (today: `RequestApproval`,
  `RememberPreference`, `SaveMemory`) are never registered for an anonymous
  principal — the model cannot invoke a write path.
- **Chat-internal intent hard-stops (not reachable by tool filtering).** Two in-process
  paths do not go through the AI tool set, so the write-tool filter alone cannot stop
  them; the endpoint therefore refuses them by the router's own classification, before
  specialist selection/execution:
  - **Memory management** — the `MemoryManagementAgent` calls
    `IConversationMemory.StoreAsync`/`ForgetAsync` **directly** (no tools). An anonymous
    turn classified as `AgentIntent.MemoryManagement` (e.g. `remember that ...`,
    `forget everything`) returns a standard safe refusal — the agent never runs, no
    model is called, and zero memory rows are written.
  - **Consensus council / portfolio health** — the council interception convenes
    `IConsensusCouncil` (a fan-out of model calls) and returns **early**, which would
    bypass the single accounted budget/audit/guardrail path. An anonymous turn
    classified as `AgentIntent.PortfolioHealth` is refused before the council is
    convened, so `ConveneAsync` is never called. The council interception is the only
    in-process alternate orchestrator reachable from `POST /api/chat`; the scorecard
    and escalation orchestrators are not registered as specialist agents and are
    reachable only via their own `/api` routes, which are already `403`.

### Provider-aware identity, disabled memory and cache

- Identity is resolved per provider by `UserIdentity.Resolve`: **Anonymous → the
  immutable server-minted `sub`; Entra → the immutable `oid`.** A request-body
  `objectId` is **never** honoured for any authenticated principal, closing the
  cross-session spoof/conflation gap (previously the resolver could key an
  authenticated principal to a spoofable body value).
- **Memory is disabled for Anonymous** — no recall, write, or extraction — which
  eliminates stored cross-prompt injection across anonymous sessions in Sprint 1.
- The **response cache is disabled for Anonymous** (both read and write), so two
  subjects issuing an identical prompt always execute independently and can never
  share a reply.
- **Hubs are not part of the anonymous surface (Sprint 1).** The former anonymous hub
  session-ownership binding is moot because a valid anonymous token is denied `403` on
  both hubs before any hub method runs. This removes the cross-subject
  telemetry/stream subscription risk entirely rather than mitigating it inside the hub.
  Entra/dev hub behaviour is unchanged.

### Billable-use safeguards

- `AnonymousGuardMiddleware` centrally enforces: the deny-by-default route allowlist
  (403), request-size bound (413 — set before the body is read **and** via a
  length-counting pre-read, so a chunked/unknown-length body without
  `Content-Length` cannot bypass it), per-subject and per-IP minute limits (429), a
  per-request timeout (504), and a global daily request/token/cost **circuit
  breaker** (503, fail-closed). History length is bounded per-message and in
  aggregate by `ChatRequestValidator` (400 before the model).
- **ACA proxy / X-Forwarded-For:** behind the Container Apps ingress the connection
  remote IP is the proxy's, not the client's, and `X-Forwarded-For` is **not**
  trusted (forgeable; no cryptographically verifiable client-IP header). The bootstrap
  limiter is therefore a per-replica **global** fixed window (config key
  `Anonymous:Bootstrap:GlobalPerMinute`, conservative default `5`; the legacy
  `Anonymous:Bootstrap:PerIpPerMinute` key is still honoured as a
  backward-compatible fallback), and the **primary** post-bootstrap control is the
  per-subject limit.
- Accounting is truthful: a request slot is charged **at admission — before the
  cache is consulted** — so cache hits cannot bypass the request ceiling; token
  and cost are metered by an `ICostTracker` decorator (`AnonymousBudgetCostTracker`)
  where cache hits cost zero. Output tokens are capped for anonymous chat.
- **Replica-local limitation (disclosed):** the ceilings and rate-limit windows are
  enforced in replica-local memory. Hosted
  Anonymous is therefore pinned to `maxReplicas=1`
  (rationale comment in `infra/modules/container-apps.bicep`) and the ceilings are
  conservative; **all of these counters reset on restart or replica
  replacement.** This is explicitly **NOT** equivalent to authenticated production.

### Sprint 1 threat model additions

| Threat | Mitigation |
|--------|------------|
| Spoofed subject via a client header/body | Identity is the signed token subject minted server-side; `UserIdentity.Resolve` is provider-aware and never honours a request-body `objectId` for an authenticated principal. |
| Token replay after expiry | Strict short TTL + `ValidateLifetime`; no refresh token; the replay window is bounded by the TTL. |
| Query token smuggled onto a REST path | `?access_token` is honored only on `/hubs/*`; a REST query token yields no `Authorization` header → 401. |
| Cross-provider (e.g. Entra) token used for anonymous access | The anonymous policy requires `provider=Anonymous` → 403. |
| Forged/tampered token | HS256 signature validated against the configured key; wrong signature/issuer/audience → 401. |
| Oversized request exhausts resources (incl. chunked/unknown-length) | Request-size bound → 413, enforced before the body is read and via a length-counting pre-read. |
| Unbounded conversation history | Per-message and aggregate history bounds → 400 before the model. |
| Cache used to bypass the request budget | The request slot is charged at admission, before the cache; the breaker still trips. |
| Cross-subject reply leakage via the response cache | The response cache is disabled (read + write) for Anonymous; identical prompts from two subjects execute independently. |
| Stored cross-prompt injection via memory | Memory (recall/write/extraction) is disabled for Anonymous in Sprint 1. |
| Cross-subject telemetry/stream subscription on a hub | The SignalR hubs are removed from the anonymous surface — a valid anonymous token is denied `403` on both hubs (connection + negotiate), so no anonymous caller reaches a hub group at all. Entra hub behaviour is unchanged. |
| Model budget escaped via an alternate route/orchestrator | All alternate LLM routes (`/api/chat/stream`, `/api/council/*`, message-extension) are removed from the anonymous surface; only the single metered `POST /api/chat` pipeline is reachable. |
| Direct memory write via the memory-management agent (no tool involved) | An anonymous turn classified as `AgentIntent.MemoryManagement` is refused before the agent runs — no `StoreAsync`/`ForgetAsync`, no model call, zero memory rows. |
| Council fan-out that skips the metered path | An anonymous turn classified as `AgentIntent.PortfolioHealth` is refused before the council interception — `IConsensusCouncil.ConveneAsync` is never called, so there is no unaccounted model fan-out. |
| Anonymous visitors exhaust the model budget | Per-subject/global limits + daily request/token/cost breaker (fail-closed), conservative and single-replica-pinned when hosted. |
| Write/mutation via an anonymous session | Deny-by-default allowlist (403) + write-capable tools stripped from the anonymous tool set. |

### Sprint 1 files

- `src/RetailPulse.Api/Security/Anonymous/*` — options, signing-key provider,
  session-token service, capability policy, usage budget + cost tracker, rate
  limiter.
- `src/RetailPulse.Api/Security/AnonymousAuthenticationSetup.cs` — scheme + policy.
- `src/RetailPulse.Api/Middleware/AnonymousGuardMiddleware.cs` — central guard
  (deny-by-default allowlist, chunked-safe size bound, limits, breaker).
- `src/RetailPulse.Api/Endpoints/AnonymousAuthEndpoints.cs` — bootstrap endpoint.
- `src/RetailPulse.Api/Auth/UserIdentity.cs` — provider-aware, spoof-proof identity
  resolution (Anonymous `sub` / Entra `oid`; body `objectId` never trusted).
- `src/RetailPulse.Api/Security/Anonymous/AnonymousChatRestrictions.cs` — intent-level
  refusals (memory-management, council) applied in `POST /api/chat` before specialist
  selection and before the council interception.
- `src/RetailPulse.Api/Hubs/SessionOwnershipRegistry.cs` — replica-local hub
  session→subject ownership binding, consulted by `TelemetryHub` / `StreamingHub`
  (retained for the Entra hub path; anonymous callers never reach the hubs).
- `src/RetailPulse.Api/Validation/ChatRequestValidator.cs` — history count / per-entry
  / aggregate bounds.
- `src/RetailPulse.Api/Auth/AnonymousPrincipalNormalizer.cs`,
  `Auth/IAnonymousChatPolicy.cs` — normalized principal + tool filter/output cap +
  cache/memory-disabled flags.
- `src/RetailPulse.Api/appsettings.Anonymous.example.json` — a non-live template
  (loaded by no environment; contains no secret).
- Tests: `tests/RetailPulse.Tests/Security/AnonymousAuthenticationTests.cs`
  (integration + threat, incl. deny-by-default route inventory, chunked-oversized, and
  a valid anonymous token → `403` on both hubs at connection + negotiate),
  `Security/AnonymousChatEndpointThreatTests.cs` (REAL `POST /api/chat` endpoint:
  body-spoof ignored, session isolation, cache-disabled, history bounds),
  `Security/AnonymousChatInternalBypassTests.cs` (REAL endpoint: memory-management
  refusal with zero memory mutation, council refusal with zero `ConveneAsync`, single
  accounted pipeline with truthful cost/audit),
  `Security/AnonymousHubOwnershipTests.cs` (Entra hub behaviour unchanged by the
  anonymous scope reduction),
  `Security/AnonymousCapabilityTests.cs` (unit — hubs denied), `Security/UserIdentityTests.cs`,
  `Security/EndpointAuthorizationCoverageTests.cs` (real route-graph deny-by-default —
  anonymous surface is bootstrap + `POST /api/chat` only, both hubs denied), extended
  `Security/RateLimitingConfigTests.cs` and
  `Deployment/ProviderNeutralDeploymentContractTests.cs`.

## Sprint 2 addendum: GitHub confidential OAuth BFF mode (implemented)

Sprint 2 implements the `GitHub` mode dispatched by the factory above. It is
additive, opt-in, and fail-closed; the live/deployed configuration stays `Entra`
and is proven to stay `Entra` by the deployment-contract tests. GitHub mode is
**not deployed** this sprint. Child issue #36, epic #27.

### Why a backend-for-frontend (BFF) confidential flow

GitHub **OAuth Apps do not support PKCE** (only the newer GitHub Apps do, and
only for some flows). A public/browser-only exchange would therefore either leak
the client secret or rely on an unprotected code exchange, and would put a GitHub
provider token in the SPA. The provider token is a bearer credential for the
GitHub API and must never reach the browser. The flow is therefore a
**confidential BFF**: the browser only ever talks to our API; the API holds the
client secret, performs the code→token exchange server-side, validates the user
with the token, and hands the SPA only a short-lived, single-use **redemption
code** that is later exchanged for a Retail Pulse session token. Without PKCE,
CSRF/fixation protection is provided by a random `state` in a server-side
one-time store **plus** a separate random secret bound to the browser in an
HttpOnly/Secure/SameSite=Lax cookie (only its SHA-256 hash is stored server-side;
compared in constant time at callback). Both are required at callback.

### Explicit activation and fail-closed hosted opt-in

- `Authentication:Mode=GitHub` is the only switch; no auto-detection/fallback.
- The GitHub **client id is public**; the **client secret** and the **session
  signing key** are secrets — required for any hosted deployment, never committed,
  never logged, never emitted in a response. Angle-bracket placeholder values
  (as in the safe example config) are rejected at startup.
- **Outside Development**, `GitHubAuthOptions.FromConfiguration` requires a
  complete validated set: client id/secret, a ≥ 256-bit signing key, issuer,
  audience, exact callback URL, exact frontend return URL, and a non-empty
  allowlist (immutable numeric user ids and/or active organization memberships).
  Missing/malformed/unsafe values
  throw at startup, so a misconfigured hosted deploy never serves traffic.
- Development may run with an ephemeral, process-local signing key (sessions do
  not survive a restart — intentional dev behavior, never a hosted fallback).

### The three narrowly-anonymous BFF endpoints

All three are mapped **only** in GitHub mode, are the only anonymous
(`AllowAnonymous`) surface besides `/health` + `/alive`, and are rate limited.

1. `GET /api/auth/github/start` — mints a random `state` (server-side one-time
   store, short TTL) and a separate random cookie secret. The cookie's security
   attributes come from the validated `RequireSecureCookies` option, **never** from
   the in-container `Request.IsHttps`: behind a TLS-terminating proxy (Azure
   Container Apps) the browser↔edge hop is HTTPS while the edge↔container hop the
   app observes is plain HTTP, so deriving `Secure`/`__Host-` from the request
   scheme would silently emit an insecure cookie in production. In hosted/secure
   mode the cookie is always `__Host-` prefixed (Secure, HttpOnly, SameSite=Lax,
   Path=/, no Domain). Development may explicitly opt into an insecure, non-`__Host`
   dev cookie over plain HTTP (`RequireSecureCookies=false`, rejected at startup
   outside Development). The cookie **name is per-state** — a `__Host-rp_gh_state_`
   base plus a bounded URL-safe suffix derived from the state (truncated SHA-256) —
   so **parallel login tabs never collide**: two concurrent starts write two
   differently-named cookies. It then **redirects only** to the fixed
   `https://github.com/login/oauth/authorize` with the exact registered
   `redirect_uri`, minimal scopes (empty by default; `read:org` **only** when an
   org allowlist is configured — never `repo`), and `allow_signup=false`. No
   user-supplied redirect target is ever honored (open-redirect closed).
2. `GET /api/auth/github/callback` — validates the `state` **format** first (exactly
   the fixed-length base64url shape our start emits), derives the exact per-state
   cookie name from the validated state, and reads/deletes **only** that cookie.
   It consumes the state entry atomically (one-use) and constant-time compares the
   cookie secret hash **before any code exchange**; deletes the state cookie on
   every path. Handles user denial safely (no token). Exchanges the code
   server-side at the fixed token endpoint, validates the token by calling `/user`,
   then runs the server-side allowlist. On success it mints a random one-time
   **redemption code** (bounded, atomic, TTL store) and **redirects to the one
   configured SPA URL** carrying only that code — never a provider or app token.
   All failures redirect with a generic error code or return a sanitized `400`.
3. `POST /api/auth/github/exchange` — atomically redeems the one-time code
   (replay/race impossible) and returns a freshly minted short-lived Retail Pulse
   GitHub session token. No refresh token. CORS is the exact configured origin.

### Server-side allowlist and minimal scopes

- Authorization is decided **server-side** on **immutable** signals only: the
  numeric GitHub user id (`AllowedUserIds`, positive integers, deduped) and/or
  **active** organization membership via `GET /user/memberships/orgs/{org}`
  requiring `state == "active"`. The mutable login handle is **never** an access
  mechanism — a renamed or re-created handle can never inherit access (handle-reuse
  is denied). If a display login is ever surfaced it is informational only. Startup
  **fails closed** unless at least one immutable mechanism (`AllowedUserIds` or
  `AllowedOrgs`) is configured, so an empty allowlist can never admit every GitHub
  account.
- Scopes are minimized: **no `repo` scope, ever**. With no org allowlist the
  requested scope is empty (public profile only). Org membership checks require
  `read:org`; private membership is only visible with that scope, so the org path
  is documented as requiring `read:org` and the user consenting to it.
- The allowlist **fails closed** on any GitHub API, rate-limit, or transport error
  (deny, never allow-on-error), and on an inactive/absent membership.

### Retail Pulse GitHub session token

- The credential is a compact, short-lived **HS256 session JWT** with a **separate
  issuer/audience** and `provider=GitHub`, subject from the immutable numeric id
  (`github:<id>`), the login carried only as an informational claim, the required
  `RetailPulse.User` role + `access_as_user` scope, a random `jti`, strict expiry,
  and **no refresh token** — no PII beyond the public login. The client re-runs the
  flow when the TTL elapses, bounding replay to the TTL.
- HS256 is pinned (algorithm confusion closed); the signing key is ≥ 256-bit and a
  secret. **Genuine signing-key rotation** is supported via
  `GitHub:AdditionalValidationKeys`: additional strong (≥ 256-bit, placeholders
  rejected) keys are accepted for **validation only** while the current
  `SigningKey` is always used to **sign** (and is tried first). Each key carries a
  stable id derived from its own material, so a token signed before a rotation keeps
  the same `kid` after its key is demoted to the validation-only list and therefore
  keeps validating until it expires — a rotation never invalidates in-flight
  sessions. Anonymous mode has the same rotation seam
  (`AnonymousSigningKeyProvider` / `Anonymous:AdditionalValidationKeys`) for parity.
- The same token is usable as a REST `Authorization: Bearer` and a SignalR
  `?access_token` (query token honored **only** on `/hubs/*`, exactly like Entra).
  A dedicated policy (default + fallback) requires the authenticated user, the
  role, the scope, and `provider==GitHub`, so an Entra/Anonymous/cross-provider
  token can never satisfy it (authenticated-but-unauthorized → 403).
- `GitHubPrincipalNormalizer` projects the token to `provider=GitHub` and the
  immutable numeric subject; it never treats the mutable login as identity.

### Concurrency, replica topology, and cleanup

- The state and redemption stores are **bounded, concurrent, one-use, TTL** stores
  with background/opportunistic cleanup. They are **replica-local** (in-memory):
  a callback served by one replica and an exchange served by another would not
  share state. The state store is also **capacity/TTL bounded**, so a flood of
  `start` requests (parallel-tab cookie-count abuse) is rejected with a `503`
  rather than growing unboundedly. Until moved to distributed storage, GitHub mode
  requires **`maxReplicas=1`**. Because the runtime cannot inspect ACA topology,
  hosted GitHub additionally requires an explicit
  `GitHub:AcknowledgeSingleReplica=true` — a fail-closed acknowledgement of the
  single-replica pin; startup fails without it. This is documented in the
  deployment doc and the example config, and is never silently multi-replica.

### Files (Sprint 2)

- `src/RetailPulse.Api/Security/GitHub/` — `GitHubAuthConstants`,
  `GitHubAuthOptions` (fail-closed config + validation), `GitHubSigningKeyProvider`
  (rotation), `GitHubSessionTokenService` (HS256 session mint),
  `GitHubOneTimeStores` (bounded one-use state + redemption stores),
  `GitHubOAuthClient` (fixed-endpoint SSRF-safe HTTP transport + `/user` +
  membership), `GitHubUserAllowlist` (id/login/active-org, fail-closed).
- `src/RetailPulse.Api/Endpoints/GitHubAuthEndpoints.cs` — the three
  narrowly-anonymous, rate-limited BFF endpoints.
- `src/RetailPulse.Api/Security/ProviderNeutralAuthentication.cs` — factory
  `AddGitHubMode` dispatch (mirrors `AddAnonymousMode`); `Program.cs` — GitHub
  rate-limit policies + `MapGitHubAuthEndpoints` guarded by GitHub mode.
- `src/RetailPulse.Api/appsettings.GitHub.example.json` — a non-live template
  (loaded by no environment; contains no secret; documents fail-closed and the
  replica-local `maxReplicas=1` constraint).
- Tests: `tests/RetailPulse.Tests/Security/GitHubAuthenticationTests.cs`
  (TestServer integration + threat suite — start/callback/exchange happy path and
  every failure: missing/mismatched/expired/replayed state, absent cookie, denial,
  exchange/`/user`/org errors, unallowlisted user, inactive membership, code
  replay/race, wrong redirect, wrong signature/issuer/audience/provider/expiry,
  cross-provider, REST + both hubs authorized after a session token, query token
  on REST denied, provider token never in redirect/body/logs, exact scopes/no repo,
  rate limits, anonymous-exception coverage), plus unit suites
  `GitHubAuthOptionsTests`, `GitHubOneTimeStoreTests`, `GitHubSessionTokenTests`,
  `GitHubUserAllowlistTests`, and extended `AuthenticationModeTests` /
  `Deployment/ProviderNeutralDeploymentContractTests`.

## Sprint 3 addendum: Provider-neutral frontend sign-in UX (implemented)

Sprints 1–2 implemented the **backend** for Anonymous and GitHub modes. Sprint 3
generalizes the **SPA** from Entra-only (MSAL) into a build-time-selected,
provider-neutral frontend that renders exactly one mode's sign-in UX, without
regressing the live Entra path. Production stays Entra; nothing is deployed and no
GitHub OAuth app/secret is created this sprint.

### Build-time deterministic mode (`VITE_AUTH_MODE`)

- A new `VITE_AUTH_MODE` variable (`Entra` / `GitHub` / `Anonymous`, case-insensitive)
  selects the provider at build time, mirroring the backend's `Authentication:Mode`.
  A deployment contract test proves `VITE_AUTH_MODE` ↔ `Authentication__Mode` parity.
- Resolution is **fail-closed** (`src/auth/authMode.ts`, pure `resolveAuthMode(env)`):
  an explicit known mode always wins; a missing mode resolves to Entra **only** when
  it is safe (the SPA already carries Entra config — back-compat — or an explicit
  local-dev build, which becomes a transparent pass-through against the API's
  Development synthetic auth); a missing mode in a production build with no Entra
  config **throws**; an unknown/numeric value always **throws**. It is never silently
  anonymous.
- Live `infra/main.bicep` emits a literal `output VITE_AUTH_MODE string = 'Entra'` and
  the azd post-provision hooks stay hardcoded to `Authentication__Mode=Entra`. Other
  deployments use the separate, secret-free templates
  (`src/RetailPulse.Web/.env.github.example`, `.env.anonymous.example`, and the
  backend `appsettings.{GitHub,Anonymous}.example.json`).

### Provider-neutral session/token architecture

- A single `SessionProvider` interface (`src/auth/session/types.ts`) is implemented by
  three adapters (`providers/{entra,github,anonymous}Provider.ts`). Entra preserves the
  exact MSAL behavior (MSAL still owns its `sessionStorage` cache). A central selector
  (`src/auth/activeProvider.ts`) exposes the one active provider, its `capabilities`,
  `requiresGate`, and `acquireActiveToken()`.
- **One** credential-acquisition path feeds both the global REST `authorizedFetch` and
  the SignalR `accessTokenFactory` (`tokenService.ts`) — provider logic is never
  duplicated in components.
- GitHub/Anonymous Retail Pulse **session** tokens live in a narrow store
  (`session/sessionCredentialStore.ts`): in-memory source of truth, mirrored to
  `sessionStorage` for same-tab reload only — never `localStorage`, never a
  broadly-readable cookie, never cross-tab. Cleared on logout, expiry, and 401/403. The
  GitHub **provider** token never reaches the browser at all (confidential BFF).

### Mode-specific UX (single gate, no chooser)

- `AuthGate.tsx` is a dispatcher: pass-through when `!requiresGate`, else it renders the
  one configured gate (`gates/{Entra,GitHub,Anonymous}AuthGate.tsx`). A single-mode
  deployment renders only its own provider — no provider chooser — minimizing attack
  surface and confusion.
- **Entra** (live): unchanged Microsoft button / redirect / role-denied states.
- **GitHub**: branded "Continue with GitHub"; a top-level navigation to the fixed
  same-origin `GET /api/auth/github/start` (no user-supplied return URL); the callback
  code is consumed and stripped from the URL immediately via `history.replaceState`
  (no replay on reload/bookmark/back), exchanged at `POST /api/auth/github/exchange`,
  and a session-only token is stored. Denial/expired/replayed/unallowlisted/provider
  errors map to safe messages with retry. Logout clears only our token (no github.com
  logout assumption).
- **Anonymous**: an explicit "Continue in limited demo" consent gate listing the
  limitations (billable, rate-limited, read-only chat, no telemetry/streaming/memory/
  observability/admin/export, short-lived). Only an explicit click bootstraps a
  session-only token. An in-app banner shows remaining time and offers "New anonymous
  session" / "Clear session".

### Central capability gating (usability layer; backend remains authoritative)

- A build-time `ProviderCapabilities` object (`FULL_CAPABILITIES` for Entra/GitHub,
  all-false `ANONYMOUS_CAPABILITIES`) centrally hides/disables Observability, Approvals,
  Memory, telemetry, streaming, write actions, and alternate operator views. The
  Dashboard's SignalR effect early-returns when `capabilities.realtimeHub` is false, and
  `getHubAccessToken()` returns `''` for those providers — so an anonymous build never
  starts a hub. This is defense-in-depth: the backend still 403s any disallowed route.

### Files (Sprint 3, all frontend + templates/tests/docs)

- `src/RetailPulse.Web/src/auth/authMode.ts`, `activeProvider.ts`,
  `session/{types.ts,sessionCredentialStore.ts}`,
  `providers/{entra,github,anonymous}Provider.ts`, `tokenService.ts` (refactor),
  `authorizedFetch.ts` (install guard now keys off `requiresGate`).
- `src/auth/AuthGate.tsx` (dispatcher), `src/auth/gates/*` (three gates + shared
  `gateStyles.ts`), `src/main.tsx` (provider-neutral bootstrap),
  `src/components/Dashboard.tsx` (capability + SignalR gating + anonymous banner),
  `src/vite-env.d.ts` (+`VITE_AUTH_MODE`).
- Templates: `infra/main.bicep` (`output VITE_AUTH_MODE = 'Entra'`),
  `src/RetailPulse.Web/.env.example` (documented), `.env.github.example`,
  `.env.anonymous.example`.
- Tests: `src/__tests__/{authMode,AuthGate,GitHubAuthGate,AnonymousAuthGate,
  sessionCredentialStore,tokenService,Dashboard.capabilities}.test.ts(x)` and
  `authorizedFetch.test.ts` (updated), plus the 5 new
  `Deployment/ProviderNeutralDeploymentContractTests` parity/template cases.

## Alternatives rejected


- **SWA-only authentication (Static Web Apps Easy Auth / `.auth`).** Rejected.
  The API — not the SWA — is the security boundary for the SWA + ACA topology.
  Easy Auth issues browser login redirects that break bearer-token REST and
  SignalR clients calling ACA directly, which is exactly why the postprovision
  hooks keep ACA Easy Auth **disabled**. Relying on platform auth would move the
  gate off the in-process JWT handler, weaken the deny-by-default policy, and give
  no clean seam for GitHub or Anonymous modes.
- **Ambient auto-detection of the provider** (infer from which config keys are
  present). Rejected. Non-deterministic and a downgrade risk — a partially
  configured environment could silently pick the wrong provider. Resolution is
  explicit and documented instead.
- **Implement GitHub / Anonymous now behind a feature flag.** Rejected for this
  sprint. It would create a runnable non-Entra path and enlarge the security
  review surface with no delivery need yet. The modes are declared and fail
  closed instead.
- **A broad provider abstraction layer up front** (interfaces for handlers,
  policies, token services). Rejected. Dead abstractions age badly; the
  foundation adds only what is testable today (the mode contract, the factory
  seam, and the normalized principal) and lets later sprints grow the seam.
- **Use a browser-only GitHub token as the API credential.** Rejected. GitHub
  OAuth requires a server-side confidential exchange and provider validation.
  Sprint 2 must use a backend-for-frontend flow, keep its client secret in
  server-managed configuration, and issue a short-lived Retail Pulse session
  token for REST and SignalR. It must add state, CSRF, rotation, replay, and
  allowlist tests before the mode can run.
- **Treat anonymous mode as harmless because it has no identity provider.**
  Rejected. Anonymous chat still reaches billable models. Sprint 1 must require
  explicit hosted opt-in and enforce per-client rate limits, daily token/cost
  budgets, isolated sessions, and disabled write-capable tools.

## Threat model

| Threat | Mitigation |
|--------|------------|
| A misconfiguration silently disables auth in Production | Missing mode outside Development throws at startup; `Security:RequireAuth` cannot be `false` outside Development. Fails closed. |
| An operator selects an unfinished provider in Production | Production is pinned to `Entra` in three artifacts and a deployment contract test proves the hooks and Production config never emit GitHub / Anonymous; a hosted GitHub / Anonymous deploy additionally fails startup without its complete validated (secret-bearing) configuration. |
| A typo or injected value selects an unintended provider | Unknown strings and bare numbers throw; only the three documented names resolve. |
| Downgrade from Entra to a weaker provider | No non-Entra path is runnable; Production is pinned to `Entra` in three places; a deployment contract test asserts the hooks and Production config never emit GitHub / Anonymous. |
| Subject / identity spoofing via client-supplied data | `Subject` comes from the token `oid` via `UserIdentity.Resolve` (claim-first), unchanged. The normalizer never trusts headers. |
| A future provider weakens the role/scope requirement | The authorization policy is untouched and centralized; `RetailPulse.User` + `access_as_user` remain required. Normalization is separate from authorization. |
| Hub token leakage via query string on REST | `?access_token` remains honored only on `/hubs/*`; unchanged. |
| Anonymous visitors exhaust the model budget | Hosted Anonymous requires a second explicit opt-in plus rate, token, and cost ceilings; write-capable tools remain disabled. |
| A GitHub OAuth code or session is replayed or redirected | The GitHub provider uses a backend confidential exchange, a random `state` in a server-side one-time store bound to a per-state HttpOnly cookie whose Secure/`__Host-` attributes come from validated config (not the proxy-observed request scheme), constant-time hash compare validated before any exchange, the exact fixed authorize/callback/token endpoints (SSRF-safe), a one-time bounded redemption code (never a provider/app token) redeemed atomically, short-lived HS256 session tokens with a separate issuer/audience/provider and genuine key rotation, and a fail-closed server-side **immutable** id / active-org allowlist (mutable login never grants) with no `repo` scope. |
| A renamed or re-created GitHub handle inherits another user's access | Authorization keys only on the immutable numeric id and/or active org membership; the mutable login is never an access mechanism, so a reused handle with a different id is denied. A login-only allowlist fails startup. |
| Cookie hardening is bypassed behind TLS termination | Behind Azure Container Apps the in-container request is plain HTTP; deriving `Secure`/`__Host-` from `Request.IsHttps` would emit an insecure cookie. Cookie attributes come from the validated `RequireSecureCookies` option instead, which must be `true` in any hosted deployment (startup fails otherwise). |
| Parallel login tabs clash or a state flood abuses cookies | Each `start` writes a distinct per-state cookie name (`__Host-rp_gh_state_` + bounded suffix derived from the state) and the callback consumes/deletes only its own, so concurrent tabs complete independently; the state store is capacity/TTL bounded and returns `503` under flood. |
| A GitHub provider token leaks to the SPA or logs | The provider token never leaves the server: it is used transiently to call `/user` and org membership, then discarded. Redirects and the exchange body carry only a one-time redemption code / the Retail Pulse session token; integration tests assert the provider token never appears in any redirect, body, or log. |

## No-downgrade and fail-closed rules

1. Authentication never auto-detects a provider. The mode is always explicit
   outside Development.
2. Outside Development, a missing mode is a startup failure, never a default.
3. GitHub is implemented but opt-in and never deployed — a hosted GitHub deploy
   fails startup without the complete validated (secret-bearing) configuration,
   and it never falls through to another provider. Anonymous is implemented but
   opt-in and never deployed — a hosted Anonymous deploy fails startup without the
   second explicit opt-in and complete guardrail configuration.
4. Production is pinned to `Entra` in base config, Production config, and the azd
   hooks, and a deployment contract test proves those artifacts cannot silently
   select GitHub or Anonymous.
5. The Entra authorization policy (authenticated + `RetailPulse.User` +
   `access_as_user`) is the single gate and is not relaxed by this foundation.

## Sprint plan (epic #27)

- **Sprint 0 (this ADR):** provider-neutral foundation — mode contract, factory
  boundary routing Entra unchanged, normalized principal, Production Entra pins,
  fail-closed tests. No live behavior change.
- **Sprint 1 (implemented — see addendum above):** Anonymous provider with
  explicit local/hosted opt-in, isolated identities and sessions, disabled
  write-capable tools, and billable-use ceilings. Production stays Entra.
- **Sprint 2 (implemented — see addendum above):** GitHub provider through a
  backend confidential OAuth BFF flow, short-lived Retail Pulse session tokens,
  and user/organization allowlists. Production stays Entra.
- **Sprint 3:** provider-neutral frontend sign-in selection and configuration
  templates. Production exposes only Microsoft sign-in.
- **Sprint 4:** full provider matrix, security review, docs, and a production
  verification proving Entra remains the only enabled live mode.

## Success criteria

- Current Entra live behavior is semantically identical (existing end-to-end auth
  tests stay green).
- No live code path enables GitHub or Anonymous; Production stays Entra-pinned and
  a hosted GitHub / Anonymous deploy fails startup without its complete validated
  configuration, with a precise message.
- Production and azd are explicitly Entra-pinned and fail closed on any mode
  error.
- The foundation is small enough for later sprints to extend without another auth
  rewrite.

## Risks and mitigations

- **Risk:** a later provider bypasses the shared policy. **Mitigation:** the
  factory returns options that feed the single centralized authorization policy;
  providers plug into resolution, not into the gate.
- **Risk:** the Development default masks a production misconfiguration.
  **Mitigation:** the default is Development-only and documented; every other
  environment fails closed on a missing mode.
- **Risk:** the mode pin drifts out of the deployment artifacts. **Mitigation:**
  `ProviderNeutralDeploymentContractTests` inspects the hooks and Production
  config statically and fails if the Entra pin is missing or a non-Entra mode
  appears.

## Consequences

**Positive:**

- One documented seam for future providers; the Entra security path is untouched.
- Explicit, testable fail-closed behavior for every misconfiguration class.
- Normalized identity contract ready for consumers without dead code.

**Negative:**

- A small amount of indirection in front of the Entra wiring.
- The mode must now be pinned in Production config and hooks (covered by a
  contract test).
