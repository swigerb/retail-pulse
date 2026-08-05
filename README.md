<div align="center">
  <img src="docs/retail-pulse-logo.jpg" alt="Retail Pulse Logo" width="400" />
</div>

# Retail Pulse

> AI-powered brand analytics for retail & consumer goods

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![React 19](https://img.shields.io/badge/React-19-61DAFB?logo=react)](https://react.dev/)
[![Aspire 13.3](https://img.shields.io/badge/Aspire-13.3.0-6C3BAA)](https://learn.microsoft.com/dotnet/aspire/)
[![CI](https://github.com/swigerb/retail-pulse/actions/workflows/ci.yml/badge.svg)](https://github.com/swigerb/retail-pulse/actions/workflows/ci.yml)
[![Coverage](https://img.shields.io/badge/coverage-70%25_target-brightgreen)](./coverage-report)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## Overview

Retail Pulse is an AI-powered brand intelligence platform that uses agentic AI to analyze depletion trends, shipment dynamics, and field sentiment for retail & CPG brands. A **multi-agent system** powered by the Microsoft AI Framework (MAF) and Model Context Protocol (MCP) reasons over natural-language questions, delegates to specialist agents, and calls MCP tools to fetch data, streaming every step back to the browser with full distributed tracing.

**Key differentiator:** Retail Pulse is **tenant-configurable**. Define your company, brands, regions, and theme in a single `tenant.yaml` file and the entire platform adapts. No code changes required.

**Built with:** .NET Aspire, Microsoft AI Framework (MAF), Model Context Protocol (MCP), React + Vite, Azure Container Apps, Azure Static Web Apps, Azure Bot Framework, and Azure API Management (AI Gateway).

---

## Architecture

![Retail Pulse Architecture](docs/architecture-diagram.png)

### Solution Architecture (6 projects + frontend)

| Project | Purpose |
|---------|---------|
| **RetailPulse.AppHost** | Aspire orchestrator - wires McpServer → Api → TeamsBot → Frontend |
| **RetailPulse.Api** | Minimal API + AI agent (Azure OpenAI via APIM), SignalR telemetry hub |
| **RetailPulse.McpServer** | MCP tool host - SQLite-backed depletions, shipments, sentiment (read + write) |
| **RetailPulse.Contracts** | Shared DTOs + tenant config model |
| **RetailPulse.ServiceDefaults** | OTel, health checks, resilience defaults |
| **RetailPulse.TeamsBot** | Microsoft Agents SDK - calls API, renders adaptive cards |
| **RetailPulse.Web** | React/Vite/TypeScript dashboard - Fluent UI, Recharts, SignalR |

### Key Patterns

- **Tenant config** (`tenant.yaml`) drives everything - prompts, data seeding, UI branding. Loaded by `FileTenantProvider`, injected into system prompts.
- **AI Gateway** - APIM fronts Azure OpenAI/Foundry with token limiting, metrics, managed identity auth. Deployed via Bicep.
- **Real-time telemetry** - SignalR hub broadcasts agent spans to the frontend. Dashboard shows live tool calls, agent thoughts, and timing.
- **MCP tools** - SQLite-backed retail metrics (depletions, shipments, field sentiment) shaped by tenant brands/regions. Agent can **read and write** data via `UpdateMetrics` tool.
- **Foundry delegation** - can hand off to persistent Azure AI Foundry agents for deeper analysis.
- **Frontend** - single-page dashboard with ChatPanel, suggested retail prompts (grocery, QSR, home improvement, office supply, furniture), chart rendering, and span timeline.

### Three-Tier Distribution Model

Retail Pulse is designed for industries using a Three-Tier distribution model (manufacturer → distributor → retailer). The AI agent can detect **pipeline clogs** - where shipments and sell-through diverge - and correlate them with field sentiment data.

---

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js 20+](https://nodejs.org/)
- An OpenAI API key (or Azure OpenAI endpoint)

### 1. Clone the repo

```bash
git clone https://github.com/swigerb/retail-pulse.git
cd retail-pulse
```

### 2. Configure tenant.yaml (or use the included Apex Retail Group sample)

The repo ships with a sample `tenant.yaml` for **Apex Retail Group**, a fictional multi-category retail conglomerate with 12 brands across 6 categories. To customize for your own brand, see the [Tenant Configuration Guide](docs/tenant-configuration.md).

### 3. Set up Azure OpenAI credentials

```bash
dotnet user-secrets set "OpenAI:ApiKey" "<your-api-key>" --project src/RetailPulse.Api
```

> To use Azure OpenAI directly (bypassing APIM), also set the endpoint:
> ```bash
> dotnet user-secrets set "OpenAI:Endpoint" "<your-azure-openai-endpoint>" --project src/RetailPulse.Api
> ```

> **Every setting in one place:** [`src/RetailPulse.Api/appsettings.json`](src/RetailPulse.Api/appsettings.json)
> is the checked-in reference config — loaded in every environment and
> documenting all sections (OpenAI, Security, FoundryAgent, ToolCache,
> TokenPricing, Observability, …) with safe defaults. Edit it directly for
> non-secret tweaks; keep real secrets in user-secrets, **not** in this
> committed file. For deployment, see
> [`appsettings.Production.json`](src/RetailPulse.Api/appsettings.Production.json),
> which lists the production surface with placeholders (supply secrets via
> environment variables or Azure Key Vault). Each service
> (`AppHost`, `McpServer`, `TeamsBot`) has its own committed
> `appsettings.json` documenting just that service's settings.

### 4. Run with Aspire

```bash
# Install frontend dependencies (first time only)
cd src/RetailPulse.Web && npm install && cd ../..

# Start the full stack
dotnet run --project src/RetailPulse.AppHost
```

### 5. Open the React dashboard

Navigate to [http://localhost:5173](http://localhost:5173) and start asking questions!

**Try these queries (using the Apex Retail Group sample tenant):**

**🥃 Spirits:**
- *"How is Sierra Gold Tequila performing in the Northeast?"*
- *"Analyze the shipment pipeline for Ridgeline Bourbon in the Midwest"*

**🛒 Grocery:**
- *"How are FreshMart depletions trending in the Northeast this quarter?"*
- *"Compare Harvest Table vs FreshMart sell-through rates by region"*

**🍔 Quick-Serve Restaurants:**
- *"How is Apex Grill performing in the Southwest this quarter?"*
- *"Compare Coastline Tacos vs Apex Grill depletions across all regions"*

**🏠 Home Improvement:**
- *"Show me Pinnacle Hardware depletion stats in the Midwest for Q1"*
- *"How is Summit Outdoor performing in the Southeast vs West Coast?"*

**📎 Office Supply:**
- *"How are ClearDesk depletions trending in the Northeast this quarter?"*

**🛋️ Furniture:**
- *"Show me Urban Living depletion trends across all regions this quarter"*
- *"Compare Foundry Home vs Urban Living performance in the West Coast"*

**📈 Chart Rendering (test all chart types):**
- *"Create a line chart showing Sierra Gold Tequila depletion trends across all regions"* → line chart
- *"Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast"* → bar chart
- *"Create a pie chart showing market share breakdown for our grocery brands nationally"* → pie chart
- *"Show a grouped bar chart comparing FreshMart and Harvest Table across all regions"* → grouped bar
- *"Create a donut chart of Apex Grill's variant mix in the Southwest"* → donut chart
- *"Show a horizontal bar chart ranking all brands by depletion growth rate"* → horizontal bar
- *"Create a table showing depletion stats for all home improvement brands by region"* → table
- *"Show a gauge chart for Pinnacle Hardware's inventory health in the Midwest"* → gauge

### One-click setup

```powershell
# Windows
.\deploy\deploy.ps1

# Linux/Mac
./deploy/deploy.sh
```

---

## Tenant Configuration

Retail Pulse reads `tenant.yaml` at the repo root to configure the entire platform:

```yaml
company: "Apex Retail Group"
industry: "Multi-Category Retail"
brands:
  - name: "Sierra Gold Tequila"
    category: "Spirits"
    variants: ["Blanco", "Reposado", "Añejo", "Extra Añejo"]
    priceSegment: "Premium"
  - name: "FreshMart"
    category: "Grocery"
    variants: ["Organic Produce", "Bakery", "Deli", "Frozen"]
    priceSegment: "Standard"
  - name: "Apex Grill"
    category: "Quick-Serve Restaurant"
    variants: ["Burgers", "Chicken", "Breakfast", "Beverages"]
    priceSegment: "Standard"
  # ... 12 brands across 6 categories
regions:
  - "Northeast"
  - "Southeast"
  - "Midwest"
  - "Southwest"
  - "West Coast"
  - "Pacific Northwest"
theme:
  primaryColor: "#1B4D7A"
  accentColor: "#E8A838"
```

The included **Apex Retail Group** sample tenant demonstrates a multi-category retail conglomerate with **12 brands** across **6 categories**:

| Category | Brands |
|----------|--------|
| 🥃 Spirits | Sierra Gold Tequila, Ridgeline Bourbon, Summit Vodka |
| 🛒 Grocery | FreshMart, Harvest Table |
| 🍔 Quick-Serve Restaurants | Apex Grill, Coastline Tacos |
| 🏠 Home Improvement | Pinnacle Hardware, Summit Outdoor |
| 📎 Office Supply | ClearDesk |
| 🛋️ Furniture | Urban Living, Foundry Home |

All brands operate across **6 regions**: Northeast, Southeast, Midwest, Southwest, West Coast, and Pacific Northwest. See the [Tenant Configuration Guide](docs/tenant-configuration.md) for full schema reference and examples for different industries.

---

## Technology stack

| Layer | Technology | Version | Purpose |
|-------|-----------|---------|---------|
| **Orchestration** | .NET Aspire | 13.3.0 | Service discovery, health checks, dashboard |
| **Runtime** | .NET | 10 | Backend services |
| **Agent** | Microsoft AI Framework (MAF) | — | AI agent with tool calling |
| **Model** | GPT-5.4-mini (via APIM AI Gateway) | — | Reasoning and natural language |
| **Tools** | Model Context Protocol (MCP) | — | Standardized tool access |
| **Data** | SQLite (Microsoft.Data.Sqlite) | — | Mutable tenant-seeded metrics store |
| **Frontend** | React + Vite + TypeScript | 19 / 8 / 6 | Interactive dashboard |
| **UI Components** | Fluent UI React | 9.x | Design system |
| **Real-time** | SignalR | 10.x | Live telemetry streaming |
| **Multi-Agent** | Azure AI Foundry Agent Service | — | Foundry-hosted Shipment Specialist (optional) |
| **Observability** | OpenTelemetry + Aspire Dashboard | — | Distributed traces, metrics, logs |
| **Monitoring** | Azure Application Insights | — | Production telemetry and traces |
| **Gateway** | Azure API Management | — | Token metering, rate limiting, audit |
| **Backend hosting** | Azure Container Apps | — | Runs the API, MCP Server, and Teams Bot as containers; scales to zero when idle |
| **Frontend hosting** | Azure Static Web Apps | — | Serves the React/Vite static build; proxies REST `/api` requests to the linked Container Apps backend while SignalR connects directly to the Container Apps API |
| **Container registry** | Azure Container Registry (Basic) | — | Stores backend images; Container Apps pull them with managed identity (no admin secrets) |
| **Testing** | xUnit + Vitest | — | Backend + frontend tests |

---

## Project Structure

```
retail-pulse/
├── tenant.yaml                       # Tenant configuration (brands, regions, theme)
├── RetailPulse.slnx                  # Solution file
├── src/
│   ├── RetailPulse.AppHost/          # Aspire 13.3.0 orchestrator
│   ├── RetailPulse.Api/              # Agent API service
│   │   ├── Agents/                   # MAF agent implementation
│   │   ├── Caching/                  # MCP response cache (DelegatingHandler)
│   │   ├── Consensus/                # Multi-agent council orchestration
│   │   ├── Health/                   # Readiness/liveness health checks
│   │   ├── Hubs/                     # SignalR telemetry hub (session-scoped groups)
│   │   ├── Middleware/               # Exception handling, correlation ID, security headers, auth
│   │   ├── Prompts/                  # PromptTemplateEngine (tenant hydration)
│   │   ├── Security/                 # Audit log, security services
│   │   ├── Telemetry/                # Custom business metrics (OpenTelemetry)
│   │   ├── Tools/                    # MCP tool wrappers
│   │   ├── Validation/               # Input validation (ChatRequestValidator)
│   │   └── prompts.yaml              # Agent prompt configuration (tenant-templated)
│   ├── RetailPulse.McpServer/        # MCP server (data tools)
│   │   ├── Tools/                    # MCP tool definitions (parameterized queries)
│   │   └── Data/                     # SQLite-backed tenant-driven metrics
│   ├── RetailPulse.Contracts/        # Shared models (immutable config, ChartSpec, etc.)
│   │   └── ValueObjects/             # BrandName, Region, SessionId
│   ├── RetailPulse.ServiceDefaults/  # Shared Aspire defaults (OTel, health, resilience)
│   ├── RetailPulse.TeamsBot/         # Microsoft Teams bot (JWT-validated, Adaptive Cards)
│   └── RetailPulse.Web/              # React/Vite/TypeScript frontend
│       ├── src/components/           # ChatPanel, SpanTimeline, Charts, ErrorBoundary
│       └── src/hooks/                # SignalR connection, telemetry
├── tests/
│   ├── RetailPulse.Tests/            # xUnit + integration tests (1815 passing)
│   ├── RetailPulse.LoadTests/        # NBomber load test scenarios
│   └── RetailPulse.Benchmarks/       # BenchmarkDotNet performance suite
├── deploy/                           # Deployment & infrastructure
│   ├── deploy.ps1 / deploy.sh        # One-click local deployment scripts
│   ├── apim-ai-gateway/              # APIM AI Gateway Bicep (main.bicep, policy.xml)
│   ├── foundry-agent/                # Foundry agent deployment
│   └── generate-traffic.ps1          # Load testing
├── infra/                            # Azure infrastructure (Bicep, used by azd)
│   ├── main.bicep                    # Subscription-scoped orchestrator
│   └── modules/                      # Monitoring, Container Apps Env, Container Apps, Container Registry, Static Web App
├── azd-hooks/                        # Azure Developer CLI lifecycle hooks
├── azure.yaml                        # Azure Developer CLI project file
├── ai-gateway-dev-portal/            # AI Gateway Dev Portal (APIM observability)
└── docs/                             # Documentation
```

---

## Features

### Teams Integration

Retail Pulse can be deployed as a **Microsoft Teams bot** with Adaptive Card responses, SSO authentication, and chart visualizations rendered inline.

See [Teams Setup Guide](docs/teams-setup.md) for step-by-step instructions.

### Charts & Visualizations

Charts are rendered **client-side**. The LLM emits structured `ChartSpec` JSON and each client renders natively:

- **Web UI** - Interactive [Recharts](https://recharts.org/) SVG charts
- **Teams** - Native Adaptive Card chart elements

**9 chart types:** line, bar, grouped bar, stacked bar, horizontal bar, pie, donut, gauge, and table. See [Chart Rendering Guide](docs/chart-rendering.md).

### APIM AI Gateway (Optional)

Route all LLM calls through Azure API Management for token metering, rate limiting, and governance. See [AI Gateway Integration](docs/ai-gateway-integration.md).

### Foundry Shipment Agent (Optional)

Deploy a specialist agent to Azure AI Foundry for Three-Tier Distribution pipeline analysis. Disabled by default - the app runs fully without it using a local analyzer. See [Architecture](docs/architecture.md).

### Enterprise Hardening

Retail Pulse implements enterprise-grade patterns:

- **Resilience** — Circuit breaker (5 failures/30s), retry with exponential backoff + jitter, dead-letter queue
- **Observability** — Correlation IDs, custom OpenTelemetry metrics, SLO/SLI definitions, health checks
- **Security** — CSP/HSTS/X-Frame-Options headers, input validation, SHA256 hash-chain audit log
- **Performance** — MCP response cache, keyword fast-path routing, lightweight council voting, cache warming
- **API Versioning** — `/api/v1/chat` with Sunset header on legacy endpoint
- **Testing** — 1815+ unit/integration/contract/E2E tests, load tests, mutation testing, benchmarks

---

## Demo Walkthrough

See the [complete demo script](docs/demo-walkthrough.md) for a step-by-step presentation guide (~10 minutes).

---

## Azure Deployment

### Azure Developer CLI (`azd up`) — Recommended

The fastest way to deploy Retail Pulse to Azure:

```bash
azd auth login
azd up
```

This deploys:
- **Backend** (API, MCP Server, Teams Bot) → Azure Container Apps. Each app runs under a system-assigned managed identity and scales to zero when idle.
- **Frontend** (React/Vite static build) → Azure Static Web Apps. The build injects the Container Apps API origin, so the SPA calls the API directly and opens the SignalR telemetry connection against that origin. The Static Web App also links the API as its `/api` backend, so relative `/api/*` calls stay same-origin.
- **Container images** → a dedicated Azure Container Registry (Basic). Container Apps pull images with their managed identities, so no registry admin secrets are stored.
- **Monitoring** → Application Insights + Log Analytics

See [docs/deployment-azd.md](docs/deployment-azd.md) for full documentation.

### APIM AI Gateway

The `deploy/apim-ai-gateway/` directory contains Bicep templates to deploy Azure API Management as an AI Gateway fronting Azure OpenAI:

```powershell
cd deploy/apim-ai-gateway
.\deploy-apim-api.ps1
```

This deploys:
- **main.bicep** — APIM instance with managed identity
- **policy.xml** — Token metering, rate limiting, content safety policies
- **role-assignment.bicep** — RBAC for APIM → Azure OpenAI access
- **a2a-api.bicep** / **mcp-api.bicep** — Agent-to-agent and MCP API definitions

### One-Click Local Deploy

```powershell
# Windows
.\deploy\deploy.ps1

# Linux/Mac
./deploy/deploy.sh
```

> **Note:** Deployment scripts use user secrets for all credentials — no API keys are stored in source.

### Infrastructure Security

- Bicep outputs do not expose secrets or APIM subscription keys
- Diagnostic settings (App Insights, Log Analytics) are deployed alongside resources
- Application Insights connection strings are configured in the AppHost, not checked into `appsettings.json`

---

## CI/CD

[![CI](https://github.com/swigerb/retail-pulse/actions/workflows/ci.yml/badge.svg)](https://github.com/swigerb/retail-pulse/actions/workflows/ci.yml)

The CI pipeline runs on every push and PR to `main`:

| Job | What it does |
|-----|-------------|
| **build** | Restore, build, test (.NET 10) with coverage |
| **frontend** | npm ci, build, vitest |
| **security** | Check for vulnerable NuGet packages |
| **lint** | Verify code style (`dotnet format --verify-no-changes`) |

### Run locally

```bash
# Full backend build + test
dotnet build RetailPulse.slnx
dotnet test RetailPulse.slnx --verbosity quiet

# Frontend
cd src/RetailPulse.Web && npm run build && npx vitest run

# Load tests (optional)
cd tests/RetailPulse.LoadTests && dotnet run -c Release

# Benchmarks (optional)
dotnet run -c Release --project tests/RetailPulse.Benchmarks
```

---

## Security

| Area | Implementation |
|------|---------------|
| **API Authentication** | Auth middleware on all API endpoints |
| **Teams Bot** | JWT token validation on incoming activities |
| **MCP Server** | Parameterized SQL queries (no string interpolation) |
| **SignalR** | Telemetry scoped to session groups (no cross-session leakage) |
| **Secrets** | App Insights keys in AppHost only; user secrets for API keys |
| **Frontend** | CSP headers, URL scheme validation in Adaptive Cards |
| **Sessions** | 2-hour TTL with automatic eviction via `SessionManager` |
| **Config** | Immutable config classes (`IReadOnlyList`) with input validation |

---

## Real-Time Telemetry

The SignalR `TelemetryHub` streams agent execution spans to connected clients in real time. Clients join **session-scoped groups** so telemetry is isolated per conversation.

**What gets streamed:**
- Agent thought process and reasoning steps
- MCP tool calls with arguments and results
- Token usage and cost estimates (per-model pricing in `appsettings.json`)
- Timing data for each span

The React dashboard renders these as an interactive span timeline alongside the chat panel.

The web app navigation is intentionally minimal by default: Chat, Real-Time Telemetry, and Observability. Real-Time Telemetry is always available, while Observability remains enabled by default for the AI Gateway via Azure APIM view of costs, token usage, and metrics. Secondary demo tabs such as Campaign Planner, Competitive, Knowledge Base, Health Council, Security, Cards, Stores, Financials, and Portfolio are gated behind `VITE_FEATURE_*` flags; copy `src/RetailPulse.Web/.env.example` to `.env.local` to enable them locally.

---

## Configuration

### User Secrets

| Setting | User Secret Key | Default |
|---------|----------------|---------|
| API Key | `OpenAI:ApiKey` | *(required)* |
| LLM Endpoint | `OpenAI:Endpoint` | APIM gateway URL |
| API Version | `OpenAI:ApiVersion` | `2025-03-01-preview` |
| MCP Server URL | `McpServer:BaseUrl` | `http://localhost:5200` |
| Foundry Enabled | `FoundryAgent:Enabled` | `false` |
| Foundry Project Endpoint | `FoundryAgent:ProjectEndpoint` | *(set by deploy script)* |
| Foundry Agent Name | `FoundryAgent:ShipmentAgentName` | `Distribution Analysis Specialist` |
| App Insights | `APPLICATIONINSIGHTS_CONNECTION_STRING` | *(set in AppHost)* |

### tenant.yaml

All tenant configuration lives in `tenant.yaml` at the repo root. Changes take effect on restart — no code changes required.

| Key | Purpose |
|-----|---------|
| `company` / `industry` | Company identity, injected into agent system prompts |
| `brands[]` | Brand definitions with category, variants, and price segment |
| `regions[]` | Geographic regions for data segmentation |
| `channels[]` | Distribution channels (On-Premise, Off-Premise, E-Commerce) |
| `theme` | UI branding (primary/accent colors, logo, font) |
| `distribution` | Distribution model config (Three-Tier, distributor types) |

## Ports

| Service | Port | URL |
|---------|------|-----|
| React Frontend | 5173 | http://localhost:5173 |
| Retail Pulse API | 5100 | http://localhost:5100 |
| MCP Server | 5200 | http://localhost:5200 |
| Teams Bot | 5300 | http://localhost:5300 |
| Aspire Dashboard | dynamic | See terminal output for login URL |

---

## Tests

**1815+ tests passing** across xUnit (.NET), Vitest (frontend), load tests, and benchmarks. Covers agent telemetry, chart/tool behavior, prompt config, tenant validation, simulated metrics, session management (TTL eviction), SignalR session-group broadcasting, Teams Adaptive Card builders, and performance profiling.

```bash
# Run all .NET tests
dotnet test

# Run frontend tests
cd src/RetailPulse.Web && npm test
```

See [Testing Guide](docs/testing-guide.md) for manual testing options and test scenarios.

---

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

---

## License

MIT - see [LICENSE](LICENSE) for details.

This project is for demonstration purposes. All data is fictional, seeded from `tenant.yaml`, and does not represent actual business data.
