using Microsoft.Extensions.AI;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Api.Services;
using RetailPulse.Api.Tools;
using RetailPulse.Contracts;

var builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults (OTel, health checks, service discovery)
builder.AddServiceDefaults();

// Load tenant configuration
var tenantConfigPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "tenant.yaml");
var tenantProvider = new FileTenantProvider(tenantConfigPath);
builder.Services.AddSingleton<ITenantProvider>(tenantProvider);

// Add our custom ActivitySource to the OTel pipeline
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("RetailPulse.Agent"));

// SignalR for real-time telemetry
builder.Services.AddSignalR();

// CORS for React frontend
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173", "https://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// Load prompts from YAML and resolve tenant placeholders
var promptsPath = Path.Combine(builder.Environment.ContentRootPath, "prompts.yaml");
var promptConfig = RetailPulseAgent.LoadPrompts(promptsPath);
var agentDef = promptConfig.Agents["retail-pulse"];

var tenant = tenantProvider.GetTenant();
agentDef.SystemPrompt = agentDef.SystemPrompt
    .Replace("{tenant.company}", tenant.Company)
    .Replace("{tenant.industry}", tenant.Industry)
    .Replace("{tenant.distribution_model}", tenant.Distribution?.Model ?? "Three-Tier")
    .Replace("{tenant.primary_color}", tenant.Theme?.PrimaryColor ?? "#1A73E8")
    .Replace("{tenant.accent_color}", tenant.Theme?.AccentColor ?? "#FFC107")
    .Replace("{tenant.brands}", string.Join(", ", tenant.Brands.Select(b => $"{b.Name} ({string.Join(", ", b.Variants)})")))
    .Replace("{tenant.regions}", string.Join(", ", tenant.Regions));

// Register HttpClient for MCP server communication
builder.Services.AddHttpClient("McpServer", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["McpServer:BaseUrl"] ?? "http://localhost:5200");
});

// Register tools
builder.Services.AddScoped<DepletionStatsTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new DepletionStatsTool(factory.CreateClient("McpServer"));
});
builder.Services.AddScoped<FieldSentimentTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new FieldSentimentTool(factory.CreateClient("McpServer"));
});
builder.Services.AddScoped<ShipmentStatsTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new ShipmentStatsTool(factory.CreateClient("McpServer"));
});

// Chart data tool (always available)
builder.Services.AddScoped<ChartDataTool>();

// Register IChatClient — Azure OpenAI via APIM AI Gateway
var openAiEndpoint = builder.Configuration["OpenAI:Endpoint"] ?? "https://bsapim-dev-northcentralus-001.azure-api.net/inference";
var openAiApiKey = builder.Configuration["OpenAI:ApiKey"] ?? "demo-key";

var azureClient = new Azure.AI.OpenAI.AzureOpenAIClient(
    new Uri(openAiEndpoint),
    new System.ClientModel.ApiKeyCredential(openAiApiKey));

builder.Services.AddChatClient(
    azureClient.GetChatClient(agentDef.Model).AsIChatClient())
    .UseFunctionInvocation()
    .UseOpenTelemetry(configure: c => c.EnableSensitiveData = true);

// Foundry agent — optional, controlled by FoundryAgent:Enabled config (default: false)
var foundryEnabled = builder.Configuration.GetValue<bool>("FoundryAgent:Enabled", false);

if (foundryEnabled)
{
    var foundryProjectEndpoint = builder.Configuration["FoundryAgent:ProjectEndpoint"]
        ?? "https://bs-dev-swedencentral-aoai.services.ai.azure.com/api/projects/bs-dev-swedencentral-aoai-project";

    builder.Services.AddAzureAgent<IBacardiShipmentAgent>(options =>
    {
        options.FriendlyName = builder.Configuration["FoundryAgent:ShipmentAgentName"]
            ?? "Bacardi Shipment Specialist";
        options.ProjectEndpoint = foundryProjectEndpoint;
        options.DirectAgentId = builder.Configuration["FoundryAgent:ShipmentAgentId"];
    });

    builder.Services.AddScoped<FoundryShipmentAgent>();
}
else
{
    builder.Services.AddScoped<LocalShipmentAnalyzer>();
}

// Register the agent with dynamic tool list
builder.Services.AddScoped<RetailPulseAgent>(sp =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var hubContext = sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<TelemetryHub>>();
    var depletionTool = sp.GetRequiredService<DepletionStatsTool>();
    var sentimentTool = sp.GetRequiredService<FieldSentimentTool>();
    var shipmentTool = sp.GetRequiredService<ShipmentStatsTool>();
    var chartTool = sp.GetRequiredService<ChartDataTool>();
    var logger = sp.GetRequiredService<ILogger<RetailPulseAgent>>();

    var tools = new List<AITool>
    {
        AIFunctionFactory.Create(depletionTool.GetDepletionStats),
        AIFunctionFactory.Create(sentimentTool.GetFieldSentiment),
        AIFunctionFactory.Create(shipmentTool.GetShipmentStats),
        AIFunctionFactory.Create(chartTool.CreateChart)
    };

    // Add either Foundry or local shipment analyzer
    if (foundryEnabled)
    {
        var foundryAgent = sp.GetRequiredService<FoundryShipmentAgent>();
        tools.Add(AIFunctionFactory.Create(foundryAgent.AnalyzeShipments));
    }
    else
    {
        var localAnalyzer = sp.GetRequiredService<LocalShipmentAnalyzer>();
        tools.Add(AIFunctionFactory.Create(localAnalyzer.AnalyzeShipments));
    }

    return new RetailPulseAgent(chatClient, agentDef, hubContext, tools, logger);
});

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseCors();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// SignalR hub
app.MapHub<TelemetryHub>("/hubs/telemetry");

// Chat endpoint
app.MapPost("/api/chat", async (ChatRequest request, RetailPulseAgent agent, CancellationToken ct) =>
{
    var response = await agent.ChatAsync(request, ct);
    return Results.Ok(response);
})
.WithName("Chat");

// Health/info endpoint
app.MapGet("/api/info", () => Results.Ok(new
{
    Name = "Retail Pulse API",
    Version = "1.0.0",
    Agent = agentDef.Name,
    Tools = agentDef.Tools
}))
.WithName("Info");

app.Run();
