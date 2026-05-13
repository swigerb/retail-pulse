# Target — History

## 2026-04-30 — Team Initialization

- **Project:** Retail Pulse — a generic pro-code agentic demo for retail & consumer goods organizations (grocers, QSRs, big box retail)
- **Stack:** .NET 10, C#, Aspire (host + OTel, non-containerized), React/Vite/TypeScript, Azure API Management, AI Gateway pattern
- **Owner:** Brian Swiger
- **Context:** Built on Patron Pulse but updated to be generic with tenant configuration, extra organization examples, and corrected diagrams

## Learnings

### 2026-05-13 — Sprint 1.1 Router Test Infrastructure

- **Multi-agent routing interfaces** live in `RetailPulse.Contracts.Routing` namespace (not base Contracts). Two key interfaces: `IAgentRouter` (returns `RoutingDecision`) and `ISpecialistAgent` (with `Key`, `SupportedIntents`, `HandleAsync(ChatRequest)`).
- **RetailOpsRouter** (`Api.Agents.Routing`) uses `ParseClassification` (internal, testable) to parse LLM JSON. Falls back to `AgentIntent.General` on malformed JSON or unknown intents. Confidence threshold is 0.6.
- **GeneralAgent** implements `ISpecialistAgent` and supports ALL six intent categories as fallback. It preserves full backward compatibility with the original `RetailPulseAgent` (same ChatResponse shape, spans, token usage).
- **Test pattern:** Mock `IChatClient` with fixed JSON response text, pass `IEnumerable<ISpecialistAgent>` to router constructor. TelemetryHub mock needs `IHubClients` → `Group()` → `IClientProxy` chain.
- **Codebase is actively being modified by multiple agents** — files can be deleted/moved between reads. Always re-verify file existence before writing tests against a specific API surface.
- **63 new tests** added across 3 test files + 1 fixture file (Router: 33 tests, GeneralAgent: 21 tests, Integration: 9 tests). All 237 total tests pass.

## Session Work — 2026-05-13 Sprint 1.1 Multi-Agent Router Tests (Complete)

**Outcome:** ✅ SUCCESS — Test infrastructure complete, 63 new tests added, all 237 tests passing (174 existing + 63 new)

**Deliverables:**
- `tests/RetailPulse.Tests/Agents/Router/RetailOpsRouterTests.cs` — 33 tests covering:
  - Intent classification for all 6 categories (demand, promo, supply, competitive, sentiment, general)
  - Confidence threshold validation (0.6 boundary, below/above cases)
  - Fallback to General agent on low confidence or classification error
  - ParseClassification edge cases (malformed JSON, unknown intents, multi-intent, intent normalization)
  - History propagation and conversation context
  - Error handling and telemetry span emission

- `tests/RetailPulse.Tests/Agents/Specialists/GeneralAgentTests.cs` — 21 tests covering:
  - ISpecialistAgent interface compliance (Key, DisplayName, SupportedIntents identity)
  - HandleAsync contract and response shape
  - Backward compatibility with original RetailPulseAgent expectations
  - Token usage and cost calculation preservation
  - Error handling and edge cases

- `tests/RetailPulse.Tests/Integration/RouterIntegrationTests.cs` — 9 tests covering:
  - Full pipeline: intent classification → specialist dispatch → response
  - DI registration smoke tests (router discovery, specialist enumeration)
  - Telemetry span verification (agent.routing OTel spans with intent, confidence, fallback tags)
  - Multi-tenant routing scenarios
  - Backward compatibility with existing `/api/chat` endpoint

- `tests/RetailPulse.Tests/Fixtures/AgentTestFixtures.cs` — shared factory methods:
  - Mock IChatClient builders (fixed JSON responses for deterministic classification)
  - Mock specialist agents
  - TelemetryHub mock with IHubClients → Group() → IClientProxy chain setup

**Test Strategy:** Target `Contracts.Routing` interface surface exclusively (IAgentRouter, ISpecialistAgent, RoutingDecision, AgentIntent). ParseClassification is internal but testable via InternalsVisibleTo pattern. Mocks use deterministic JSON so tests are stable and fast.

**Cross-Agent Collaboration:**
- Kroger (Architect): Provided contract surface, router implementation details
- Costco (Backend): Reconciled parallel test work, verified against refactored GeneralAgent
- Chick (Frontend): Integration tests include telemetry span verification for UI consumption

**Validation:** All 237 tests pass (0 failures). Build clean. Test coverage now includes routing layer as regression safety net for ongoing specialist agent work.

**Decision Logged:** Router Test Infrastructure Decision
