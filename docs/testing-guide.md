# Retail Pulse Testing Guide

This guide covers how to run tests and manually verify the Teams bot integration and chart visualization features.

---

## Authentication Environments

Retail Pulse behaves differently depending on where you're testing. Understanding the auth boundary is critical for accurate test expectations.

### Local Development (Bot Framework Emulator / HTTP Harness)

| Aspect | Behavior |
|--------|----------|
| **Auth handler** | `DevelopmentAuthHandler` bypasses authentication — no SSO, no JWT validation |
| **User identity** | Falls back to `activity.From` info (anonymous context) |
| **Tenant validation** | Inactive — no `TenantId` required |
| **SSO token** | Not available — the Emulator does not support Teams SSO |
| **API auth** | `Security:RequireAuth` defaults to `false` in Development; optional `ApiKey` middleware disabled by default |
| **Rate limiting** | Active but lenient — `relaxed` (100/min) policy on most endpoints |

> **Bottom line:** In local dev, the entire auth stack is bypassed. You can test all agent, tool, and visualization behavior without any Azure resources.

### Real Teams Client (SSO Required)

| Aspect | Behavior |
|--------|----------|
| **Auth handler** | `TeamsSsoHandler` validates JWT tokens from Teams SSO |
| **User identity** | Extracted from SSO token claims (`name`, `email`, `oid`) |
| **Tenant validation** | Active — `MicrosoftEntra:TenantId` required in production; `StrictTenantValidation` enforces `tid` claim match |
| **SSO token** | Provided by Teams client via `webApplicationInfo` manifest config |
| **API auth** | JWT Bearer authentication via `Security:JwtAuthority` and `Security:JwtAudience` |
| **Rate limiting** | Full policy enforcement — `strict` (10/min) on chat/AI routes, `upload` (5/min) on knowledge upload |

> **Bottom line:** In real Teams, auth is fully active. Missing or misconfigured tenant IDs will reject requests.

---

## Running Unit Tests

### Run All Tests

```bash
dotnet test
```

### Run Specific Test Project

```bash
dotnet test tests/RetailPulse.Tests/RetailPulse.Tests.csproj
```

### Run Tests with Detailed Output

```bash
dotnet test --logger "console;verbosity=detailed"
```

### Run Tests with Coverage (if configured)

```bash
dotnet test --collect:"XPlat Code Coverage"
```

## Unit Test Coverage

The test suite covers the following components:

### AdaptiveCardBuilder Tests
- ✅ Text-only chat responses (no telemetry, no images)
- ✅ Chat responses with telemetry spans (verify telemetry section exists)
- ✅ Chat responses with charts (verify chart elements present)
- ✅ Chat responses with both spans and charts
- ✅ Welcome card generation with branding and suggested actions
- ✅ Error card generation
- ✅ Detailed telemetry report cards
- ✅ All cards use Adaptive Card version "1.8"
- ✅ All cards produce valid JSON

### TelemetryFormatter Tests
- ✅ Duration formatting: 0ms → "0ms", 500ms → "500ms", 1500ms → "1.5s", 60000ms → "60.0s"
- ✅ Span icon mapping: "thought" → 🤔, "tool" → 🔧, etc.
- ✅ Type badge generation for all span types
- ✅ Name truncation with ellipsis for long names
- ✅ Detail truncation and newline replacement
- ✅ Waterfall width calculation with minimum 5% visibility

### SessionManager Tests
- ✅ Session creation and retrieval per conversation
- ✅ Session ID persistence across multiple calls
- ✅ Concurrent access handling for multiple conversations
- ✅ Span storage and retrieval
- ✅ Session clearing functionality

### Router & Agent Tests (Sprint 1+)
- ✅ RetailOpsRouter: 33 tests — intent classification, confidence threshold (0.6), fallback, ParseClassification edge cases
- ✅ GeneralAgent: 21 tests — ISpecialistAgent identity, HandleAsync contract, backward compatibility
- ✅ Router Integration: 9 tests — full pipeline, DI registration, telemetry span verification, multi-tenant scenarios
- ✅ Specialist agents: demand forecast, promo planning, competitive intel, supply chain, store ops, planogram, margin
- ✅ Guardrails, streaming, caching middleware
- ✅ Collaborative adaptive cards, observability suite
- ✅ Escalation orchestrator, scorecard orchestrator, explainability service

## Manual Testing the Teams Bot

### Prerequisites

1. **Aspire Host Running**: Start from the repo root:
   ```bash
   dotnet run --project src/RetailPulse.AppHost
   ```
   The TeamsBot will be available at `http://localhost:5300`.

### Option A: Bot Framework Emulator (No App Registration, No SSO)

The [Bot Framework Emulator](https://github.com/microsoft/BotFramework-Emulator/releases) provides a full chat UI — no Azure resources needed. **SSO is not available** in this mode; the bot uses `DevelopmentAuthHandler` and anonymous user context.

1. Download and open the Bot Framework Emulator
2. Click **Open Bot** and enter: `http://localhost:5300/api/messages`
3. Leave **Microsoft App ID** and **Microsoft App Password** blank
4. Click **Connect** and start chatting

**What works:** Message processing, Adaptive Cards, chart rendering, telemetry, multi-agent routing, all specialist agents.

**What does NOT work:** SSO authentication, tenant validation, JWT token flows. User identity falls back to `activity.From` info.

### Option B: HTTP Test Harness (No SSO)

Use the pre-built `.http` file with VS Code REST Client or JetBrains HTTP Client:

```
tests/RetailPulse.Tests/bot-test.http
```

This includes 6 Activity payloads covering messages, conversation updates, and card actions. See `tests/RetailPulse.Tests/bot-test-README.md` for details.

**Auth note:** Like the Emulator, the HTTP harness bypasses auth entirely. No SSO tokens are exchanged.

### Option C: Real Teams Client (Requires App Registration + SSO)

For testing in the actual Teams client with full SSO:

1. **Bot Registration**: Register your bot in Azure Bot Service with:
   - App ID and password configured in user secrets
   - Messaging endpoint pointed to your tunnel or Azure deployment
   - Teams channel enabled

2. **SSO Configuration**: Configure Entra ID app registration per [Teams Setup Guide](teams-setup.md):
   - `MicrosoftEntra:TenantId` — required for tenant validation
   - `MicrosoftEntra:StrictTenantValidation` — set to `true` for production
   - `MicrosoftEntra:ClientId` — your Entra ID app client ID

3. **Tunnel**: Use ngrok or dev tunnels:
   ```bash
   ngrok http 5300
   ```
   Update the bot messaging endpoint in Azure Portal to: `https://your-ngrok-url.ngrok.io/api/messages`

### Test Scenarios by Environment

#### Emulator / HTTP Harness (Local Dev — No SSO)

##### ✅ Basic Chat Response
1. Open the Emulator and connect to `http://localhost:5300/api/messages`
2. Send a simple message: "Hello"
3. **Expected**: 
   - Receive a chat response card with the Retail Pulse branding (🥃)
   - Reply text is displayed
   - "View Telemetry" button is visible but telemetry section is collapsed by default

##### ✅ Telemetry Toggle Visibility
1. Send any message to the bot
2. Click "📊 View Telemetry" button
3. **Expected**:
   - Telemetry section expands
   - Shows telemetry summary with span icons (🤔, 🔧, ✅, etc.)
   - Shows total duration and span count
   - Each span shows name, type badge, and duration
4. Click "📊 View Telemetry" again
5. **Expected**: Telemetry section collapses

##### ✅ Detailed Telemetry Report
1. Send a message that generates telemetry
2. Click "View Telemetry" to expand the section
3. Click "📋 Full Telemetry Report" button
4. **Expected**:
   - New card appears with full telemetry report
   - Summary section shows total duration, span count, average, and slowest span
   - Waterfall visualization displays timing bars
   - Detailed spans section lists all spans with full details

##### ✅ Chart Generation and Display
1. Send a message requesting visualization: "Show me a chart of sales trends"
2. **Expected**:
   - Response card includes both text reply and chart section
   - "📊 Visualizations" header appears
   - Native Adaptive Card chart element is rendered (Chart.Line, Chart.Donut, etc.)
   - Chart title and data labels are visible

##### ✅ Multi-Agent Routing
1. Send: "What's the 90-day demand forecast for Sierra Gold Tequila?"
2. **Expected**: Routing span shows `demand/forecasting`, blue "Demand Forecast" badge
3. Send: "What are the competitive threats in the Spirits category?"
4. **Expected**: Routing span shows `competitive/intelligence`, red "Competitive Intel" badge

##### ✅ Welcome Card on Member Join
1. Add the bot to a new chat (or simulate a conversation update)
2. **Expected**:
   - Welcome card appears with "👋 Welcome to Retail Pulse, [Your Name]!"
   - Branding (🥃) is prominent
   - Suggested actions are displayed

##### ✅ Error Handling (API Down)
1. Stop the RetailPulse.Api service (stop Aspire or kill the API process)
2. Send a message to the bot
3. **Expected**:
   - Error card appears with ⚠️ icon
   - "Error" header in attention style
   - Error message displayed (e.g., "Connection to API failed")
   - "🔄 Try Again" button is visible
4. Restart the API and click "Try Again"
5. **Expected**: Bot should process the request successfully

##### ✅ Multi-turn Conversation (Session Persistence)
1. Start a new conversation: "What's the status of shipment SH-2025-042?"
2. Send a follow-up: "Show me its history"
3. Send another follow-up: "Create a chart"
4. **Expected**:
   - Each response maintains context from previous messages
   - Session ID remains the same across all turns (visible in telemetry if detailed report requested)
   - Telemetry from all turns is available

##### ✅ Chart Data Validation
1. Send a message that generates a chart
2. Check the API response in logs or Aspire dashboard
3. **Expected**:
   - `ChatResponse` includes a `Charts` array with `ChartSpec` objects
   - Each `ChartSpec` has: `Type`, `Title`, `Data` (with series and data points)
   - Web UI renders the chart as an interactive Recharts SVG
   - Teams renders the chart as a native Adaptive Card chart element

#### Real Teams Client (SSO Active)

##### ✅ SSO Authentication Flow
1. Open the bot in Teams and send a message
2. **Expected**:
   - Bot uses SSO token to authenticate — no manual sign-in prompt
   - User's display name and email are extracted from JWT token claims
   - `TeamsSsoHandler` validates the token signature, issuer, audience, and expiry
   - Tenant (`tid`) claim matches the configured `MicrosoftEntra:TenantId`

##### ✅ Tenant Validation
1. With `StrictTenantValidation: true`, send a message from a user in a different tenant
2. **Expected**: Request is rejected — `tid` claim does not match configured tenant
3. With `StrictTenantValidation: false` (or unconfigured), multi-tenant `common` issuer is accepted

##### ✅ Rate Limiting Enforcement
1. Send more than 10 messages in rapid succession
2. **Expected**: After the limit, responses return HTTP 429 (Too Many Requests) per the `strict` rate policy

## Verifying Telemetry Flow

### Check Telemetry Spans End-to-End

1. **Enable detailed logging** in `appsettings.Development.json`:
   ```json
   {
     "Logging": {
       "LogLevel": {
         "RetailPulse.TeamsBot": "Debug",
         "RetailPulse.Api": "Debug"
       }
     }
   }
   ```

2. **Send a test message** through Teams
3. **Check logs** for:
   - API receives chat request with session ID
   - Agent pipeline executes and generates spans
   - Spans are captured and returned in ChatResponse
   - TeamsBot receives response with spans
   - AdaptiveCardBuilder processes spans into telemetry section

### Telemetry Data Integrity

Verify each span contains:
- ✅ **Name**: Non-empty string
- ✅ **Type**: One of: "thought", "tool_call", "tool_result", "response", "foundry", "agent_call"
- ✅ **Detail**: Descriptive text (may be empty)
- ✅ **DurationMs**: Positive number
- ✅ **Timestamp**: Valid DateTimeOffset

## Testing Chart Rendering

### Generate a Sample Chart

1. Send this message: "Create a bar chart showing Q1 sales: January $50K, February $75K, March $100K"
2. **Expected**:
   - Agent calls `CreateChart` tool and emits a `ChartSpec` JSON
   - `ChatResponse` includes a `Charts` array with the chart spec
   - ChartSpec contains:
     - `Type`: "bar"
     - `Title`: Description of the chart
     - `Data`: Series with data points for each month
   - Web UI renders an interactive Recharts bar chart
   - Teams renders a native `Chart.HorizontalBar` Adaptive Card element

### Test Multiple Charts in One Response

1. Send: "Compare revenue and expenses with two separate charts"
2. **Expected**:
   - Multiple `ChartSpec` objects in the `Charts` array
   - Each chart renders independently
   - Web UI shows multiple interactive Recharts charts
   - Teams shows multiple native Adaptive Card chart elements

## Troubleshooting

### Tests Fail to Compile

**Issue**: Missing references or package restore needed

**Solution**:
```bash
cd tests/RetailPulse.Tests
dotnet restore
dotnet build
```

### Bot Doesn't Respond in Teams

**Check**:
1. Aspire dashboard shows all services running (green)
2. Ngrok tunnel is active and endpoint is updated in Azure Bot Service
3. Bot credentials (App ID, password) are correct in user secrets
4. Check logs in Aspire dashboard for errors

### Charts Don't Render

**Check**:
1. API response includes `Charts` array with valid `ChartSpec` objects
2. Web: Recharts package is installed (`npm ls recharts`)
3. Teams: Adaptive Card schema version is `1.8` or higher
4. See [Chart Rendering Guide](chart-rendering.md) for supported chart types

### Telemetry Not Showing

**Check**:
1. API is returning `Spans` in the `ChatResponse`
2. SessionManager is storing spans correctly (check logs)
3. AdaptiveCardBuilder is receiving spans (add breakpoint or log)
4. Card JSON includes `telemetrySection` element

### Session Not Persisting Across Turns

**Check**:
1. SessionManager is registered as a singleton in DI
2. Conversation ID is consistent across turns (Teams provides this)
3. Session ID is being passed in API requests

## CI/CD Integration

### Run Tests in GitHub Actions

Example workflow step:

```yaml
- name: Run Tests
  run: dotnet test --no-build --verbosity normal --logger "trx;LogFileName=test-results.trx"

- name: Publish Test Results
  uses: EnricoMi/publish-unit-test-result-action@v2
  if: always()
  with:
    files: '**/test-results.trx'
```

### Test Coverage Reports

Generate coverage and upload to CodeCov:

```yaml
- name: Generate Coverage
  run: dotnet test --collect:"XPlat Code Coverage" --results-directory ./coverage

- name: Upload Coverage
  uses: codecov/codecov-action@v3
  with:
    directory: ./coverage
```

## Further Testing

- **Load Testing**: Use Bot Framework Emulator or custom scripts to simulate high message volume
- **E2E Testing**: Use Playwright or Selenium to automate Teams web client interactions
- **Integration Testing**: Test API → Teams Bot flow with TestServer and in-memory bot adapter

## Questions or Issues?

Contact the development team or file an issue in the repository.

---

## Test Infrastructure Overview

RetailPulse has **3,200+ tests** across multiple testing strategies:

| Category | Location | Framework | Count |
|----------|----------|-----------|-------|
| Backend unit + integration + contract + E2E | tests/RetailPulse.Tests/ | xUnit + FluentAssertions + Moq + WebApplicationFactory | ~2,669 |
| Load tests | tests/RetailPulse.LoadTests/ | NBomber | 2 scenarios |
| Benchmarks | tests/RetailPulse.Benchmarks/ | BenchmarkDotNet | 3 suites |
| Frontend | src/RetailPulse.Web/ | Vitest + Testing Library | ~552 |

Backend coverage includes OWASP/security suites (`Security/`), chaos suites (`Chaos/`), value-object suites (`ValueObjects/`), deployment/IaC contract suites (`Deployment/`), and provider-matrix suites — all in the single `RetailPulse.Tests` project.

---

## Running All Tests

```bash
# Backend (all ~2,669 tests)
dotnet test RetailPulse.slnx --verbosity quiet

# Frontend
cd src/RetailPulse.Web && npx vitest run

# With coverage
dotnet test RetailPulse.slnx --collect:"XPlat Code Coverage"
```

---

## Contract Tests

Contract tests verify the API's request/response shape stays stable:

- **ChatEndpointContractTests** — Validates `POST /api/chat` request schema, response structure, and error format (RFC 7807)
- **McpToolContractTests** — Validates MCP tool schemas haven't changed (breaking change detection)

These use `WebApplicationFactory<Program>` with mocked external services.

```bash
dotnet test --filter "Category=Contract"
```

---

## E2E Demo Scenario Tests

The 5 executive demo queries are tested end-to-end with deterministic mocks:

1. "How is Apex Grill performing in the Southwest this quarter?"
2. "What's our competitive pricing position for premium burgers?"
3. "What's the sentiment from field reps about our new Smokehouse line?"
4. "Show me the portfolio health across all regions"
5. "What are the top inventory depletion risks this week?"

Each test verifies the full pipeline (routing → agent → tool calls → response) returns a valid response in < 10s with mocked services.

```bash
dotnet test --filter "Category=E2E"
```

---

## Load Tests (NBomber)

Two load test scenarios in `tests/RetailPulse.LoadTests/`:

| Scenario | Pattern | Assertion |
|----------|---------|-----------|
| ChatEndpointScenario | Ramp 1→10 users over 30s | p95 < 5s |
| HealthCheckScenario | Sustained 50 req/s for 60s | p99 < 200ms |

```bash
cd tests/RetailPulse.LoadTests
dotnet run -c Release
```

Results are written to `reports/` directory with HTML visualization.

---

## Mutation Testing (Stryker.NET)

Mutation testing validates test effectiveness by injecting bugs:

**Config:** `stryker-config.json` at repo root

| Setting | Value |
|---------|-------|
| Target | RetailPulse.Api |
| Mutate paths | Agents/Routing, Validation, Caching |
| High threshold | 80% |
| Low threshold | 60% |
| Break threshold | 50% |

```bash
# Install (first time)
dotnet tool install -g dotnet-stryker

# Run
dotnet stryker
```

---

## Benchmarks (BenchmarkDotNet)

Performance regression detection for critical paths:

| Benchmark | What it measures |
|-----------|-----------------|
| RouterClassification | Keyword fast-path vs LLM classification latency |
| CacheLookup | MCP response cache hit/miss overhead |
| VoteParsing | Consensus council vote JSON parsing |
| HybridFastPath | Decision-layer overhead for the single-specialist fast path (issue #95) |

```bash
dotnet run -c Release --project tests/RetailPulse.Benchmarks
```

### Hybrid fast-path baseline (issue #95)

A deterministic p50/p95 baseline for the single-specialist fast path is captured
separately so before/after comparisons for the hybrid-execution decision layer do
not depend on a full BenchmarkDotNet run. It measures
`RetailOpsRouter.TryKeywordClassify` against the reference prompt
`"How is Sierra Gold Tequila performing in the Northeast?"` — no network, LLM, or
MCP call is issued.

```bash
dotnet run -c Release --project tests/RetailPulse.Benchmarks -- baseline
```

Writes `tests/RetailPulse.Benchmarks/baselines/hybrid-fast-path-baseline.json`
with commit SHA, environment identifier, sample count, and p50/p95 in
nanoseconds. A material regression (>5% p50 or p95 vs the recorded baseline on
the same environment) is blocking for the hybrid-execution change.

---

## Cache Warming

`CacheWarmingService` (IHostedService) pre-populates the MCP response cache on startup:

- Fires all 5 demo queries through the cache
- Ensures first demo query is a cache hit (fast response)
- Toggle: `CacheWarming:Enabled` (default: true in Development)
- Logs timing for each warmed query

---

## Chaos Tests

Chaos tests validate resilience under failure conditions:

- Circuit breaker behavior (opens after 5 failures)
- Retry exhaustion (3 attempts then dead-letter)
- MCP server unavailability (graceful degradation)
- Timeout handling (75s → clean error)
- Concurrent request safety
- Memory pressure behavior

---

## Answer-Quality Evaluation Harness (Issue #110)

The answer-quality harness lives in `tests/RetailPulse.Tests/Eval/` and grades every
prompt in a versioned golden dataset against the properties Retail Pulse can be held
objectively accountable for: routing intent, explicit-chart detection, chart type,
refusal-adjacent behavior (memory-command detection), and whether the LLM path was
reached at all. It is deliberately narrow. Model-graded rubric properties (answer
wording, refusal quality, clarification quality) are reported separately and never
gate CI on their own.

### Files

| File | Purpose |
|------|---------|
| `Eval/Data/golden-dataset.json` | Curated retail prompts and their deterministic expectations. |
| `Eval/Data/baseline-v1.json` | Versioned per-case observed values. Diff target for regressions. |
| `Eval/Data/known-bad-cases.json` | Deliberately incorrect expectations for scorer self-tests. |
| `Eval/DeterministicEvaluator.cs` | The scorer. Uses the real `RetailOpsRouter` + `ChartRequestDetector`. |
| `Eval/EvaluationRunner.cs` | Orchestrator that produces `EvaluationReport`. |
| `Eval/EvaluationHarnessTests.cs` | The CI gate. |
| `Eval/StabilityTests.cs` | Repeat-run byte-identical determinism check. |
| `Eval/BaselineTests.cs` | Baseline diff. |
| `Eval/ScorerSelfTests.cs` | Known-good + known-bad self-tests. |
| `Eval/DatasetContractTests.cs` | Structural invariants on the dataset. |

### Run the harness

```bash
dotnet test tests/RetailPulse.Tests/RetailPulse.Tests.csproj \
  --filter "FullyQualifiedName~RetailPulse.Tests.Eval"
```

The harness writes a full JSON report to
`tests/RetailPulse.Tests/bin/{Debug,Release}/net10.0/EvalArtifacts/eval-report-offline.json`
on every run. CI also uploads this file as the `eval-harness-report` artifact.

### Extend the dataset

1. Add a new case to `Eval/Data/golden-dataset.json`. Every case needs a unique id,
   a category, a fictional prompt consistent with the seeded Apex Retail Group tenant
   (see `tenant.yaml`), and an `expectations` block with:
   - `explicit_chart` (bool) + `chart_type` (canonical `ChartSpec.Type` or `null`)
   - `routing_mode`: `keyword-fast-path` (deterministically graded) or `llm-required`
     (routing recorded but not graded — the deterministic scorer only gates on the
     keyword-fast-path cases)
   - `routing_intent`: an `AgentIntent.*` value when `routing_mode = keyword-fast-path`
   - `memory_command` (bool)
   - `refusal_expected`, `requires_clarification`, `retrieval_expected`,
     `retrieval_source`: recorded for future live evaluation, not graded now
2. Run the harness. If the case genuinely reflects live behavior, all properties will
   pass on the first run.
3. Refresh the baseline (see below) in the same commit as the dataset change.
4. If the case adds a new category, update the `categories` list in the dataset and the
   required-categories lists in `DatasetContractTests.cs`.

All new categories must retain the pre-existing coverage guarantees: every one of the
nine `ChartSpec.Type` values (`line`, `bar`, `groupedBar`, `stackedBar`,
`horizontalBar`, `pie`, `donut`, `gauge`, `table`) must remain represented; the
ambiguous, refusal, tenant-unavailable, and adversarial-injection categories must
remain represented; and every prompt must remain fictional.

### Interpret the report

`eval-report-offline.json` has four top-level sections:

| Section | Contents |
|---------|----------|
| `run` | Timestamp, mode (`offline-deterministic`), harness/dataset versions, case counts, CI gate threshold. |
| `cost` | Prompt tokens (rough estimate), completion tokens (0 offline), USD estimate (0 offline), and the enforced `cap_usd`. |
| `summary` | Deterministic pass/fail counts, pass rate, and the `gate_status` (`pass`/`fail`). |
| `category_pass_rate` | Pass rate per golden category (`null` for categories composed entirely of `llm-required` cases). |
| `cases` | Per-case per-property expected/observed/pass records with notes. |
| `model_rubric` | Placeholder for future live-eval rubric scores. Always separate from the deterministic gate. |

To triage a failure, open the report artifact and look for `all_properties_passed = false`
cases. Each property has its own `pass` flag, so you can tell whether the router
intent, the chart type, or the memory-command detection is what regressed.

### Baseline

`Eval/Data/baseline-v1.json` is the versioned snapshot of the current run. The
`BaselineTests` suite compares every fresh run's observed values against it and prints
a per-property diff on mismatch.

**When behavior intentionally changes:**

1. Run the harness locally; the report is written to
   `tests/RetailPulse.Tests/bin/Debug/net10.0/EvalArtifacts/eval-report-offline.json`.
2. Copy that file over `tests/RetailPulse.Tests/Eval/Data/baseline-v1.json`.
3. Commit the code change and the refreshed baseline in the same commit so
   `git blame` records the shift.

### Stability

`StabilityTests` runs the harness 5–10 times sequentially with a fixed timestamp and
asserts every serialized report is byte-identical. Because the scoring path uses only
regex + string compare with no live model call, unchanged code must produce
bit-for-bit stable output. If this suite ever begins to flake, the harness has drifted
into model-dependent territory and the deterministic gate must not be re-enabled until
the source of nondeterminism is fixed.

### Cost

Offline runs consume **zero LLM tokens** and are recorded at `usd_estimated = 0.0`.
The per-run cap is set to `$5.00` (`EvaluationRunner.CostCapUsd`) and enforced by the
CI gate; any future live-inference mode must stay under that ceiling.

### CI gate

The workflow step `Answer quality gate (offline deterministic)` in `.github/workflows/ci.yml`
runs everything under `RetailPulse.Tests.Eval` in Release. The documented pass-rate
threshold is **100% of deterministically-graded cases** (encoded in
`EvaluationRunner.CiGateThreshold`). Any deterministic failure — routing drift, chart
detection regression, memory-command detection regression, or baseline drift — trips
the gate. The `llm-required` cases in the dataset are not gated deterministically;
their routing decisions are recorded for the baseline but never used to fail the gate
on their own.

### Separation of deterministic vs model-graded

The harness is designed so no model-graded score can ever gate CI on its own:

- The scorer only inspects properties the router and chart detector produce
  deterministically. No `IChatClient` is ever consulted for a graded verdict.
- The `model_rubric` section of the report is always populated but always separate
  from `summary`. A future live-eval mode adds numbers there; it never edits
  `deterministic_pass_rate` or `gate_status`.
- Cases whose routing depends on the live LLM classifier are marked
  `routing_mode: llm-required` in the golden. The scorer records their observed
  routing (from the offline stubbed classifier) for the baseline but does not compare
  it to any expected value.

---

## Knowledge Provider Test Strategy (Issue #107)

The Wave-5 knowledge provider surface (InMemory BM25 default, Azure AI Search
opt-in, Foundry IQ opt-in) is guarded by a layered test strategy so operator
trust in optionality, degradation, safety, and cost is a build-time guarantee
rather than a runtime hope. See
`docs/rag/knowledge-provider-parity-matrix.md` for the per-operation matrix.

### Layered test map

| Concern                                        | Test class                                                                  | Notes                                                                 |
| ---------------------------------------------- | --------------------------------------------------------------------------- | --------------------------------------------------------------------- |
| Pre-Wave-5 InMemory byte-for-byte regression   | `Rag/Baselines/PreWave5InMemoryBaselineTests`                               | Golden JSON + static BM25 kernel contract. Regen is opt-in.           |
| Optional providers not materialized by default | `Rag/Optionality/ZeroCloudDependencyStartupTests`                           | DI-graph shape proof: no cloud SDK client resolvable when disabled.   |
| Optional providers explicit skip when unset    | `Rag/AzureAISearch/AzureAISearchLiveConformanceTests` + Foundry equivalent  | The skip-reason itself is asserted so "silent no-op" never passes.    |
| Shared conformance suite                       | `Rag/KnowledgeBaseConformanceTests` + per-provider subclasses               | InMemory + Foundry-fake in-process; Azure AI Search live-only.        |
| Static provider parity vs documented matrix    | `Rag/Parity/KnowledgeProviderParityMatrixTests`                             | Fails the build if a provider drifts from the parity matrix doc.      |
| Per-agent binding across providers             | `Rag/Parity/PerAgentBindingProviderParityTests`                             | Verifies scoped-source scope reaches every provider's scoped overload.|
| Silent-empty-impossible invariant              | `Rag/Optionality/SilentEmptyImpossibleInvariantTests`                       | Exhaustive walk of `DegradingKnowledgeBase` state space.              |
| Indirect-injection safety on every provider    | `Rag/Security/IndirectInjectionProviderParityTests`                         | Same poisoned+benign corpus through every provider; Content Safety drops the poisoned chunk. |
| APIM traversal + embedding cost telemetry      | `Rag/CostLatency/EmbeddingApimTraversalTests`                               | api-key header + AOAI-shape URL + `ICostTracker` UsageEvent.          |
| Local retrieval latency baseline               | `Rag/CostLatency/RetrievalLatencyBaselineTests`                             | Informational p50/p95 output for InMemory + Foundry-fake.             |
| Retrieval quality Recall@3 comparison          | `Rag/AzureAISearch/AzureAISearchRetrievalQualityComparisonTests`, `Rag/FoundryIQ/FoundryIQRetrievalQualityComparisonTests` | Informational-only; live tests skip cleanly when unconfigured. |

### Cloud test contract

Every cloud-dependent test class:

1. Guards the fact with `[LiveAzureAISearchFact]` / `[LiveFoundryIqFact]`
   whose constructor sets `Skip` to a documented reason string when the
   required environment variables are absent.
2. Ships a plain `[Fact]` that always runs and asserts the exact skip
   reason string when unconfigured. That way "unconfigured, cleanly skipped"
   is visible in CI as an asserted state, not a silently-empty run.

Required environment variables live on `AzureAISearchLiveTestConfig` /
`FoundryIQLiveTestConfig`. When you extend a cloud test class, add its
skip-reason assertion the same day so the CI output honestly distinguishes
"unconfigured" from "silently no-op".

### Regenerating the pre-Wave-5 baseline

`Rag/Baselines/inmemory-pre-wave5.json` locks the InMemory BM25 output for
the fixed corpus in `PreWave5BaselineFixture`. Regeneration is a two-step
reviewable process:

```powershell
$env:RETAIL_PULSE_REGEN_BM25_BASELINE = "1"
dotnet test tests/RetailPulse.Tests/RetailPulse.Tests.csproj `
  --filter "FullyQualifiedName~PreWave5InMemoryBaselineTests.InMemoryBM25_MatchesPreWave5Baseline_ByteForByte"
Remove-Item Env:RETAIL_PULSE_REGEN_BM25_BASELINE
```

The test throws after writing so regeneration is intentional. Review the
resulting JSON diff and the accompanying static BM25 kernel contract test
before committing.

### What NOT to assert

Do not compare raw scores across providers - they are provider-local by
contract. Do not assert exact model wording, even in structural tests. Do
not treat an empty search result as a hidden success path: a healthy
empty corpus is legitimate; the degradation layer never emits an empty
result to hide an outage. If a real defect exists that would violate one
of these invariants, file a separate issue rather than weakening a test.
