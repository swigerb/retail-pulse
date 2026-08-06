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
| `Anonymous` | Implemented (Sprint 1) — opt-in, fail-closed, never deployed | Never enabled (Entra pinned) |

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

Anonymous mode is an **opt-in, fail-closed** capability (never deployed). Identity is a
server-minted, short-lived session token — never a client-supplied header. The single
unauthenticated surface besides health is the bootstrap endpoint.

| Request | Credential | Expected result |
|---------|-----------|-----------------|
| `POST /api/auth/anonymous/session` (bootstrap) | none | 200 — mints a short-lived session token (random subject, `provider=Anonymous`, role `RetailPulse.Anonymous`, scope `chat_limited`, no PII, no refresh) |
| `POST /api/auth/anonymous/session` (bootstrap) | none, over per-IP limit | 429 |
| Read-only REST (e.g. `GET /api/scorecard`) | valid anonymous session token | 200 |
| Protected REST/hub | no token | 401 |
| Protected REST/hub | malformed / expired / wrong issuer / wrong audience / wrong signature token | 401 |
| Protected REST/hub | valid-signature token with `provider != Anonymous` (cross-provider) | 403 — the anonymous policy requires `provider=Anonymous` |
| `/hubs/*` | valid anonymous token via `?access_token` | connects |
| REST endpoint | anonymous token via `?access_token` query string only | 401 (query token honored only on `/hubs/*`) |
| Mutation endpoint / write verb not on the read-only allowlist | valid anonymous token | 403 — Anonymous is read-only, enforced centrally |
| Chat tool invocation of a write-capable tool (e.g. `RequestApproval`) | valid anonymous token | Tool is not registered for the anonymous principal — never invoked |
| Read-only chat/query within limits | valid anonymous token | 200, output-token-capped |
| Read-only chat/query over per-subject or per-IP minute limit | valid anonymous token | 429 |
| Read-only chat/query after the daily request/token/cost ceiling trips | valid anonymous token | 503 — circuit breaker, fail-closed (cache hits still consume the request slot) |
| Oversized request body | valid anonymous token | 413 |
| `/health`, `/alive` | none | 200 |

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
