### Cost Tracking: Config-Driven Pricing + Model Passthrough (2026-05-13)

- **Context:** The cost tracker hardcoded `"gpt-4o"` as the model name for every usage event, and `InMemoryCostTracker` maintained a separate hardcoded pricing table that diverged from the `TokenPricing` section in `appsettings.json`. Additionally, `Program.cs` had duplicate singleton registrations for `AdaptiveCardState`, `CostTracker`, `AuditLog`, and `ConversationExporter`.
- **Decision:**
  1. Added `Model` property to `ISpecialistAgent` contract. All specialist agents expose their configured model from `AgentDefinition.Model`. `MemoryManagementAgent` returns `"none"` since it doesn't call an LLM.
  2. `Program.cs` chat endpoint now uses `specialist.Model` instead of hardcoded `"gpt-4o"` when creating `UsageEvent`.
  3. `InMemoryCostTracker` reads pricing from `IConfiguration` (`TokenPricing:*` section) instead of a hardcoded dictionary. Unknown models fall back to $1.00/$5.00 per 1M tokens (input/output).
  4. Removed duplicate DI registrations — each service registered exactly once.
- **Impact:** Cost dashboards now show accurate per-model costs. Adding new model pricing requires only an `appsettings.json` update — no code changes. All 1,413 tests pass.
- **Owner:** Costco (Backend Dev)
