# Authentication matrix

> The authoritative behavior matrix for Retail Pulse authentication after the
> provider-neutral foundation (Sprint 0). It enumerates every mode and
> environment combination and the exact expected outcome. Each row is backed by
> an automated test — see [Coverage](#coverage).

## Modes

Retail Pulse selects an authentication provider through the `Authentication:Mode`
configuration key (`Authentication__Mode` as an environment variable). The
resolver is deterministic and never auto-detects a provider.

| Mode | Status in this sprint | Production |
|------|-----------------------|------------|
| `Entra` | Implemented (unchanged) | Supported and pinned |
| `GitHub` | Declared, not implemented — fails startup | Never enabled |
| `Anonymous` | Implemented (Sprint 1) — opt-in, fail-closed, not deployed by default (hosted only with `Anonymous:AllowHosted=true`) | Never enabled (Entra pinned) |

## Mode resolution matrix

| `Authentication:Mode` value | Environment | Outcome |
|-----------------------------|-------------|---------|
| `Entra` (any case) | any | Resolves to `Entra`; wires the existing Entra boundary |
| `GitHub` (any case) | any | Resolves, then the factory throws `NotSupportedException` — not implemented this sprint |
| `Anonymous` (any case) | Development | Resolves; wires the Anonymous boundary with an ephemeral process-local signing key (sessions die on restart). No hosted guardrails required. |
| `Anonymous` (any case) | Production (or any non-Development) | Resolves; requires `Anonymous:AllowHosted=true` **plus** a strong signing key (≥ 256-bit) **plus** positive daily request/token/cost ceilings, or startup fails closed |
| missing / blank | Development | Defaults to `Entra` (documented Development-only default) |
| missing / blank | Production (or any non-Development) | Startup fails — mode must be pinned explicitly, fails closed |
| unknown string (for example `Okta`) | any | Startup fails — not a recognized mode |
| bare number (for example `1`) | any | Startup fails — numeric selection is rejected |

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
header or body value. The surface is **deny-by-default**: the only reachable routes
are the unauthenticated bootstrap, authenticated `POST /api/chat`, and the two
SignalR hubs. There is **no blanket GET allowance** — every other route is `403`.

| Request | Credential | Expected result |
|---------|-----------|-----------------|
| `POST /api/auth/anonymous/session` (bootstrap) | none | 200 — mints a short-lived session token (random subject, `provider=Anonymous`, role `RetailPulse.Anonymous`, scope `chat_limited`, no PII, no refresh) |
| `POST /api/auth/anonymous/session` (bootstrap) | none, over the global bootstrap limit | 429 |
| `POST /api/chat` (within limits) | valid anonymous session token | 200 — the single allowlisted chat capability, output-token-capped, memory + response-cache disabled |
| Any broad GET / observability / admin / export / memory / cards / approvals / guardrail-log route (e.g. `GET /api/scorecard`, `GET /api/sessions`, `GET /api/audit`, `GET /api/memory`) | valid anonymous session token | 403 — not on the allowlist (deny-by-default). Read-only data is reached through the filtered chat tool path, not a direct operator endpoint |
| Alternate LLM / orchestrator routes (`POST /api/chat/stream`, `POST /api/council/convene`) | valid anonymous session token | 403 — removed from the anonymous surface so all model use is accounted through `POST /api/chat` |
| Protected REST/hub | no token | 401 |
| Protected REST/hub | malformed / expired / wrong issuer / wrong audience / wrong signature token | 401 |
| Protected REST/hub | valid-signature token with `provider != Anonymous` (cross-provider) | 403 — the anonymous policy requires `provider=Anonymous` |
| `/hubs/telemetry`, `/hubs/streaming` | valid anonymous token via `?access_token` | connects |
| Hub `JoinSession` for a session owned by a **different** subject | valid anonymous token | rejected (`HubException`) — hub groups are namespaced to the caller's immutable subject (Finding 6). Entra behaviour unchanged |
| Hub `JoinCard` (cards/approvals) | valid anonymous token | rejected — not part of the anonymous surface |
| REST endpoint | anonymous token via `?access_token` query string only | 401 (query token honored only on `/hubs/*`) |
| Chat body carrying a spoofed `user.objectId` | valid anonymous token | ignored — identity is the immutable token `sub`; a body `objectId` is never honoured for an authenticated principal |
| Chat tool invocation of a write-capable tool (e.g. `RequestApproval`) | valid anonymous token | Tool is not registered for the anonymous principal — never invoked |
| `POST /api/chat` over per-subject or per-IP minute limit | valid anonymous token | 429 |
| `POST /api/chat` after the daily request/token/cost ceiling trips | valid anonymous token | 503 — circuit breaker, fail-closed (cache hits still consume the request slot) |
| Oversized request body (including chunked / unknown-length without `Content-Length`) | valid anonymous token | 413 — enforced before the body is read and via a length-counting pre-read |
| History over the per-message / aggregate bound | valid anonymous token | 400 — rejected by validation before the model |
| `/health`, `/alive` | none | 200 |

> **Scope & reset limitation.** The rate-limit windows, daily ceilings, and the hub
> session-ownership registry are **replica-local, in-memory**. Hosted Anonymous is
> therefore pinned to `maxReplicas=1`, and all of these counters/bindings **reset on
> restart or replica replacement**. Behind the ACA ingress the connection IP is the
> proxy's, not the client's, and `X-Forwarded-For` is not trusted — so the per-IP
> limit is effectively global and the **primary** control is the per-subject limit
> applied after bootstrap.

## Environment behavior

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
- GitHub is an opt-in capability for a later sprint and is never enabled in
  production.
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
| Entra wiring + GitHub fail closed at the factory; Anonymous factory wiring | `tests/RetailPulse.Tests/Security/AuthenticationModeTests.cs` |
| Entra success / 401 / 403 / hubs | `tests/RetailPulse.Tests/Security/EntraAuthenticationTests.cs` |
| Anonymous bootstrap, token validation, read-only 403, budget/rate breakers, isolation, threat cases | `tests/RetailPulse.Tests/Security/AnonymousAuthenticationTests.cs` |
| Anonymous options fail-closed validation, token claims (no PII), capability policy, normalizer | `tests/RetailPulse.Tests/Security/AnonymousCapabilityTests.cs` |
| REST + both hubs remain protected | `tests/RetailPulse.Tests/Security/EndpointAuthorizationCoverageTests.cs` |
| Normalized principal mapping | `tests/RetailPulse.Tests/Security/NormalizedPrincipalTests.cs` |
| Production / hooks pinned to Entra, never GitHub/Anonymous; single-replica pin | `tests/RetailPulse.Tests/Deployment/ProviderNeutralDeploymentContractTests.cs` |

See [ADR-005](adr/005-provider-neutral-authentication.md) for the design and
threat model, and [Entra authentication](authentication-entra.md) for the
end-to-end Entra flow.
