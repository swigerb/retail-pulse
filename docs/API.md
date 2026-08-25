# API Reference

> **Base URL:** `http://localhost:5100` (local dev via Aspire)
>
> **Authentication:** Production runs in `Authentication__Mode=Entra` with
> `Security__RequireAuth=true`. Every protected REST call carries a
> `Authorization: Bearer <token>` header; SignalR clients pass the same token via the
> `?access_token=<token>` query string (honored **only** on `/hubs/*`). Both must
> present the `RetailPulse.User` app role and the `access_as_user` scope. See
> [Entra Authentication](authentication-entra.md), the [authentication matrix](authentication-matrix.md),
> and [ADR-005](adr/005-provider-neutral-authentication.md). Local `Development`
> environments bypass authentication via `DevelopmentAuthHandler`.

---

## Chat

### POST /api/chat

Send a message to the multi-agent routing system. The router classifies intent, selects a specialist agent, and returns an AI-generated response with telemetry spans.

**Request Body:**

```json
{
  "message": "What's the demand forecast for Ridgeline Bourbon?",
  "sessionId": "abc123",
  "user": {
    "objectId": "user-001",
    "displayName": "Jane Smith"
  },
  "history": [
    { "role": "user", "content": "Previous message" },
    { "role": "assistant", "content": "Previous response" }
  ]
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `message` | string | ✅ | User message text |
| `sessionId` | string | | Session identifier (auto-generated if omitted) |
| `user` | object | | User context with `objectId` and `displayName` |
| `history` | array | | Previous conversation turns |

**Response (200):**

```json
{
  "reply": "Based on the forecast model...",
  "sessionId": "abc123",
  "spans": [
    {
      "name": "GenerateForecast",
      "type": "tool_call",
      "detail": "...",
      "durationMs": 145,
      "timestamp": "2026-05-13T21:00:00Z",
      "sessionId": "abc123"
    }
  ],
  "tokenUsage": { "inputTokens": 1200, "outputTokens": 450 },
  "totalDurationMs": 2340
}
```

**Error Responses:**
- `400` — Missing or empty `message` field
- `503` — AI service temporarily unavailable

---

### POST /api/chat/stream

Streaming chat endpoint. Same request/response shape as `/api/chat`, but additionally pushes progressive tokens via the `/hubs/streaming` SignalR hub in parallel.

**Request Body:** Same as `POST /api/chat`

**Response:** Same as `POST /api/chat` (final assembled response)

---

## System Info

### GET /api/info

Returns system metadata including available agents and tools.

**Response (200):**

```json
{
  "name": "Retail Pulse API",
  "version": "1.0.0",
  "agent": "retail-pulse",
  "tools": ["GetDepletionStats", "GetPortfolioDepletionStats", "..."],
  "router": "RetailOpsRouter",
  "specialists": [
    { "key": "demand-forecasting", "displayName": "Demand Forecast Specialist" },
    { "key": "general", "displayName": "General Agent" }
  ]
}
```

---

## Alerts

### GET /api/alerts/active

Get all active (non-dismissed, non-snoozed) proactive alerts.

**Response (200):** Array of active alert objects.

---

### GET /api/alerts/history

Get alert history for a user.

| Query Param | Type | Default | Description |
|-------------|------|---------|-------------|
| `userId` | string | `"default"` | User ID to filter history |
| `limit` | int | `50` | Max alerts to return |

**Response (200):** Array of historical alert objects.

---

### POST /api/alerts/{alertId}/snooze

Snooze an alert for a specified duration.

**Request Body:**

```json
{
  "userId": "user-001",
  "alertType": "demand_anomaly",
  "durationHours": 4
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `userId` | string | ✅ | User performing the snooze |
| `alertType` | string | | Alert type override (defaults to `alertId`) |
| `durationHours` | double | | Snooze duration in hours (default: 1) |

**Response (200):**

```json
{ "alertId": "alert-123", "snoozedFor": "04:00:00", "userId": "user-001" }
```

---

### POST /api/alerts/{alertId}/dismiss

Permanently dismiss an alert.

**Request Body:**

```json
{ "userId": "user-001" }
```

**Response (200):**

```json
{ "alertId": "alert-123", "dismissed": true, "userId": "user-001" }
```

---

## Approvals

### GET /api/approvals/pending

Get pending approval requests for a user.

| Query Param | Type | Default | Description |
|-------------|------|---------|-------------|
| `userId` | string | `"default"` | User to check pending approvals for |

**Response (200):**

```json
[
  {
    "id": "req-001",
    "action": "Execute discount promotion for Ridgeline Bourbon in Southwest",
    "reasoning": "High-budget promo: $600,000",
    "impact": "Budget: $600,000, Expected ROI: 2.5x",
    "urgency": "high",
    "agentId": "promo-planning",
    "agentName": "promo-planning",
    "requestedAt": "2026-05-13T17:00:00Z",
    "timeoutAt": "2026-05-13T17:15:00Z",
    "status": "pending",
    "comment": null
  }
]
```

---

### GET /api/approvals/{requestId}

Get the status of a specific approval request.

**Response (200):**

```json
{
  "requestId": "req-001",
  "decision": "approved",
  "comment": "Looks good, proceed.",
  "respondedAt": "2026-05-13T17:05:00Z"
}
```

**Error:** `404` if request not found.

---

### POST /api/approvals/{requestId}/respond

Approve, reject, or modify an approval request.

**Request Body:**

```json
{
  "decision": "Approved",
  "comment": "Proceed with the campaign."
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `decision` | string | ✅ | `"Approved"`, `"Rejected"`, or `"Modified"` |
| `comment` | string | | Optional comment |

**Response (200):**

```json
{ "requestId": "req-001", "decision": "approved", "comment": "Proceed." }
```

**Errors:** `400` invalid decision, `404` request not found.

---

### GET /api/approvals/history

Get resolved approval history (last 50).

**Response (200):** Array of approval objects with `status`, `decidedAt`, and `comment` fields.

---

## Plan Review (issue #94)

> **Feature flag:** endpoints are only mapped when `PlanReview:Enabled = true`
> AND `PlanPersistence:Enabled = true`. When either is off these routes 404 and
> the chat pipeline runs the pre-#94 plan-first path — no observable difference.

Plan review reuses the same durable `SqliteApprovalGate` storage the tool
`ApprovalTool` uses, discriminated by a new `Kind` column. Plan rows have
`Kind = "plan_review"` and clarification rows have `Kind = "clarification"`;
every decision appears in the shared `/api/approvals/history` audit trail.

### Non-blocking suspend/resume — how `/api/chat` interacts

When plan review is enabled and the router flags a request as multi-domain,
`POST /api/chat` NO LONGER blocks the request thread on the reviewer decision.
Instead:

1. The orchestrator runs the planner, persists the plan as
   `awaiting_review`, and calls
   `PlanReviewCoordinator.OpenRoundAsync`, which:
   - Writes a genuine `Microsoft.Agents.AI.Workflows.Checkpointing.ICheckpointStore<JsonElement>`
     checkpoint via `CreateCheckpointAsync(sessionId, ...)` — the framework's
     public JSON checkpoint API, not a marker written next to it.
   - Opens a Pending approval row of kind `plan_review`.
2. `/api/chat` returns **HTTP 202 Accepted** with `planId`, `reviewRequestId`,
   `round`, and `sessionId`. Clients subscribe to the SignalR
   `plan_final_response` event or poll `GET /api/plans/{planId}`.
3. When the reviewer records a decision via
   `POST /api/plans/{planId}/reviews/{requestId}/decision`, the endpoint kicks
   off `PlanReviewCompletionService.ResolveAsync`. That service:
   - Reads the latest framework checkpoint + the approval row.
   - On approve / edit → executes the effective plan through `PlanExecutor`,
     runs `GuardrailsMiddleware.FilterOutputAsync` for PII redaction, writes
     audit / session-turn / export parity, and broadcasts the filtered final
     response on the SignalR hub.
   - On reject with cap remaining → invokes the planner, saves a new
     checkpoint, and opens the next review round (round N+1). The client
     receives a `plan_review_next_round` event with the new request id.
   - On terminal-without-execution (timeout, replan exhausted, edit invalid)
     → transitions the plan to `Failed` with the specific terminal reason.
4. If the API restarts between steps 2 and 3, the
   `PlanReviewRestartRecoveryService` scans the plan store for
   `awaiting_review` / `awaiting_clarification` rows on boot, and re-drives
   `ResolveAsync` for any whose approval row is already terminal — so a
   decision that arrived while the API was down still delivers the final
   response on next boot.
5. Review timeouts are enforced by a
   `PlanReviewTimeoutBackgroundService` sweep (uses the injected
   `TimeProvider`) — no request thread is blocked on the review deadline.

The pre-#94 hot path is preserved byte-for-byte when
`PlanReview:Enabled = false`: `RunAsync` executes the plan inline and
`/api/chat` returns the standard `200 OK` `ChatResponse` envelope.

### Mid-plan clarification (`[[CLARIFY]] <question>`) and replan (`[[REPLAN]] <feedback>`)

The `PlanExecutor` inspects each step's `action` field for two markers:

- `[[CLARIFY]] <question>` — opens a clarification row + framework
  checkpoint capturing the paused plan state, then halts. When the reviewer
  answers via `POST /api/plans/{planId}/clarifications/{requestId}/answer`,
  the completion service resumes: the answer becomes the paused step's
  result and downstream steps continue.
- `[[REPLAN]] <feedback>` — the reachable mid-execution revision surface.
  Opens a NEW `plan_review` row for the remaining steps with the feedback
  as revision-reason and halts. The reviewer's next decision drives the
  same suspend/resume cycle.

Both markers share the identical durable persistence path with plan review,
so a restart in the middle of either flow is invisible to the reviewer.

### Relationship to #91 (approval hardening)

#91 introduced `IApprovalResumeStrategy` as the single reconciliation seam. #94
extends that seam without adding a second polling loop: on restart the same
`ApprovalReconciliationBackgroundService` walks every Pending row exactly once
and asks the strategy per-row. `PlanReviewResumeStrategy` returns:

* `OrphanTerminal` for `Kind = "tool"` rows — byte-identical to the Wave 1
  `OrphanUnresumableStrategy`.
* `Resume` for `Kind = "plan_review"` and `Kind = "clarification"` rows — the
  gate re-owns them to the current process. The durable approval row remains the
  source of truth for the decision, and the framework
  `ICheckpointStore<JsonElement>` (JSON store rooted at
  `{data-dir}/plan-reviews`) holds the paused execution envelope the
  completion service reads to rebuild the effective plan on resume.

### GET /api/plans/{planId}/reviews

List open plan reviews owned by the caller.

**Auth:** authenticated caller only. Anonymous callers receive `403`. Cross-
subject / unknown plans collapse to `404` (probe resistant — mirrors
`/api/plans/{planId}`).

**Response (200):**

```json
[
  {
    "requestId": "...",
    "planId": "...",
    "round": 0,
    "subject": "user-oid",
    "action": "Review plan proposal (2 step(s)).",
    "impact": "Specialists: scorecard, demand-forecasting",
    "urgency": "medium",
    "reasoning": "Initial plan proposal awaiting reviewer decision.",
    "createdAt": "2026-...",
    "expiresAt": "2026-...",
    "status": "pending",
    "payload": "{ \"planId\": ... }"
  }
]
```

### POST /api/plans/{planId}/reviews/{requestId}/decision

Approve, reject-with-feedback, or edit-then-approve the proposal.

**Request body:**

```json
{ "kind": "approve" }
{ "kind": "reject", "feedback": "narrow the scope to Q4" }
{ "kind": "edit", "editedSteps": [ { "specialistKey": "scorecard", "intent": "scorecard", "action": "..." } ] }
```

* `approve` — executes the original plan.
* `edit` — validated against the live specialist roster; the edited plan is what
  the executor actually runs. Edit-to-empty terminates the plan as `Failed` with
  reason `PlanReviewEditedToEmpty`; unknown specialists terminate with
  `PlanReviewEditInvalid`.
* `reject` — coordinator invokes the planner with the feedback appended and
  presents the revised plan for another review round, bounded by
  `PlanReview:MaxReplanRounds` (default 2). Exhausting the cap terminates with
  `PlanReviewReplanExhausted`.

**Errors:** `400` for missing feedback/editedSteps, `403` for anonymous callers,
`404` for cross-subject / unknown plan or review, and the same terminal-reason
propagation the gate uses for late race losers (see #91 for the winner-return
contract — the same rule applies here).

### POST /api/plans/{planId}/clarifications/{requestId}/answer

Answer a mid-plan clarification question raised by a specialist through
`IPlanClarifier`.

```json
{ "answer": "Northeast region only" }
```

### Timeout & terminal outcomes

Every review round has an authoritative timeout persisted on the approval row
(`PlanReview:DefaultReviewTimeout`, default 30 minutes). When exceeded the row
transitions to `TimedOut` and the plan terminates with reason
`PlanReviewTimedOut` — never an indefinite hang.

---

## Demand Forecasting

### Demand forecasting is an MCP-tool-driven capability, not a REST endpoint

There is no `GET /api/demand/forecast` route on the API. Demand forecasting is exposed
through the chat pipeline — the router classifies intent as `demand/forecasting`,
dispatches to the `DemandForecastAgent` specialist, and the agent invokes MCP tools
(`GenerateForecast`, `GetHistoricalDemand`, `GetSellThrough`, etc.) via
`RetailPulse.McpServer` tool invocation. Callers should `POST /api/chat` (or
`POST /api/chat/stream` for token streaming) with a natural-language question — the
`agentsConsulted` field of the response will contain `"demand-forecasting"` when the
router selected this specialist.

See [MCP-TOOLS.md](MCP-TOOLS.md) for the full tool reference used by this specialist.

---

## Promotion Planning

### GET /api/promo/calendar

Get the promotional calendar. Proxied to the MCP server.

| Query Param | Type | Default | Description |
|-------------|------|---------|-------------|
| `brand` | string | | Filter by brand name |
| `region` | string | | Filter by region |
| `months` | int | `6` | Number of months to retrieve |

**Response (200):** JSON array of upcoming and past promotional campaigns.

---

### GET /api/promo/types

Get available promotion types (discount, bogo, display, digital, bundle).

**Response (200):** JSON array of promo type definitions.

---

### POST /api/taskmodule/promo

Full promotional evaluation task module. Orchestrates all four promo MCP tools in parallel (history, lift, timing, ROI) and optionally triggers an approval gate for high-budget or low-ROI campaigns.

**Request Body:**

```json
{
  "brand": "Ridgeline Bourbon",
  "region": "Southwest",
  "promoType": "discount",
  "budget": 150000,
  "startDate": "2026-06-01",
  "endDate": "2026-06-28",
  "targetLift": 15.0
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `brand` | string | ✅ | Brand name |
| `region` | string | ✅ | Target region |
| `promoType` | string | ✅ | `discount`, `bogo`, `display`, `digital`, `bundle` |
| `budget` | double | ✅ | Budget in dollars (must be > 0) |
| `startDate` | string | ✅ | ISO date (yyyy-MM-dd) |
| `endDate` | string | ✅ | ISO date (yyyy-MM-dd) |
| `targetLift` | double | | Target lift percentage (optional) |

**Approval Triggers:**
- Budget > $500,000 → always requires approval
- Budget > $100,000 with ROI < 2.0x → requires approval

**Response (200):**

```json
{
  "recommendation": "recommended",
  "brand": "Ridgeline Bourbon",
  "region": "Southwest",
  "promo_type": "discount",
  "budget": 150000,
  "period": { "start": "2026-06-01", "end": "2026-06-28", "duration_weeks": 4 },
  "roi_estimate": { "expected_roi": 2.85, "upper_bound": 3.4, "lower_bound": 2.1 },
  "timing_assessment": { "timing_score": 0.82, "conflicts": [], "risks": [] },
  "lift_analysis": { "expected_lift_pct": 12.5, "confidence": "high" },
  "historical_context": { "campaigns": [...] },
  "risk_factors": [],
  "approval": { "required": false, "request_id": null, "reason": null }
}
```

---

## Supply Chain

### GET /api/supply/health

Get aggregate supply chain health summary for a brand.

| Query Param | Type | Required | Description |
|-------------|------|----------|-------------|
| `brand` | string | ✅ | Brand name |
| `region` | string | | Region filter |

**Response (200):** Health summary with overall status (Green/Yellow/Red).

---

### GET /api/supply/inventory

Get current inventory levels.

| Query Param | Type | Description |
|-------------|------|-------------|
| `brand` | string | Filter by brand |
| `region` | string | Filter by region |
| `category` | string | Filter by category |
| `status` | string | Filter: `healthy`, `low`, `critical`, `out_of_stock` |

**Response (200):** Array of inventory records with stock levels, safety stock, and days of supply.

---

### GET /api/supply/disruptions

Get active supply chain disruptions.

| Query Param | Type | Default | Description |
|-------------|------|---------|-------------|
| `brand` | string | | Filter by brand |
| `region` | string | | Filter by region |
| `severity` | string | | Filter: `high`, `medium`, `low` |
| `activeOnly` | bool | `true` | Only active disruptions |

**Response (200):** Array of disruption records.

---

### GET /api/supply/fulfillment

Get order fulfillment rate trends.

| Query Param | Type | Default | Description |
|-------------|------|---------|-------------|
| `brand` | string | | Filter by brand |
| `region` | string | | Filter by region |
| `period` | string | | Specific period (e.g. `2026-04`) |
| `minPeriods` | int | `6` | Minimum periods to return |

**Response (200):** Array of fulfillment rate records by period.

---

## Store Operations

### GET /api/stores/performance

Get store performance metrics (revenue vs target, foot traffic, conversion).

| Query Param | Type | Description |
|-------------|------|-------------|
| `region` | string | Filter by region |

**Response (200):** Array of store performance records.

---

### GET /api/stores/{storeId}/planogram/{aisleId}

Get the current shelf layout for a specific aisle. Returns SKU positions, shelf levels, and facing widths.

**Response (200):** Planogram data with shelf positions.

---

### POST /api/stores/{storeId}/planogram/{aisleId}/optimize

Generate an optimized planogram for an aisle. Returns predicted revenue uplift and recommendations.

| Query Param | Type | Description |
|-------------|------|-------------|
| `brandFocus` | string | Optional brand to prioritize |

**Response (200):** Optimized planogram with uplift predictions.

---

### GET /api/stores/{storeId}/stockout-risk

Predict stockout risk for SKUs at a store.

| Query Param | Type | Description |
|-------------|------|-------------|
| `skuId` | string | Optional specific SKU to check |

**Response (200):** Array of stockout risk predictions with days-to-stockout.

---

## Margin & Financials

### GET /api/margin/{brandId}

Get P&L breakdown for a brand.

| Query Param | Type | Description |
|-------------|------|-------------|
| `period` | string | Filter by period (e.g. `2026-Q1`) |

**Response (200):** Margin data with revenue, COGS, marketing, distribution, and margin percentages.

---

### GET /api/margin/drivers/{brandId}

Identify what's driving margin changes for a brand. Returns cost categories, impact percentages, and trends.

**Response (200):** Array of margin driver records.

---

### GET /api/margin/trend/{brandId}

Get margin trajectory over time.

| Query Param | Type | Default | Description |
|-------------|------|---------|-------------|
| `quarters` | int | `4` | Number of quarters to show |

**Response (200):** Array of quarterly margin records.

---

### GET /api/margin/risks

Detect margin-destructive patterns across brands.

| Query Param | Type | Description |
|-------------|------|-------------|
| `brandId` | string | Optional brand filter |

**Response (200):** Array of ranked margin risks with recommendations.

---

### POST /api/escalate  *(deprecated — compatibility only)*

> **Deprecated.** The `/api/chat` pipeline no longer routes multi-domain
> requests through this endpoint. Admission for cross-domain analysis is
> owned by `HybridExecutionDecider` (issue #95), which lifts qualifying
> turns onto the plan-first path (`ADR-014`, `/api/chat` → **HTTP 202
> Accepted** with `planId`/`reviewRequestId`). `/api/escalate` is retained
> for explicit legacy callers only; new integrations should use `/api/chat`
> and observe the 202 handoff. See the class documentation on
> [`EscalationEndpoints`](../src/RetailPulse.Api/Endpoints/EscalationEndpoints.cs)
> and [`EscalationOrchestrator`](../src/RetailPulse.Api/Escalation/EscalationOrchestrator.cs).

Escalate a query through the legacy L1→L2→L3 escalation pipeline. Routes through progressively senior agent layers.

**Request Body:** Same as `POST /api/chat`

**Response (200):**

```json
{
  "reply": "After consulting multiple specialists...",
  "level": 2,
  "agentsConsulted": ["demand-forecasting", "supply-chain"],
  "durationMs": 4500,
  "needsHumanReview": false,
  "escalationReason": "Cross-domain query requiring multi-agent synthesis"
}
```

---

## Plan store & execution control  *(opt-in — `PlanPersistence:Enabled=true`)*

These endpoints are only mapped when `PlanPersistence:Enabled` is `true` and
the caller is authenticated. Anonymous callers are refused at entry and
cross-subject reads collapse into `404` so a plan id cannot be probed. All
routes below are wired by
[`PlanEndpoints`](../src/RetailPulse.Api/Endpoints/PlanEndpoints.cs) and
[`ExecutionControlEndpoints`](../src/RetailPulse.Api/Endpoints/ExecutionControlEndpoints.cs).

### GET /api/plans

List the caller's persisted plans, newest activity first.

**Response (200):** array of `PlanSummaryDto` (`planId`, `sessionId`,
`intent`, `status`, `stepCount`, `updatedAt`, …).

### GET /api/plans/{planId}

Rehydrate one persisted plan with its ordered steps.

**Response (200):** `PlanDetailDto` — plan header plus `steps[]`
(`stepIndex`, `specialistKey`, `intent`, `action`, `status`, `output`,
`tokens`, `startedAt`, `completedAt`).

**Errors:** `403` for anonymous callers, `404` for unknown or cross-subject
plan ids.

### DELETE /api/plans/{planId}

Remove one persisted plan (and every step under it) owned by the caller.
Cascades in a single transaction.

**Response:** `204 No Content` on success, `404` if the plan does not exist
or belongs to a different subject.

### POST /api/chat/{sessionId}/cancel

Cancel the caller's in-flight `/api/chat` (fast-path or streaming) run for
`sessionId`. Ownership-scoped through `IExecutionCancellationRegistry`.

**Response:** `204 No Content` on cancel; `404` for no active run OR a
foreign owner (foreign owners collapse to `404` so live sessions of other
callers cannot be probed).

### POST /api/plans/{planId}/cancel

Cancel the caller's in-flight plan orchestration keyed on `planId`. Same
ownership contract as `/api/chat/{sessionId}/cancel`.

**Response:** `204 No Content`, `403` for anonymous callers, `404`
otherwise.

### GET /api/plans/{planId}/reconcile

Rehydrate durable plan state after a reconnect. Accepts an optional
`afterStepIndex` query parameter — the response only includes steps whose
`stepIndex > afterStepIndex`, so a reconnecting client can render just the
ones it missed.

**Response (200):**

```json
{
  "planId": "...",
  "sessionId": "...",
  "status": "in_progress",
  "failureReason": null,
  "updatedAt": "2026-...",
  "totalStepCount": 3,
  "afterStepIndex": 0,
  "steps": [ { "stepIndex": 1, "specialistKey": "demand-forecasting", "status": "completed", "output": "…" } ]
}
```

**Errors:** `403` for anonymous callers, `404` for unknown or cross-subject
plan ids.

---

## Portfolio Scorecard

### POST /api/scorecard

Generate a multi-brand portfolio scorecard. Convenes specialist agents to score each brand.

**Request Body:**

```json
{
  "brands": ["Sierra Gold Tequila", "Ridgeline Bourbon"],
  "region": "Southwest"
}
```

**Response (200):** Scorecard with per-brand scores from each specialist domain.

**Error:** `503` if ScorecardOrchestrator is not registered.

---

## Council

### POST /api/council/convene

Convene the Portfolio Health Council — all specialist agents vote on brand health.

**Request Body:**

```json
{
  "brand": "Sierra Gold Tequila",
  "region": "Southwest"
}
```

**Response (200):**

```json
{
  "brand": "Sierra Gold Tequila",
  "region": "Southwest",
  "overall_rating": "Healthy",
  "synthesis": "All agents agree the brand is performing well...",
  "is_unanimous": true,
  "disagreements": [],
  "action_items": ["Monitor competitive pricing in Q3"],
  "convened_at": "2026-05-13T17:00:00Z",
  "total_duration_ms": 8500,
  "votes": [
    {
      "agent_id": "demand-forecasting",
      "agent_name": "Demand Forecast Specialist",
      "rating": "Healthy",
      "reasoning": "Demand trends are positive...",
      "confidence": 0.92,
      "key_metrics": { "growth_rate": "12%" },
      "response_time_ms": 1200
    }
  ]
}
```

---

### GET /api/council/agents

List all specialist agents available for council participation.

**Response (200):**

```json
{
  "agents": [
    { "key": "demand-forecasting", "display_name": "Demand Forecast Specialist", "supported_intents": [...], "domain": "Demand & forecasting analysis" }
  ],
  "total": 6
}
```

---

## Explainability

### GET /api/explain/{traceId}

Get a human-readable explanation of how an AI response was generated, including data sources, reasoning chain, and tool calls.

**Response (200):**

```json
{
  "traceId": "abc123",
  "sessionId": "session-001",
  "query": "What's the demand forecast?",
  "toolCallCount": 2,
  "totalDurationMs": 2340,
  "startedAt": "2026-05-13T17:00:00Z",
  "dataSources": ["HistoricalDemand", "SeasonalityFactors"],
  "reasoningChain": ["Retrieved 12 months of history", "Applied seasonal multipliers"],
  "explanation": "The forecast was generated by..."
}
```

**Error:** `404` if trace not found.

---

### GET /api/explain/session/{sessionId}

Get all explainability traces for a session.

**Response (200):** Array of trace summaries.

---

## Knowledge Base (RAG)

### POST /api/knowledge/upload

Ingest a document into the BM25-based knowledge base.

**Request Body:**

```json
{
  "title": "Q2 Brand Strategy",
  "content": "Full document text...",
  "source": "strategy-team"
}
```

**Response (200):**

```json
{ "documentId": "doc-001", "title": "Q2 Brand Strategy", "status": "ingested" }
```

---

### GET /api/knowledge/documents

List all documents in the knowledge base.

**Response (200):** Array of document metadata (id, title, source, chunkCount).

---

### DELETE /api/knowledge/documents/{id}

Delete a document from the knowledge base.

**Response (200):**

```json
{ "documentId": "doc-001", "status": "deleted" }
```

---

### POST /api/knowledge/search

Search the knowledge base using BM25 ranking.

**Request Body:**

```json
{ "query": "premium spirits pricing strategy", "topK": 5 }
```

**Response (200):**

```json
{
  "query": "premium spirits pricing strategy",
  "results": [
    { "title": "Q2 Brand Strategy", "chunk": "...", "chunkIndex": 2, "score": 0.87 }
  ]
}
```

---

### GET /api/knowledge/stats

Get knowledge base statistics.

**Response (200):**

```json
{ "documentCount": 12, "chunkCount": 84, "averageChunksPerDocument": 7.0 }
```

---

## Collaborative Cards

### POST /api/cards

Create a new adaptive card for multi-user collaboration.

**Request Body:**

```json
{
  "title": "Health Assessment: Sierra Gold",
  "type": "Voting",
  "createdBy": "user-001",
  "data": { "brand": "Sierra Gold Tequila" }
}
```

**Response (200):** Full card object with id, state, and actions.

---

### GET /api/cards

List cards with optional filters.

| Query Param | Type | Description |
|-------------|------|-------------|
| `type` | string | Card type filter (e.g. `Voting`, `Approval`) |
| `lifecycle` | string | Lifecycle filter (e.g. `Active`, `Archived`) |

**Response (200):** Array of card objects.

---

### GET /api/cards/{id}

Get a specific card by ID.

**Response (200):** Full card object. **Error:** `404` if not found.

---

### POST /api/cards/{id}/action

Perform an action on a card (vote, approve, comment).

**Request Body:**

```json
{
  "userId": "user-001",
  "userName": "Jane Smith",
  "actionType": "Vote",
  "data": { "vote": "Healthy" }
}
```

**Response (200):** Updated card object. **Errors:** `404` not found, `400` invalid action.

---

### POST /api/cards/{id}/archive

Archive a card (move to archived lifecycle).

**Response (200):**

```json
{ "id": "card-001", "status": "archived" }
```

---

## Cache Management

### GET /api/cache/stats

Get response cache statistics.

**Response (200):**

```json
{
  "totalEntries": 42,
  "hits": 156,
  "misses": 89,
  "hitRate": 0.6367,
  "memoryBytes": 245760
}
```

---

### DELETE /api/cache

Clear all cached responses.

**Response (200):**

```json
{ "status": "cleared" }
```

---

### DELETE /api/cache/{key}

Invalidate a specific cache entry by key.

**Response (200):**

```json
{ "key": "pre-route:abc123", "status": "invalidated" }
```

---

## Guardrails

### GET /api/guardrails/log

Get recent suspicious request log entries.

| Query Param | Type | Default | Description |
|-------------|------|---------|-------------|
| `count` | int | `50` | Number of entries to return |

**Response (200):** Array of suspicious request records with `detectionType`, `requestText`, `action`.

---

### GET /api/guardrails/stats

Get guardrail statistics (blocked counts by type).

**Response (200):**

```json
{
  "totalBlocked": 15,
  "jailbreakAttempts": 8,
  "piiDetections": 5,
  "accessDenials": 2,
  "since": "2026-05-13T00:00:00Z"
}
```

---

### GET /api/guardrails/config

Get current guardrails configuration.

**Response (200):**

```json
{
  "piiDetectionEnabled": true,
  "jailbreakDetectionEnabled": true,
  "autoRedactPii": true,
  "maxInputLength": 10000,
  "piiPatterns": ["SSN", "CreditCard", "Email"],
  "jailbreakPatterns": ["IgnoreInstructions", "RolePlay"]
}
```

---

### PUT /api/guardrails/config

Update guardrails configuration at runtime. Only provided fields are updated.

**Request Body:**

```json
{
  "piiDetectionEnabled": true,
  "jailbreakDetectionEnabled": true,
  "autoRedactPii": false,
  "maxInputLength": 5000
}
```

**Response (200):** Updated config with `status: "updated"`.

---

## Observability

### GET /api/observability/costs

Get cost summary for a period.

| Query Param | Type | Default | Description |
|-------------|------|---------|-------------|
| `period` | string | `"week"` | `day`, `week`, `month` |

**Response (200):** Cost summary with total tokens, estimated cost.

---

### GET /api/observability/costs/agents

Get cost breakdown by agent.

| Query Param | Type | Default | Description |
|-------------|------|---------|-------------|
| `period` | string | `"week"` | `day`, `week`, `month` |

**Response (200):** Per-agent cost breakdown.

---

### GET /api/observability/costs/trend

Get daily cost trend.

| Query Param | Type | Default | Description |
|-------------|------|---------|-------------|
| `days` | int | `7` | Number of days |

**Response (200):** Array of daily cost data points.

---

### GET /api/observability/costs/tools

Get per-tool call counts and duration for the current period. Sourced from the
in-memory `ITraceCollector` rather than the persisted cost tracker, so numbers
reset with the process and reflect only spans that closed successfully.

| Query Param | Type | Default | Description |
|-------------|------|---------|-------------|
| `period` | string | `"week"` | `day`, `week`, `month` — mirrors the other cost endpoints for UI consistency |

**Response (200):** Array of `{ tool, callCount, totalDurationMs, avgDurationMs }` rows sorted by `callCount` descending. Powers the "Tool Usage" table in the CostDashboard component.

---

## Conversation Memory

Long-lived per-user memory entries surfaced by the SPA's **Memory** panel and used by the agent middleware to inject recall context into subsequent turns. All routes require the same Entra bearer as the rest of the API and resolve the caller's stable `oid` claim via `UserIdentity.Resolve(HttpContext.User)`.

### GET /api/memory

List the current user's memory entries (most recent first, capped at 100).

**Response (200):**
```json
[
  {
    "id": "mem_01HXR9...",
    "content": "Prefers Q3 promo review meetings on Tuesdays.",
    "storedAt": "2026-08-10T14:22:11.000Z",
    "expiresAt": "2027-08-10T14:22:11.000Z",
    "type": "preference"
  }
]
```

`type` is one of `conversation` (rolling summary), `preference` (user-stated preference), or `entity` (extracted entity mention).

### DELETE /api/memory/{id}

Delete a single memory entry belonging to the current user.

**Response (204):** No content on success. Returns 404 if the entry does not exist or belongs to a different user (fail-closed).

---
---

### GET /api/observability/audit

Query the audit log with filters.

| Query Param | Type | Description |
|-------------|------|-------------|
| `agentId` | string | Filter by agent |
| `userId` | string | Filter by user |
| `from` | datetime | Start date |
| `to` | datetime | End date |
| `action` | string | Filter by action type |
| `limit` | int | Max entries (default: 50) |

**Response (200):** Array of audit log entries. Each entry exposes `id`,
`timestamp`, `userId`, `agentId` (opaque identifiers — not display names),
`action`, `inputSummary`, `outputSummary`, `tokens`, and `durationMs` (numeric
milliseconds, not a serialized `TimeSpan`).

---

### GET /api/observability/audit/stats

Get audit log summary statistics.

**Response (200):** Aggregated audit statistics.

---

### GET /api/observability/export/sessions

List all conversation sessions available for export.

**Response (200):** Array of session metadata. Each item exposes `sessionId`,
`startTime` (session start, camelCase — not `startedAt`), `messageCount`,
`agentsUsed`, and `totalTokens`.

---

### GET /api/observability/export/{sessionId}/preview

Return session metadata plus a bounded, oldest-first slice of the conversation
for in-UI preview.

| Query Param | Type | Default | Description |
|-------------|------|---------|-------------|
| `max` | int | `20` | Max messages in the preview slice |

**Response (200):** `{ sessionId, messages: [{ role, content, timestamp }], totalMessages }`
where `totalMessages` is the true message count (which may exceed the returned
slice). **Error:** `404` if the session does not exist — the endpoint never
returns a silent empty-success body, so the UI surfaces missing sessions as real
errors.

---

### POST /api/observability/export/{sessionId}

Export a conversation session. The requested format may be supplied either in the
JSON request body (`{ "format": "markdown" | "json" }`) or as a `format` query
parameter; the body takes precedence.

| Param | Location | Type | Default | Description |
|-------|----------|------|---------|-------------|
| `format` | body or query | string | `"markdown"` | `markdown` or `json` |

**Response (200):** The raw exported document — **not** a JSON envelope. Markdown
exports return `Content-Type: text/markdown; charset=utf-8` and JSON exports
return `Content-Type: application/json; charset=utf-8`, both with
`Content-Disposition: attachment; filename="session-<id>.<ext>"` so the browser
downloads a correctly-typed file. **Error:** `404` if session not found.

---

## Traces

### GET /api/traces/recent

Get recent distributed traces.

| Query Param | Type | Default | Description |
|-------------|------|---------|-------------|
| `count` | int | `20` | Number of traces to return |

**Response (200):** Array of trace summaries.

---

### GET /api/traces/{traceId}/summary

Get a structured summary for a specific trace.

**Response (200):** Trace summary with spans, durations, and agent info. **Error:** `404` if not found.

---

### GET /api/traces/{traceId}/spans

Get all spans for a specific trace.

**Response (200):** Array of span objects. **Error:** `404` if not found.

---

## Message Extension (Teams)

### POST /api/message-extension/query

Teams message extension query — searches the knowledge base and generates a grounded response.

**Request Body:**

```json
{ "text": "premium spirits pricing strategy", "context": "Sales channel discussion" }
```

**Response (200):**

```json
{
  "answer": "Based on the knowledge base...",
  "citations": [{ "source": "Q2 Strategy", "chunk": "Chunk 2", "relevance": 0.87 }],
  "confidence": "high",
  "agentUsed": "General Agent"
}
```

---

### GET /api/message-extension/manifest

Get the Teams message extension manifest JSON.

**Response (200):** Teams manifest JSON.

---

## SignalR Hubs

| Hub | Path | Purpose |
|-----|------|---------|
| TelemetryHub | `/hubs/telemetry` | Real-time trace events, approval notifications, card updates |
| StreamingHub | `/hubs/streaming` | Progressive token delivery for streaming chat |

---

## Streaming Chat

### POST /api/chat/stream

Same request contract and pipeline as `POST /api/chat`, but streams the assistant
response as **Server-Sent Events (SSE)** so the SPA can render tokens as they are
produced. Middleware order (auth → guardrails → router → specialist → memory) is
identical; the only difference is that the specialist's `IChatClient` runs in
streaming mode and each token/tool-boundary is flushed to the client as an SSE
`data:` frame. Final `total_duration_ms` / `token_usage` / `routing` metadata is
delivered as a trailing SSE event so the UI can populate the SpansSummary and
telemetry pane at the end of the turn.

Use `/api/chat` for classic request/response clients (Teams bot, integration
tests); use `/api/chat/stream` for the SPA.

---

## Health Endpoints

Health probes are wired by `RetailPulse.ServiceDefaults.MapDefaultEndpoints()`
(Aspire convention) and are always anonymous, even under
`Security:RequireAuth=true`.

### GET /health

**Readiness probe.** Runs all health checks tagged `ready`, currently
`mcp-server` (pings the MCP server's own `/health`) and `azure-openai` (verifies
the Azure OpenAI / APIM connection is reachable and the configured deployment
resolves). Returns 200 with a JSON body listing each check's status; returns 503
if any `ready`-tagged check is unhealthy — Container Apps / Kubernetes should
remove the instance from the load balancer when this happens.

**Response (200):**
```json
{
  "status": "Healthy",
  "entries": {
    "mcp-server": { "status": "Healthy" },
    "azure-openai": { "status": "Healthy" }
  }
}
```

### GET /alive

**Liveness probe.** Filters health checks to none (no `ready` tag), so it returns
200 as long as the process is running and the ASP.NET Core pipeline can respond.
Use for Kubernetes / Container Apps liveness probes — an unhealthy `/health` will
NOT restart the container, but a failing `/alive` will.

**Response (200):** `Healthy`

---

## API Documentation UI

### GET /api/docs

Interactive API documentation powered by [Scalar](https://scalar.com/). Browse all endpoints, view request/response schemas, and try requests directly.

Available in all environments.

---

## Error Responses

All errors follow [RFC 7807 Problem Details](https://www.rfc-editor.org/rfc/rfc7807):

```json
{
  "type": "https://tools.ietf.org/html/rfc7807",
  "title": "Validation Error",
  "status": 400,
  "detail": "Message exceeds maximum length of 2000 characters",
  "instance": "/api/chat",
  "traceId": "abc-123-def",
  "correlationId": "550e8400-e29b-41d4-a716-446655440000"
}
```

Every response includes an `X-Correlation-ID` header for request tracing.
