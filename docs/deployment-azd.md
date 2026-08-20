# Deploying Retail Pulse with Azure Developer CLI (`azd`)

Retail Pulse supports one-command deployment to Azure using the [Azure Developer CLI](https://learn.microsoft.com/azure/developer/azure-developer-cli/).

## Architecture

| Component | Azure Service | Notes |
|-----------|--------------|-------|
| API | Azure Container Apps | .NET minimal API + AI agent |
| MCP Server | Azure Container Apps | MCP tools provider |
| Teams Bot | Azure Container Apps | Microsoft Agents SDK |
| Container Registry | Azure Container Registry (Basic) | Dedicated registry; Container Apps pull images via managed identity (no admin secrets) |
| Frontend | Azure Static Web Apps | React/Vite static build (Standard SKU); calls the Container Apps API directly over CORS |
| Monitoring | Application Insights + Log Analytics | Full OpenTelemetry pipeline |
| App data storage | Container-local temp (no durable volume) | The API's SQLite stores (cost/audit/memory/approvals/alerts) live in the replica's temp dir. **No Azure Files mount** — tenant governance forbids account-key CIFS mounts (see the incident note below), so observability history is per-replica and resets on replacement. |
| AI Gateway | Azure API Management | First-class azd IaC in `infra/modules/apim*.bicep` |
| Authentication | Microsoft Entra ID | Single-tenant SPA/API app registration (MSAL PKCE). See [authentication-entra.md](./authentication-entra.md). Set `RETAIL_PULSE_ENTRA_*` before `azd provision`; `infra/modules/container-apps.bicep` pins `Authentication__Mode=Entra`, `Security__RequireAuth=true`, and `ASPNETCORE_ENVIRONMENT=Production` directly on the API Container App (provider-neutral mode contract — see [ADR-005](./adr/005-provider-neutral-authentication.md)), and the postprovision hook then disables ACA platform (Easy Auth) so it can't double-wrap the in-process JWT boundary. |

## Prerequisites

- [Azure Developer CLI](https://aka.ms/azd-install) (v1.11+)
- [Azure CLI](https://aka.ms/install-azure-cli)
- [.NET 10 SDK](https://dot.net/download)
- [Node.js 20+](https://nodejs.org)
- An Azure subscription in which the deploying principal can **create role assignments** — for example **Owner** or **User Access Administrator**, *plus* the resource-deployment permissions needed to create the resources (e.g. **Contributor**). Role-assignment permission is required because the postprovision hook grants each Container App's managed identity `AcrPull` on the registry; **Contributor alone cannot create role assignments** and the identity-based registry pull will fail.

## Quick Start

```bash
# 1. Clone and navigate to the repo
cd retail-pulse

# 2. Authenticate with Azure
azd auth login

# 3. Initialize environment (first time only)
azd init

# 4. Deploy everything
azd up
```

This single command will:
1. Provision all Azure resources (Container Registry, Container Apps Environment, Container Apps, Static Web App, App Insights, Log Analytics, Azure API Management). Provisioning captures the API and frontend origins as azd environment values (`VITE_API_ORIGIN`, `RETAIL_PULSE_FRONTEND_ORIGIN`, `MCP_SERVER_BASE_URL`), the dedicated registry coordinates (`AZURE_CONTAINER_REGISTRY_ENDPOINT`, `AZURE_CONTAINER_REGISTRY_NAME`, `AZURE_CONTAINER_REGISTRY_RESOURCE_ID`), and the APIM gateway coordinates (`AZURE_APIM_NAME`, `AZURE_APIM_GATEWAY_URL`, `AZURE_APIM_INFERENCE_ENDPOINT`, `AZURE_APIM_INFERENCE_SUBSCRIPTION_NAME`). The API's runtime env (model/APIM endpoint, subscription-key ref, CORS origin, MCP base URL, Entra identifiers, `Authentication__Mode=Entra`, `Security__RequireAuth=true`, `ASPNETCORE_ENVIRONMENT=Production`, `RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE=true`) is asserted directly on the Container App by `infra/modules/container-apps.bicep`. A **postprovision hook** then binds each Container App's system-assigned identity to `AcrPull` on the registry, links the Static Web App to the API backend, and disables ACA platform (Easy Auth) on the API.
2. Build the .NET services and containerize them (pushed to the dedicated Container Registry)
3. Deploy backend containers to Azure Container Apps (Bicep has already pinned the API's model, CORS, auth-mode, and MCP settings on the Container App revision template)
4. Build the React frontend (`npm run build`) **with `VITE_API_ORIGIN` injected from the provisioned API origin** and deploy `dist/` to Azure Static Web Apps
5. Output the frontend URL and connection strings

Because provisioning runs first, both the API-origin injection into the frontend build and the frontend-origin injection into API CORS happen automatically in a single `azd up` — no manual two-pass step is required. Runtime settings are asserted directly by `infra/modules/container-apps.bicep` on the Container App revision template, rather than by `azure.yaml` service environment-variable updates (which are not consistently applied to pre-existing ACA targets); the postprovision hook then layers the identity/RBAC glue and disables Easy Auth. See [Build-time API origin injection](#build-time-api-origin-injection-vite_api_origin) for the one ordering caveat.

## Environment Configuration

After `azd init`, configure your environment:

```bash
# Set the deployment region
azd env set AZURE_LOCATION northcentralus

# Optional: Configure Azure OpenAI (if not using APIM gateway)
azd env set AZURE_OPENAI_ENDPOINT https://your-openai.openai.azure.com/
azd env set AZURE_OPENAI_API_KEY sk-...
```

See `.env.template` for all available configuration options.

## Common Commands

| Command | Description |
|---------|-------------|
| `azd up` | Provision infrastructure + deploy all services |
| `azd provision` | Provision/update infrastructure only |
| `azd deploy` | Deploy code to existing infrastructure |
| `azd deploy frontend` | Deploy only the frontend |
| `azd deploy api` | Deploy only the API |
| `azd down` | Tear down all Azure resources |
| `azd monitor` | Open Application Insights in the portal |
| `azd env list` | List configured environments |

## Infrastructure

The `infra/` directory contains Bicep templates:

```
infra/
├── main.bicep              # Orchestrator (subscription-scoped)
├── main.bicepparam         # Parameter file (reads azd env vars)
├── abbreviations.json      # Azure resource naming abbreviations
└── modules/
    ├── monitoring.bicep        # App Insights + Log Analytics
    ├── container-registry.bicep # Dedicated Basic-SKU Azure Container Registry
    ├── container-apps-env.bicep # Container Apps Environment
    ├── container-apps.bicep     # API, MCP server, Teams Bot container apps
    ├── static-web-app.bicep    # Standard-SKU static frontend hosting (direct-CORS to the API)
    ├── content-safety.bicep    # Optional Azure AI Content Safety (issue #100). Provisioned only when AZURE_CONTENT_SAFETY_ENABLED=true.
    └── ai-search.bicep          # Optional Azure AI Search knowledge provider (issue #103). Provisioned only when AZURE_AI_SEARCH_ENABLED=true.
```

### Optional features (opt-in, disabled by default)

Two Wave-5 cloud-backed features ship as fully-optional modules. Each is
gated by a single `azd env set` toggle; the default `azd up` leaves them
off so no additional resources are provisioned and no additional cost is
incurred.

| Feature | Toggle | Provisioned when on | Docs |
|---|---|---|---|
| Azure AI Content Safety (input/output moderation, Prompt Shields) | `azd env set AZURE_CONTENT_SAFETY_ENABLED true` | `infra/modules/content-safety.bicep` — Cognitive Services (`kind=ContentSafety`), managed identity, `disableLocalAuth=true`. Postprovision hooks grant `Cognitive Services User`. | ADR-010 |
| Azure AI Search knowledge provider (durable hybrid retrieval) | `azd env set AZURE_AI_SEARCH_ENABLED true` and `azd env set Knowledge__Provider__Mode AzureAISearch` | `infra/modules/ai-search.bicep` — Basic-SKU Search service with system identity and `disableLocalAuth=true`. Postprovision hooks grant `Search Service Contributor` + `Search Index Data Contributor`. Also requires `Knowledge__AzureAISearch__Endpoint` and `Knowledge__AzureAISearch__Embeddings__Endpoint`. | ADR-012, `docs/rag/azure-ai-search-index.md` |

Neither feature is referenced by the default runtime path: with both
toggles left at `false`, the API boots against the InMemory BM25
knowledge base and the regex-only guardrails baseline — exactly the
laptop-demo path.

### azd environment aliases (Bicep outputs → azure.yaml / Vite build)

`azure.yaml` and the frontend build consume three azd environment values that
`infra/main.bicep` emits as outputs. The output names match the consumers exactly
so azd's `${...}` substitution and Vite's `import.meta.env` resolve them:

| Output (Bicep) | Source | Consumed by |
|----------------|--------|-------------|
| `VITE_API_ORIGIN` | API container app origin | Frontend Vite build → `import.meta.env.VITE_API_ORIGIN` → absolute `/hubs/telemetry` |
| `VITE_AUTH_MODE` | literal `'Entra'` (live) | Frontend Vite build → `import.meta.env.VITE_AUTH_MODE` → selects the sign-in UX (must equal the API's `Authentication__Mode`) |
| `RETAIL_PULSE_FRONTEND_ORIGIN` | Static Web App origin | `api` `Security__AllowedOrigins__0` (CORS) |
| `MCP_SERVER_BASE_URL` | MCP server container app origin | `api` `McpServer__BaseUrl` |

**Frontend/API mode parity.** `VITE_AUTH_MODE` (frontend) **must equal**
`Authentication__Mode` (API) so the rendered sign-in UX matches the boundary the API
enforces. The live Bicep emits `output VITE_AUTH_MODE = 'Entra'` and
`infra/modules/container-apps.bicep` pins `Authentication__Mode=Entra` directly on
the API Container App, so the deployed pair is always `Entra`. A deployment contract test
(`tests/RetailPulse.Tests/Deployment/ProviderNeutralDeploymentContractTests.cs`)
asserts this parity and that the Bicep/hooks never emit `GitHub`/`Anonymous`. Non-live
web builds can be produced from the secret-free templates
`src/RetailPulse.Web/.env.github.example` and `.env.anonymous.example` (see below); they
are documentation-only and are **not** auto-loaded by any deployment.

The pre-existing `AZURE_*` outputs (`AZURE_API_APP_URL`, `AZURE_MCP_SERVER_APP_URL`,
`AZURE_FRONTEND_APP_URL`, …) are retained as-is for tooling compatibility; the
aliases above are additive.

### Container registry outputs (consumed by azd + the postprovision hook)

`infra/main.bicep` also emits the dedicated registry coordinates. `azd` reads
`AZURE_CONTAINER_REGISTRY_ENDPOINT` to decide **where to push** service images, and
the postprovision hook reads all three to wire secretless pull:

| Output (Bicep) | Source | Consumed by |
|----------------|--------|-------------|
| `AZURE_CONTAINER_REGISTRY_ENDPOINT` | ACR login server | azd image push target + `az containerapp registry set --server` in the hook |
| `AZURE_CONTAINER_REGISTRY_NAME` | ACR resource name | postprovision hook diagnostics |
| `AZURE_CONTAINER_REGISTRY_RESOURCE_ID` | ACR resource id | postprovision hook `AcrPull` role scope |

## Multiple Environments

```bash
# Create a staging environment
azd env new staging
azd env set AZURE_LOCATION eastus2

# Deploy to staging
azd up

# Switch back to dev
azd env select dev
```

## AI Gateway (APIM)

The AI Gateway is provisioned directly by `infra/main.bicep`:

- `infra/modules/apim.bicep` creates the Developer-tier APIM instance, service-level diagnostic settings, and the `appinsights-logger` / `azuremonitor` loggers.
- `infra/modules/apim-openai-api.bicep` attaches the Azure AI Foundry backend, inference API, AI Gateway policy, API-level diagnostics, APIM subscription, and the cross-resource-group `Cognitive Services OpenAI User` assignment for the APIM managed identity.

`azd provision` now emits:

- `AZURE_APIM_NAME`
- `AZURE_APIM_GATEWAY_URL`
- `AZURE_APIM_INFERENCE_ENDPOINT`
- `AZURE_APIM_INFERENCE_SUBSCRIPTION_NAME`

Every `azd provision` / `azd up` also runs the mandatory
[APIM AI Gateway live verifier](./ai-gateway-integration.md#mandatory-post-provision-verifier-gate)
(`scripts/Verify-ApimAiGateway.ps1`, invoked from `azd-hooks/postprovision.*`) as a
hard gate — a live invariant failure fails the whole `azd up` rather than reporting
false success. This is the primary defense against a "provisioning succeeded" report
that masks a broken gateway.

## Deployment behavior & operational tradeoffs

The current `azd` topology is tuned for a **low-cost public demo**. The following
behaviors are intentional but have operational consequences you should understand
before relying on this deployment for anything beyond a demo.

### Container images & secretless registry pull

Retail Pulse provisions a **dedicated Basic-SKU Azure Container Registry**
(`infra/modules/container-registry.bicep`) instead of letting `azd` create an
implicit one. Emitting `AZURE_CONTAINER_REGISTRY_ENDPOINT` from `infra/main.bicep`
tells `azd` to push the API/MCP/Teams Bot images to **this** registry. Admin
credentials are disabled (`adminUserEnabled: false`) — the three Container Apps
authenticate to the registry with their **system-assigned managed identities**, so
no registry username/password secret is ever created or stored.

**Why a postprovision hook (and not pure Bicep).** Binding a Container App to pull
from ACR *using its own system-assigned identity* is circular in a single
ARM/Bicep pass: the app's `principalId` does not exist until the app is created,
and the `AcrPull` role assignment that the registry-identity binding depends on
needs that `principalId`. Expressing the whole chain inline makes provisioning
circular and unreliable — and, critically, a **re-provision can strip the registry
configuration** that `azd deploy` set on a prior run, which is exactly the
`UNAUTHORIZED` image-pull failure (`...azurecr.io/... : UNAUTHORIZED`) seen after a
repeated `azd up`.

The fix is a cross-platform **postprovision hook** — `azd-hooks/postprovision.ps1`
(Windows/`pwsh`) and `azd-hooks/postprovision.sh` (POSIX/`sh`), selected by `azd`
per host OS in `azure.yaml`. After every provision it, for each of the three apps:

1. reads the app's live system-assigned `principalId`
   (`az containerapp show ... --query identity.principalId`),
2. **idempotently** grants `AcrPull` on the registry scope (it first lists
   assignments and only creates the grant when missing, filtering on `principalId`
   client-side to avoid AAD Graph lookups on freshly created identities), and
3. sets the app's registry auth to its system identity
   (`az containerapp registry set --server <acr> --identity system`) — no secrets.

Because the hook runs on **every** `azd provision` / `azd up` and re-asserts the
desired state, clean and repeated deployments are **self-contained and
idempotent**: a re-provision that momentarily clears the registry block is
immediately re-corrected before `azd deploy` pushes and rolls out the images. The
hook derives every value from azd environment outputs, quotes all arguments, and
**fails loudly** (non-zero exit, `continueOnError: false`) on any missing value or
`az` error — it never silently falls back to admin credentials.

> **Operational caveat — placeholder image reapplied on every provision.** Because
> the Container Apps are declared in Bicep with the public placeholder image
> (`mcr.microsoft.com/k8se/quickstart:latest`), every `azd provision` / `azd up`
> **temporarily reapplies that placeholder** to each app before `azd deploy` rolls
> out the built application images. During that window the apps briefly serve the
> placeholder rather than the Retail Pulse services; this is expected and
> self-correcting once deploy completes.

**Identity / RBAC sequencing.** Provision → postprovision hook (grant `AcrPull` +
bind system identity) → `azd deploy` (build, push to ACR, roll out revision that
pulls with the managed identity). Newly created role assignments can take a short
time to propagate; because the grant happens during postprovision and the image
pull happens later during deploy, there is normally enough delay for propagation.
If a very first deploy ever races propagation, simply re-run `azd deploy` (or
`azd up`) — the hook is idempotent and the grant will already be in place.

### Managed identity → Azure OpenAI via APIM subscription key (shipped) or direct MI (opt-in)

Each Container App (`api`, `mcpserver`, `teamsbot`) is provisioned with a
**system-assigned managed identity** (`infra/modules/container-apps.bicep`). In the
shipped topology the API talks to Azure OpenAI **through APIM**, not directly:
`infra/modules/container-apps.bicep` pins `OpenAI__UseManagedIdentity=false` and passes
`OpenAI__ApimSubscriptionKey` (an ACA secret referencing the APIM subscription's primary
key) so the API authenticates to APIM with an `Ocp-Apim-Subscription-Key` header. APIM
itself then uses **its own** managed identity (via `<authentication-managed-identity>` in
`infra/modules/apim-openai-policy.xml`) to call the Azure AI Foundry backend — no AI-model
keys are stored in or transit through the API. This is the mode the shipped Bicep and the
mandatory verifier gate enforce.

**If you point `OpenAI__Endpoint` at a direct Azure OpenAI resource** (bypassing APIM),
set `OpenAI__UseManagedIdentity=true` so the API authenticates with `DefaultAzureCredential`
instead. The direct model endpoint is **external to this Bicep** — it is supplied via
`AZURE_OPENAI_ENDPOINT` and is not provisioned here — so the infra **cannot** create the
required RBAC. After `azd up` you must grant the API's principal access on the target
resource:

```bash
az role assignment create \
  --assignee <AZURE_API_APP principalId> \
  --role "Cognitive Services OpenAI User" \
  --scope <Azure OpenAI resource id>
```

The API's principal id is exposed as the `apiPrincipalId` output of the container-apps
module. To go the other direction — bypass APIM AND use a raw API key — set
`OpenAI__UseManagedIdentity=false` and provide the key via `OpenAI__ApiKey` instead of
`OpenAI__ApimSubscriptionKey`.

### Scale-to-zero (cold start) and in-memory state loss

All three Container Apps use `minReplicas: 0` / `maxReplicas: 1`. Consequences:

- **Cold start:** the first request after an idle period pays a container start +
  .NET warm-up penalty (several seconds).
- **In-memory state is volatile:** the API keeps trace spans, the streaming ring
  buffer, the conversation-export sessions, and the live telemetry panel in
  singletons. When the API scales to zero these reset. Background services
  (proactive alerts) only run while a replica is live. The SQLite stores
  (cost, audit, memory, approvals, alerts) live in the replica's local temp
  directory and reset the same way — there is **no** durable volume (see below).
- **Anonymous mode (if opted-in) is replica-local too:** Anonymous mode is
  **not deployed by default** and is permitted hosted only behind the explicit
  `Anonymous:AllowHosted=true` opt-in. When enabled, its daily request/token/cost
  **ceilings**, its **rate-limit windows** (including the global bootstrap window),
  and the **hub session-ownership registry** are all held in replica-local memory
  and **reset on restart or replica replacement**. This is exactly why hosted
  Anonymous requires `maxReplicas: 1` (a single writer/counter owner) and ships with
  conservative limits — it is explicitly not equivalent to authenticated production.
  See [ADR-005](adr/005-provider-neutral-authentication.md) and the
  [authentication matrix](authentication-matrix.md).
- **GitHub mode (if opted-in) is replica-local too:** GitHub confidential-OAuth
  mode is **not deployed** and is permitted hosted only with a complete validated
  (secret-bearing) configuration. When enabled, its OAuth **state store** and its
  one-time **redemption-code store** (both bounded, one-use, TTL-expiring) are held
  in replica-local memory. A login `callback` served by one replica and the
  `POST /api/auth/github/exchange` served by another would not share state, so
  hosted GitHub requires `maxReplicas: 1` until the stores are moved to distributed
  storage. Because the runtime cannot inspect ACA topology, hosted GitHub also
  requires an explicit `AcknowledgeSingleReplica=true` fail-closed acknowledgement
  of that pin (startup fails without it). See
  [ADR-005](adr/005-provider-neutral-authentication.md) and the
  [authentication matrix](authentication-matrix.md).
- **SignalR:** the Teams Bot holds a persistent SignalR connection to the API telemetry
  hub, and the frontend connects to `/hubs/telemetry` and `/hubs/streaming`. An active
  SignalR/WebSocket connection keeps the API replica warm; once all clients disconnect
  the API can scale to zero and in-flight hub state is dropped.

To keep the demo continuously warm (at higher cost) set the API's `minReplicas` to `1`
in `infra/modules/container-apps.bicep`.

### App storage data semantics — no durable volume (governance incident)

> **Incident (resolved):** A prior change (PR #21) mounted the API's SQLite data
> directory on an **Azure Files** share via a managed-environment storage
> registration that authenticates with the storage **account key**. This tenant's
> governance policy forces every new storage account to
> `allowSharedKeyAccess=false` and `publicNetworkAccess=Disabled` immediately after
> creation. With shared-key access disabled the ACA CIFS mount fails with
> `Permission denied`, so every API replica crash-looped on startup — a production
> outage. Service was restored by reprovisioning the previous no-volume IaC and
> redeploying the current API. This hotfix removes the incompatible Azure Files
> topology from the committed IaC so a future `azd provision` cannot re-break
> production. See the incident issue/PR linked from the repo history.

The **API** persists its SQLite databases (audit, cost, memory, approvals, alerts)
to the container's **local temporary directory** (resolved by
`DataDirectoryResolver`). There is **no** durable Azure volume. The deployed demo
runs `ASPNETCORE_ENVIRONMENT=Production` under Entra authentication and does **not**
set a durable data directory. Because Production would otherwise fail closed (see
below), it **explicitly** sets `RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE=true` — an
honest opt-out that permits the writable temp fallback without claiming durability.
It is never pointed at a missing mount path.

**What this means for observability history:** cost/audit/memory/approval/alert
history survives process restarts *within the same replica*, but it **resets when
the replica is replaced** — a new revision, a redeploy, or a scale-to-zero cold
start starts from an empty store. Export, audit, and cost **functionality is
unaffected**: every endpoint still works against the live replica's data; only
cross-replica persistence is not provided. This is an honest downgrade from the
(non-functional) durability the removed mount claimed.

**Fail-closed behavior retained.** `DataDirectoryResolver` still refuses to fall
back to temp when durability is *explicitly* required — the ephemeral opt-out never
weakens these:
- `RETAIL_PULSE_DATA_DIRECTORY` set but absent/unwritable → startup fails.
- `RETAIL_PULSE_REQUIRE_DURABLE_STORAGE` truthy → startup fails without a writable
  path, **even if** `RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE=true` (the require flag
  always wins).
- `ASPNETCORE_ENVIRONMENT=Production` with no configured path **and no**
  `RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE=true` → startup fails.

The Entra auth cutover (already merged to `main`, rebased on top of the storage hotfix) runs
the API in `Production`. It resolves the resulting fail-closed startup by option (b): it
**explicitly** acknowledges non-durable storage via
`RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE=true` for the synthetic demo, rather than
reintroducing the policy-incompatible Azure Files mount. The Production/required
fail-closed path is **not** weakened — a future policy-compatible durable backing (see
options below) can drop the opt-out and set a real durable path instead.

The **MCP server** still writes its seeded retail dataset (`retailpulse.db`) under
the OS temp directory. That is intentional and safe: it **re-seeds from
`tenant.yaml` on first run**, so the demo data regenerates automatically and needs
no durable volume.

**Single writer & SMB-safe pragmas (retained helper).** The API keeps
`maxReplicas: 1`, so one SQLite writer owns the files. Every store still opens
through the centralized `SqliteMount` helper (`busy_timeout=10000`, then
`journal_mode=DELETE`, then `synchronous=FULL`). These pragmas are safe on local
disk and on any future network-filesystem backing (WAL's `-shm` file is
unsupported over SMB), so the helper is retained even with no share mounted today.
Do **not** raise `maxReplicas` while the stores share one directory.

### Policy-compatible durable options (proposed, not yet implemented)

Ranked by increasing cost/complexity. All avoid account-key CIFS mounts and are
compatible with `allowSharedKeyAccess=false` / `publicNetworkAccess=Disabled`:

1. **Application Insights / Log Analytics-derived metrics (lowest cost).** Cost and
   audit signals are already emitted through the OpenTelemetry pipeline into App
   Insights + Log Analytics (both provisioned). Derive the cost/audit dashboards
   from Kusto queries instead of a local SQLite store. No new resource, no
   secrets, already policy-compatible; trade-off is query-time aggregation and
   Log Analytics ingestion latency/retention rather than a row-level app store.
2. **Cosmos DB / Table / Blob with managed identity + network reachability
   (moderate).** Repoint the durable stores at a managed-identity-authenticated
   Azure data service (Table Storage or Cosmos serverless are the cheapest). This
   uses AAD tokens, not account keys, so it satisfies `allowSharedKeyAccess=false`
   — provided the resource's `publicNetworkAccess`/firewall is configured so the
   ACA egress can reach it (e.g. a private endpoint or an allowed-services rule).
   Requires a data-layer rewrite away from SQLite and an RBAC role assignment in
   the postprovision hook.
3. **`minReplicas=1` ephemeral compromise (simplest code, ongoing cost).** Keep the
   SQLite-on-temp design but pin the API to a single always-warm replica so history
   is not lost to scale-to-zero. History still resets on a new revision/redeploy or
   an infra-forced replica move, so this is a partial mitigation only, and it incurs
   continuous compute cost. No policy interaction.

Constraints verified from code/IaC: `infra/modules/container-apps.bicep`
(`maxReplicas: 1`, no volume), `infra/modules/monitoring.bicep` (App Insights +
Log Analytics exist), `DataDirectoryResolver`/`SqliteMount` (local SQLite model),
and the governance failure mode described in the incident note above.

### Live auth posture (Entra-only, fail-closed)

The deployed stack is **Entra-only**. The Bicep pins the API's environment to:

- `Authentication__Mode=Entra` (provider-neutral mode contract — see
  [ADR-005](adr/005-provider-neutral-authentication.md))
- `Security__RequireAuth=true` (in-process JWT bearer gate is the authoritative boundary)
- `ASPNETCORE_ENVIRONMENT=Production`
- `RETAIL_PULSE_ALLOW_EPHEMERAL_STORAGE=true` (honest opt-out for the demo's non-durable
  SQLite stores — see the storage section above)

The post-provision hook (`azd-hooks/postprovision.*`) also:

- Grants each Container App's system-assigned managed identity `AcrPull` on the
  dedicated Container Registry (idempotent).
- Links the API Container App as the Static Web App's `/api` backend so relative
  `/api/*` calls stay same-origin, while SignalR intentionally uses the absolute
  `VITE_API_ORIGIN` (linked backends do not proxy WebSockets).
- **Disables** ACA platform auth (Easy Auth) on the API. Easy Auth would issue login
  redirects that break bearer-token REST and SignalR clients calling ACA directly; the
  in-process Entra `JwtBearer` handler is the real security boundary.
- Runs the **mandatory APIM AI Gateway live verifier**
  (`scripts/Verify-ApimAiGateway.ps1`) as a hard gate — a live invariant failure fails
  the whole `azd up` rather than reporting false success. See
  [AI Gateway Integration → Mandatory post-provision verifier gate](ai-gateway-integration.md#mandatory-post-provision-verifier-gate).

The Anonymous and GitHub provider modes are fully implemented but **never deployed** by
`azd up` — they are opt-in, fail-closed capabilities for non-production environments
only. A deployment-contract test
(`tests/RetailPulse.Tests/Deployment/ProviderNeutralDeploymentContractTests.cs`) asserts
that neither Bicep nor the azd hooks ever configure GitHub/Anonymous guardrails, and
that `VITE_AUTH_MODE` (frontend) and `Authentication__Mode` (API) both equal `Entra`.

#### Safe web-build mode templates (`.env.*.example`)

The frontend ships two **secret-free** example env files for producing a non-live web
build in `GitHub` or `Anonymous` mode:

- `src/RetailPulse.Web/.env.github.example` → `VITE_AUTH_MODE=GitHub`
- `src/RetailPulse.Web/.env.anonymous.example` → `VITE_AUTH_MODE=Anonymous`

Both are documentation/templates only — copy to `.env.local` and set `VITE_API_ORIGIN`
to an API deployed in the **matching** backend mode. They contain no secrets and are
**not** auto-loaded by azd, the Bicep, or any hook; the live deployment stays `Entra`.
Because the SPA renders exactly the mode it was built with, a mismatched
`VITE_AUTH_MODE`/`Authentication__Mode` pair is a build-time configuration error, not a
runtime provider switch.

#### Verify the live production posture (read-only)

After `azd up` completes (and any post-outputs frontend rebuild), confirm the deployed
environment is the expected **Entra-only, fail-closed** posture with the read-only verifier.
It never obtains, prints, or logs a token/secret, never signs you in, and never mutates a
resource — it exits non-zero on any mismatch:

```pwsh
# Preview the checks without contacting anything:
pwsh scripts/Verify-ProductionAuth.ps1 -TenantId <tenant-guid> -ClientId <client-guid> -ResourceGroup <rg> -WhatIf

# Verify the live environment (needs an existing `az login` with reader access to the RG):
pwsh scripts/Verify-ProductionAuth.ps1 -TenantId <tenant-guid> -ClientId <client-guid> -ResourceGroup <rg>
```

It checks the signed-in tenant/subscription, the API revision health and Entra env pins
(`ASPNETCORE_ENVIRONMENT=Production`, `Authentication__Mode=Entra`, `Security__RequireAuth=true`,
matching tenant/client ids, the ephemeral-storage acknowledgement, and the **absence** of any
`Anonymous__*`/`GitHub__*` var), that ACA Easy Auth is disabled, the anonymous `401` surface plus
`health`/`alive` `200`s, that the Static Web App serves an Entra build (GitHub/Anonymous **not**
exposed), and the Entra app-registration posture (delegating to `Verify-EntraAuth.ps1`). Pair it
with `pwsh scripts/Test-ProviderMatrix.ps1` before a release to exercise the full secret-free
provider build/test matrix.

### Frontend → API routing and SignalR

The postprovision hook idempotently links the ACA API as the Static Web App backend,
so ordinary relative `/api/*` requests remain same-origin and are proxied to ACA.
The long-running `/api/chat` request uses the exact `VITE_API_ORIGIN` ACA origin
directly to avoid the linked-backend request timeout; the frontend attaches its
Entra bearer token only to that exact origin and `/api` path.
SWA linked backends do **not** proxy WebSockets, so SignalR deliberately bypasses the
link and connects directly to the ACA API using `VITE_API_ORIGIN`.

Linking a Container App automatically enables the SWA identity provider on ACA. The
hook disables that platform auth immediately afterward because this synthetic demo
uses its fixed Development identity. The configured SWA origin is applied to
Development CORS with `AllowCredentials()`, enabling the direct SignalR negotiate and
WebSocket upgrade without widening CORS to arbitrary origins.

#### Build-time API origin injection (`VITE_API_ORIGIN`)

Because the frontend must know the API URL at **build time**, the Vite build reads
`VITE_API_ORIGIN` (`import.meta.env`) and, when set, points the telemetry hub at the
absolute `${VITE_API_ORIGIN}/hubs/telemetry` (SWA only proxies `/api`, not `/hubs`).
This is wired automatically and needs no manual steps:

1. `infra/main.bicep` emits `output VITE_API_ORIGIN` = the provisioned ACA **API origin**.
2. `azd provision` writes that output into the azd environment (`.azure/<env>/.env`).
3. During the frontend service deploy, azd runs `npm run build` with the azd
   environment exposed as process env vars. Vite's `loadEnv` picks up the
   `VITE_`-prefixed `VITE_API_ORIGIN` from `process.env`, so the built SPA embeds
   the absolute API origin.

Local Aspire/Vite leaves `VITE_API_ORIGIN` unset, so `resolveTelemetryHubUrl()`
returns the relative `/hubs/telemetry` and the dev-server `/hubs` proxy is used —
production wiring does not change local behavior.

**Ordering caveat:** the API origin is baked into the SPA at build time. `azd up`
provisions before it builds/deploys the frontend, so a first deployment works in one
pass. But if the API origin later changes (new azd environment, different region, or a
renamed app), you must **re-run the frontend build+deploy** (`azd provision` then
`azd deploy frontend`, or simply `azd up`) — redeploying only the API is not enough,
since the old origin is already compiled into the previously deployed bundle.

### Content-file packaging

`tenant.yaml` is now packaged into the API and MCP server publish output
(`CopyToPublishDirectory`), and both resolve it from the content root first, falling back
to the repo-relative path for local `dotnet run`. This is required because the container
image does not contain the repository layout.

### Cross-platform provision hooks

The **preprovision** hook **validates prerequisites only** (`az`, `dotnet`, `node`, `npm`
on PATH) and ships in two parity forms: `azd-hooks/preprovision.ps1` (Windows/`pwsh`)
and `azd-hooks/preprovision.sh` (POSIX/`sh`), selected by `azd` per host OS in
`azure.yaml`. It deliberately does **not** build the frontend: the production build
runs during the frontend service deploy (after provision) so the Vite build can
receive `VITE_API_ORIGIN` (see [Build-time API origin injection](#build-time-api-origin-injection-vite_api_origin)).
Building in the hook would run before the API FQDN exists and would duplicate the
build azd already performs.

The **postprovision** hook (`azd-hooks/postprovision.ps1` / `azd-hooks/postprovision.sh`,
same per-OS selection) wires secretless ACR pull for the three Container Apps,
idempotently links SWA `/api` traffic to ACA, disables ACA platform (Easy Auth) on
the API, and runs the mandatory APIM AI Gateway live verifier as a hard gate. The
API's runtime env (model endpoint, APIM subscription key ref, MCP/API URLs, CORS
origin, `Authentication__Mode=Entra`, `Security__RequireAuth=true`,
`ASPNETCORE_ENVIRONMENT=Production`, Teams bot API URL) is pinned directly on the
Container App revision template by `infra/modules/container-apps.bicep` — not by the
hook — so a re-provision re-asserts the same env.

The prompt model remains `gpt-5.4-mini`; Azure deployment selection is separate:
`OpenAI__Deployment=gpt-5.4-mini-2026-03-17`. Versioned deployment names make
upgrades explicit without coupling prompt semantics or token-pricing keys to an
infrastructure alias.
Both hook pairs are wired with `continueOnError: false`, so a failure aborts the
deploy loudly rather than proceeding with a broken registry configuration.

## Troubleshooting

| Issue | Solution |
|-------|----------|
| `azd up` fails on provision | Check `azd env get-values` for missing required vars |
| Image pull `UNAUTHORIZED` from `*.azurecr.io` | Re-run `azd up` (or `azd provision`) — the postprovision hook re-grants `AcrPull` and re-binds the system identity idempotently. Confirm the hook ran and `AZURE_CONTAINER_REGISTRY_*` outputs are present via `azd env get-values` |
| Frontend 404 after deploy | Ensure the frontend deploy ran `npm run build` after provision (`azd deploy frontend`); confirm `dist/` was produced |
| Container Apps unhealthy | Run `azd monitor` → check container logs in Log Analytics; confirm the postprovision hook applied runtime environment values |
| CORS errors on frontend | Confirm the postprovision hook set `Security__AllowedOrigins__0` to the SWA origin and that API ingress is external |
