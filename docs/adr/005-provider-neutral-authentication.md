# ADR-005: Provider-neutral authentication foundation

## Status

Accepted (Sprint 0 — foundation only; no live behavior change)

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
- Health probes (`/health`, `/alive`) stay anonymous by design because the app
  sets `DefaultPolicy`, not `FallbackPolicy` — a source-scan test guards against
  a future endpoint group forgetting `RequireAuthorization`.

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
   - `GitHub` / `Anonymous` → throws `NotSupportedException` **before any
     authentication scheme is registered**, so the app can never fall through to
     Entra, Development, or anonymous access.

This is a deliberate two-layer split: the resolver classifies *unknown/malformed*
input as a fail-closed error, while the factory classifies *known-but-
unimplemented* modes (GitHub, Anonymous) as a distinct fail-closed error. The two
failure classes carry different, precise messages.

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

## Threat model

| Threat | Mitigation |
|--------|------------|
| A misconfiguration silently disables auth in Production | Missing mode outside Development throws at startup; `Security:RequireAuth` cannot be `false` outside Development. Fails closed. |
| An operator selects an unfinished provider in Production | GitHub / Anonymous throw `NotSupportedException` before any scheme is registered — no fall-through to Entra / Development / anonymous. |
| A typo or injected value selects an unintended provider | Unknown strings and bare numbers throw; only the three documented names resolve. |
| Downgrade from Entra to a weaker provider | No non-Entra path is runnable; Production is pinned to `Entra` in three places; a deployment contract test asserts the hooks and Production config never emit GitHub / Anonymous. |
| Subject / identity spoofing via client-supplied data | `Subject` comes from the token `oid` via `UserIdentity.Resolve` (claim-first), unchanged. The normalizer never trusts headers. |
| A future provider weakens the role/scope requirement | The authorization policy is untouched and centralized; `RetailPulse.User` + `access_as_user` remain required. Normalization is separate from authorization. |
| Hub token leakage via query string on REST | `?access_token` remains honored only on `/hubs/*`; unchanged. |

## No-downgrade and fail-closed rules

1. Authentication never auto-detects a provider. The mode is always explicit
   outside Development.
2. Outside Development, a missing mode is a startup failure, never a default.
3. GitHub and Anonymous fail startup until a later sprint implements them; they
   never fall through to another provider.
4. Production is pinned to `Entra` in base config, Production config, and the azd
   hooks, and a deployment contract test proves those artifacts cannot silently
   select GitHub or Anonymous.
5. The Entra authorization policy (authenticated + `RetailPulse.User` +
   `access_as_user`) is the single gate and is not relaxed by this foundation.

## Sprint plan (epic #27)

- **Sprint 0 (this ADR):** provider-neutral foundation — mode contract, factory
  boundary routing Entra unchanged, normalized principal, Production Entra pins,
  fail-closed tests. No live behavior change.
- **Sprint 1:** GitHub provider (opt-in, non-production) — implement the GitHub
  case in the factory and a `GitHubPrincipalNormalizer`; keep production Entra.
- **Sprint 2:** Anonymous provider (opt-in, non-production, health-only-style
  invariants) with explicit guardrails.
- **Sprint 3:** normalized-principal consumers and per-provider authorization
  refinements where justified by tests.
- **Sprint 4:** hardening, docs, and operational rollout controls.

## Success criteria

- Current Entra live behavior is semantically identical (existing end-to-end auth
  tests stay green).
- No code path enables GitHub or Anonymous; selecting either fails startup with a
  precise message.
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
