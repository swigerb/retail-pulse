# Deploying Retail Pulse with Azure Developer CLI (`azd`)

Retail Pulse supports one-command deployment to Azure using the [Azure Developer CLI](https://learn.microsoft.com/azure/developer/azure-developer-cli/).

## Architecture

| Component | Azure Service | Notes |
|-----------|--------------|-------|
| API | Azure Container Apps | .NET minimal API + AI agent |
| MCP Server | Azure Container Apps | MCP tools provider |
| Teams Bot | Azure Container Apps | Microsoft Agents SDK |
| Frontend | Azure App Service | React/Vite static build (Node 20 LTS) |
| Monitoring | Application Insights + Log Analytics | Full OpenTelemetry pipeline |
| AI Gateway | Azure API Management | Existing APIM Bicep in `deploy/apim-ai-gateway/` |

## Prerequisites

- [Azure Developer CLI](https://aka.ms/azd-install) (v1.11+)
- [Azure CLI](https://aka.ms/install-azure-cli)
- [.NET 10 SDK](https://dot.net/download)
- [Node.js 20+](https://nodejs.org)
- An Azure subscription with Contributor access

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
1. Provision all Azure resources (Container Apps Environment, App Service, App Insights, Log Analytics)
2. Build the .NET services and containerize them
3. Build the React frontend (`npm run build`)
4. Deploy backend containers to Azure Container Apps
5. Deploy the frontend to Azure App Service
6. Output the frontend URL and connection strings

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
    ├── container-apps-env.bicep # Container Apps Environment
    └── app-service.bicep       # App Service Plan + Frontend Web App
```

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

## Troubleshooting

| Issue | Solution |
|-------|----------|
| `azd up` fails on provision | Check `azd env get-values` for missing required vars |
| Frontend 404 after deploy | Ensure `npm run build` completed (check `azd-hooks/preprovision.sh`) |
| Container Apps unhealthy | Run `azd monitor` → check container logs in Log Analytics |
| CORS errors on frontend | API Container App needs external ingress enabled |
