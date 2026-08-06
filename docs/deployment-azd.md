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
| Durable storage | Azure Storage (Standard_LRS StorageV2) + Azure Files share | 1 GiB SMB share mounted into the API at `/mnt/retailpulse-data` for durable SQLite (cost/audit/memory/approvals/alerts); durability enforced env-agnostically by `RETAIL_PULSE_REQUIRE_DURABLE_STORAGE=true` |
| AI Gateway | Azure API Management | Existing APIM Bicep in `deploy/apim-ai-gateway/` |
| Authentication | Microsoft Entra ID | Single-tenant SPA/API app registration (MSAL PKCE). See [authentication-entra.md](./authentication-entra.md). Set `RETAIL_PULSE_ENTRA_*` before `azd provision`; the postprovision hook flips `RequireAuth` on and disables Easy Auth. |

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
1. Provision all Azure resources (Container Registry, Storage account + Azure Files share, Container Apps Environment, Container Apps, Static Web App, App Insights, Log Analytics). Provisioning captures the API and frontend origins as azd environment values (`VITE_API_ORIGIN`, `RETAIL_PULSE_FRONTEND_ORIGIN`, `MCP_SERVER_BASE_URL`) and the dedicated registry coordinates (`AZURE_CONTAINER_REGISTRY_ENDPOINT`, `AZURE_CONTAINER_REGISTRY_NAME`, `AZURE_CONTAINER_REGISTRY_RESOURCE_ID`). A **postprovision hook** then binds each Container App's system-assigned identity to `AcrPull`, applies the synthetic-demo runtime environment, and disables ACA platform auth on the demo API.
2. Build the .NET services and containerize them (pushed to the dedicated Container Registry)
3. Deploy backend containers to Azure Container Apps (the postprovision hook has already applied the API's model, CORS, auth, and MCP settings)
4. Build the React frontend (`npm run build`) **with `VITE_API_ORIGIN` injected from the provisioned API origin** and deploy `dist/` to Azure Static Web Apps
5. Output the frontend URL and connection strings

Because provisioning runs first, both the API-origin injection into the frontend build and the frontend-origin injection into API CORS happen automatically in a single `azd up` — no manual two-pass step is required. Runtime settings are asserted by the postprovision hook rather than relying on `azure.yaml` service environment-variable updates, which are not consistently applied to pre-existing ACA targets. See [Build-time API origin injection](#build-time-api-origin-injection-vite_api_origin) for the one ordering caveat.

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
    ├── storage.bicep           # Standard_LRS StorageV2 account + Azure Files share (durable app data)
    ├── container-apps-env.bicep # Container Apps Environment (+ Azure Files storage registration)
    ├── container-apps.bicep     # API, MCP server, Teams Bot container apps
    └── static-web-app.bicep    # Standard-SKU static frontend hosting (direct-CORS to the API)
```

### azd environment aliases (Bicep outputs → azure.yaml / Vite build)

`azure.yaml` and the frontend build consume three azd environment values that
`infra/main.bicep` emits as outputs. The output names match the consumers exactly
so azd's `${...}` substitution and Vite's `import.meta.env` resolve them:

| Output (Bicep) | Source | Consumed by |
|----------------|--------|-------------|
| `VITE_API_ORIGIN` | API container app origin | Frontend Vite build → `import.meta.env.VITE_API_ORIGIN` → absolute `/hubs/telemetry` |
| `RETAIL_PULSE_FRONTEND_ORIGIN` | Static Web App origin | `api` `Security__AllowedOrigins__0` (CORS) |
| `MCP_SERVER_BASE_URL` | MCP server container app origin | `api` `McpServer__BaseUrl` |

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

The AI Gateway (APIM) is deployed separately using the existing Bicep in `deploy/apim-ai-gateway/`. After `azd up`, configure the API service to point to your APIM endpoint:

```bash
# Set APIM endpoint as the OpenAI endpoint for the API service
azd env set AZURE_OPENAI_ENDPOINT https://your-apim.azure-api.net/openai
```

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

### Managed identity → Azure OpenAI requires an out-of-band role assignment

Each Container App (`api`, `mcpserver`, `teamsbot`) is provisioned with a
**system-assigned managed identity** (`infra/modules/container-apps.bicep`), and the
`api` service sets `OpenAI__UseManagedIdentity=true` (`azure.yaml`), so the API
authenticates to the model endpoint with `DefaultAzureCredential` instead of an API key.

**The model endpoint is external to this Bicep** — it is supplied via
`AZURE_OPENAI_ENDPOINT` and is not provisioned here — so the infra **cannot** create the
required RBAC. After `azd up` you must grant the API's principal access on the target
resource, e.g. for a direct Azure OpenAI resource:

```bash
az role assignment create \
  --assignee <AZURE_API_APP principalId> \
  --role "Cognitive Services OpenAI User" \
  --scope <Azure OpenAI resource id>
```

The API's principal id is exposed as the `apiPrincipalId` output of the container-apps
module. **If you point `AZURE_OPENAI_ENDPOINT` at the APIM AI Gateway instead of a direct
Azure OpenAI resource**, `DefaultAzureCredential` bearer tokens will only work if APIM is
configured to accept AAD tokens (or to inject the upstream key itself); otherwise set
`OpenAI__UseManagedIdentity=false` and provide a subscription key via `OpenAI__ApiKey`.

### Scale-to-zero (cold start) and in-memory state loss

All three Container Apps use `minReplicas: 0` / `maxReplicas: 1`. Consequences:

- **Cold start:** the first request after an idle period pays a container start +
  .NET warm-up penalty (several seconds).
- **In-memory state is volatile:** the API keeps trace spans, the streaming ring
  buffer, the conversation-export sessions, and the live telemetry panel in
  singletons. When the API scales to zero these reset. Background services
  (proactive alerts) only run while a replica is live. **Durable** SQLite stores
  (cost, audit, memory, approvals, alerts) are unaffected — they live on the
  mounted Azure Files share (see below).
- **SignalR:** the Teams Bot holds a persistent SignalR connection to the API telemetry
  hub, and the frontend connects to `/hubs/telemetry` and `/hubs/streaming`. An active
  SignalR/WebSocket connection keeps the API replica warm; once all clients disconnect
  the API can scale to zero and in-flight hub state is dropped.

To keep the demo continuously warm (at higher cost) set the API's `minReplicas` to `1`
in `infra/modules/container-apps.bicep`.

### Durable API storage and ephemeral MCP storage — data semantics

The **API** persists its SQLite databases (audit, cost, memory, approvals, alerts)
to a durable **Azure Files** share, mounted at `/mnt/retailpulse-data` and selected
via `RETAIL_PULSE_DATA_DIRECTORY`. This history now **survives** container restarts,
new revisions, and full scale-to-zero cycles — fixing the earlier defect where the
API wrote under `Path.GetTempPath()` and lost everything whenever a fresh replica
started. `DataDirectoryResolver` **fails fast** if that path is missing or
unwritable rather than silently reverting to ephemeral temp storage, so a broken
mount surfaces as a startup error instead of quietly losing data.

Because the deployed API runs with `ASPNETCORE_ENVIRONMENT=Development`, this
fail-fast must **not** be gated on the environment. Deployment sets an explicit,
environment-agnostic `RETAIL_PULSE_REQUIRE_DURABLE_STORAGE=true` env var alongside
the mount — emitted by `main.bicep`, applied in `container-apps.bicep`, and
re-asserted by the postprovision hooks. When the flag is truthy the resolver
enforces the durable path regardless of environment, and a malformed value
(anything other than `true`/`false`/`1`/`0`, case-insensitive) throws instead of
silently downgrading. Local development leaves the flag unset (or `false`) and may
fall back to a temp directory. PR #23 will later flip the deployed API to
`Production`, but this durability guarantee is independent of that change and holds
against the current `Development` deployment and future config drift.

The **MCP server** still writes its seeded retail dataset (`retailpulse.db`) under
the OS temp directory. That is intentional and safe: it **re-seeds from
`tenant.yaml` on first run**, so the demo data regenerates automatically and needs
no durable volume.

Constraints and cost:

- **Single writer:** SQLite over SMB is safe only for one replica, so the API keeps
  `maxReplicas: 1`. Every mounted store opens through the centralized `SqliteMount`
  helper, which applies the SMB-safe pragmas in order: `busy_timeout=10000` first
  (so the journal switch waits rather than throwing `SQLITE_BUSY` under contention),
  then `journal_mode=DELETE` (rollback journal, not WAL, whose `-shm` file is
  unsupported over SMB), then `synchronous=FULL` (required for durability with a
  DELETE journal — `NORMAL` is only safe under WAL). Do **not** raise `maxReplicas`
  while the stores share one mount.
- **Cost:** one Standard_LRS StorageV2 account with a 1 GiB `TransactionOptimized`
  file share — a few cents/month at demo volumes.
- **Cleanup:** the account lives in the demo resource group and is destroyed by
  `azd down` with the rest of the stack. Only bounded app-data SQLite files are
  written to it — never secrets or credentials (the account key is fetched inside
  Bicep via `listKeys()` and never stored in the azd environment, logs, or repo).
- **First deployment:** the durable store starts **empty**. Any history that
  accumulated under the old ephemeral temp path is not migrated and is
  unrecoverable — this is expected.

### Public-demo auth posture

The postprovision hook sets `Security__RequireAuth=false`, disables ACA platform auth,
and runs the API/MCP/bot with their fixed synthetic Development identities. When auth is disabled the API installs
an allow-all default authorization policy, which is what lets the SignalR hubs
(`.RequireAuthorization()`) and other protected endpoints serve the anonymous demo
frontend. Because ingress is **external**, this means the API — and the paid model calls
behind it — is **publicly reachable without authentication**, protected only by the
built-in rate limiter. Acceptable for a throwaway demo; do **not** use this posture for
anything with real data or cost exposure. Set `Security__RequireAuth=true` (and wire an
identity provider) for non-demo environments.

### Frontend → API routing and SignalR

The postprovision hook idempotently links the ACA API as the Static Web App backend,
so existing relative `/api/*` requests remain same-origin and are proxied to ACA.
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
idempotently links SWA `/api` traffic to ACA, and applies the synthetic-demo runtime
settings (managed-identity model endpoint, MCP/API URLs, CORS origin, demo auth mode,
and Teams bot API URL).

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
