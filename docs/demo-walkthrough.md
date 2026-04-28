# Patron Pulse — Demo Walkthrough

> A pro-code agentic demo showcasing AI-powered brand analytics for Bacardi

This guide walks you through presenting Patron Pulse to stakeholders. Total demo time: **~10 minutes**.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js 20+](https://nodejs.org/)
- OpenAI API key (or Azure OpenAI endpoint)
- A modern browser (Edge or Chrome recommended)
- **For Act 2 (multi-agent delegation):** Foundry Shipment Agent must be enabled (`FoundryAgent:Enabled: true` in configuration)

## Quick Start (30 seconds)

### 1. Configure your API key

```bash
# From the repo root
dotnet user-secrets set "OpenAI:ApiKey" "sk-your-key-here" --project src/RetailPulse.Api
```

For Azure OpenAI:

```bash
dotnet user-secrets set "OpenAI:ApiKey" "your-azure-api-key" --project src/RetailPulse.Api
dotnet user-secrets set "OpenAI:Endpoint" "https://your-resource.openai.azure.com" --project src/RetailPulse.Api
```

### 2. Start everything

```bash
# Install frontend dependencies (first time only)
cd src/RetailPulse.Web && npm install && cd ../..

# Start the full stack
dotnet run --project src/RetailPulse.AppHost
```

This launches the API (`:5100`), MCP Server (`:5200`), Teams Bot (`:5300`), React frontend (`:5173`), and the Aspire Dashboard (URL shown in terminal output). Aspire starts the frontend automatically — no separate `npm run dev` needed.

### 3. Open the app

- **Dashboard:** [http://localhost:5173](http://localhost:5173)
- **Aspire Dashboard:** check the terminal for `Login to the dashboard at https://localhost:XXXXX/login?t=...` (keep this tab ready for Act 3)

---

## Demo Script

### Act 0: Infrastructure Setup (One-Time)

Before the first demo run, deploy the APIM AI Gateway:

1. Open a terminal in the repo root
2. Run the deployment:
   ```powershell
   .\deploy\apim-ai-gateway\deploy-apim-api.ps1 -SetUserSecrets
   ```
3. This deploys the inference API to APIM and sets your subscription key automatically
4. Verify the deployment:
   ```powershell
   # Test the endpoint directly
   $key = dotnet user-secrets list --project src/RetailPulse.Api | Select-String "OpenAI:ApiKey" | ForEach-Object { ($_ -split " = ")[1] }
   curl "https://bsapim-dev-northcentralus-001.azure-api.net/inference/openai/deployments/gpt-5.4-mini/chat/completions?api-version=2025-03-01-preview" `
     -H "api-key: $key" `
     -H "Content-Type: application/json" `
     -d '{"messages":[{"role":"user","content":"Hello"}]}'
   ```

### Act 1: "The Question" (~2 min)

**Setup:** Open the Patron Pulse dashboard at `http://localhost:5173`. The audience sees a clean chat interface with suggested queries.

**Narration:**

> *"Imagine you're a Bacardi brand manager. You just walked out of a quarterly review and need quick answers about how Patrón is performing in a key market. Instead of pulling up spreadsheets or waiting for an analyst, you just ask."*

**Action:** Click or type the first suggested query:

```
How is Patrón Silver performing in Florida?
```

**What happens (explain as it unfolds):**

1. The **telemetry panel** on the right lights up in real-time
2. Watch the span timeline populate:
   - 🧠 `thought` — The agent is reasoning about your question
   - 🔧 `tool_call` — It decides to call `GetDepletionStats` for Patrón Silver in Florida
   - 📊 `tool_result` — Data comes back: +2.1% depletion growth, -4.0% velocity change, 8.5 weeks of supply
   - 🔧 `tool_call` — It also calls `GetFieldSentiment` for rep feedback
   - 📊 `tool_result` — Sentiment data returns with distributor observations
   - 💬 `response` — The agent synthesizes everything into a coherent answer

**Key talking point:**

> *"Every single step the AI takes is visible. This isn't a black box — you can see exactly what data it accessed, what it decided, and how it formed its answer. That's the foundation of enterprise trust."*

**Expected response highlights:**
- Depletion growth of +2.1% but velocity declining at -4.0%
- 8.5 weeks of supply — flagged as "Overstocked"
- Distributor sentiment about consumer shift toward competitors at the $45 price point
- Miami on-premise velocity remains high, but suburban retail is lagging

> **Pro tip:** Click the chevron (▸) on the "Real-Time Telemetry" header to collapse the telemetry panel when you want to focus on the conversation, then expand it again when you want to show the enterprise observability story.

---

### Act 2: "The Pipeline Clog" (~3 min)

> **⚠️ Prerequisites:** This act requires the Foundry Shipment Agent to be enabled. Set `FoundryAgent:Enabled: true` in configuration. Without Foundry, the `LocalShipmentAnalyzer` handles shipment analysis directly (no agent delegation visible in telemetry).

**Narration:**

> *"Now here's where it gets interesting. In the spirits industry, the Three-Tier system — manufacturer, distributor, retailer — creates hidden tensions. Let's ask the agent to analyze the shipment pipeline."*

**Action:** Type:

```
Analyze the shipment pipeline for Patron Silver in Florida
```

**What happens (explain as it unfolds):**

1. The agent recognizes this needs shipment analysis and **delegates to the Foundry Shipment Specialist**
2. Watch the telemetry panel:
   - 🧠 `thought` — The orchestrator agent reasons about the question
   - 🤝 `agent_delegation` — MAF orchestrator delegates to the Foundry agent
   - 🔧 `tool_call` — The Foundry agent calls `GetShipmentStats` via MCP
   - 📊 `tool_result` — Shipment data returns with the Pipeline Clog anomaly
   - 🤖 `agent_call` — Foundry agent analyzes the data
   - 📨 `agent_response` — Specialist returns analysis to the orchestrator
   - 💬 `response` — Orchestrator synthesizes the final answer

**The "Wow" Data Point:**

> *"Look at what the agent found: Shipments are UP 5.2%, but Sell-Through is DOWN 3.0%. That's a 2,600 case gap sitting in distributor warehouses in Jacksonville and Tampa. This is a Pipeline Clog — Bacardi is pushing more product into the channel than consumers are buying. And it gets worse: the agent correlated this with field sentiment showing Patrón Silver's $59 price point is losing ground to Casamigos at $52."*

**Key talking points:**

> *"This isn't just one agent — it's a multi-agent system. The MAF orchestrator decided it needed a specialist and delegated to the Foundry Shipment Agent. You can see the delegation in the telemetry. In production, that Foundry agent could be a separately deployed microservice with its own scaling and governance."*

> *"The Three-Tier tension — where shipments and sell-through diverge — is exactly the kind of signal that gets buried in spreadsheets. An AI agent can correlate it with sentiment data in seconds."*

---

### Act 3: "The Deep Dive" (~3 min)

**Narration:**

> *"Now let's say the brand manager wants to compare performance across markets. This is where the agent really shines — it knows it needs to make multiple data calls and synthesize them."*

**Action:** Type:

```
Compare Angel's Envy performance across New York and Illinois
```

**What to highlight:**

1. **Two tool calls** appear in the telemetry — `GetDepletionStats` called once for New York, once for Illinois
2. **Two sentiment calls** — `GetFieldSentiment` for each region
3. The agent **compares** the data side-by-side in its response
4. Point out the span count badge on the message: *"📊 6+ spans recorded"*

**Expected data points:**
- **New York:** +12.3% depletion growth, +8.7% velocity — "Growth Leader" with allocation concerns
- **Illinois:** +9.1% depletion, +7.4% velocity — Chicago's cocktail renaissance driving demand
- Agent should note both are "Growth Leader" status and recommend supply chain attention

**Follow-up query** (if time allows):

```
What's the field sentiment for Grey Goose in California?
```

This demonstrates the agent handling different brands and a different tool (`GetFieldSentiment` focus).

**Key talking point:**

> *"The agent isn't following a script. It's reasoning about which tools to call based on the question. Ask about performance — it fetches depletion stats. Ask about sentiment — it focuses on field feedback. Ask to compare — it makes parallel calls and synthesizes."*

---

### Act 4: "The Enterprise Story" (~2 min)

**Action:** Switch to the Aspire Dashboard (URL from terminal output).

**Walk through these tabs:**

#### Traces

> *"Every request flows through as a distributed trace. You can see the full journey — from the API receiving the chat request, to the agent reasoning, to each MCP tool call, to the response back."*

- Click on a recent trace — show the waterfall view
- Point out the `RetailPulse.Agent` spans nested under the HTTP request
- Show timing: how long each tool call took, how long the LLM reasoning took

#### Structured Logs

> *"Every decision the agent makes is logged with structured data. You can search, filter, alert — all the things your ops team expects."*

#### Metrics

> *"Token usage, request latency, tool call counts — all available as OpenTelemetry metrics, ready to pipe into Prometheus, Grafana, or Azure Monitor."*

#### Application Insights

> *"Beyond the local Aspire dashboard, everything flows to Azure Application Insights. Open the Azure Portal → Application Insights → bsappinsights-dev-northcentralus-001. Here you can see:"*

- **Transaction search** — find specific agent conversations
- **Application map** — see the full dependency graph (API → APIM → Azure OpenAI, API → MCP Server)
- **Live metrics** — real-time request rates and failures
- **End-to-end transaction details** — every span from the agent's reasoning to the tool calls

> *"This is production-grade observability. Every agent thought, every tool call, every token — all queryable in KQL."*

#### AI Gateway (Optional — if APIM is configured)

> *"For enterprise deployment, Azure API Management sits in front of the OpenAI calls. That gives you token metering per team, rate limiting, content safety policies, and a complete audit trail. Open the AI Gateway Dev Portal to see this in action."*

---

## Talking Points

### Why .NET Aspire?

> *"Aspire gives us unified orchestration and observability without container complexity. One `dotnet run` launches everything — API, MCP server, dashboard. In production, these same definitions drive your deployment to Azure Container Apps."*

### Why MAF (Microsoft Agent Framework)?

> *"MAF is Microsoft's agent framework built natively for .NET. It integrates directly with `Microsoft.Extensions.AI`, which means OpenTelemetry tracing, dependency injection, and the entire ASP.NET ecosystem just work. No Python glue code."*

### Why MCP (Model Context Protocol)?

> *"MCP is the emerging standard for how AI agents access tools and data. By exposing our data through MCP, any agent — not just ours — can plug in. Today it's simulated data; tomorrow, swap the MCP server to call real Bacardi APIs, SAP, or Snowflake. The agent code doesn't change."*

### Why AI Gateway (Azure API Management)?

> *"Every enterprise question is about governance. 'Who called the model? How many tokens? What did they ask?' APIM answers all of these. It adds token metering, rate limiting by team, content safety policies, and a complete audit trail — without changing a line of application code."*

---

## FAQ / Objection Handling

### "Can this connect to real data?"

> Absolutely. The MCP server is a standard protocol — swap the simulated data methods in `BacardiSimulatedData.cs` with calls to real APIs, databases, or data warehouses. The agent and frontend don't change at all. That's the power of the MCP abstraction.

### "How does this scale?"

> Aspire handles service orchestration and can deploy to Azure Container Apps with auto-scaling. APIM handles rate limiting and load balancing across multiple OpenAI endpoints. SignalR scales with Azure SignalR Service. Each component scales independently.

### "What about security?"

> Multiple layers: APIM policies enforce authentication and rate limits. Managed identity eliminates API keys in production. OpenTelemetry provides a complete audit trail. The MCP server can enforce row-level security. No secrets are stored in code — they're in user-secrets or Key Vault.

### "Why not just use ChatGPT/Copilot directly?"

> Three reasons: (1) **Data grounding** — the agent calls your specific business tools, not generic internet data. (2) **Observability** — every decision is traced and auditable. (3) **Governance** — APIM gives you enterprise controls that consumer AI products don't offer.

### "What model does it use?"

> GPT-5.4-mini via Azure AI Foundry (through APIM AI Gateway). The architecture is model-agnostic — swap to GPT-4.1, Claude, or any `IChatClient`-compatible model by changing one line in `prompts.yaml`.

### "How long did this take to build?"

> The core agent, MCP server, frontend, and observability pipeline — a few days. That's the benefit of building on Aspire + MAF: the infrastructure plumbing is handled for you so you can focus on business logic.

---

## Brands & Regions Available for Demo

### Brands
| Brand | Category |
|-------|----------|
| Patrón Silver | Tequila |
| Patrón Reposado | Tequila |
| Patrón Añejo | Tequila |
| Angel's Envy | Bourbon |
| Bacardi Superior | Rum |
| Bacardi Gold | Rum |
| Grey Goose | Vodka |
| Bombay Sapphire | Gin |
| Cazadores | Tequila |
| Dewar's | Scotch |
| St-Germain | Liqueur |

### Regions
Florida, Texas, California, New York, Illinois, Georgia, National

### Impressive Queries to Have Ready

1. **Pipeline Clog (the "wow"):** *"Analyze the shipment pipeline for Patron Silver in Florida"*
2. **Three-Tier tension:** *"Show me the Three-Tier distribution tension for Patron Silver nationally"*
3. **Growth story:** *"Which brands are growth leaders nationally?"*
4. **Supply constraint:** *"What's the supply situation for Angel's Envy in New York?"*
5. **Multi-tool synthesis:** *"Compare depletion trends and field sentiment for Grey Goose across California and Texas"*
6. **Anomaly detection:** *"Are there any brands with shipment-to-depletion gaps I should worry about?"*

---

## Troubleshooting

| Issue | Solution |
|-------|---------|
| Frontend can't connect to API | Ensure the API is running on port 5100 and CORS is configured |
| "demo-key" error | Set your real OpenAI API key via `dotnet user-secrets` |
| Aspire Dashboard not loading | The dashboard URL is dynamic; check the terminal for `Login to the dashboard at...` |
| SignalR connection fails | Verify the API is running; check browser console for WebSocket errors |
| Telemetry shows "Disconnected" | This is expected before the first query. Send a message and it will connect. |
| MCP tools return empty data | Diacritics are handled automatically — "Patron" matches "Patrón". Check brand/region spelling. |
| MCP tools return no data | Diacritics are handled automatically — "Patron" matches "Patrón". Check brand/region spelling. |
| No data in App Insights | Allow 2-5 minutes for telemetry to appear. Check the connection string in AppHost.cs. |
