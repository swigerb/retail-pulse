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

RetailPulse has **1815+ tests** across multiple testing strategies:

| Category | Location | Framework | Count |
|----------|----------|-----------|-------|
| Unit tests | tests/RetailPulse.Tests/ | xUnit + FluentAssertions + Moq | ~1700 |
| Contract tests | tests/RetailPulse.Tests/Contract/ | WebApplicationFactory | ~10 |
| E2E demo scenarios | tests/RetailPulse.Tests/E2E/ | WebApplicationFactory | 5 |
| OWASP security | tests/RetailPulse.Tests/Security/ | xUnit | ~20 |
| Chaos tests | tests/RetailPulse.Tests/Chaos/ | xUnit | ~15 |
| Value object tests | tests/RetailPulse.Tests/ValueObjects/ | xUnit | 45 |
| Load tests | tests/RetailPulse.LoadTests/ | NBomber | 2 scenarios |
| Benchmarks | tests/RetailPulse.Benchmarks/ | BenchmarkDotNet | 3 suites |
| Frontend | src/RetailPulse.Web/ | Vitest | ~250 |

---

## Running All Tests

```bash
# Backend (all 1815 tests)
dotnet test RetailPulse.slnx --verbosity quiet

# Frontend
cd src/RetailPulse.Web && npx vitest run

# With coverage
dotnet test RetailPulse.slnx --collect:"XPlat Code Coverage"
```

---

## Contract Tests

Contract tests verify the API's request/response shape stays stable:

- **ChatEndpointContractTests** — Validates POST /api/v1/chat request schema, response structure, and error format (RFC 7807)
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

```bash
dotnet run -c Release --project tests/RetailPulse.Benchmarks
```

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
