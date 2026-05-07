# Retail Pulse — Demo Walkthrough

> A pro-code agentic demo showcasing AI-powered brand analytics with **mutable data** — the agent can read, analyze, and update business metrics in real time

This guide walks you through presenting Retail Pulse to stakeholders. Total demo time: **~12 minutes**.

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

**Setup:** Open the Retail Pulse dashboard at `http://localhost:5173`. The audience sees a clean chat interface with suggested queries.

**Narration:**

> *"Imagine you're a category manager at Apex Retail Group. You oversee a portfolio spanning spirits, grocery, QSR, home improvement, and more. You just walked out of a quarterly review and need quick answers about how one of your brands is performing in a key market. Instead of pulling up spreadsheets or waiting for an analyst, you just ask."*

**Action:** Click or type the first suggested query:

```
How is Sierra Gold Tequila performing in Northeast?
```

**What happens (explain as it unfolds):**

1. The **telemetry panel** on the right lights up in real-time
2. Watch the span timeline populate:
   - 🧠 `thought` — The agent is reasoning about your question
   - 🔧 `tool_call` — It decides to call `GetDepletionStats` for Sierra Gold Tequila in Northeast
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
- major metro on-premise velocity remains high, but suburban retail is lagging

> **Pro tip:** Click the chevron (▸) on the "Real-Time Telemetry" header to collapse the telemetry panel when you want to focus on the conversation, then expand it again when you want to show the enterprise observability story.

---

### Act 2: "The Pipeline Clog" (~3 min)

> **⚠️ Prerequisites:** This act requires the Foundry Shipment Agent to be enabled. Set `FoundryAgent:Enabled: true` in configuration. Without Foundry, the `LocalShipmentAnalyzer` handles shipment analysis directly (no agent delegation visible in telemetry).

**Narration:**

> *"Now here's where it gets interesting. in distribution, the Three-Tier system — manufacturer, distributor, retailer — creates hidden tensions. Let's ask the agent to analyze the shipment pipeline."*

**Action:** Type:

```
Analyze the shipment pipeline for Sierra Gold Tequila in Northeast
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

> *"Look at what the agent found: Shipments are UP 5.2%, but Sell-Through is DOWN 3.0%. That's a 2,600 case gap sitting in distributor warehouses in key distributor markets. This is a Pipeline Clog — The company is pushing more product into the channel than consumers are buying. And it gets worse: the agent correlated this with field sentiment showing Sierra Gold Tequila's $59 price point is losing ground to competitors at $52."*

**Key talking points:**

> *"This isn't just one agent — it's a multi-agent system. The MAF orchestrator decided it needed a specialist and delegated to the Foundry Shipment Agent. You can see the delegation in the telemetry. In production, that Foundry agent could be a separately deployed microservice with its own scaling and governance."*

> *"The Three-Tier tension — where shipments and sell-through diverge — is exactly the kind of signal that gets buried in spreadsheets. An AI agent can correlate it with sentiment data in seconds."*

---

### Act 3: "The Deep Dive" (~3 min)

**Narration:**

> *"Now let's say the brand manager wants to compare performance across markets. This is where the agent really shines — it knows it needs to make multiple data calls and synthesize them."*

**Action:** Type:

```
Compare Ridgeline Bourbon performance across Midwest and West Coast
```

**What to highlight:**

1. **Two tool calls** appear in the telemetry — `GetDepletionStats` called once for Midwest, once for West Coast
2. **Two sentiment calls** — `GetFieldSentiment` for each region
3. The agent **compares** the data side-by-side in its response
4. Point out the span count badge on the message: *"📊 6+ spans recorded"*

**Expected data points:**
- **Midwest:** +12.3% depletion growth, +8.7% velocity — "Growth Leader" with allocation concerns
- **West Coast:** +9.1% depletion, +7.4% velocity — Chicago's cocktail renaissance driving demand
- Agent should note both are "Growth Leader" status and recommend supply chain attention

**Follow-up query** (if time allows):

```
What's the field sentiment for Summit Vodka in Southwest?
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

### Act 5: "The Live Update" (~3 min)

**Narration:**

> *"Everything you've seen so far has been read-only — the agent queries data and synthesizes insights. But here's where it gets really powerful. With our SQLite-backed data store, the agent can actually update the data in real time. This turns our platform from a reporting tool into a dynamic command center."*

> *"Think about the scenarios a category manager faces every day: a product recall, a supply chain disruption, an influencer going viral. They need to update their data and immediately see the downstream impact. Let me show you."*

---

#### Scenario 1: "The Social Crisis" — Sentiment & Brand Health

**Narration:**

> *"Imagine we just got word that FreshMart is facing a food safety concern in the Pacific Northwest. Social mentions are spiking negative. In a traditional system, you'd file a ticket, wait for the data team, and hope the dashboards update by tomorrow. Watch what happens when we just tell the agent."*

**Action:** Type:

```
I'm seeing a spike in negative social mentions for FreshMart in the Pacific Northwest due to a food safety recall on their Organic Produce line. Set their sentiment to 15 and update their depletion status to 'At Risk' to reflect the brand hit.
```

**What happens (explain as it unfolds):**

1. The agent reasons about the request and identifies it needs **two updates** across different tables
2. Watch the telemetry panel:
   - 🧠 `thought` — The agent plans the multi-table update
   - 🔧 `tool_call` — `UpdateMetrics` called for the Sentiment table (sentiment → 15)
   - 📊 `tool_result` — Confirmation: "Updated Sentiment for FreshMart in Pacific Northwest"
   - 🔧 `tool_call` — `UpdateMetrics` called for the Depletions table (status → At Risk)
   - 📊 `tool_result` — Confirmation: "Updated Status for FreshMart in Pacific Northwest"
   - 💬 `response` — The agent summarizes both changes

**Verification (the "wow"):** Now ask:

```
What's the current sentiment and status for FreshMart in the Pacific Northwest?
```

> *"See? The data actually changed. The agent reads back the updated values — sentiment at 15, status 'At Risk'. This isn't a simulation of an update; the SQLite database was physically modified. Every subsequent query reflects this new reality."*

**Key talking point:**

> *"This bridges the gap between 'Social Listening' and 'ERP Reality.' The category manager gets an alert, tells the AI, and the system updates instantly — no ticket queue, no data team delay."*

---

#### Scenario 2: "The Supply Chain Reroute" — Shipment Disruption

**Narration:**

> *"Now let's look at operational agility. A hurricane warning just came through for the Southeast, and our primary distribution hub is shutting down."*

**Action:** Type:

```
A hurricane warning in the Southeast is shutting down our primary distribution hub for 72 hours. Set cases shipped for Sierra Gold Tequila in the Southeast to 0 and update the anomaly type to 'Supply Disruption' with risk level 'Critical'.
```

**What happens:**

1. The agent makes **three** `UpdateMetrics` calls on the Shipments table:
   - `CasesShipped` → 0
   - `AnomalyType` → "Supply Disruption"
   - `RiskLevel` → "Critical"
2. Each update is confirmed in the telemetry panel

**Verification:** Ask:

```
Show me the shipment pipeline for Sierra Gold Tequila in the Southeast
```

> *"The agent now reads back the disrupted state — zero cases shipped, 'Supply Disruption' anomaly, 'Critical' risk level. In a read-only system, this scenario is impossible to model. Here, the user sees inventory levels actually change in the database."*

**Key talking point:**

> *"This showcases operational agility. When logistics friction hits, the data needs to reflect reality immediately — not after a batch ETL runs overnight."*

---

#### Scenario 3: "The Influencer Lift" — Update + Immediate Analysis

**Narration:**

> *"Here's where the agentic pattern really shines. A major food influencer just featured Coastline Tacos, and the West Coast is seeing unprecedented demand. But we don't just want to update the data — we want the agent to immediately analyze the impact."*

**Action:** Type:

```
Our partnership with a major food influencer just went viral for Coastline Tacos on the West Coast. Update their depletions YoY to 35.0 to reflect the surge, set sentiment to 95, and change their status to 'Growth Leader'. Then tell me which region now has the highest depletions for Coastline Tacos.
```

**What happens:**

1. The agent updates **three values** across two tables (Depletions + Sentiment)
2. Then it **immediately queries** the data to answer the follow-up question
3. Watch the telemetry — you'll see `UpdateMetrics` calls followed by `GetDepletionStats` or `GetPortfolioDepletionStats` calls in the same conversation turn

**Key talking point:**

> *"After updating the data, the AI immediately performs a follow-up analysis. It doesn't just write — it reads back, compares across regions, and identifies which market is now leading. That's the 'Pulse' in Retail Pulse — moving from data entry to proactive alerting in a single interaction."*

---

#### Scenario 4: "The Competitive Pivot" — What-If Analysis

**Narration:**

> *"Finally, let's use this for strategic 'What-If' analysis. A competitor just launched a massive sale in the Southwest. What happens to our Home Improvement numbers?"*

**Action:** Type:

```
A competitor just launched a massive Memorial Day sale in the Southwest. Decrease Pinnacle Hardware's depletions YoY to -5.0 to simulate the market share loss, but increase their sentiment to 70 to reflect our new 'Pro Quality' campaign. What does their overall picture look like now?
```

**What happens:**

1. The agent updates depletions (simulating market share loss) and sentiment (reflecting the counter-campaign)
2. Then it performs a **holistic analysis** — fetching the full brand picture to give the category manager a strategic view
3. The response synthesizes: "Depletions are down, but sentiment is recovering due to the quality positioning."

**Key talking point:**

> *"The data isn't a static snapshot — it's a dynamic playground for testing business hypotheses. 'What if we lose share but invest in brand perception?' The agent executes the pivot, analyzes the result, and gives the category manager a strategy-level view. That's how a Copilot assists in real-time decision-making."*

**Closing for Act 5:**

> *"What you've just seen is a fundamental shift. The AI isn't just reading data — it's a collaborator that can update, analyze, and advise. And every single change is traceable in the telemetry. You can see exactly what was updated, when, and by whom. That's enterprise-grade agentic AI."*

> **Pro tip:** To reset the data for the next demo, simply delete the SQLite database file (`data/retailpulse.db`) and restart the MCP Server. It will re-seed from `tenant.yaml` automatically.

---

## Talking Points

### Why .NET Aspire?

> *"Aspire gives us unified orchestration and observability without container complexity. One `dotnet run` launches everything — API, MCP server, dashboard. In production, these same definitions drive your deployment to Azure Container Apps."*

### Why MAF (Microsoft Agent Framework)?

> *"MAF is Microsoft's agent framework built natively for .NET. It integrates directly with `Microsoft.Extensions.AI`, which means OpenTelemetry tracing, dependency injection, and the entire ASP.NET ecosystem just work. No Python glue code."*

### Why MCP (Model Context Protocol)?

> *"MCP is the emerging standard for how AI agents access tools and data. By exposing our data through MCP, any agent — not just ours — can plug in. Today the data lives in SQLite; tomorrow, swap the MCP server to call real business APIs, SAP, or Snowflake. The agent code doesn't change."*

### Why AI Gateway (Azure API Management)?

> *"Every enterprise question is about governance. 'Who called the model? How many tokens? What did they ask?' APIM answers all of these. It adds token metering, rate limiting by team, content safety policies, and a complete audit trail — without changing a line of application code."*

---

## FAQ / Objection Handling

### "Can this connect to real data?"

> Absolutely. The MCP server uses a standard protocol — the data layer is already a SQLite database (`RetailPulseDb.cs`) that the agent can both read and write. To connect to production data, swap the SQLite queries with calls to real APIs, databases, or data warehouses. The agent and frontend don't change at all. That's the power of the MCP abstraction. The current SQLite implementation is itself a demonstration of how the agent interacts with a real, mutable data store.

### "How does this scale?"

> Aspire handles service orchestration and can deploy to Azure Container Apps with auto-scaling. APIM handles rate limiting and load balancing across multiple OpenAI endpoints. SignalR scales with Azure SignalR Service. Each component scales independently.

### "What about security?"

> Multiple layers: APIM policies enforce authentication and rate limits. Managed identity eliminates API keys in production. OpenTelemetry provides a complete audit trail. The MCP server can enforce row-level security. No secrets are stored in code — they're in user-secrets or Key Vault.

### "Why not just use ChatGPT/Copilot directly?"

> Three reasons: (1) **Data grounding** — the agent calls your specific business tools, not generic internet data. (2) **Observability** — every decision is traced and auditable. (3) **Governance** — APIM gives you enterprise controls that consumer AI products don't offer.

### "What model does it use?"

> GPT-5.4-mini via Azure AI Foundry (through APIM AI Gateway). The architecture is model-agnostic — swap to GPT-4.1, Claude, or any `IChatClient`-compatible model by changing one line in `prompts.yaml`.

### "Does the agent actually change the data?"

> Yes. The `UpdateMetrics` MCP tool writes directly to the SQLite database. Changes persist across queries within the same session and survive MCP Server restarts (the database file is on disk). To reset to the original state, delete `data/retailpulse.db` and restart — it re-seeds automatically from `tenant.yaml`.

### "How long did this take to build?"

> The core agent, MCP server, frontend, and observability pipeline — a few days. That's the benefit of building on Aspire + MAF: the infrastructure plumbing is handled for you so you can focus on business logic.

---

## Brands & Regions Available for Demo

### Brands
| Brand | Category | Variants |
|-------|----------|----------|
| Sierra Gold Tequila | Spirits | Blanco, Reposado, Añejo, Extra Añejo |
| Ridgeline Bourbon | Spirits | Small Batch, Single Barrel, Cask Strength |
| Summit Vodka | Spirits | Original, Citrus, Pepper |
| FreshMart | Grocery | Organic Produce, Bakery, Deli, Frozen |
| Harvest Table | Grocery | Fresh Meals, Meal Kits, Prepared Foods |
| Apex Grill | Quick-Serve Restaurant | Burgers, Chicken, Breakfast, Beverages |
| Coastline Tacos | Quick-Serve Restaurant | Tacos, Burritos, Bowls, Sides |
| Pinnacle Hardware | Home Improvement | Lumber, Power Tools, Paint, Plumbing |
| Summit Outdoor | Home Improvement | Patio Furniture, Grills, Garden, Landscaping |
| ClearDesk | Office Supply | Paper Products, Ink & Toner, Technology, Furniture |
| Urban Living | Furniture | Living Room, Bedroom, Dining, Outdoor |
| Foundry Home | Furniture | Sofas, Mattresses, Desks, Storage |

### Regions
Northeast, Southeast, Midwest, Southwest, West Coast, Pacific Northwest

### Impressive Queries to Have Ready

#### Read-Only Queries (Acts 1–4)

1. **Pipeline Clog (the "wow"):** *"Analyze the shipment pipeline for Sierra Gold Tequila in Northeast"*
2. **Three-Tier tension:** *"Show me the Three-Tier distribution tension for Sierra Gold Tequila nationally"*
3. **Growth story:** *"Which brands are growth leaders nationally?"*
4. **Supply constraint:** *"What's the supply situation for Ridgeline Bourbon in Midwest?"*
5. **Multi-tool synthesis:** *"Compare depletion trends and field sentiment for Summit Vodka across Southwest and Southeast"*
6. **Anomaly detection:** *"Are there any brands with shipment-to-depletion gaps I should worry about?"*
7. **QSR cross-region:** *"How is Apex Grill performing across Southeast and Southwest?"*
8. **Grocery category:** *"Compare FreshMart and Harvest Table depletion trends in the Northeast"*
9. **Home improvement:** *"What's the field sentiment for Pinnacle Hardware in the Midwest?"*
10. **Furniture pipeline:** *"Analyze the shipment pipeline for Urban Living in West Coast"*

#### Data Mutation Queries (Act 5)

11. **Social crisis:** *"FreshMart is facing a food safety recall in the Pacific Northwest. Set their sentiment to 15 and update their depletion status to 'At Risk'."*
12. **Supply disruption:** *"A hurricane is closing our Southeast hub. Set cases shipped for Sierra Gold Tequila in the Southeast to 0 and mark the anomaly type as 'Supply Disruption' with risk level 'Critical'."*
13. **Influencer lift:** *"Coastline Tacos went viral on the West Coast. Update depletions YoY to 35.0, sentiment to 95, and status to 'Growth Leader'. Which region now has the highest depletions?"*
14. **Competitive pivot:** *"A competitor launched a sale in the Southwest. Decrease Pinnacle Hardware depletions YoY to -5.0 but increase sentiment to 70. What does their picture look like now?"*

---

## Troubleshooting

| Issue | Solution |
|-------|---------|
| Frontend can't connect to API | Ensure the API is running on port 5100 and CORS is configured |
| "demo-key" error | Set your real OpenAI API key via `dotnet user-secrets` |
| Aspire Dashboard not loading | The dashboard URL is dynamic; check the terminal for `Login to the dashboard at...` |
| SignalR connection fails | Verify the API is running; check browser console for WebSocket errors |
| Telemetry shows "Disconnected" | This is expected before the first query. Send a message and it will connect. |
| MCP tools return empty data | Diacritics are handled automatically - "Anejo" matches "Añejo". Check brand/region spelling against tenant.yaml. |
| MCP tools return no data | Verify brand name matches tenant.yaml exactly. Check region spelling. |
| No data in App Insights | Allow 2-5 minutes for telemetry to appear. Check the connection string in AppHost.cs. |
| Data not resetting between demos | Delete `data/retailpulse.db` and restart the MCP Server. It will re-seed from `tenant.yaml`. |
| UpdateMetrics returns "Invalid field" | Check field spelling against valid fields: Depletions (`DepletionsYoY`, `SellThroughYoY`, `InventoryWeeks`, `Status`, `SentimentSummary`), Shipments (`ShipmentsYoY`, `CasesShipped`, `CasesDepleted`, `AnomalyType`, `RiskLevel`), Sentiment (`Sentiment`). |
