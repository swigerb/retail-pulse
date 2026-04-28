# 🥃 Patron Pulse

> AI-powered brand analytics for Bacardi — a pro-code agentic demo built with .NET Aspire, Microsoft Agent Framework (MAF), and MCP

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![React 19](https://img.shields.io/badge/React-19-61DAFB?logo=react)](https://react.dev/)
[![Aspire](https://img.shields.io/badge/Aspire-Orchestrator-6C3BAA)](https://learn.microsoft.com/dotnet/aspire/)
[![OpenAI](https://img.shields.io/badge/OpenAI-GPT--5.4--mini-412991?logo=openai)](https://openai.com/)
[![MCP](https://img.shields.io/badge/MCP-Protocol-green)](https://modelcontextprotocol.io/)

Patron Pulse is a real-time agentic application that lets Bacardi brand managers ask natural-language questions about brand performance, depletion trends, field sentiment, and **Three-Tier Distribution pipeline analysis**. A **multi-agent system** powered by GPT-5.4-mini uses the MAF orchestrator to reason over questions, delegate to specialist agents (like the **Foundry Shipment Agent** for A2A pipeline analysis), and call MCP tools to fetch data — streaming every step back to the browser with full distributed tracing through .NET Aspire and **Azure Application Insights**.

---

## Architecture

![Patrón Pulse Architecture](docs/architecture-diagram.jpg)

## Demo Walkthrough

See the [complete demo script](docs/demo-walkthrough.md) for a step-by-step presentation guide (~10 minutes).

## Teams Integration

Patron Pulse can be deployed as a **Microsoft Teams bot**, enabling brand managers to ask questions directly in Teams and receive rich Adaptive Card responses with telemetry insights and chart visualizations.

**Key Features:**
- **Adaptive Card responses** with collapsible telemetry
- **SSO authentication** via Azure AD (user context in API calls)
- **Chart visualizations** rendered inline in Teams
- **Conversation memory** across sessions
- **Real-time telemetry** via SignalR

> **Local Development:** You can test the bot locally without an Azure app registration using the [Bot Framework Emulator](https://github.com/microsoft/BotFramework-Emulator/releases). See the [Teams Setup Guide](docs/teams-setup.md#testing-locally-without-app-registration) for details.

See [Teams Setup Guide](docs/teams-setup.md) for step-by-step instructions.

## Charts & Visualizations

Charts are rendered **client-side** with no server-side image generation. The LLM emits structured `ChartSpec` JSON via the `CreateChart` tool, and each client renders natively:

- **Web UI** - Interactive [Recharts](https://recharts.org/) SVG charts with tooltips and hover effects
- **Teams** - Native Adaptive Card chart elements (`Chart.Line`, `Chart.Donut`, `Chart.HorizontalBar`, etc.)

**9 chart types:** line, bar, grouped bar, stacked bar, horizontal bar, pie, donut, gauge, and table.

See [Chart Rendering Guide](docs/chart-rendering.md) for architecture, ChartSpec model, and supported types.

## Deploy APIM AI Gateway (One-Time Setup)

Before running the app, deploy the inference API to your APIM instance:

```powershell
cd deploy/apim-ai-gateway

# Deploy the Bicep template and auto-configure user secrets
.\deploy-apim-api.ps1 -SetUserSecrets

# Or deploy without auto-configuring secrets
.\deploy-apim-api.ps1
```

This creates:
- An inference API on your APIM with Azure OpenAI spec
- A backend pointing to Azure AI Foundry (managed identity auth)
- AI Gateway policies: token rate limiting (10K TPM), token metrics
- An APIM subscription key for the app to use

## Deploy Foundry Shipment Agent (Optional)

> **Note:** The Foundry agent is **disabled by default**. The app runs fully without it using a local `LocalShipmentAnalyzer`. Enable it by setting `FoundryAgent:Enabled` to `true` in configuration.

Deploy the "Bacardi Shipment Specialist" agent to your Azure AI Foundry project:

```powershell
# Deploy the agent (uses DefaultAzureCredential — ensure you're logged in via az login)
dotnet run --project deploy/foundry-agent/DeployAgent.csproj

# The script outputs the agent ID — set it in user secrets:
dotnet user-secrets set "FoundryAgent:ShipmentAgentId" "<agent-id>" --project src/RetailPulse.Api
dotnet user-secrets set "FoundryAgent:ProjectEndpoint" "https://your-foundry.services.ai.azure.com/api/projects/your-project" --project src/RetailPulse.Api
```

This creates a persistent agent in Azure AI Foundry that the MAF orchestrator calls via the `Azure.AI.Agents.Persistent` SDK for Three-Tier Distribution pipeline analysis.

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js 20+](https://nodejs.org/)
- An OpenAI API key (or Azure OpenAI endpoint)

### 1. Configure your API keys

```bash
# Set your APIM subscription key (from the repo root)
dotnet user-secrets set "OpenAI:ApiKey" "<your-apim-subscription-key>" --project src/RetailPulse.Api
```

> **Default mode:** All LLM calls route through your APIM AI Gateway at  
> `https://bsapim-dev-northcentralus-001.azure-api.net/inference`  
> To bypass APIM and go direct to Azure AI Foundry, override the endpoint:
> ```bash
> dotnet user-secrets set "OpenAI:Endpoint" "https://bs-dev-swedencentral-aoai.services.ai.azure.com/api/projects/bs-dev-swedencentral-aoai-project/openai/v1" --project src/RetailPulse.Api
> ```

### 2. Start everything (Aspire orchestrator)

```bash
# Install frontend dependencies (first time only)
cd src/RetailPulse.Web && npm install && cd ../..

# Start the full stack
dotnet run --project src/RetailPulse.AppHost
```

This starts **everything** in one command:
- **API** at `:5100`
- **MCP Server** at `:5200`
- **Teams Bot** at `:5300`
- **React Frontend** at `:5173` (click the URL in the Aspire dashboard)
- **Aspire Dashboard** — look for the `Login to the dashboard at https://localhost:XXXXX/login?t=...` URL in the terminal output

### 3. Open the app

Navigate to [http://localhost:5173](http://localhost:5173) (or click the frontend URL in the Aspire dashboard) and start asking questions!

**Try these queries:**
- *"How is Patrón Silver performing in Florida?"*
- *"Analyze the shipment pipeline for Patron Silver in Florida"* ← **The Pipeline Clog "wow" moment**
- *"Compare Angel's Envy performance across New York and Illinois"*
- *"What's the field sentiment for Grey Goose in California?"*
- *"Show me the Three-Tier tension for Patron Silver nationally"*

### One-click setup

```powershell
# Windows
.\deploy\deploy.ps1

# Linux/Mac
./deploy/deploy.sh
```

## Project Structure

```
retail-pulse/
├── src/
│   ├── RetailPulse.AppHost/          # Aspire orchestrator
│   │   └── AppHost.cs                # Resource definitions & ports
│   ├── RetailPulse.Api/              # Agent API service
│   │   ├── Agents/                   # MAF agent implementation
│   │   ├── Hubs/                     # SignalR telemetry hub
│   │   ├── Tools/                    # MCP tool wrappers (ChartDataTool.cs, DepletionStatsTool.cs, FieldSentimentTool.cs, FoundryShipmentAgent.cs, LocalShipmentAnalyzer.cs, ShipmentStatsTool.cs)
│   │   ├── Models/                   # Request/response models
│   │   └── prompts.yaml              # Agent prompt configuration
│   ├── RetailPulse.McpServer/        # MCP server (data tools)
│   │   ├── Tools/                    # MCP tool definitions (GetShipmentStatsTool.cs)
│   │   └── Data/                     # Simulated Bacardi data
│   ├── RetailPulse.ServiceDefaults/  # Shared Aspire defaults
│   └── RetailPulse.Web/              # React/Vite/TypeScript frontend
│       └── src/
│           ├── components/           # ChartRenderer, ChatPanel, Dashboard, PatronLogo, SpanTimeline, TelemetryPanel
│           └── services/             # API client, SignalR hub connection
├── ai-gateway-dev-portal/            # AI Gateway Dev Portal (APIM observability)
├── deploy/                           # Deployment scripts
├── docs/                             # Documentation
└── RetailPulse.slnx                  # Solution file
```

## Tech Stack

| Layer | Technology | Purpose |
|-------|-----------|---------|
| **Orchestration** | .NET Aspire | Service discovery, health checks, dashboard |
| **Agent** | Microsoft.Extensions.AI (MAF) | AI agent with tool calling |
| **Model** | GPT-5.4-mini (via APIM AI Gateway) | Reasoning and natural language |
| **Tools** | Model Context Protocol (MCP) | Standardized tool access |
| **Frontend** | React 19 + Vite + TypeScript | Interactive dashboard |
| **Real-time** | SignalR | Live telemetry streaming |
| **Multi-Agent** | Azure AI Foundry Agent Service | Foundry-hosted Shipment Specialist agent (optional, disabled by default) |
| **Observability** | OpenTelemetry + Aspire Dashboard | Distributed traces, metrics, logs |
| **Monitoring** | Azure Application Insights | Production telemetry, traces, and metrics |
| **Gateway** | Azure API Management | Token metering, rate limiting, audit |

## Configuration

| Setting | User Secret Key | Default |
|---------|----------------|---------|
| APIM / OpenAI Key | `OpenAI:ApiKey` | *(required)* |
| LLM Endpoint | `OpenAI:Endpoint` | `https://bsapim-dev-northcentralus-001.azure-api.net/inference` |
| MCP Server URL | `McpServer:BaseUrl` | `http://localhost:5200` |
| Foundry Enabled | `FoundryAgent:Enabled` | `false` |
| Foundry Project Endpoint | `FoundryAgent:ProjectEndpoint` | `https://bs-dev-swedencentral-aoai...` |
| Foundry Shipment Agent ID | `FoundryAgent:ShipmentAgentId` | *(set by deploy script)* |

## Ports

| Service | Port | URL |
|---------|------|-----|
| React Frontend | 5173 | http://localhost:5173 |
| Patron Pulse API | 5100 | http://localhost:5100 |
| MCP Server | 5200 | http://localhost:5200 |
| Teams Bot | 5300 | http://localhost:5300 |
| Aspire Dashboard | dynamic | See terminal output for login URL |

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## License

This project is for demonstration purposes. All simulated data is fictional and does not represent actual Bacardi business data.
