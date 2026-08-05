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

---

## 2026-06-29 — PR #1 Review: REQUEST_CHANGES

**Branch:** `squad/upgrade-deps-and-429-fix` → `main`  
**PR Title:** Upgrade deps + fix cross-region 429s + memory-store identity fix + Squad v0.10.0

### Review Outcome: ❌ REQUEST_CHANGES

**Blocking Issue:** Critical security vulnerability in `src/RetailPulse.Api/Auth/UserIdentity.cs`

### Security Vulnerability (BLOCKING)
**File:** `UserIdentity.cs:26`  
**CWE:** CWE-290 (Authentication Bypass), CWE-639 (Authorization Bypass)

**Problem:**  
`UserIdentity.Resolve()` prioritizes the untrusted request body `ObjectId` parameter over the authenticated `oid` claim from `ClaimsPrincipal`. This allows identity spoofing — any authenticated client can send an arbitrary `ObjectId` in the POST body and act as another user for memory writes, conversation history access, and all user-scoped operations.

**Attack Vector:**
``http
POST /api/chat
Content-Type: application/json
{ "User": { "ObjectId": "victim-user-guid" }, "Message": "Remember my credit card..." }
``

**Root Cause:**
``csharp
// INSECURE: trusts client input first
if (!string.IsNullOrWhiteSpace(bodyObjectId))
    return bodyObjectId;  // ❌ Any client can spoof this
``

**Required Fix:**
Reverse priority to trust claims first, fall back to body only in anonymous/dev scenarios:
``csharp
string? oid = principal?.FindFirst("oid")?.Value
    ?? principal?.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

if (!string.IsNullOrWhiteSpace(oid))
    return oid;  // ✅ Trusted source

if (!string.IsNullOrWhiteSpace(bodyObjectId))
    return bodyObjectId;  // Only used when no auth claim present

return AnonymousUserId;
``

**Assignment:** Kroger (me) will fix this in a follow-up commit on the same branch. Costco authored the original code (per task context), so I'm taking ownership of the fix as the Lead responsible for architecture and security.

### Approved Sections

✅ **429 Throttling Fix** — Sound  
- Cross-region keyword fast-path (`RetailOpsRouter.cs:L73-80`) saves LLM calls
- Retry budget 1→2 (`Program.cs:L572-583`) with exponential backoff is well-justified
- Prompt guidance (`prompts.yaml:L99-106`) prevents token exhaustion
- No infinite loops, errors propagate correctly

✅ **Dependency Upgrades** — Validated  
- Aspire 13.4.3, Microsoft.Agents.AI 1.9.0, MCP 1.4.0, IdentityModel 8.19.1
- Frontend: ESLint 10, React 19.2.7, Vite 8.0.16
- All CI checks pass (0 warnings, 1,992 tests passing)
- Note: IdentityModel 8.18.0→8.19.1 bump conflicts with decisions.md "pinning" entry, but build passes — entry should be updated

✅ **Memory Store Normalization** — Functionally Correct  
- `ChatEndpoints` / `MemoryEndpoints` now use `UserIdentity.Resolve()` consistently
- Fixes write/read path divergence (Memory Panel "0 memories" bug)
- 7 new tests cover all resolution paths
- Security issue is orthogonal to the functional fix

✅ **Architecture** — Compliant  
- Aspire non-containerized ✓
- Tenant config generic ✓
- .NET 10 best practices ✓
- Clean boundaries ✓

✅ **Squad v0.10.0 Upgrade** — Clean  
- Governance files refreshed, `squad doctor`: 9 passed

### Non-Blocking Observations

1. **Memory type enum changes** (`MemoryEndpoints.cs:L27-31`):
   - `ConversationSummary → "conversation"` (was `"fact"`)
   - `EntityMention → "entity"` (was `"context"`)
   - New field: `expiresAt`
   
   Frontend contract change — **Chick should verify** types align before final merge.

### GitHub Review Posting
Attempted formal `gh pr review 1 --request-changes` but failed:
- Personal account (swigerb): "Can not request changes on your own pull request"
- EMU account (brswig_microsoft): "Unauthorized: As an EMU, you cannot access this content"

Coordinator will need to communicate the verdict to Brian manually.

### Next Steps
1. Kroger pushes security fix to `squad/upgrade-deps-and-429-fix`
2. Kroger re-reviews for final approval
3. Chick verifies frontend memory type compatibility
4. Merge to `main`

### Decision
No new team-wide decision needed — this is a code-level fix, not an architectural policy change. The existing "UserId Resolution Must Go Through UserIdentity.Resolve" decision in `decisions.md` remains valid; this fix hardens its implementation.


---

### 2026-08-05T09:57:34-04:00 — Architecture gate: Issue #11 secretless-ACR deployment

**Status:** ✅ APPROVE (final). Independent architecture/code review of Costco's dedicated-ACR, outputs, and postprovision hooks.

**Notes:** Initial APPROVE with two notes — document the RBAC prerequisite and placeholder-image behavior. Final recheck APPROVE after Costco corrected operational docs.

**Team impact:** Secretless system-identity ACR pull is the deployment standard; RBAC sequencing and placeholder-image behavior must stay documented in `docs/deployment-azd.md`.
