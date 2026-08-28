using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.SignalR.Client;
using RetailPulse.TeamsBot;
using RetailPulse.TeamsBot.Auth;
using RetailPulse.TeamsBot.Cards;
using RetailPulse.TeamsBot.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Health checks — includes SignalR connection status
builder.Services.AddHealthChecks()
    .AddCheck("bot", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Bot is running"))
    .AddCheck<SignalRHealthCheck>("signalr");

// M365 Agents SDK setup
builder.Services.AddHttpClient();
builder.AddAgentApplicationOptions();
builder.AddAgent<RetailPulseAgent>();
builder.Services.AddSingleton<IStorage, MemoryStorage>();
builder.Services.AddControllers();

// Inbound channel authentication. MapAgentApplicationEndpoints(requireAuth: true) below
// requires an authenticated principal, so without a registered scheme every Activity
// from Teams fails with "No authenticationScheme was specified" — a 500 where the
// channel expects a 401. Development skips it to keep the local loop keyless, matching
// the requireAuth flag on the endpoint mapping.
if (!builder.Environment.IsDevelopment())
{
    builder.AddAgentAspNetAuthentication();
}

string apiBaseUrl = builder.Configuration["services:api:https:0"]
    ?? builder.Configuration["services:api:http:0"]
    ?? builder.Configuration["TeamsBot:ApiBaseUrl"]
    ?? (builder.Environment.IsDevelopment() ? "http://localhost:5000" : null)
    ?? throw new InvalidOperationException(
        "TeamsBot could not resolve the RetailPulse API base URL. " +
        "Configure 'TeamsBot:ApiBaseUrl' or run under Aspire.");

// HttpClient for calling RetailPulse API via Aspire service discovery or explicit deployment URL
builder.Services.AddHttpClient("RetailPulseApi", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// SignalR client for telemetry hub via Aspire service discovery
builder.Services.AddSingleton(sp =>
{
    string telemetryUrl = $"{apiBaseUrl.TrimEnd('/')}/hubs/telemetry";

    HubConnection connection = new HubConnectionBuilder()
        .WithUrl(telemetryUrl)
        .WithAutomaticReconnect()
        .Build();

    return connection;
});

// Services
builder.Services.AddSingleton(sp =>
{
    HubConnection conn = sp.GetRequiredService<HubConnection>();
    ILogger<TelemetrySignalRClient> logger = sp.GetRequiredService<ILogger<TelemetrySignalRClient>>();
    string mode = builder.Configuration["TeamsBot:HealthMode"] ?? "degraded";
    return new TelemetrySignalRClient(conn, logger, mode);
});
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddScoped<TeamsSsoHandler>();
builder.Services.AddSingleton<AdaptiveCardBuilder>();

WebApplication app = builder.Build();

// Connect SignalR client at startup
TelemetrySignalRClient telemetryClient = app.Services.GetRequiredService<TelemetrySignalRClient>();
await telemetryClient.ConnectAsync();

app.MapDefaultEndpoints();

app.UseAuthentication();
app.UseAuthorization();

// M365 Agents SDK endpoints
app.MapAgentRootEndpoint();
app.MapAgentApplicationEndpoints(requireAuth: !app.Environment.IsDevelopment());

app.Run();
