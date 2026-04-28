# RetailPulse Teams Bot — Local Testing Guide

## Prerequisites

- .NET 10 SDK
- [VS Code REST Client](https://marketplace.visualstudio.com/items?itemName=humao.rest-client) extension **or** JetBrains HTTP Client (built-in)

## No App Registration Required

The bot endpoint (`POST /api/messages`) manually deserializes Bot Framework `Activity` JSON with `System.Text.Json`. There is no `CloudAdapter` auth validation, so you can POST Activity payloads directly — no Azure Bot registration, no Microsoft App ID, and no client secret needed.

The test payloads use `"channelId": "test"` (not `"msteams"`) so the bot's SSO handler falls back to the `from` field for user identity instead of attempting Teams token exchange.

## Finding the Bot Port

1. Start the Aspire host:

   ```
   dotnet run --project src/RetailPulse.AppHost
   ```

2. Open the Aspire dashboard (default: `https://localhost:17222`).
3. Locate the **teamsbot** resource row — its endpoint is `http://localhost:5300`.
4. The `@baseUrl` variable in `bot-test.http` is already set to `http://localhost:5300`.

## Running Requests

### VS Code

1. Open `bot-test.http`.
2. Click **Send Request** above any `GET` or `POST` line.
3. The response appears in a split pane.

### JetBrains (Rider / IntelliJ)

1. Open `bot-test.http`.
2. Click the green ▶ icon in the gutter next to each request.

## What to Expect

| Request | Expected Response |
|---|---|
| **Health check** | `200 OK` — `Healthy` |
| **Send a message** | `200 OK` — JSON body with an Adaptive Card attachment (look for `contentType: "application/vnd.microsoft.card.adaptive"`) |
| **Welcome trigger** | `200 OK` — Welcome Adaptive Card |
| **Card action** | `200 OK` — Telemetry detail Adaptive Card |
| **Reset command** | `200 OK` — Welcome/reset Adaptive Card |
| **Smoke test (Help)** | `200 OK` — Help or introductory Adaptive Card |

> **Tip:** If the RetailPulse API (`https+http://api`) is not reachable, message requests may return `500` with a connection error in the Aspire logs. Make sure the full Aspire app host is running so service discovery resolves correctly.
