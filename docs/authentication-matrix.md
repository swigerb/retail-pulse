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
| `Anonymous` | Declared, not implemented — fails startup | Never enabled |

## Mode resolution matrix

| `Authentication:Mode` value | Environment | Outcome |
|-----------------------------|-------------|---------|
| `Entra` (any case) | any | Resolves to `Entra`; wires the existing Entra boundary |
| `GitHub` (any case) | any | Resolves, then the factory throws `NotSupportedException` — not implemented this sprint |
| `Anonymous` (any case) | any | Resolves, then the factory throws `NotSupportedException` — not implemented this sprint |
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
- GitHub and Anonymous are opt-in capabilities for later sprints and are never
  enabled in production.

## Coverage

| Matrix area | Test |
|-------------|------|
| Mode resolution (all rows above) | `tests/RetailPulse.Tests/Security/AuthenticationModeTests.cs` |
| Entra wiring + GitHub/Anonymous fail closed at the factory | `tests/RetailPulse.Tests/Security/AuthenticationModeTests.cs` |
| Entra success / 401 / 403 / hubs | `tests/RetailPulse.Tests/Security/EntraAuthenticationTests.cs` |
| REST + both hubs remain protected | `tests/RetailPulse.Tests/Security/EndpointAuthorizationCoverageTests.cs` |
| Normalized principal mapping | `tests/RetailPulse.Tests/Security/NormalizedPrincipalTests.cs` |
| Production / hooks pinned to Entra, never GitHub/Anonymous | `tests/RetailPulse.Tests/Deployment/ProviderNeutralDeploymentContractTests.cs` |

See [ADR-005](adr/005-provider-neutral-authentication.md) for the design and
threat model, and [Entra authentication](authentication-entra.md) for the
end-to-end Entra flow.
