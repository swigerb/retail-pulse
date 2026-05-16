# Target — History

## Notification — 2026-05-16 Timeout Fix from Costco

🔔 **Action Required:** Update timeout assertions in tests to expect **60s** instead of 150s (or 75s per-request).

**Context:** Costco fixed 504 timeouts by setting request-level timeout to 60s (both `/api/chat` and `/api/chat/stream`). Any tests that mock slow IChatClient should expect cancellation at 60s, not the old 150s limit. Tests with `MaximumIterationsPerRequest` assertions should expect 1, not 2.

**See:** `.squad/decisions.md` — "Aggressive fast-fail timeouts for chat endpoints (2026-05-16)"

---

## Recent Work (2026-05-15 onwards)

## 2026-05-15 — Executive demo readiness sweep (26 default UI prompts)

**Scope:** Validated every default/suggested question rendered on the empty-state chat UI in `ChatPanel.tsx` (`PROMPT_CATEGORIES`) — 7 categories x ~3 prompts = **26 prompts** total covering General Retail, Grocery, QSR, Home Improvement, Office Supply, Furniture, and Charts.

**New tests:** `tests/RetailPulse.Tests/Integration/DemoReadinessTests.cs` (58 cases). Mirrors production `RoutingServiceExtensions.AddAgentRouting` registration order so first-wins lookup in `RetailOpsRouter` reflects what the live API actually does.

**Coverage added:**
- `DefaultPrompt_RoutesToRegisteredSpecialist_AndReturnsNonEmptyReply` — every UI prompt classifies, dispatches, and returns a non-empty reply.
- `DefaultPrompt_IsWellFormed` — length 20-200, no TODO/XXX placeholders.
- `IntentCoverage_IsDocumentedAndNoOrphans` — every `AgentIntent` is claimed by a specialist (or documented fallback).
- `UnclaimedIntent_RouterFallsBackToGeneral_NotException` — `scorecard/portfolio` and `council/health` degrade to General.
- `LowConfidenceClassification_FallsBackToGeneral` — confidence < 0.6 routes to General.
- `RouterClassificationFailure_FallsBackToGeneral` — model timeout / HttpRequestException does NOT crash the endpoint.
- `MalformedRouterJson_FallsBackToGeneral` — bad JSON from upstream model does NOT crash.

**Demo-risk findings (surfaced to record, NOT regressions in the existing tests):**

1. **`GeneralAgent` shadows four specialists.** `GeneralAgent.SupportedIntents` claims `PromotionTrade`, `SupplyShipments`, `CompetitiveMarket`, `SentimentField` and is registered first in DI. `RetailOpsRouter` uses `TryAdd` (first-wins), so `PromoPlanningAgent`, `SupplyChainAgent`, `CompetitiveIntelAgent` are NEVER selected by the chat router for those intents. Specialist-specific behavior (e.g., `CompetitiveIntelAgent` alert hooks, `PromoPlanningAgent` approval gate) won't fire from chat. Likely intentional today (specialists power dedicated UI panels), but the team should confirm.

2. **`scorecard/portfolio` has no specialist.** Defined in `AgentIntent.All` but no agent claims it — silently falls back to General. Tracked via `UnclaimedIntent_RouterFallsBackToGeneral_NotException`.

3. **`sentiment/field` is handled by GeneralAgent.** No dedicated sentiment agent exists. The 6 "field sentiment" prompts on the UI all hit General — its tools must cover sentiment, otherwise reply quality on those clicks degrades to generic LLM output.

4. **No HTTP integration tests for `/api/chat` or `/api/chat/stream`.** Existing suite tests router + specialists in isolation but never exercises the full endpoint (which is gated on Azure credentials at startup, per existing comment in `RouterIntegrationTests`). The demo will hit the live endpoint for the first time. Risks: guardrails ordering, cache-key collisions, `OperationCanceledException` handling, the council interception path on line 191 of `ChatEndpoints.cs`.

5. **75-second per-request timeout.** Both endpoints linked-cancel after 75s. If gpt-5.4 model latency spikes during the demo, expect `504 GATEWAY_TIMEOUT` with `request_timeout` code. Streaming endpoint emits `streaming:error` so the UI spinner stops gracefully.

6. **Chart prompts route via `DemandForecasting`** (or `CompetitiveMarket` for "market share", `SupplyShipments` for "inventory health"). Chart rendering depends on the agent calling `ChartDataTool` — verify in a live smoke test that each chart-style prompt actually triggers tool invocation.

**Baseline:** Backend went 1576 → 1634 passing (added 58 new). Frontend 251 passing (no changes).

**Commands verified:**
- `dotnet test tests/RetailPulse.Tests/RetailPulse.Tests.csproj` — 1634 passing in ~1m22s
- `cd src/RetailPulse.Web && npx vitest run` — 251 passing in ~82s

**Recommendation for the live demo:** smoke-test the 6 "field sentiment" prompts and the 8 chart prompts on the actual `/api/chat` endpoint at least 10 minutes before the executive presentation. These two clusters depend on tool routing that the in-memory tests cannot exercise.

## Learnings

### 2026-05-17 — Empty response bug fix + smoke tests (Apex Grill regression)

**Scope:** Investigated "How is Apex Grill performing in the Southwest this quarter?" returning empty response in the UI. Traced full code path and identified root cause.

**Root cause:** `AgentExecutionPipeline.cs` lines 160/324 used `response.Text ?? context.FallbackReply`. When the LLM returns no text content (e.g., MaxIterations=1 exhausted after a tool call), `ChatResponse.Text` returns empty string `""` (not `null`), so the `??` operator passes through the empty string instead of activating the fallback.

**Fix:** Changed both locations to `string.IsNullOrWhiteSpace(rawText) ? context.FallbackReply : rawText` — catches null, empty, and whitespace-only responses.

**New test file:** `tests/RetailPulse.Tests/Agents/Router/DemoQuerySmokeTests.cs` — 9 tests:
- Routing fast-path correctness (BrandPerformingRegex → General)
- LLM bypass verification (keyword match skips classification)
- Non-empty response assertion (the original symptom)
- Fallback reply activation when LLM returns null/empty
- Full pipeline integration (route → select → execute)
- Specialist resolution by AgentKey
- Confidence threshold verification (≥0.9)
- Intent constant correctness (general/fallback)
- Dual-regex non-interference (brand vs portfolio)

**Also fixed:**
- `DemoReadinessTests.cs` lines 52/62: Updated expected intents for "How is X performing..." queries from DemandForecasting to General (matches actual BrandPerformingRegex behavior)
- `AgentPipelineTests.cs` line 177-179: Updated fallback test assertion to expect "Custom fallback" instead of empty string (old test was asserting the broken behavior)

**Test count:** 1915 passing (added 9 new smoke tests, net +9 after fixing existing assertion).

### 2026-05-16 — Rate-limit fix test coverage (429 remediation)

**Scope:** Added tests for 3 of 4 rate-limit fixes (Fix 2: classification cache, Fix 3: expanded keywords, Fix 4: separate router model).

**New test files:**
- `tests/RetailPulse.Tests/Caching/RouterClassificationCacheTests.cs` — 9 tests covering cache hit/miss, TTL expiration, message normalization (trim + case-insensitive), different messages → different entries, multi-intent preservation, overwrite semantics.
- Extended `tests/RetailPulse.Tests/Agents/Router/RetailOpsRouterTests.cs` — added keyword fast-path tests (PortfolioPerformingRegex, BrandPerformingRegex, planogram, promotion ROI, brand scorecard), plus separate router model verification tests.

**Key findings:**
1. Costco added `BrandPerformingRegex` (`how is .+ (performing|doing)`) which routes brand+region performance queries to General via fast-path. This means "How is Apex Grill performing in the Southwest?" never hits the LLM — saves ~200ms and a TPM token.
2. Costco expanded `_keywordPatterns` with `"promotion"`, `"promotion roi"`, `"promo effectiveness"` → PromotionTrade and `"scorecard"`, `"brand scorecard"`, `"performance scorecard"` → Scorecard.
3. `RouterClassificationCache` uses SHA256 key generation with `message.Trim().ToLowerInvariant()` normalization — simple but effective deduplication.

**Test count:** Router tests went from ~30 to 104 passing. Cache tests: 9 passing. Total verified: 113 tests passing.

---

**Archive:** See target/history-archive.md for prior sessions.