# Kroger — History

## Notification — 2026-05-16 Timeout Fix from Costco

🔔 **Pattern Established:** If a future agent genuinely needs multi-iteration tool calling and the 60s request timeout is insufficient, implement **endpoint-specific timeout override** rather than raising the global cap.

**Context:** Costco fixed 504 timeouts by setting global request timeout to 60s (both `/api/chat` and `/api/chat/stream`). This tight budget works for current single-iteration agents. Future complex agents (e.g., council convene orchestrator with nested tool calls) should request their own `/api/agent-name/execute` endpoint with a higher timeout rather than changing the global limit.

**See:** `.squad/decisions.md` — "Aggressive fast-fail timeouts for chat endpoints (2026-05-16)"

---

## Recent Work (2026-05-14 onwards)

## 2026-05-14 — Promotion Planning Agent + Task Module (Sprint 2.1)

**Architecture Decisions:**
- PromoPlanningAgent follows same specialist pattern as DemandForecastAgent: implements ISpecialistAgent, uses Key="promo-planning", temp 0.3 for analytical precision.
- Promo tools (GetPromoHistory, CalculateLift, EvaluateTiming, EstimateROI) follow the MCP tool → REST endpoint → API proxy tool chain pattern established in Sprint 1.3.
- ROI model uses diminishing returns on spend: effectiveness = min(spend/optimal, 1.0) × (1 - diminishing_factor). Above MaxEffectiveSpend, additional spend yields declining lift.
- Approval gate integration uses spend thresholds: $500K+ always requires approval, $100K-$500K requires approval when ROI < 2.0x.
- Task Module endpoint (POST /api/taskmodule/promo) orchestrates all promo tools in parallel, then applies approval gating. Returns structured evaluation without LLM involvement.
- PromoHistory seeding uses GetStableHash deterministic seeding with 4-6 campaigns per brand, ~25% poor performers for realistic data distribution.
- LiftCoefficients seeded per category × promo type (6 categories × 5 types = 30 rows) with realistic values for CPG industry.

**Key File Paths:**
- `src/RetailPulse.Api/Agents/Specialists/PromoPlanningAgent.cs` — specialist agent
- `src/RetailPulse.Api/Tools/{PromoHistoryTool,CalculateLiftTool,EvaluateTimingTool,EstimateROITool}.cs` — API proxy tools
- `src/RetailPulse.McpServer/Tools/PromoTools.cs` — MCP server tools
- `src/RetailPulse.McpServer/Data/RetailPulseDb.cs` — seeding + query methods
- `src/RetailPulse.Api/prompts.yaml` — promo-planning agent definition
- `src/RetailPulse.Api/Agents/RoutingServiceExtensions.cs` — promo DI wiring
- `POST /api/taskmodule/promo` — Task Module endpoint in Program.cs

**Test Status:** All 574 tests pass (34 new promo tests added by parallel sessions)
---

**Archive:** See kroger/history-archive.md for detailed May 14 session work and prior sessions.