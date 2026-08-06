# Entra Authentication (SPA + API)

> How Retail Pulse authenticates users end‑to‑end with Microsoft Entra ID:
> an MSAL React sign‑in gate on the SPA, bearer tokens on every protected REST
> call and both SignalR hubs, and pinned JWT validation on the API.

---

## Architecture at a glance

```
Browser (React SPA)                       Azure Container Apps (API)
┌───────────────────────────┐            ┌────────────────────────────────┐
│ MSAL (PKCE, no secret)     │  bearer    │ JwtBearer                       │
│  AuthGate  → sign-in       │──────────▶ │  authority pinned to tenant     │
│  installAuthorizedFetch()  │  Authorization: Bearer …     audience api://{clientId}
│   → /api/**  + VITE_API_*  │            │  issuer  https://login…/{tid}/v2.0
│  SignalR accessTokenFactory│  ?access_token (hubs only)   │
│   → /hubs/telemetry        │──────────▶ │  RequireAuthorization + app role │
└───────────────────────────┘            └────────────────────────────────┘
        single-tenant Entra app registration (SPA + API in one app)
```

**One app registration** fronts both the SPA and the API:

- **Single tenant** (`signInAudience = AzureADMyOrg`).
- **Public SPA client** — PKCE, **no client secret / password credential**.
- **Delegated scope** `access_as_user`, exposed as `api://{clientId}/access_as_user`.
- **App role** `RetailPulse.User` (allowedMemberTypes `User`), **required** for all
  protected API endpoints and hubs.
- **Service principal** with `appRoleAssignmentRequired = true` (users must be
  explicitly assigned the role to sign in).

---

## Frontend (SPA)

The auth module lives in `src/RetailPulse.Web/src/auth/`:

| File | Responsibility |
|------|----------------|
| `authConfig.ts` | Builds MSAL config from `VITE_ENTRA_*`; `isConfigured` gate. |
| `msalInstance.ts` | Lazy, idempotent `PublicClientApplication` init. |
| `tokenService.ts` | `acquireApiToken()` (silent→interactive, `forceRefresh`); `getHubAccessToken()`. |
| `authorizedFetch.ts` | Wraps `window.fetch` to attach the bearer to every `/api`, `/hubs`, and `VITE_API_ORIGIN` request; 401 → refresh + one retry → `AUTH_REQUIRED_EVENT`; 403 → `AUTH_FORBIDDEN_EVENT`. |
| `AuthGate.tsx` | Polished Fluent sign‑in gate; access‑denied on 403; **pass‑through when unconfigured**. |

Both SignalR clients (`services/telemetryHub.ts`,
`components/cards/AdaptiveCardPanel.tsx`) set `accessTokenFactory: getHubAccessToken`,
so the token rides the `?access_token` query string the API honours **only** on `/hubs`.

**Local dev / not‑yet‑provisioned:** when `VITE_ENTRA_TENANT_ID` / `_CLIENT_ID`
are empty, `isConfigured` is `false` — no MSAL is loaded, `AuthGate` renders its
children directly, and the API's Development auth handler stamps a local identity.
There is **no anonymous fallback in Production**: the azd hooks set
`Security__RequireAuth=true` and `ASPNETCORE_ENVIRONMENT=Production`.

---

## API (backend)

`src/RetailPulse.Api/Security/AuthenticationSetup.cs` + `EntraAuthOptions.cs`:

- JwtBearer **authority/issuer/audience pinned** to the configured tenant and
  `api://{clientId}` — validates issuer, audience, lifetime, and signing key.
- The default authorization policy requires an **authenticated user + the
  `RetailPulse.User` app role + the `access_as_user` scope**. It is never
  permissive (`RequireAssertion(_ => true)` is intentionally absent).
- `?access_token` is read **only** for `/hubs/*` requests.
- Every protected endpoint and hub calls `.RequireAuthorization()`. Health and
  `/alive` stay anonymous by design (the app sets `DefaultPolicy`, not
  `FallbackPolicy`). A source‑scan test
  (`tests/RetailPulse.Tests/Security/EndpointAuthorizationCoverageTests.cs`)
  guards against a future endpoint group forgetting `RequireAuthorization`.
- Config binds `MicrosoftEntra:*` (TenantId/ClientId/ApiScope/AppRole) and
  **fails fast in Production** if required values are missing.

---

## Provider-neutral mode contract

Entra is now selected through an explicit, provider-neutral **mode contract**
(the Sprint 0 foundation — see
[ADR-005](adr/005-provider-neutral-authentication.md) and the
[authentication matrix](authentication-matrix.md)). `Program.cs` calls
`AddProviderNeutralAuthentication`, which resolves the `Authentication:Mode`
configuration key and dispatches to a mode-specific wiring path:

- **`Entra`** routes to the existing, unchanged Entra boundary above. Security
  semantics are identical to before the contract existed.
- **`GitHub`** and **`Anonymous`** are declared but **not implemented in this
  sprint**. Selecting either fails startup with a precise error — it never falls
  through to Entra, Development, or anonymous access.

Resolution is deterministic and never auto-detects a provider:

- an explicit recognized mode (case-insensitive `Entra`, `GitHub`, `Anonymous`)
  is honored as-is;
- a missing mode defaults to `Entra` **only in Development** (documented default);
- a missing mode outside Development, an unknown value, or a bare number **fails
  startup — the app fails closed**.

Production pins `Authentication__Mode=Entra` explicitly in
`appsettings.Production.json` and the azd hooks; it never merely defaults to it.
The normalized principal seam (`IPrincipalNormalizer` / `NormalizedPrincipal`)
maps Entra claims to a provider-neutral identity for future providers without
changing the `RetailPulse.User` + `access_as_user` requirement.

---

## Environment / config contract

| azd env var (operator sets) | Bicep param | Frontend build var | API env var |
|-----------------------------|-------------|--------------------|-------------|
| `RETAIL_PULSE_ENTRA_TENANT_ID` | `entraTenantId` | `VITE_ENTRA_TENANT_ID` | `MicrosoftEntra__TenantId` |
| `RETAIL_PULSE_ENTRA_CLIENT_ID` | `entraClientId` | `VITE_ENTRA_CLIENT_ID` | `MicrosoftEntra__ClientId` |
| `RETAIL_PULSE_ENTRA_API_SCOPE` | `entraApiScope` | `VITE_ENTRA_API_SCOPE` | `MicrosoftEntra__ApiScope` |
| `RETAIL_PULSE_ENTRA_AUDIENCE` | `entraAudience` | `VITE_ENTRA_AUDIENCE` | (derived `api://{clientId}`) |
| (pinned, not operator-set) | — | — | `Authentication__Mode=Entra` |

- `infra/main.bicepparam` reads the `RETAIL_PULSE_ENTRA_*` env vars; `infra/main.bicep`
  round‑trips them as `VITE_ENTRA_*` outputs, which azd exposes to the Static Web
  App (Vite) build — exactly like `VITE_API_ORIGIN`.
- The API values are injected at runtime by the `azd-hooks/postprovision.*` hooks
  (which also set `Security__RequireAuth=true`, `ASPNETCORE_ENVIRONMENT=Production`,
  and **disable ACA Easy Auth** so it can't interfere with the in‑process JWT boundary).
- All of these are **public identifiers — never secrets**.

---

## Provisioning the app registration (idempotent, non‑secret)

`scripts/Setup-EntraAuth.ps1` creates/reconciles the app via Microsoft Graph
(`az rest`, caller's delegated token). It is **preview‑by‑default** — nothing is
written unless you pass `-Apply` — creates **no secrets**, and prints only safe
identifiers.

```powershell
# 1. Sign in to the target tenant
az login --tenant <tenantId>

# 2. Preview (no changes)
./scripts/Setup-EntraAuth.ps1 -TenantId <tenantId> `
  -FrontendOrigin https://<your-swa>.azurestaticapps.net `
  -RedirectUri http://localhost:5173

# 3. Apply
./scripts/Setup-EntraAuth.ps1 -TenantId <tenantId> `
  -FrontendOrigin https://<your-swa>.azurestaticapps.net `
  -RedirectUri http://localhost:5173 -Apply

# 4. Verify (read-only; exits non-zero on any gap)
./scripts/Verify-EntraAuth.ps1 -TenantId <tenantId> -ClientId <clientId>
```

The setup script's final output includes the four `azd env set` commands.

---

## Cutover (wire it into azd and deploy)

```powershell
# Public identifiers from Setup-EntraAuth.ps1 output
azd env set RETAIL_PULSE_ENTRA_TENANT_ID <tenantId>
azd env set RETAIL_PULSE_ENTRA_CLIENT_ID <clientId>
azd env set RETAIL_PULSE_ENTRA_API_SCOPE access_as_user
azd env set RETAIL_PULSE_ENTRA_AUDIENCE api://<clientId>

# Provision (postprovision hook flips RequireAuth on + disables Easy Auth) then deploy
azd provision
azd deploy

# Register the SWA URL as a redirect URI if it changed, then re-verify
./scripts/Setup-EntraAuth.ps1 -TenantId <tenantId> -FrontendOrigin https://<swa-url> -Apply
./scripts/Verify-EntraAuth.ps1 -TenantId <tenantId> -ClientId <clientId>
```

To grant additional users access: assign them the **RetailPulse.User** app role on
the enterprise application (or re‑run the setup script with `-AssignUserUpn`).

---

## Risks & operational notes

- **Assignment required** means every user must be granted `RetailPulse.User` or
  they'll receive a 403 (surfaced by `AuthGate` as an access‑denied message).
- **Redirect‑URI drift:** if the Static Web App hostname changes, re‑run the setup
  script with the new `-FrontendOrigin` or sign‑in will fail with `AADSTS50011`.
- **Easy Auth must stay disabled** on the API Container App; the postprovision hook
  disables it. Re‑enabling it would double‑wrap auth and break the SPA redirect.
- **No secrets anywhere:** the SPA is a PKCE public client; the API validates
  tokens with Entra's published signing keys. Do not add a client secret.
- **Local development is unauthenticated by design** (Development handler). Never
  run the API in `Development` in a shared/hosted environment.
