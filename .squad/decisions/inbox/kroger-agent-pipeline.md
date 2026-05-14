# Decision: Extract Shared Agent Execution Pipeline

**Date:** 2025-07-24
**Author:** Kroger (Lead Architect)
**Status:** Implemented

## Context

All 8 LLM-calling specialist agents duplicated ~200 lines of identical `HandleAsync` logic:
message construction, LLM invocation, error handling (429/general), telemetry collection,
tool span extraction, chart extraction, token accounting, and response building.

## Decision

Extract the shared execution pattern into `IAgentExecutionPipeline` / `AgentExecutionPipeline`.

- **Interface:** Single `ExecuteAsync(AgentExecutionContext, CancellationToken)` method.
- **Context record:** Carries per-invocation data (agent name, system prompt, temperature, model, request, tools, fallback reply, optional `OnToolResult` callback).
- **DI:** Registered as scoped service; agents receive it via constructor injection.
- **Extensibility:** `OnToolResult` callback enables CompetitiveIntelAgent to fire alerts without duplicating the pipeline.

## Agents Affected

GeneralAgent, DemandForecastAgent, PromoPlanningAgent, CompetitiveIntelAgent,
SupplyChainAgent, StoreOpsAgent, MarginAgent, PlanogramAgent.

MemoryManagementAgent excluded (no LLM call pattern).

## Consequences

- Each agent reduced from ~260 lines to ~55 lines (delegates to pipeline).
- Single place to fix error handling, telemetry, or token accounting bugs.
- `BuildTokenUsage` now lives on the pipeline (takes explicit `modelName` parameter).
- Tests create `AgentExecutionPipeline` instances directly rather than mocking removed constructor params.
