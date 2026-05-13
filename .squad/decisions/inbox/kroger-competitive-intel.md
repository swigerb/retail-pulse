# Competitive Intelligence Agent Architecture

**Date:** 2026-05-20
**Author:** Kroger (Lead Architect)
**Sprint:** 2.2

## Decision

Implemented the Competitive Intelligence Agent as a full specialist in the multi-agent routing pipeline, with three key architectural choices:

### 1. Inline Proactive Alert Integration
CompetitiveIntelAgent is the first specialist to fire proactive alerts inline during tool result processing (not via the background ProactiveAlertService). When `DetectThreats` or pricing tools return high-severity results, the agent immediately fires `competitive_threat` alerts via SignalR, using the same SqliteAlertService throttling (1 alert per type/brand/region per hour).

**Rationale:** Competitive threats are time-sensitive — waiting for the next background check cycle (5 min) could delay reaction. Inline firing provides sub-second alert delivery while the user is actively analyzing competitive data.

### 2. Defensive Strategy Framework (MATCH/DIFFERENTIATE/IGNORE/PREEMPT)
The system prompt codifies a four-strategy defensive framework for competitive responses, with clear triggers:
- **MATCH** — price gap >15% and losing share
- **DIFFERENTIATE** — price gap 5-15% with strong brand loyalty
- **IGNORE** — niche/regional/temporary competitor moves
- **PREEMPT** — early signals of competitive entry

**Rationale:** Provides consistent, actionable recommendations instead of generic "monitor the situation" advice. The framework is embedded in the system prompt (not code) so it can be tuned without deployments.

### 3. Temperature 0.4 (Higher Than Other Analytical Agents)
Competitive intelligence uses temperature 0.4 vs 0.3 for demand forecasting and promo planning.

**Rationale:** Competitive strategy requires more creative thinking than pure numerical analysis. The slightly higher temperature allows the agent to suggest innovative defensive strategies while still grounding responses in data from tools.

## Alternatives Considered

- **Background-only alerts:** Would add 0-5 minute latency to competitive threat notifications. Rejected for time-sensitivity reasons.
- **Separate alert agent:** Over-engineering for the current scope. The inline pattern can be extracted later if needed.
- **Same temperature as other analysts (0.3):** Produced overly conservative recommendations in testing. 0.4 struck the right balance.

## Impact

- New files: 8 (4 MCP/API proxy tools, 1 MCP tool class, 1 specialist agent, 1 decisions file)
- Modified files: 4 (RetailPulseDb.cs, McpServer/Program.cs, Api/Program.cs, RoutingServiceExtensions.cs, prompts.yaml)
- Schema version: 4 → 5 (forces re-seed for competitive tables)
- Router already had `competitive/market` intent — no router prompt changes needed
