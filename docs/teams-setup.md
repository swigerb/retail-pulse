# Microsoft Teams Integration Setup Guide

This guide walks you through deploying Retail Pulse as a Microsoft Teams bot with SSO authentication, Adaptive Card responses, and chart visualizations.

---

## Prerequisites

Before you begin, ensure you have:

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) installed
- [Node.js 20+](https://nodejs.org/) installed
- [Azure CLI](https://docs.microsoft.com/cli/azure/install-azure-cli) installed and authenticated (`az login`)
- A **Microsoft 365 developer tenant** or access to a Teams tenant with app sideloading enabled
- An **Azure subscription** with permissions to create:
  - Azure Bot resources
  - Entra ID (Azure AD) app registrations
- Admin access to **Teams Admin Center** (or a developer tenant with sideloading enabled)

---

## Testing Locally Without App Registration

> **Can't create an Entra ID app registration?** You can still develop, test, and debug the bot locally. The `/api/messages` endpoint has no authentication middleware, and the custom `ProcessActivityAsync` handler directly deserializes Activity JSON—so an app registration is only required for real Teams client connectivity and SSO.

### Option A: Bot Framework Emulator (Recommended)

The [Bot Framework Emulator](https://github.com/microsoft/BotFramework-Emulator/releases) gives you a full chat UI for testing bot conversations without any Azure resources.

1. **Start the Aspire app host:**

   ```powershell
   dotnet run --project src/RetailPulse.AppHost
   ```

2. Open the Aspire dashboard — the **teamsbot** resource shows `http://localhost:5300`.
3. Download and open the [Bot Framework Emulator](https://github.com/microsoft/BotFramework-Emulator/releases).
4. Click **Open Bot** and enter the bot URL:

   ```
   http://localhost:5300/api/messages
   ```

5. Leave **Microsoft App ID** and **Microsoft App Password** blank.
6. Click **Connect** and start sending messages.

> **What works:** Message processing, Adaptive Card responses, chart rendering, and telemetry display all function normally in the Emulator.
>
> **What doesn't:** SSO authentication is unavailable—the bot falls back to anonymous user context using `activity.From` info.

### Option B: HTTP Test Harness

For quick smoke testing without any UI, you can send Activity JSON directly to the bot endpoint.

A pre-built `.http` file is available at [`tests/RetailPulse.Tests/bot-test.http`](../tests/RetailPulse.Tests/bot-test.http).

You can also use PowerShell:

```powershell
$body = @{
    type = "message"
    text = "How is Sierra Gold Tequila performing in the Northeast?"
    from = @{ id = "test-user"; name = "Test User" }
    conversation = @{ id = "test-conv-1" }
    channelId = "test"
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:5300/api/messages" -Method Post -Body $body -ContentType "application/json"
```

### Option C: M365 Developer Program (Full Teams Testing)

If you need to test inside the real Teams client (SSO, sideloading, Teams-specific UI), sign up for a free developer tenant:

1. Join the [Microsoft 365 Developer Program](https://developer.microsoft.com/microsoft-365/dev-program).
2. Provision a developer sandbox—this gives you a tenant where you **can** create app registrations.
3. Follow the full **Steps 1–8** below using your developer tenant credentials.

> **Note:** This is the only way to test real Teams UI, SSO authentication, and app sideloading.

### What Works Without App Registration

| Feature | Without App Reg | Notes |
|---|---|---|
| Message processing | ✅ | Full pipeline works |
| Adaptive Card responses | ✅ | Rendered in Emulator |
| Chart visualizations | ✅ | Recharts (web), native AC elements (Teams) |
| Telemetry display | ✅ | SignalR spans collected |
| SSO authentication | ❌ | Falls back to anonymous |
| Real Teams client | ❌ | Requires app reg + Azure Bot |
| Teams-specific UI | ❌ | Use Emulator approximation |

---

## Step 1: Create Entra ID App Registration

The Teams bot requires an Entra ID (Azure AD) app registration for authentication.

### 1.1 Register the App

1. Navigate to [Azure Portal → Entra ID → App Registrations](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade)
2. Click **New registration**
3. Configure:
   - **Name:** `Retail Pulse Teams Bot`
   - **Supported account types:** `Accounts in this organizational directory only (Single tenant)`
   - **Redirect URI:** Leave blank for now (we'll add it later)
4. Click **Register**
5. **Save the following values** (you'll need them later):
   - **Application (client) ID** → This is your `{AAD_APP_CLIENT_ID}`
   - **Directory (tenant) ID** → This is your `{TENANT_ID}`

### 1.2 Create Client Secret

1. In your app registration, navigate to **Certificates & secrets**
2. Click **New client secret**
3. Set a description (e.g., `TeamsBot Secret`) and expiration (e.g., 24 months)
4. Click **Add**
5. **Save the secret value immediately** → This is your `{CLIENT_SECRET}` (you cannot view it again!)

### 1.3 Configure API Permissions

1. Navigate to **API permissions**
2. Click **Add a permission** → **Microsoft Graph** → **Delegated permissions**
3. Add the following permissions:
   - `User.Read` (default)
   - `email`
   - `openid`
   - `profile`
4. Click **Add permissions**
5. (Optional) Click **Grant admin consent for {your-tenant}** to avoid user consent prompts

### 1.4 Expose an API for SSO

1. Navigate to **Expose an API**
2. Click **Add** next to **Application ID URI**
3. Accept the default format: `api://{AAD_APP_CLIENT_ID}`
   - Or customize it: `api://RetailPulse.yourdomain.com/{AAD_APP_CLIENT_ID}`
4. Click **Save**
5. Click **Add a scope**
6. Configure the scope:
   - **Scope name:** `access_as_user`
   - **Who can consent:** `Admins and users`
   - **Admin consent display name:** `Access Retail Pulse as the user`
   - **Admin consent description:** `Allows the bot to access Retail Pulse on behalf of the signed-in user`
   - **User consent display name:** `Access Retail Pulse as you`
   - **User consent description:** `Allows the bot to access Retail Pulse on your behalf`
   - **State:** `Enabled`
7. Click **Add scope**

---

## Step 2: Create Azure Bot Resource

### 2.1 Create the Bot

1. Navigate to [Azure Portal → Create a resource](https://portal.azure.com/#create/hub)
2. Search for **Azure Bot** and click **Create**
3. Configure:
   - **Bot handle:** `retail-pulse-bot` (must be globally unique)
   - **Subscription:** Your Azure subscription
   - **Resource group:** Create new or use existing (e.g., `rg-RetailPulse-dev`)
   - **Pricing tier:** `F0` (Free tier for development)
   - **Type of App:** `Multi Tenant`
   - **Microsoft App ID:** Select **Use existing app registration** and paste your `{AAD_APP_CLIENT_ID}` from Step 1
4. Click **Review + create** → **Create**

### 2.2 Configure the Bot

1. Once deployed, navigate to your Azure Bot resource
2. Go to **Configuration**
3. Set the **Messaging endpoint:**
   - For **local development** (using dev tunnel or ngrok): `https://{YOUR_DEV_TUNNEL}.ngrok.io/api/messages`
   - For **Azure deployment**: `https://RetailPulse.azurewebsites.net/api/messages`
4. Save the configuration

### 2.3 Enable Teams Channel

1. In your Azure Bot, navigate to **Channels**
2. Click the **Microsoft Teams** icon
3. Accept the terms and click **Agree**
4. **Save** — the Teams channel is now enabled

---

## Step 3: Configure the TeamsBot Service

### 3.1 Copy Configuration Template

```powershell
cd src/RetailPulse.TeamsBot
Copy-Item appsettings.example.json appsettings.json
```

### 3.2 Fill in Configuration

Edit `src/RetailPulse.TeamsBot/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "MicrosoftAppId": "{AAD_APP_CLIENT_ID}",
  "MicrosoftAppPassword": "{CLIENT_SECRET}",
  "MicrosoftAppTenantId": "{TENANT_ID}",
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "{TENANT_ID}",
    "ClientId": "{AAD_APP_CLIENT_ID}",
    "ClientSecret": "{CLIENT_SECRET}"
  },
  "RetailPulseApi": {
    "BaseUrl": "http://localhost:5100"
  },
  "APPLICATIONINSIGHTS_CONNECTION_STRING": "{YOUR_APP_INSIGHTS_CONNECTION_STRING}"
}
```

**Replace placeholders:**
- `{AAD_APP_CLIENT_ID}` — from Step 1.1
- `{CLIENT_SECRET}` — from Step 1.2
- `{TENANT_ID}` — from Step 1.1
- `{YOUR_APP_INSIGHTS_CONNECTION_STRING}` — (Optional) from Azure Application Insights

> **Note:** `appsettings.json` is gitignored. Alternatively, use [.NET User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) for sensitive values.

---

## Step 4: Create Teams App Package

The Teams app package is a ZIP file containing the manifest and icons.

### 4.1 Update Manifest Placeholders

Edit `src/RetailPulse.TeamsBot/appPackage/manifest.json`:

```json
{
  "$schema": "https://developer.microsoft.com/en-us/json-schemas/teams/v1.25/MicrosoftTeams.schema.json",
  "manifestVersion": "1.25",
  "version": "1.0.0",
  "id": "{AAD_APP_CLIENT_ID}",
  "packageName": "com.RetailPulse.teamsbot",
  "developer": {
    "name": "Retail Pulse",
    "websiteUrl": "https://RetailPulse.example.com",
    "privacyUrl": "https://RetailPulse.example.com/privacy",
    "termsOfUseUrl": "https://RetailPulse.example.com/terms"
  },
  ...
  "bots": [
    {
      "botId": "{AAD_APP_CLIENT_ID}",
      "scopes": ["personal", "team", "groupchat"],
      ...
    }
  ],
  "validDomains": [
    "{YOUR_BOT_DOMAIN}"
  ],
  "webApplicationInfo": {
    "id": "{AAD_APP_CLIENT_ID}",
    "resource": "api://{YOUR_BOT_DOMAIN}/{AAD_APP_CLIENT_ID}"
  }
}
```

**Replace:**
- `{AAD_APP_CLIENT_ID}` — from Step 1.1
- `{YOUR_BOT_DOMAIN}` — e.g., `yourtunnel.ngrok.io` (for local dev) or `RetailPulse.azurewebsites.net` (for production)

> **Important:** The `resource` URI in `webApplicationInfo` must match the **Application ID URI** you set in Step 1.4.

### 4.2 Create the ZIP Package

```powershell
cd src/RetailPulse.TeamsBot/appPackage
Compress-Archive -Path manifest.json,color.png,outline.png -DestinationPath RetailPulse.zip -Force
```

This creates `RetailPulse.zip` containing:
- `manifest.json`
- `color.png` (192x192 color icon)
- `outline.png` (32x32 transparent outline icon)

---

## Step 5: Sideload the Teams App

### 5.1 Upload via Teams Admin Center (Recommended)

1. Navigate to [Teams Admin Center](https://admin.teams.microsoft.com/)
2. Go to **Teams apps** → **Manage apps**
3. Click **Upload new app**
4. Upload `RetailPulse.zip`
5. Once uploaded, search for "Retail Pulse" in the app list
6. Set the app availability:
   - **Allowed for users:** Select specific users, groups, or entire organization
7. Click **Save**

### 5.2 Sideload via Teams Client (Developer Mode)

If you have a developer tenant with sideloading enabled:

1. Open **Microsoft Teams**
2. Click **Apps** in the sidebar
3. Click **Manage your apps** → **Upload a custom app** → **Upload for me or my teams**
4. Select `RetailPulse.zip`
5. Click **Add** to install the bot

---

## Step 6: Local Development with Dev Tunnel

To test locally, you need to expose your local bot endpoint to the internet.

### Option A: Use Dev Tunnels (Recommended)

**Install:**
```powershell
dotnet tool install --global Microsoft.DevTunnels.Cli
```

**Create a persistent tunnel:**
```powershell
devtunnel create --allow-anonymous
devtunnel port create -p 5000
devtunnel host
```

**Note the public URL** (e.g., `https://abc123.devtunnels.ms`) and update:
1. Azure Bot **Messaging endpoint** (Step 2.2)
2. Teams manifest `validDomains` and `webApplicationInfo.resource` (Step 4.1)

### Option B: Use ngrok

```powershell
ngrok http 5000
```

**Note the public URL** (e.g., `https://abc123.ngrok.io`) and update:
1. Azure Bot **Messaging endpoint** (Step 2.2)
2. Teams manifest `validDomains` and `webApplicationInfo.resource` (Step 4.1)

---

## Step 7: Run the Application

Start all services via Aspire:

```powershell
cd C:\Users\brswig\source\repos\retail-pulse
dotnet run --project src/RetailPulse.AppHost
```

This starts:
- **API** (Port 5100)
- **MCP Server** (Port 5200)
- **TeamsBot** (Port 5300)
- **React Frontend** (Port 5173)
- **Aspire Dashboard** (Dynamic port — see terminal output)

**Verify the TeamsBot is running:**
1. Open the Aspire Dashboard (URL shown in terminal)
2. Check that the `teamsbot` service is **Running**
3. Note the assigned HTTP port — the TeamsBot runs at `http://localhost:5300`

---

## Step 8: Test the Bot in Teams

1. Open **Microsoft Teams**
2. Click **Apps** → Search for **Retail Pulse**
3. Click **Add** to start a chat
4. Send a test message: **"How is Sierra Gold Tequila performing in the Northeast in Florida?"**
5. The bot should respond with an **Adaptive Card** containing:
   - AI-generated response
   - Collapsible telemetry summary
   - (Optional) Chart visualizations

### Expected Behavior

- **Welcome Message:** When you first chat with the bot, it sends a welcome card
- **Adaptive Card Response:** All bot replies are rendered as rich Adaptive Cards with branding
- **Telemetry:** Each response includes a collapsible telemetry section showing:
  - Agent activity (delegation, tool calls, reasoning)
  - Execution times
  - Data sources accessed
- **Chart Visualizations:** If the agent generates a chart (e.g., "Show me depletion trends"), it appears as a native Adaptive Card chart element
- **Actions:** Click "View Detailed Telemetry" to expand full trace data

---

## Troubleshooting

### Bot doesn't respond

**Symptoms:** You send a message, but the bot doesn't reply.

**Fixes:**
1. **Check messaging endpoint:** Verify the Azure Bot **Messaging endpoint** matches your dev tunnel/ngrok URL
2. **Check logs:** Open Aspire Dashboard → `teamsbot` → Logs for errors
3. **Verify App ID:** Ensure `MicrosoftAppId` in `appsettings.json` matches the Entra ID app registration
4. **Test the endpoint:**
   ```powershell
   curl https://{YOUR_DEV_TUNNEL}/api/messages
   ```
   Should return HTTP 405 (Method Not Allowed) — this confirms the endpoint is reachable

### SSO not working

**Symptoms:** The bot works but doesn't extract user identity.

**Fixes:**
1. **Check Application ID URI:** In Entra ID app registration → Expose an API, ensure the URI format is:
   ```
   api://{YOUR_BOT_DOMAIN}/{AAD_APP_CLIENT_ID}
   ```
2. **Check Teams manifest:** `webApplicationInfo.resource` must match the Application ID URI
3. **Grant admin consent:** In Entra ID app registration → API permissions → Grant admin consent
4. **Check logs:** Look for "SSO not available, using fallback user context" in TeamsBot logs

### Charts not displaying

**Symptoms:** The bot responds but charts are missing from the Adaptive Card.

**Fixes:**
1. **Check Adaptive Card version:** Charts require AC schema version `1.8` or higher - verify your Teams client supports it
2. **Check chart data:** Verify the `ChartSpec` JSON in the API response includes valid `Data` with at least one series
3. **Check Teams channel:** Native Adaptive Card chart elements (`Chart.Line`, `Chart.Donut`, etc.) require Teams desktop or web - some mobile clients may not render them
4. **See [Chart Rendering Guide](chart-rendering.md)** for supported chart types and architecture

### Adaptive Card parsing errors

**Symptoms:** The bot replies with "Unable to parse activity" or "Invalid card schema".

**Fixes:**
1. **Check manifest version:** Ensure `manifestVersion` is `1.25` or higher
2. **Validate Adaptive Card schema:** Use [Adaptive Cards Designer](https://adaptivecards.io/designer/) to test the card JSON
3. **Check logs:** Look for JSON serialization errors in TeamsBot logs

### Teams app installation fails

**Symptoms:** "There was a problem reaching this app" or "We couldn't upload your custom app".

**Fixes:**
1. **Validate manifest:** Use [Teams App Validator](https://dev.teams.microsoft.com/appvalidation.html) to check for errors
2. **Check ZIP structure:** The ZIP must contain `manifest.json`, `color.png`, `outline.png` at the root (no folders)
3. **Check app size:** The ZIP must be < 10 MB
4. **Verify bot ID:** `id` and `botId` in manifest must match your Entra ID app registration client ID

---

## Deployment to Azure

For production deployment, replace local URLs with Azure-hosted endpoints:

1. **Deploy the API, MCP Server, and TeamsBot** to Azure App Service or Container Apps
2. **Update Azure Bot messaging endpoint** to point to your production URL
3. **Update Teams manifest** with production domain and re-upload the app package
4. **Configure Application Insights** for production telemetry

See [Azure Deployment Guide](https://learn.microsoft.com/azure/bot-service/bot-service-quickstart-create-bot) for detailed instructions.

---

## Next Steps

- **Customize the bot:** Edit `RetailPulseTeamsBot.cs` to add custom commands or handlers
- **Add card actions:** Extend `AdaptiveCardBuilder.cs` to add interactive buttons or forms
- **Enable proactive messaging:** Use the Bot Framework to send notifications to users
- **Deploy to production:** Follow the Azure deployment guide above

For questions or issues, refer to:
- [Microsoft Teams Platform Docs](https://learn.microsoft.com/microsoftteams/platform/)
- [Bot Framework SDK Docs](https://learn.microsoft.com/azure/bot-service/)
- [Adaptive Cards Documentation](https://adaptivecards.io/)
