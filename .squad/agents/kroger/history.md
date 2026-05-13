# Kroger — History

## 2026-04-30 — Team Initialization

- **Project:** Retail Pulse — a generic pro-code agentic demo for retail & consumer goods organizations (grocers, QSRs, big box retail)
- **Stack:** .NET 10, C#, Aspire (host + OTel, non-containerized), React/Vite/TypeScript, Azure API Management, AI Gateway pattern
- **Owner:** Brian Swiger
- **Context:** Built on Patron Pulse but updated to be generic with tenant configuration, extra organization examples, and corrected diagrams

## Learnings

### 2026-05-13 — Multi-Agent Router Architecture (Sprint 1.1)

**Architecture Decisions:**
- Router uses LLM-based classification with JSON-mode response format at temperature 0.1 for deterministic routing.
- Confidence threshold of 0.6 — below this, everything falls back to General agent. This prevents hallucinated routing.
- Interfaces (`IAgentRouter`, `ISpecialistAgent`) live in `RetailPulse.Contracts` so TeamsBot and any future consumers can reference them without depending on the Api project.
- `RetailOpsRouter` parses JSON classification and validates against `AgentIntent.All` — unknown intents get normalized to `general/fallback`.

**Key File Paths:**
- `src/RetailPulse.Contracts/Routing/` — `IAgentRouter.cs`, `ISpecialistAgent.cs`, `AgentIntent.cs`
- `src/RetailPulse.Api/Agents/Routing/RetailOpsRouter.cs` — the LLM-based router
- `src/RetailPulse.Api/Agents/Specialists/GeneralAgent.cs` — refactored from RetailPulseAgent
- `src/RetailPulse.Api/Agents/RoutingServiceExtensions.cs` — `AddAgentRouting()` DI registration
- `src/RetailPulse.Api/prompts.yaml` — `agents.router` section added

**Patterns:**
- Adding a new specialist: implement `ISpecialistAgent`, register as `services.AddScoped<ISpecialistAgent>(sp => ...)` in `AddAgentRouting()`.
- Router prompt is in YAML — easy to tune without code changes.
- Legacy `RetailPulseAgent` kept as thin wrapper delegating to `GeneralAgent.HandleAsync()` — existing test suite (174 tests) passes unchanged.

## Session Work — 2026-05-13 Sprint 1.1 Multi-Agent Router (Complete)

**Outcome:** ✅ SUCCESS — Lead architect role, all contracts and router implementations complete, 174 tests passing unchanged

**Deliverables:**
- `RetailPulse.Contracts/Routing/IAgentRouter.cs` — routing classification contract with RoutingDecision record
- `RetailPulse.Contracts/Routing/ISpecialistAgent.cs` — specialist agent interface (Key, DisplayName, SupportedIntents, HandleAsync)
- `RetailPulse.Api/Agents/Routing/RetailOpsRouter.cs` — LLM-based router with 0.6 confidence threshold, internal ParseClassification method
- `RetailPulse.Api/Agents/Specialists/GeneralAgent.cs` — refactored from RetailPulseAgent, implements ISpecialistAgent, backward compatible
- `RetailPulse.Api/Extensions/AgentRoutingServiceCollectionExtensions.cs` — `AddAgentRouting()` DI registration
- `src/RetailPulse.Api/prompts.yaml` — router classification prompt under `agents.router` key

**Reconciliation:** Costco parallelized the same work; adopted my contract design (IAgentRouter, ISpecialistAgent, slash-separated intents, Contracts.Routing namespace). Costco reconciled their parallel implementation around my interfaces.

**Cross-Agent Collaboration:**
- Costco (Backend): Contract reconciliation, RoutingInfo on ChatResponse, legacy wrapper verification
- Chick (Frontend): Agent routing UI constants and components (AgentRoutingIndicator, AgentRoutingPanel)
- Target (Tester): 63 comprehensive tests for router classification, GeneralAgent, integration pipeline

**Test Status:** All 174 existing tests pass unchanged; 63 new tests bring total to 237 (all passing)

**Decision Logged:** Multi-Agent Router Architecture
