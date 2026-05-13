using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Agents.Tools;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Memory;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Api.Tools;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Memory;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;

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
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// CORS for React frontend — origins are configurable via Cors:AllowedOrigins
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173", "https://localhost:5173" };
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// Load prompts from YAML and resolve tenant placeholders
var promptsPath = Path.Combine(builder.Environment.ContentRootPath, "prompts.yaml");
var promptConfig = RetailPulse.Api.Agents.RetailPulseAgent.LoadPrompts(promptsPath);
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

// Resolve demand-forecast agent prompt with tenant placeholders
var demandForecastDef = promptConfig.Agents["demand-forecast"];
demandForecastDef.SystemPrompt = demandForecastDef.SystemPrompt
    .Replace("{tenant.company}", tenant.Company)
    .Replace("{tenant.industry}", tenant.Industry)
    .Replace("{tenant.distribution_model}", tenant.Distribution?.Model ?? "Three-Tier")
    .Replace("{tenant.primary_color}", tenant.Theme?.PrimaryColor ?? "#1A73E8")
    .Replace("{tenant.accent_color}", tenant.Theme?.AccentColor ?? "#FFC107")
    .Replace("{tenant.brands}", string.Join(", ", tenant.Brands.Select(b => $"{b.Name} ({string.Join(", ", b.Variants)})")))
    .Replace("{tenant.regions}", string.Join(", ", tenant.Regions));

// Router prompt doesn't need tenant placeholders (intent classification is domain-generic)
var routerDef = promptConfig.Agents["router"];

// Register HttpClient for MCP server communication. The default URL is a
// dev convenience — production should always set McpServer:BaseUrl.
var mcpBaseUrl = builder.Configuration["McpServer:BaseUrl"]
    ?? (builder.Environment.IsDevelopment() ? "http://localhost:5200" : null)
    ?? throw new InvalidOperationException(
        "Configuration value 'McpServer:BaseUrl' is required outside of Development.");
builder.Services.AddHttpClient("McpServer", client =>
{
    client.BaseAddress = new Uri(mcpBaseUrl);
});

// Register tools
builder.Services.AddScoped<DepletionStatsTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new DepletionStatsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<DepletionStatsTool>>());
});
builder.Services.AddScoped<PortfolioDepletionStatsTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new PortfolioDepletionStatsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<PortfolioDepletionStatsTool>>());
});
builder.Services.AddScoped<FieldSentimentTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new FieldSentimentTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<FieldSentimentTool>>());
});
builder.Services.AddScoped<ShipmentStatsTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new ShipmentStatsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<ShipmentStatsTool>>());
});

// Chart data tool (always available)
builder.Services.AddScoped<ChartDataTool>();

builder.Services.AddScoped<VariantMixTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new VariantMixTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<VariantMixTool>>());
});

// Demand forecasting tools
builder.Services.AddScoped<HistoricalDemandTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new HistoricalDemandTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<HistoricalDemandTool>>());
});
builder.Services.AddScoped<ForecastTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new ForecastTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<ForecastTool>>());
});
builder.Services.AddScoped<SeasonalityFactorsTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new SeasonalityFactorsTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<SeasonalityFactorsTool>>());
});
builder.Services.AddScoped<DemandRisksTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new DemandRisksTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<DemandRisksTool>>());
});

// Human-in-the-loop approval gate (SQLite-backed, singleton for shared state)
var approvalDbPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "approvals.db");
builder.Services.AddSingleton<IApprovalGate>(sp =>
    new SqliteApprovalGate(approvalDbPath, sp.GetRequiredService<ILogger<SqliteApprovalGate>>()));

// Approval tool — available to specialist agents for high-impact recommendations
builder.Services.AddScoped<ApprovalTool>(sp =>
    new ApprovalTool(
        sp.GetRequiredService<IApprovalGate>(),
        sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<TelemetryHub>>(),
        sp.GetRequiredService<ILogger<ApprovalTool>>()));

// Conversation memory — SQLite-backed, per-user, with configurable TTL
var memoryDbPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "memory.db");
builder.Services.AddConversationMemory(memoryDbPath);

// Register IChatClient — Azure OpenAI via APIM AI Gateway.
// In Production we fail fast if the API key is missing rather than silently
// using "demo-key" which would surface as opaque 401s at runtime.
var openAiEndpoint = builder.Configuration["OpenAI:Endpoint"]
    ?? (builder.Environment.IsDevelopment()
        ? "https://bsapim-dev-northcentralus-001.azure-api.net/inference"
        : null)
    ?? throw new InvalidOperationException(
        "Configuration value 'OpenAI:Endpoint' is required outside of Development.");

var openAiApiKey = builder.Configuration["OpenAI:ApiKey"];
if (string.IsNullOrWhiteSpace(openAiApiKey))
{
    if (builder.Environment.IsDevelopment())
    {
        openAiApiKey = "demo-key";
    }
    else
    {
        throw new InvalidOperationException(
            "Configuration value 'OpenAI:ApiKey' is required outside of Development.");
    }
}

var azureClient = new Azure.AI.OpenAI.AzureOpenAIClient(
    new Uri(openAiEndpoint),
    new System.ClientModel.ApiKeyCredential(openAiApiKey));

builder.Services.AddChatClient(
    azureClient.GetChatClient(agentDef.Model).AsIChatClient())
    .UseFunctionInvocation()
    // EnableSensitiveData logs prompts, responses, and tool arguments which
    // can include user PII. Only enable in Development.
    .UseOpenTelemetry(configure: c => c.EnableSensitiveData = builder.Environment.IsDevelopment());

// Foundry agent — optional, controlled by FoundryAgent:Enabled config (default: false)
var foundryEnabled = builder.Configuration.GetValue<bool>("FoundryAgent:Enabled", false);

if (foundryEnabled)
{
    var foundryProjectEndpoint = builder.Configuration["FoundryAgent:ProjectEndpoint"]
        ?? (builder.Environment.IsDevelopment()
            ? "https://bs-dev-swedencentral-aoai.services.ai.azure.com/api/projects/bs-dev-swedencentral-aoai-project"
            : null)
        ?? throw new InvalidOperationException(
            "Configuration value 'FoundryAgent:ProjectEndpoint' is required when FoundryAgent:Enabled=true outside of Development.");

    builder.Services.AddAzureAgent<IDistributionAnalysisAgent>(options =>
    {
        options.FriendlyName = builder.Configuration["FoundryAgent:ShipmentAgentName"]
            ?? "Distribution Analysis Specialist";
        options.ProjectEndpoint = foundryProjectEndpoint;
        options.DirectAgentId = builder.Configuration["FoundryAgent:ShipmentAgentId"];
    });

    builder.Services.AddScoped<FoundryShipmentAgent>();
}
else
{
    builder.Services.AddScoped<LocalShipmentAnalyzer>();
}

// Register the multi-agent routing pipeline
builder.Services.AddAgentRouting(promptConfig, agentDef, foundryEnabled, sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var depletionTool = sp.GetRequiredService<DepletionStatsTool>();
    var portfolioTool = sp.GetRequiredService<PortfolioDepletionStatsTool>();
    var sentimentTool = sp.GetRequiredService<FieldSentimentTool>();
    var shipmentTool = sp.GetRequiredService<ShipmentStatsTool>();
    var chartTool = sp.GetRequiredService<ChartDataTool>();
    var variantMixTool = sp.GetRequiredService<VariantMixTool>();

    var tools = new List<AITool>
    {
        AIFunctionFactory.Create(depletionTool.GetDepletionStats),
        AIFunctionFactory.Create(portfolioTool.GetPortfolioDepletionStats),
        AIFunctionFactory.Create(sentimentTool.GetFieldSentiment),
        AIFunctionFactory.Create(shipmentTool.GetShipmentStats),
        AIFunctionFactory.Create(variantMixTool.GetVariantMix),
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

    return tools;
},
demandForecastDef: demandForecastDef,
demandToolsFactory: sp =>
{
    var historicalDemandTool = sp.GetRequiredService<HistoricalDemandTool>();
    var forecastTool = sp.GetRequiredService<ForecastTool>();
    var seasonalityTool = sp.GetRequiredService<SeasonalityFactorsTool>();
    var demandRisksTool = sp.GetRequiredService<DemandRisksTool>();
    var chartTool = sp.GetRequiredService<ChartDataTool>();
    var approvalTool = sp.GetRequiredService<ApprovalTool>();

    return new List<AITool>
    {
        AIFunctionFactory.Create(historicalDemandTool.GetHistoricalDemand),
        AIFunctionFactory.Create(forecastTool.GenerateForecast),
        AIFunctionFactory.Create(seasonalityTool.GetSeasonalityFactors),
        AIFunctionFactory.Create(demandRisksTool.IdentifyDemandRisks),
        AIFunctionFactory.Create(chartTool.CreateChart),
        AIFunctionFactory.Create(approvalTool.RequestApproval)
    };
});

// Keep legacy RetailPulseAgent registration for backward compatibility
builder.Services.AddScoped<RetailPulse.Api.Agents.RetailPulseAgent>(sp =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var hubContext = sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<TelemetryHub>>();
    var depletionTool = sp.GetRequiredService<DepletionStatsTool>();
    var portfolioTool = sp.GetRequiredService<PortfolioDepletionStatsTool>();
    var sentimentTool = sp.GetRequiredService<FieldSentimentTool>();
    var shipmentTool = sp.GetRequiredService<ShipmentStatsTool>();
    var chartTool = sp.GetRequiredService<ChartDataTool>();
    var variantMixTool = sp.GetRequiredService<VariantMixTool>();
    var logger = sp.GetRequiredService<ILogger<RetailPulse.Api.Agents.RetailPulseAgent>>();

    var tools = new List<AITool>
    {
        AIFunctionFactory.Create(depletionTool.GetDepletionStats),
        AIFunctionFactory.Create(portfolioTool.GetPortfolioDepletionStats),
        AIFunctionFactory.Create(sentimentTool.GetFieldSentiment),
        AIFunctionFactory.Create(shipmentTool.GetShipmentStats),
        AIFunctionFactory.Create(variantMixTool.GetVariantMix),
        AIFunctionFactory.Create(chartTool.CreateChart)
    };

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

    var configuration = sp.GetRequiredService<IConfiguration>();

    return new RetailPulse.Api.Agents.RetailPulseAgent(chatClient, agentDef, hubContext, tools, logger, configuration);
});

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseCors();
app.UseMiddleware<ApiKeyAuthMiddleware>();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// SignalR hub
app.MapHub<TelemetryHub>("/hubs/telemetry");

// Chat endpoint — routes through multi-agent router with conversation memory
app.MapPost("/api/chat", async (ChatRequest request, IAgentRouter router, IEnumerable<ISpecialistAgent> specialists, ConversationMemoryMiddleware memoryMiddleware, ILogger<Program> logger, CancellationToken ct) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { error = "Field 'message' is required." });
    }

    try
    {
        var userId = request.User?.ObjectId ?? "anonymous";

        // Inject memory context into the request before routing
        var memoryContext = await memoryMiddleware.BuildMemoryContextAsync(userId, request.Message, ct);
        var enrichedRequest = request;
        if (memoryContext is not null)
        {
            // Prepend memory context to conversation history so agents see it
            var historyWithMemory = new List<ChatHistoryMessage>
            {
                new("system", memoryContext)
            };
            if (request.History is { Count: > 0 })
                historyWithMemory.AddRange(request.History);

            enrichedRequest = request with { History = historyWithMemory };
        }

        // Route the message to the appropriate specialist
        var decision = await router.RouteAsync(
            enrichedRequest.Message,
            enrichedRequest.History,
            enrichedRequest.User,
            tenantId: null,
            ct);

        // Find the specialist by key
        var specialist = specialists.FirstOrDefault(s =>
            string.Equals(s.Key, decision.AgentKey, StringComparison.OrdinalIgnoreCase));

        if (specialist is null)
        {
            logger.LogWarning("No specialist found for key '{AgentKey}' — using General agent", decision.AgentKey);
            specialist = specialists.First(s => s.Key == "general");
        }

        logger.LogInformation(
            "Routing to {AgentKey} ({DisplayName}) — intent: {Intent}, confidence: {Confidence:F2}",
            specialist.Key, specialist.DisplayName, decision.Intent, decision.Confidence);

        var response = await specialist.HandleAsync(enrichedRequest, ct);

        // Extract and store memory after the response (fire-and-forget style, non-blocking)
        if (decision.Intent != AgentIntent.MemoryManagement)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await memoryMiddleware.ExtractAndStoreAsync(userId, request.Message, response.Reply, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Background memory extraction failed for user {UserId}", userId);
                }
            }, CancellationToken.None);
        }

        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled error from chat agent for session {SessionId}", request.SessionId);

        return Results.Json(
            new
            {
                error = "The AI service is temporarily unavailable. Please try again shortly.",
                code = "service_unavailable"
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("Chat");

// Health/info endpoint
app.MapGet("/api/info", (IEnumerable<ISpecialistAgent> specialists) => Results.Ok(new
{
    Name = "Retail Pulse API",
    Version = "1.0.0",
    Agent = agentDef.Name,
    Tools = agentDef.Tools,
    Router = "RetailOpsRouter",
    Specialists = specialists.Select(s => new { s.Key, s.DisplayName }).ToList()
}))
.WithName("Info");

// ── Approval endpoints ───────────────────────────────────────────────────

app.MapGet("/api/approvals/pending", async (IApprovalGate gate, HttpContext http, CancellationToken ct) =>
{
    // Use authenticated user's ObjectId when available, otherwise fall back to query param
    var userId = http.Request.Query["userId"].FirstOrDefault() ?? "default";
    var pending = await gate.GetPendingAsync(userId, ct);

    return Results.Ok(pending.Select(r => new
    {
        id = r.RequestId,
        action = r.Context.Action,
        reasoning = r.Context.Reasoning,
        impact = r.Context.Impact,
        urgency = r.Context.Urgency,
        agentId = r.Context.AgentId,
        agentName = r.Context.AgentId,
        requestedAt = r.CreatedAt,
        timeoutAt = r.ExpiresAt,
        status = r.Decision.ToString().ToLowerInvariant(),
        comment = r.Comment
    }));
})
.WithName("GetPendingApprovals");

app.MapGet("/api/approvals/{requestId}", async (string requestId, IApprovalGate gate, CancellationToken ct) =>
{
    try
    {
        var result = await gate.GetResultAsync(requestId, ct);
        return Results.Ok(new
        {
            requestId = result.RequestId,
            decision = result.Decision.ToString().ToLowerInvariant(),
            comment = result.Comment,
            respondedAt = result.RespondedAt
        });
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = $"Approval request '{requestId}' not found." });
    }
})
.WithName("GetApprovalStatus");

app.MapPost("/api/approvals/{requestId}/respond", async (string requestId, ApprovalResponseDto body, IApprovalGate gate, Microsoft.AspNetCore.SignalR.IHubContext<TelemetryHub> hubContext, CancellationToken ct) =>
{
    if (!Enum.TryParse<ApprovalDecision>(body.Decision, true, out var decision) || decision == ApprovalDecision.Pending || decision == ApprovalDecision.TimedOut)
    {
        return Results.BadRequest(new { error = "Decision must be 'Approved', 'Rejected', or 'Modified'." });
    }

    try
    {
        await gate.RespondAsync(requestId, decision, body.Comment, ct);

        // Notify connected dashboard clients of the resolution
        await hubContext.Clients.All.SendAsync("approval_resolved", new
        {
            requestId,
            decision = decision.ToString().ToLowerInvariant(),
            comment = body.Comment,
            respondedAt = DateTimeOffset.UtcNow
        });

        return Results.Ok(new { requestId, decision = decision.ToString().ToLowerInvariant(), comment = body.Comment });
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = $"Approval request '{requestId}' not found." });
    }
})
.WithName("RespondToApproval");

app.MapGet("/api/approvals/history", async (IApprovalGate gate, CancellationToken ct) =>
{
    var history = await gate.GetHistoryAsync(50, ct);

    return Results.Ok(history.Select(r => new
    {
        id = r.RequestId,
        action = r.Context.Action,
        reasoning = r.Context.Reasoning,
        impact = r.Context.Impact,
        urgency = r.Context.Urgency,
        agentId = r.Context.AgentId,
        agentName = r.Context.AgentId,
        requestedAt = r.CreatedAt,
        timeoutAt = r.ExpiresAt,
        status = r.Decision.ToString().ToLowerInvariant(),
        decidedAt = r.RespondedAt,
        comment = r.Comment
    }));
})
.WithName("GetApprovalHistory");

app.Run();

// ── DTOs ─────────────────────────────────────────────────────────────────

/// <summary>
/// Request body for the POST /api/approvals/{requestId}/respond endpoint.
/// </summary>
record ApprovalResponseDto(string Decision, string? Comment = null);
