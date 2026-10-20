# ADR 017: Governed APIM MCP runtime and authentication boundary

- **Status:** Accepted (design only; implemented by the runtime issue under epic #321)
- **Issue:** #334 (parent #321, blocked-by #322)
- **Supersedes:** §2.6 "option (a)" of `docs/proposals/retail-ai-gateway-control-plane.md` (external MCP ingress restricted to APIM and a static API key). The proposal's governing goals (explicit read-only allow-list, subscription key plus Entra at the edge, no implicit tools) are kept.

## Context

- `ca-retailpulse-mcp` has `external: false` ingress and a static `X-Api-Key` secret (`infra/modules/container-apps.bicep`). APIM is public (`virtualNetworkType: None`, `infra/modules/apim.bicep`) and the Container Apps environment has no VNet, so APIM cannot reach the internal MCP app.
- The existing MCP attach-on does not inject `X-Api-Key`, so deploying it unchanged would produce 401s, or tempt us to weaken the server's key check.
- The public Retail Pulse API (`ca-retailpulse-api`) already validates tenant-pinned Entra JWTs and, when `MicrosoftEntra:AllowAppOnlyTokens=true`, accepts app-only (client-credentials / managed-identity) tokens that carry the `RetailPulse.User` app role, optionally restricted to `MicrosoftEntra:AllowedAppClientIds` (`src/RetailPulse.Api/Security/AuthenticationSetup.cs`, `EntraAuthOptions.cs`). The API already proxies to the internal MCP server using the key.

## Decision

**Expose the four approved read-only operations as MCP tools by having APIM "Expose REST API as MCP server" over the public Retail Pulse API. APIM authenticates to the API with its system-assigned managed identity (Entra app-only token). The internal `ca-retailpulse-mcp` app is not touched: it stays `external: false`, keeps its API key, and is never exposed or addressed by APIM.**

No static secret leaves the environment, no ingress changes, no APIM or environment VNet, no IP restriction. This is the "managed identity to the public API" pattern the issue prefers.

### Approved v1 tools (all `GET`, read-only)

| MCP tool name (APIM operation id) | Public API operation | Notes |
|---|---|---|
| `get_inventory` | `GET /api/supply/inventory` (`brand`, `region`, `category`, `status`) | Item-level stock; store/SKU identifiers only |
| `get_stockout_risk` | `GET /api/stores/stockout-risks` (`region`, `limit` ≤ 50) | Derived from inventory |
| `get_supply_disruptions` | `GET /api/supply/disruptions` (`brand`, `region`, `severity`, `activeOnly`) | |
| `get_demand_forecast` | `GET /api/demand/forecast` (`brand`, `region`) | **New** thin endpoint (§Follow-on); backed by existing `ForecastTool` |

Everything else (`/api/supply/health`, `/api/supply/fulfillment`, margin, promo, plans, sessions, memory, admin, chat, approvals, hubs) is **not imported** into the MCP API. Retail Pulse data is synthetic retail data; v1 exposes no patient or customer records and no write operation.

### Caller authentication (client → APIM)

1. **Subscription key** required on the MCP API (`subscriptionRequired: true`, product `retailpulse-mcp`, header `Ocp-Apim-Subscription-Key`). The product is approval-required; keys identify the calling team for quotas and logs.
2. **`validate-jwt`** in the API inbound policy: Entra v2 issuer pinned to the tenant, audience `api://retailpulse-mcp-gateway` (new app registration, named value `mcpGatewayAudience`), and required app role claim `RetailPulse.Mcp.Invoke` (`roles`). `require-expiration-time`, `require-signed-tokens`, clock-skew 60s. Failure → 401 with no body detail.
3. The caller's `Authorization` header is **stripped** before the backend call; it is never forwarded.

### APIM → backend authentication

- `authentication-managed-identity resource="{{retailPulseApiAudience}}"` stores the token in a variable and sets a fresh `Authorization: Bearer` header (the policy runs after the caller header is removed). `retailPulseApiAudience` is the audience the API already validates (`MicrosoftEntra:ClientId`/`Audience`).
- The APIM managed identity service principal is assigned the API's app role `RetailPulse.User` (Graph `appRoleAssignment`, scripted in the runtime issue's post-provision hook).
- API configuration (Container App env vars): `MicrosoftEntra__AllowAppOnlyTokens=true`, `MicrosoftEntra__AllowedAppClientIds__0={apim MI appId}`. Any app-only token from another client is rejected by the existing policy.
- **Defense in depth (new):** `AppOnlyRouteAllowListMiddleware` in the API. For tokens with no `scp` claim (app-only), the request is allowed only if the method is `GET` and the route template matches `McpGateway:AllowedOperations` (the four routes above); otherwise 403. A compromised or mis-scoped APIM identity therefore cannot reach any other API route, and no write route is ever reachable by app-only tokens in v1.
- The backend `set-backend-service` targets `https://{{retailPulseApiFqdn}}` (named value from Bicep output); `forward-request timeout=30`.

### Allow-listing, quotas, write denial

- **Tool allow-list:** only the four operations above exist in the MCP API (explicit operation list in Bicep; no wildcard operations, no implicit import). The APIM MCP `tools/list` therefore returns exactly four tools. Policy `choose` on `context.Operation.Id` rejects anything not in the set with 403 as a belt-and-braces check.
- **Write denial:** every operation is `GET`; policy returns 405 for any other method; the API middleware denies non-GET for app-only tokens. Adding any write tool requires a new ADR, an `approval-required` subscription scope, human-in-the-loop approval in the caller, and a dedicated app role. None are in v1.
- **Quotas (per subscription key):** `rate-limit-by-key` 30 calls/60 s; `quota-by-key` 2,000 calls/day and 20 MB/day bandwidth (`counter-key=@(context.Subscription.Id)`); `limit` query param clamped to ≤ 50 at the API; response size capped at 256 KB (`set-body` truncation is **not** used; API paging defaults apply and oversize responses return 413 via `forward-request` buffering limits). 429 returns `Retry-After`.
- **Logging:** APIM diagnostics (existing `GatewayLogs`) record subscription id, operation id, status; request/response bodies are not logged; `Authorization` and `Ocp-Apim-Subscription-Key` headers are excluded from diagnostics.

### SKU precondition

The Developer SKU is retained. The runtime issue must first confirm in the target subscription that the APIM instance supports the REST→MCP export (preview feature). The decision does not change if it does not: the same four operations, policies and identity are served as an ordinary APIM REST API at `/mcp-rest/*`, and the MCP surface is enabled once the feature is available. This is the only conditional, and it affects only the transport wrapper, not the security design.

## Sequence

```mermaid
sequenceDiagram
    autonumber
    participant C as MCP client (agent/IDE)
    participant E as Entra ID
    participant G as APIM (MCP API)
    participant M as APIM managed identity
    participant A as Retail Pulse API (public)
    participant S as MCP server (internal, X-Api-Key)
    C->>E: client credentials (aud api://retailpulse-mcp-gateway)
    E-->>C: JWT with role RetailPulse.Mcp.Invoke
    C->>G: tools/call get_inventory + subscription key + JWT
    G->>G: validate subscription, validate-jwt, tool allow-list, GET only
    G->>G: rate-limit-by-key / quota-by-key
    G->>M: acquire token for API audience
    M->>E: managed identity token request
    E-->>G: app-only JWT (role RetailPulse.User)
    G->>A: GET /api/supply/inventory (Bearer app-only JWT; caller token removed)
    A->>A: JwtBearer + AllowedAppClientIds + route allow-list middleware
    A->>S: GET /api/supply/inventory (X-Api-Key, internal)
    S-->>A: JSON
    A-->>G: JSON
    G-->>C: tool result
```

## Threat model

```mermaid
flowchart LR
    subgraph Internet
        X[Attacker / abusive caller]
        C[Legitimate MCP client]
    end
    subgraph Edge["APIM (trust boundary 1)"]
        P[subscription key + validate-jwt + allow-list + quotas]
    end
    subgraph Env["Container Apps env (trust boundary 2)"]
        A[Retail Pulse API\nEntra app-only + route allow-list]
        S[MCP server\ninternal ingress + API key]
    end
    C --> P --> A --> S
    X -. T1 no/forged token .-> P
    X -. T2 direct call to API with stolen app-only token .-> A
    X -. T3 direct call to MCP FQDN .-> S
```

| # | Threat | Mitigation | Residual |
|---|---|---|---|
| T1 | Unauthenticated / forged / replayed caller token | Subscription key and pinned-issuer `validate-jwt` with audience and role; short token lifetime | Leaked key + valid token = full v1 read access; bounded by quotas and key rotation |
| T2 | Stolen or other-tenant/other-client app-only token against the API | `AllowedAppClientIds` pins the APIM MI; route allow-list rejects every non-v1 route and method | Token for the MI is only issuable to APIM; it can read the four routes |
| T3 | Direct call to the MCP server | Ingress stays internal; key retained; APIM never targets it | None new |
| T4 | Tool injection / unapproved tools (e.g. write tools) | Explicit operation list, GET-only policy, API middleware, ADR required for any addition | Review discipline |
| T5 | Prompt injection via tool output | Existing tool-result content-safety policy (ADR 015) applies in the agent; outputs are numeric/structured | Caller-side agents must treat results as untrusted data |
| T6 | Denial of wallet / scraping | Per-key rate limit, daily quota, `limit` clamp, response size cap | Within quota data volume |
| T7 | Secret sprawl | No static secret for APIM→backend; named values only hold non-secret audiences/FQDNs; subscription keys managed by APIM | — |
| T8 | Sensitive data exposure | v1 data set contains no patient/customer records; no bodies in logs | Contract test asserts schemas |

## Alternatives rejected

1. **External MCP ingress restricted to APIM egress IP + static key (proposal option a).** Developer SKU public IP is shared/ changeable, IP allow-lists are weak identity, static secret must be copied into APIM named values/Key Vault, and it widens the attack surface of a service designed to be internal.
2. **VNet-inject APIM and the Container Apps environment.** The production-credible long-term network answer, but requires a SKU change and a destructive environment recreation; out of scope for this epic. Retained in the production table of the proposal.
3. **Deploy the existing attach-on unchanged.** Cannot reach the internal app and sends no key; rejected by the issue.
4. **Make the MCP server public with Entra validation of its own.** Duplicates the auth stack in a second service and still exposes write-capable tools surface (`UpdateMetrics`) on the internet.
5. **Per-tool Entra delegated user tokens end to end.** Overkill for read-only demo data; the gateway identity plus caller roles gives the needed attribution via subscription id.

## Follow-on implementation surfaces (runtime issue)

| Surface | Change |
|---|---|
| `infra/modules/apim-mcp-api.bicep` (new) | MCP API, four operations, product `retailpulse-mcp`, named values (`retailPulseApiFqdn`, `retailPulseApiAudience`, `mcpGatewayAudience`, tenant id) |
| `infra/modules/apim-mcp-policy.xml` (new) | `validate-jwt`, strip `Authorization`, GET-only, operation allow-list, rate/quota, `authentication-managed-identity`, `set-backend-service` |
| `infra/main.bicep`, `infra/main.bicepparam` | Wire module; pass API FQDN/audience; outputs `AZURE_APIM_MCP_ENDPOINT` |
| `infra/modules/container-apps.bicep` | API env vars `MicrosoftEntra__AllowAppOnlyTokens`, `MicrosoftEntra__AllowedAppClientIds__0`, `McpGateway__AllowedOperations__*`; **MCP server ingress unchanged** |
| `src/RetailPulse.Api/Security/AppOnlyRouteAllowListMiddleware.cs` (new), `Program.cs` | Route/method allow-list for app-only tokens, registered after `UseAuthentication` |
| `src/RetailPulse.Api/Endpoints/DemandEndpoints.cs` (new) | `GET /api/demand/forecast` using `ForecastTool` |
| `scripts/` or `hooks/postprovision` | Create gateway app registration + `RetailPulse.Mcp.Invoke` role; assign `RetailPulse.User` to APIM MI |
| `tests/RetailPulse.Tests/` | Middleware tests (allowed GET, denied POST, denied other route, delegated unaffected); forecast endpoint test; Bicep/policy contract tests under `Deployment` asserting no wildcard operations, GET-only, quotas present, no `X-Api-Key` named value |
| `docs/MCP-TOOLS.md`, `docs/security.md`, `docs/authentication-matrix.md`, proposal §2.6 | Document the gateway path and update §2.6 to point here |

## Acceptance checks for the runtime issue

- Direct call to the MCP FQDN from the internet fails (no ingress); direct call to the API with no token or a token from another client fails (401/403).
- `tools/list` via APIM returns exactly the four tools; `POST` or any non-listed operation returns 403/405.
- Exceeding rate/daily quota returns 429.
- No static MCP key appears in APIM configuration.
