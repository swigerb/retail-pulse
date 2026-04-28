# Retail Pulse Testing Guide

This guide covers how to run tests and manually verify the Teams bot integration and chart visualization features.

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

## Manual Testing the Teams Bot

### Prerequisites

1. **Aspire Host Running**: Start from the repo root:
   ```bash
   dotnet run --project src/RetailPulse.AppHost
   ```
   The TeamsBot will be available at `http://localhost:5300`.

### Option A: Bot Framework Emulator (No App Registration)

The [Bot Framework Emulator](https://github.com/microsoft/BotFramework-Emulator/releases) provides a full chat UI — no Azure resources needed.

1. Download and open the Bot Framework Emulator
2. Click **Open Bot** and enter: `http://localhost:5300/api/messages`
3. Leave **Microsoft App ID** and **Microsoft App Password** blank
4. Click **Connect** and start chatting

### Option B: HTTP Test Harness

Use the pre-built `.http` file with VS Code REST Client or JetBrains HTTP Client:

```
tests/RetailPulse.Tests/bot-test.http
```

This includes 6 Activity payloads covering messages, conversation updates, and card actions. See `tests/RetailPulse.Tests/bot-test-README.md` for details.

### Option C: Real Teams Client (Requires App Registration)

For testing in the actual Teams client:

1. **Bot Registration**: Register your bot in Azure Bot Service with:
   - App ID and password configured in user secrets
   - Messaging endpoint pointed to your tunnel or Azure deployment
   - Teams channel enabled

2. **Tunnel**: Use ngrok or dev tunnels:
   ```bash
   ngrok http 5300
   ```
   Update the bot messaging endpoint in Azure Portal to: `https://your-ngrok-url.ngrok.io/api/messages`

### Test Scenarios Checklist

#### ✅ Basic Chat Response
1. Open Teams and navigate to your bot
2. Send a simple message: "Hello"
3. **Expected**: 
   - Receive a chat response card with the Retail Pulse branding (🥃)
   - Reply text is displayed
   - "View Telemetry" button is visible but telemetry section is collapsed by default

#### ✅ Telemetry Toggle Visibility
1. Send any message to the bot
2. Click "📊 View Telemetry" button
3. **Expected**:
   - Telemetry section expands
   - Shows telemetry summary with span icons (🤔, 🔧, ✅, etc.)
   - Shows total duration and span count
   - Each span shows name, type badge, and duration
4. Click "📊 View Telemetry" again
5. **Expected**: Telemetry section collapses

#### ✅ Detailed Telemetry Report
1. Send a message that generates telemetry
2. Click "View Telemetry" to expand the section
3. Click "📋 Full Telemetry Report" button
4. **Expected**:
   - New card appears with full telemetry report
   - Summary section shows total duration, span count, average, and slowest span
   - Waterfall visualization displays timing bars
   - Detailed spans section lists all spans with full details

#### ✅ Chart Generation and Display
1. Send a message requesting visualization: "Show me a chart of sales trends"
2. **Expected**:
   - Response card includes both text reply and chart section
   - "📊 Visualizations" header appears
   - Native Adaptive Card chart element is rendered (Chart.Line, Chart.Donut, etc.)
   - Chart title and data labels are visible

#### ✅ Welcome Card on Member Join
1. Add the bot to a new Teams chat or channel (or rejoin if testing)
2. **Expected**:
   - Welcome card appears with "👋 Welcome to Retail Pulse, [Your Name]!"
   - Branding (🥃) is prominent
   - Suggested actions are displayed:
     - "Show shipment status — Track SH-2025-042"
     - "Analyze trends — Performance over time"
     - "Generate report — Charts and insights"

#### ✅ Error Handling (API Down)
1. Stop the RetailPulse.Api service (stop Aspire or kill the API process)
2. Send a message to the bot
3. **Expected**:
   - Error card appears with ⚠️ icon
   - "Error" header in attention style
   - Error message displayed (e.g., "Connection to API failed")
   - "🔄 Try Again" button is visible
4. Restart the API and click "Try Again"
5. **Expected**: Bot should process the request successfully

#### ✅ SSO Authentication Flow
1. Send a message that requires user context
2. **Expected**:
   - Bot should use SSO token to authenticate
   - User's display name and email are passed to the API
   - No manual sign-in prompt (if already authenticated in Teams)

#### ✅ Multi-turn Conversation (Session Persistence)
1. Start a new conversation: "What's the status of shipment SH-2025-042?"
2. Send a follow-up: "Show me its history"
3. Send another follow-up: "Create a chart"
4. **Expected**:
   - Each response maintains context from previous messages
   - Session ID remains the same across all turns (visible in telemetry if detailed report requested)
   - Telemetry from all turns is available

#### ✅ Chart Data Validation
1. Send a message that generates a chart
2. Check the API response in logs or Aspire dashboard
3. **Expected**:
   - `ChatResponse` includes a `Charts` array with `ChartSpec` objects
   - Each `ChartSpec` has: `Type`, `Title`, `Data` (with series and data points)
   - Web UI renders the chart as an interactive Recharts SVG
   - Teams renders the chart as a native Adaptive Card chart element

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
