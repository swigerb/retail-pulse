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

## Learnings

### 2026-06-29 — Board Cleanup: Stray Template Duplicates

**Context:**
The working tree on branch `squad/upgrade-deps-and-429-fix` accumulated 15 untracked `.md` files at the `.squad/` root directory—exact byte-for-byte duplicates of files in `.squad/templates/`. These were artifacts from a bad copy operation that cluttered the working tree.

**Action Taken:**
- Verified all 15 stray files were byte-identical to their template counterparts using MD5 hashing before deletion
- All 15 deletions proceeded without issues (no differing files, no missing templates)
- Committed the legitimate 6 tracked modifications (Squad v0.9.4 governance upgrade) separately from the cleanup
- Final commit hash: `61516b8` on branch `squad/upgrade-deps-and-429-fix`

**Key Insight:**
Duplicate template files at root were 100% bit-for-bit identical—this strongly indicates a copy-and-paste artifact (e.g., from a script or prior workspace sync) rather than intentional variants. The `.squad/templates/` directory is the single source of truth for template content; any duplicates at root should be treated as erroneous clutter.

**Files Deleted:**
charter.md, constraint-tracking.md, copilot-instructions.md, fact-checker-charter.md, history.md, issue-lifecycle.md, mcp-config.md, multi-agent-format.md, orchestration-log.md, plugin-marketplace.md, raw-agent-output.md, roster.md, run-output.md, scribe-charter.md, skill.md

**Commit Message:**
```
chore(squad): upgrade governance to v0.9.4 + remove stray template duplicates

- Update squad.agent.md and squad.agent.md.template to v0.9.4 governance
- Update copilot-instructions, routing, scribe-charter templates
- Remove 15 stray template duplicates accidentally copied to .squad/ root
```