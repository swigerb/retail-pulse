# ADR-011: Plan-first orchestration using MAF Workflows

## Status

Accepted (issue #93 — plan-first orchestration for multi-domain requests).

## Context

Retail Pulse's router (`RetailOpsRouter`) already produces a rich signal for
each turn: a chosen intent AND a `DetectedIntents` list that captures every
domain the LLM found in the message. Before #93 the pipeline discarded that
signal past the first hit — it dispatched exactly one specialist, so a request
that spanned scorecard + demand + competitive intel produced a single-agent
answer that ignored two thirds of what the user actually asked.

We considered three shapes for adding a plan step:

1. Hand-roll a scheduler that fans a request across specialists, tracks their
   status, and stitches replies.
2. Reuse `EscalationOrchestrator` or `ConsensusOrchestrator`, both of which
   already sequence multiple specialists.
3. Adopt the `Microsoft.Agents.AI.Workflows` package's declarative
   `WorkflowBuilder` runtime — the framework already ships an executor
   protocol, edge routing, checkpointing, and the exact "sequential graph
   of function executors" primitive we need.

Option 1 loses the framework's checkpointing (which #94's suspend/resume story
needs) and forces us to invent our own protocol for step handoff, budget, and
telemetry. Option 2 conflates a plan-first path with an already-loaded
concept: council votes and escalations both make sense for different reasons
(voting synthesis, escalating past a specialist that gave up). Option 3 gives
us the framework's checkpoint story for free and keeps our own code short.

We chose option 3.

## Decision

* **Planner is a distinct data-driven agent definition** in `prompts.yaml`
  (`planner` section, `role: orchestration`). It is registered in
  `RoutingServiceExtensions._orchestrationKeys` so it is excluded from the
  specialist roster and can never plan to invoke itself. Tenant hydration
  works through the same #98 mechanism that hydrates specialists — no
  hardcoded planner prompt.

* **Plan size is hard-capped at 5 steps** (`PlanPersistenceOptions.MaxStepCount`,
  default 5). Anything beyond that is rejected as unusable during
  validation. The router's `DetectedIntents` list is passed to the planner
  verbatim so multi-domain breadth is preserved without asking the planner
  to re-classify.

* **Steps run through the existing `ISpecialistAgent.HandleAsync` seam.** No
  bespoke tool-call routing, no per-plan specialist reimplementation. The
  planner sees the live roster (`ISpecialistAgent` collection) at plan-build
  time and can only emit keys that exist.

* **Persistence is subject-scoped and additive.** New tables live in a new
  `plans.db` file behind `PlanPersistenceOptions.Enabled` (off by default —
  same convention as `SessionPersistenceOptions`). Every step transition
  (`Pending -> Running -> Completed / Failed / TimedOut / Skipped / Unusable`)
  is persisted so a mid-plan crash leaves an honest record. When the flag is
  off the whole plan-first path stays inert and the API behaves byte-for-byte
  identically.

* **One `RequestToolContext.Begin` scope encloses the whole plan.** ADR-006
  (tool-context budget) accounts distinct tool calls and cumulative returned
  chars cumulatively across the plan, not per step. The specialist's own
  `Begin` call becomes a no-op nested scope when the outer plan scope is in
  force. This preserves ADR-006's semantics for single-specialist requests
  (the specialist still opens its own scope) while making wide fan-out
  impossible to reset the budget.

* **Usage/cost attribution is additive.** `UsageEvent` gained nullable
  `PlanId` and `PlanStepId` fields with `null` defaults. Non-plan turns keep
  writing the same rows they did before; plan steps write a plan roll-up plus
  per-step attribution that reconciles.

* **Two new `span.type` values.** `plan` (root plan span) and `plan_step`
  (per-step span) join the existing `memory`, `tool_call`, and other span
  types. Existing dashboards that key off `span.type` continue to work.

* **Timeouts are bounded and configurable.** `StepTimeout` (default 60s) and
  `PlanTimeout` (default 3m) enforce hard ceilings. On step timeout, the
  step is persisted `TimedOut` and remaining steps are marked `Skipped`.
  On plan timeout, the plan is persisted `Failed` and remaining steps are
  skipped. Nothing hangs.

* **API surface stays minimal.** `/api/plans` GET/GET/{id}/DELETE, subject-
  scoped, mirrors `/api/sessions`. Human plan review, plan editing, and the
  frontend for both are explicitly out of scope for #93 (tracked in #94 and
  #96).

* **Planner failures are honest.** An unusable planner output (invalid JSON,
  step over the cap, unknown specialist key, or an empty steps array with a
  reason) persists a plan row with status `Unusable` and returns without
  invoking any specialist. The chat reply surfaces the terminal state
  instead of pretending we produced content.

* **Checkpointing uses the framework.** `PlanExecutor` runs on
  `InProcessExecution.RunAsync` with `CheckpointManager.CreateInMemory()`.
  #94 (suspend/resume) will be able to swap that for a durable checkpoint
  manager without redesigning the executor.

## Consequences

* Wave 1 single-specialist requests are entirely unaffected. The plan branch
  is skipped unless every gate passes (planner registered, plan persistence
  enabled, non-anonymous caller, non-council intent, and
  `decision.DetectedIntents.Count >= MinDetectedIntentsForPlan`, default 2).

* Council remains the priority for `council/health` requests — the plan
  branch explicitly defers to it. Multi-domain non-council requests get a
  plan; anything else keeps its existing dispatch.

* The plan store is the third opt-in persistence stripe (memory, sessions,
  plans). Operators who want the feature flip one flag; those who don't get
  zero extra database files or endpoints.

* Anonymous callers can never enter the plan-first path. Their identity is
  a shared bootstrap key, so persistence and cost attribution would be
  meaningless. The single-specialist path continues to handle them as it
  always has.

* `RequestToolContext.Begin` is now idempotent w.r.t. an outer scope. Callers
  that legitimately want a fresh scope must confirm no outer scope is in
  force first, which every current caller already does implicitly (they run
  at the request root). The change is behavior-preserving for the pre-#93
  single-specialist path.

* The `plan_step` span is chatty — a five-step plan emits five per-step spans
  plus a root plan span. That is intentional; the frontend telemetry panel
  needs both the per-step and the roll-up view. If we ever need a quieter
  option, the collector can drop `plan_step` spans while keeping the plan
  root, without any executor change.

## References

* Issue #93 — plan-first orchestration
* ADR-006 — tool-context budget (cumulative accounting contract)
* ADR-007 — MAF agent primitives (ChatClientAgent seam reused by the planner)
* ADR-008 — data-driven agent definitions (planner hydration)
* #94 — human plan review (future, builds on the checkpoint persistence)
* #96 — plan frontend (future, consumes `/api/plans`)
