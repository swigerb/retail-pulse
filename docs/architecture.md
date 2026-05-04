# Retail Pulse — Architecture

> Technical architecture for the Retail Pulse agentic analytics platform

---

## Component Diagram

![Retail Pulse Architecture](retail-pulse-component-diagram.png)

---

## Data Flow

### Request Flow: User Question → Answer

![Retail Pulse Request Flow](retail-pulse-request-flow.png)

### Telemetry Flow: Agent → Dashboard

![Retail Pulse Telemetry Flow](retail-pulse-telemetry-flow.png)

### Data Flow: Agent Analyzing Data

When a user asks a question like *"Compare depletion trends across all regions"*, the agent orchestrates a multi-step data flow across four services. Here is the complete lifecycle:

```
Browser (ChatPanel) → POST /api/chat → RetailPulseAgent
  → Azure OpenAI (via APIM AI Gateway) → model selects tools
  → MAF tool-calling loop → API proxy tools → MCP Server
  → SimulatedMetricsData (in-memory) → results back up the chain
```

#### Step-by-Step Flow

| Step | Component | What Happens |
|------|-----------|-------------|
| **1. User sends message** | `ChatPanel.tsx` | Frontend sends `POST /api/chat` with `{ message, sessionId, history }`. Conversation history (up to 10 turns) is included for context continuity. |
| **2. Agent builds prompt** | `RetailPulseAgent.cs` | Assembles a message array: system prompt (from `prompts.yaml`) + conversation history + current user message. Starts a `Stopwatch` to measure wall-clock duration. |
| **3. Model inference** | Azure OpenAI via APIM | The `IChatClient` (backed by `AzureOpenAIClient`) sends the messages to APIM. APIM applies token limiting (10k TPM), emits metrics, and forwards to Azure OpenAI using managed identity. Model: `gpt-5.4-mini`. |
| **4. Tool selection** | Azure OpenAI | The model examines the user's question and the available tool schemas, then decides which tools to call and with what parameters. For a portfolio-wide question, it may call `GetPortfolioDepletionStats`; for a single brand, `GetDepletionStats`. |
| **5. Tool execution loop** | MAF (`UseFunctionInvocation`) | Microsoft.Extensions.AI middleware intercepts each tool call. It invokes the registered `AITool` implementation, captures the result, and sends it back to the model. The model may call additional tools or generate its final response. Tools execute sequentially within a single turn. |
| **6. API proxy call** | e.g., `DepletionStatsTool.cs` | Each tool is an HTTP proxy. It calls the MCP Server's REST endpoint (e.g., `GET /api/depletion-stats?brand=X&region=Y&period=Z`). If the MCP Server is unreachable, the tool returns hardcoded fallback data and logs a warning. |
| **7. Data generation** | `SimulatedMetricsData.cs` | The MCP Server's singleton data service generates the response. Data is computed from `tenant.yaml` configuration (12 brands, 6 regions, 3 channels) using deterministic algorithms with quarterly multipliers. |
| **8. Response assembly** | `RetailPulseAgent.cs` | After the model produces its final text response, the agent packages it with telemetry spans, chart data (if any), and `TotalDurationMs` (from the `Stopwatch`). |
| **9. Frontend rendering** | `ChatPanel.tsx` | The response is displayed as formatted markdown. Charts render via `ChartRenderer` (Recharts). Telemetry spans stream to `TelemetryPanel` via SignalR for real-time display. |

#### Where the Data Resides

| Data | Location | Persistence |
|------|----------|-------------|
| **Depletion metrics** (sales velocity, YoY trends, inventory) | `SimulatedMetricsData._depletionData` — in-memory dictionary keyed by `(Brand, Region)` | Per-process. Regenerated identically on restart (deterministic from `tenant.yaml`). |
| **Shipment data** (distribution, fill rates) | `SimulatedMetricsData._shipmentData` — in-memory dictionary | Same as above. |
| **Field sentiment** (rep feedback, scores) | `SimulatedMetricsData._sentimentData` — in-memory dictionary | Same as above. |
| **Tenant configuration** (brands, regions, channels) | `tenant.yaml` at repo root → loaded by `FileTenantProvider` | On disk. Single source of truth for the business domain. |
| **Conversation history** | Frontend state (`ChatPanel.tsx`) → sent with each request | Browser session only. Not persisted server-side. |
| **Telemetry spans** | Frontend state (`Dashboard.tsx`) via SignalR | Browser session only. Resets on "Clear Telemetry" or "+ New Chat". |

> **Key insight:** There is no database. The entire analytics dataset is generated in-memory by `SimulatedMetricsData` based on `tenant.yaml`. The intelligence comes from the LLM interpreting and synthesizing the simulated metrics — not from the data itself. This is by design for a demo platform; swapping to real data sources requires only replacing the MCP Server tool implementations.

---

## Technology Choices & Rationale

### .NET Aspire — Orchestration & Observability

| Decision | Rationale |
|----------|-----------|
| **Why Aspire over Docker Compose?** | Single `dotnet run` starts everything. Type-safe resource definitions in C#. Built-in dashboard with no YAML configuration. Same definitions work locally and deploy to Azure Container Apps. |
| **Why not Kubernetes locally?** | Unnecessary complexity for a demo. Aspire abstracts container orchestration while still supporting K8s deployment in production. |
| **Service defaults pattern** | `AddServiceDefaults()` ensures every service gets OpenTelemetry, health checks, resilience, and service discovery with one line. Consistency without boilerplate. |

### Microsoft Agent Framework (MAF) — AI Agent

| Decision | Rationale |
|----------|-----------|
| **Why MAF over LangChain/Semantic Kernel?** | Native .NET integration. Built on `Microsoft.Extensions.AI` abstraction — works with any `IChatClient` implementation. OpenTelemetry tracing is built in, not bolted on. |
| **Why GPT-5.4-mini?** | Best balance of reasoning quality, speed, and cost for tool-calling scenarios. Architecture is model-agnostic — swap via `prompts.yaml`. |
| **Prompt configuration in YAML** | Separates prompt engineering from code. Non-developers can iterate on prompts without touching C#. Supports multiple agent definitions. |

### Model Context Protocol (MCP) — Tool Access

| Decision | Rationale |
|----------|-----------|
| **Why MCP over direct HTTP calls?** | MCP is an emerging standard. Today's tools are simulated; tomorrow, swap to real APIs without changing agent code. Any MCP-compatible agent can use these tools. |
| **REST + MCP dual endpoints** | MCP SSE for agent communication. REST endpoints (`/api/depletion-stats`) for direct testing and integration. Same backing data, two access patterns. |
| **Simulated data** | Enables demo without real data dependencies. Rich enough to show realistic patterns (growth leaders, declining brands, overstocked inventory). |

### React + Vite + TypeScript — Frontend

| Decision | Rationale |
|----------|-----------|
| **Why React over Blazor?** | Broader ecosystem for rapid UI development. SignalR client library works seamlessly. Most frontend developers know React. |
| **SignalR for telemetry** | Real-time span streaming without polling. WebSocket transport for low latency. Graceful fallback to Server-Sent Events. |
| **Component architecture** | `ChatPanel` (input/output), `TelemetryPanel` (metrics), `SpanTimeline` (visual trace) — each independently testable. |

### Azure API Management — AI Gateway

| Decision | Rationale |
|----------|-----------|
| **Why APIM for AI?** | Token metering per team/department. Rate limiting prevents runaway costs. Content safety policies. Complete audit trail for compliance. |
| **Separate from core demo** | The app works without APIM. Gateway is an enterprise overlay — add it when the conversation turns to production governance. |

---

## Observability Architecture

![Retail Pulse Observability Architecture](retail-pulse-observability-architecture.png)

### Span Hierarchy Example

**Default (Foundry disabled):**

![Span Hierarchy - Default](span-hierarchy-default.png)

**With Foundry Shipment Agent enabled (`FoundryAgent:Enabled: true`):**

![Span Hierarchy - With Foundry Shipment Agent](span-hierarchy-foundry.png)

---

## APIM AI Gateway Pattern

Retail Pulse uses Azure API Management as an AI Gateway following the [Azure-Samples/AI-Gateway](https://github.com/Azure-Samples/AI-Gateway) pattern.

### Request Flow

1. **RetailPulse API** sends chat completion requests to APIM using the Azure OpenAI SDK
2. **APIM** validates the `api-key` header (subscription key)
3. **AI Gateway policies** apply:
   - `llm-token-limit`: Rate limits to 10,000 tokens per minute per subscription
   - `llm-emit-token-metric`: Emits token usage metrics to Azure Monitor (namespace: RetailPulse)
   - Circuit breaker: Trips on 429s for 1 minute
4. **APIM** forwards to Azure AI Foundry using its managed identity (no keys in transit)
5. **Azure AI Foundry** processes with the `gpt-5.4-mini` deployment

### URL Pattern

```
POST {apim_gateway}/inference/openai/deployments/{model}/chat/completions?api-version={version}
```

Example:
```
POST https://bsapim-dev-northcentralus-001.azure-api.net/inference/openai/deployments/gpt-5.4-mini/chat/completions?api-version=2025-03-01-preview
```

### Why APIM as AI Gateway?

| Capability | Value |
|-----------|-------|
| **Token Rate Limiting** | Prevent runaway costs — cap TPM per consumer |
| **Token Metrics** | Monitor token usage in Azure Monitor / App Insights |
| **Managed Identity** | No API keys in application code |
| **Circuit Breaker** | Graceful degradation when backend is throttled |
| **Centralized Governance** | One gateway for all AI model access |
| **Dev Portal** | Self-service API key management for consumers |

---

## Security Considerations

| Concern | Mitigation |
|---------|-----------|
| API key storage | User secrets locally; Azure Key Vault in production |
| API key in transit | HTTPS enforced; APIM terminates TLS |
| Prompt injection | Agent has constrained system prompt; tools only return structured data |
| Data access | MCP server can enforce row-level security per user/role |
| Audit trail | OpenTelemetry traces + APIM logs capture every interaction |
| Rate limiting | APIM token-per-minute and request-per-second policies |
| Content safety | APIM content filtering policies (Azure AI Content Safety) |

> **`/api/chat` auth note:** The chat endpoint is intentionally open in the
> demo so contributors can run the sample without standing up an identity
> provider. An off-by-default API-key gate is wired in
> `Middleware/ApiKeyAuthMiddleware.cs` (`ApiKey:Enabled`, `ApiKey:Value`) to
> demonstrate the pattern. Production deployments must replace this with
> JWT bearer authentication and `.RequireAuthorization()` policies.

---

## Resilience Patterns

### Tool Errors: Fallback With Logging

Every MCP-backed tool (`DepletionStatsTool`, `ShipmentStatsTool`,
`FieldSentimentTool`) and the optional `FoundryShipmentAgent` follow the
**same fallback-with-logging contract**:

1. **Try the upstream call** (MCP server, Foundry agent, etc.).
2. **On failure, log the exception** with the tool name, parameters, and
   correlation IDs via `ILogger`. This surfaces in App Insights and the
   Aspire dashboard so operators see the outage instead of having it
   swallowed.
3. **Return a typed, empty/neutral payload** (e.g., zero-value stats with
   an `error` field) so the LLM can keep reasoning and tell the user that
   "shipment data is currently unavailable" rather than crashing the turn.

This pattern is intentional — the agent loop is more useful degraded than
broken. It is **not** a license to silently swallow exceptions: any new
tool added to the agent must log first, then fall back.

---

## Deployment Topology

### Local Development

![Local Development Topology](deployment-local.png)

### Azure Production (Target)

![Azure Production Topology](deployment-azure.png)
