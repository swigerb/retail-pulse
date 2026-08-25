# ADR-001: Multi-Agent Routing Architecture

## Status

Superseded by:
- **ADR-008** (data-driven agent definitions) — specialists are no longer
  one-class-per-agent; they are declared in `packs/<pack>/agents.yaml` and
  materialised by `ConfiguredSpecialistAgent`.
- **ADR-014** (plan-first orchestration using MAF Workflows) — the router's
  `DetectedIntents` is no longer discarded after the first hit; multi-domain
  requests are lifted onto a `PlanExecutor` workflow.
- **ADR-007** (MAF agent primitives) — every specialist invocation, including
  the router itself, now runs through `MafAgentInvoker` /
  `Microsoft.Agents.AI.ChatClientAgent` instead of a bespoke chat loop.

The original intent classification / confidence-threshold / fallback shape
recorded below is still what happens on the fast single-shot path, but the
admission decision (fast vs. plan vs. council) is owned by
`HybridExecutionDecider`, not the router.

## Context

RetailPulse serves a multi-category retail conglomerate with diverse analytical needs — demand forecasting, promotional planning, competitive intelligence, supply chain, store operations, margin analysis, and more. A single monolithic agent cannot effectively handle this breadth of domain knowledge while maintaining response quality and manageable prompt sizes.

## Decision

We implement a **specialist agent routing architecture** where:

1. A lightweight **Router Agent** classifies incoming user messages by intent using an LLM-based classification prompt.
2. The router produces a `RoutingDecision` containing the target agent key, detected intent, and confidence score.
3. A pool of **Specialist Agents** (demand-forecast, promo-planning, competitive-intel, supply-chain, store-ops, planogram, margin, field-sentiment) each handle their domain with focused system prompts and domain-specific tools.
4. A **General Agent** serves as the fallback when no specialist matches or confidence is below threshold.
5. Each specialist registers via `ISpecialistAgent` and is resolved by key at runtime.

## Consequences

**Positive:**
- Each agent has a focused system prompt (~500 tokens vs. ~5000 for a monolithic prompt), improving response quality.
- New domains can be added by implementing `ISpecialistAgent` without modifying existing agents.
- Router classification adds only ~200ms latency while saving context window budget downstream.
- Tool registration is per-agent, reducing hallucinated tool calls.

**Negative:**
- Misrouting can occur when user intent is ambiguous; the confidence threshold must be tuned.
- Adding a new specialist requires updating the router prompt with the new intent category.
- Memory context is loaded before routing, which adds latency even if the agent doesn't need it (addressed in Sprint 5.6).

## Why superseded

- "Adding a specialist requires implementing `ISpecialistAgent`" is no longer
  true. Adding a specialist is now a `packs/<pack>/agents.yaml` edit —
  `ConfiguredSpecialistAgent` binds the definition to a shared MAF invoker
  (ADR-008). Bespoke C# specialists (`MemoryManagement`,
  `CompetitiveIntel`, `PromoPlanning`) still exist for behaviour that cannot
  be expressed as prompt + tool bindings.
- "Router chooses one specialist" understates the current pipeline. The
  router still classifies, but `HybridExecutionDecider` inspects
  `DetectedIntents`, confidence, and advisory phrasing to lift multi-domain
  turns onto the plan path (ADR-014). `/api/escalate` is retained only as a
  deprecated compatibility endpoint.
- Confidence threshold tuning is now the
  `PlanPersistence:MinConfidenceForFastPath` setting evaluated by
  `HybridExecutionDecider` (bound via `PlanPersistenceOptions.SectionName =
  "PlanPersistence"`), not a router-internal constant.
