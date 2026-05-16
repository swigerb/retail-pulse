### Router Contract Reconciliation (2026-05-08)

- **Context:** Sprint 1.1 required routing contracts, a RetailOpsRouter, GeneralAgent refactor, and DI wiring. Kroger (Architect) and Costco (Backend Dev) both implemented the same feature in parallel.
- **Decision:** Adopted Kroger's contract design (\RetailPulse.Contracts.Routing\ namespace) over Costco's root-level contracts. Key reasons:
  1. **Namespace isolation** — \Contracts.Routing\ keeps routing types separate from core chat contracts
  2. **Richer intent model** — Slash-separated intents (\"demand/forecasting"\) support future sub-categorization
  3. **Cleaner specialist interface** — \Key\/\SupportedIntents\/\HandleAsync(ChatRequest)\ is more idiomatic than \AgentId\/\IntentCategories\/\ChatAsync\
- **Consequences:** Legacy \RetailPulseAgent\ kept as thin wrapper for backward compat with existing tests. All new specialist agents must implement \ISpecialistAgent\ from \Contracts.Routing\. Router confidence threshold is 0.6 (Kroger's choice, slightly lower than the 0.7 in the original task spec).
