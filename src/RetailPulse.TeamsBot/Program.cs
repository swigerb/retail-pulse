using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Storage;
using Microsoft.Agents.Hosting.AspNetCore;
using RetailPulse.TeamsBot;
using RetailPulse.TeamsBot.Cards;
using RetailPulse.TeamsBot.Services;
using RetailPulse.TeamsBot.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck("bot", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Bot is running"));

// M365 Agents SDK setup
builder.Services.AddHttpClient();
builder.AddAgentApplicationOptions();
builder.AddAgent<RetailPulseAgent>();
builder.Services.AddSingleton<IStorage, MemoryStorage>();
builder.Services.AddControllers();

// HttpClient for calling RetailPulse API via Aspire service discovery
builder.Services.AddHttpClient("RetailPulseApi", client =>
{
    client.BaseAddress = new Uri("https+http://api"); // Aspire service discovery
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// SignalR client for telemetry hub via Aspire service discovery
builder.Services.AddSingleton(sp =>
{
    // Prefer Aspire service discovery; in non-Aspire/non-Development the API
    // URL must be explicitly configured via TeamsBot:ApiBaseUrl.
    var apiUrl = builder.Configuration["services:api:https:0"]
        ?? builder.Configuration["services:api:http:0"]
        ?? builder.Configuration["TeamsBot:ApiBaseUrl"]
        ?? (builder.Environment.IsDevelopment() ? "http://localhost:5000" : null);

    if (string.IsNullOrWhiteSpace(apiUrl))
    {
        throw new InvalidOperationException(
            "TeamsBot could not resolve the RetailPulse API base URL. " +
            "Configure 'TeamsBot:ApiBaseUrl' or run under Aspire (which sets 'services:api:https:0').");
    }

    var telemetryUrl = $"{apiUrl.TrimEnd('/')}/hubs/telemetry";

    var connection = new HubConnectionBuilder()
        .WithUrl(telemetryUrl)
        .WithAutomaticReconnect()
        .Build();

    return connection;
});

// Services
builder.Services.AddSingleton<TelemetrySignalRClient>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddScoped<TeamsSsoHandler>();
builder.Services.AddSingleton<AdaptiveCardBuilder>();

var app = builder.Build();

// Connect SignalR client at startup
var telemetryClient = app.Services.GetRequiredService<TelemetrySignalRClient>();
await telemetryClient.ConnectAsync();

app.MapDefaultEndpoints();

app.UseAuthentication();
app.UseAuthorization();

// M365 Agents SDK endpoints
app.MapAgentRootEndpoint();
app.MapAgentApplicationEndpoints(requireAuth: !app.Environment.IsDevelopment());

app.Run();
