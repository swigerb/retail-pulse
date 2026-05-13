using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Agents.Tools;
using RetailPulse.Api.Alerts;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Memory;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Api.Tools;
using RetailPulse.Api.Tracing;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Alerts;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Memory;
using RetailPulse.Contracts.Routing;
using RetailPulse.Contracts.Tracing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

var builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults (OTel, health checks, service discovery)
builder.AddServiceDefaults();

// Load tenant configuration
var tenantConfigPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "tenant.yaml");
var tenantProvider = new FileTenantProvider(tenantConfigPath);
builder.Services.AddSingleton<ITenantProvider>(tenantProvider);

// Add our custom ActivitySource to the OTel pipeline
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("RetailPulse.Agent").AddSource("RetailPulse.Alerts"));

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

// Resolve promo-planning agent prompt with tenant placeholders
var promoPlanningDef = promptConfig.Agents.TryGetValue("promo-planning", out var promoDef) ? promoDef : null;
if (promoPlanningDef != null)
{
    promoPlanningDef.SystemPrompt = promoPlanningDef.SystemPrompt
        .Replace("{tenant.company}", tenant.Company)
        .Replace("{tenant.industry}", tenant.Industry)
        .Replace("{tenant.distribution_model}", tenant.Distribution?.Model ?? "Three-Tier")
        .Replace("{tenant.primary_color}", tenant.Theme?.PrimaryColor ?? "#1A73E8")
        .Replace("{tenant.accent_color}", tenant.Theme?.AccentColor ?? "#FFC107")
        .Replace("{tenant.brands}", string.Join(", ", tenant.Brands.Select(b => $"{b.Name} ({string.Join(", ", b.Variants)})")))
        .Replace("{tenant.regions}", string.Join(", ", tenant.Regions));
}

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

// Promo planning tools
builder.Services.AddScoped<PromoHistoryTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new PromoHistoryTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<PromoHistoryTool>>());
});
builder.Services.AddScoped<CalculateLiftTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new CalculateLiftTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<CalculateLiftTool>>());
});
builder.Services.AddScoped<EvaluateTimingTool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new EvaluateTimingTool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<EvaluateTimingTool>>());
});
builder.Services.AddScoped<EstimateROITool>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new EstimateROITool(
        factory.CreateClient("McpServer"),
        sp.GetService<ILogger<EstimateROITool>>());
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

// Proactive alerts — background anomaly detection with SQLite persistence
var alertsDbPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "alerts.db");
builder.Services.AddProactiveAlerts(alertsDbPath);

// Distributed tracing — in-memory ring buffer with SignalR push for real-time trace events
builder.Services.AddSingleton<InMemoryTraceCollector>(sp =>
    new InMemoryTraceCollector(
        sp.GetRequiredService<IHubContext<TelemetryHub>>(),
        sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<ITraceCollector>(sp => sp.GetRequiredService<InMemoryTraceCollector>());

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
},
promoPlanningDef: promoPlanningDef,
promoToolsFactory: sp =>
{
    var promoHistoryTool = sp.GetRequiredService<PromoHistoryTool>();
    var calculateLiftTool = sp.GetRequiredService<CalculateLiftTool>();
    var evaluateTimingTool = sp.GetRequiredService<EvaluateTimingTool>();
    var estimateROITool = sp.GetRequiredService<EstimateROITool>();
    var chartTool = sp.GetRequiredService<ChartDataTool>();
    var approvalTool = sp.GetRequiredService<ApprovalTool>();

    return new List<AITool>
    {
        AIFunctionFactory.Create(promoHistoryTool.GetPromoHistory),
        AIFunctionFactory.Create(calculateLiftTool.CalculateLift),
        AIFunctionFactory.Create(evaluateTimingTool.EvaluateTiming),
        AIFunctionFactory.Create(estimateROITool.EstimateROI),
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

// Chat endpoint — routes through multi-agent router with conversation memory and distributed tracing
app.MapPost("/api/chat", async (ChatRequest request, IAgentRouter router, IEnumerable<ISpecialistAgent> specialists, ConversationMemoryMiddleware memoryMiddleware, InMemoryTraceCollector traceCollector, ILogger<Program> logger, CancellationToken ct) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { error = "Field 'message' is required." });
    }

    try
    {
        var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        var userId = request.User?.ObjectId ?? "anonymous";

        // Start root trace span: chat_request
        using var chatActivity = AgentTelemetry.StartChatRequest(sessionId, request.Message);
        var traceId = chatActivity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var traceStartTime = DateTimeOffset.UtcNow;

        // Memory recall with tracing
        string? memoryContext = null;
        using (var memoryRecallActivity = AgentTelemetry.StartMemoryRecall(userId))
        {
            var memoryStart = DateTimeOffset.UtcNow;
            memoryContext = await memoryMiddleware.BuildMemoryContextAsync(userId, request.Message, ct);
            var memoryEnd = DateTimeOffset.UtcNow;
            var memoryDurationMs = (memoryEnd - memoryStart).TotalMilliseconds;

            memoryRecallActivity?.SetTag("memory.entries_recalled", memoryContext is not null ? "context_found" : "none");

            traceCollector.CaptureSpan(new TraceSpan(
                SpanId: Guid.NewGuid().ToString("N")[..16],
                TraceId: traceId,
                ParentSpanId: chatActivity?.SpanId.ToString(),
                OperationName: "memory.recall",
                StartTime: memoryStart,
                EndTime: memoryEnd,
                DurationMs: memoryDurationMs,
                Tags: new Dictionary<string, string>
                {
                    ["memory.user_id"] = userId,
                    ["memory.entries_recalled"] = memoryContext is not null ? "context_found" : "none"
                }));
        }

        var enrichedRequest = request;
        if (memoryContext is not null)
        {
            var historyWithMemory = new List<ChatHistoryMessage>
            {
                new("system", memoryContext)
            };
            if (request.History is { Count: > 0 })
                historyWithMemory.AddRange(request.History);

            enrichedRequest = request with { History = historyWithMemory };
        }

        // Router classification with tracing
        RoutingDecision decision;
        {
            var classifyStart = DateTimeOffset.UtcNow;
            using var classifyActivity = AgentTelemetry.StartRouterClassify(enrichedRequest.Message);

            decision = await router.RouteAsync(
                enrichedRequest.Message,
                enrichedRequest.History,
                enrichedRequest.User,
                tenantId: null,
                ct);

            var classifyEnd = DateTimeOffset.UtcNow;
            classifyActivity?.SetTag("router.intent", decision.Intent);
            classifyActivity?.SetTag("router.confidence", decision.Confidence);

            traceCollector.CaptureSpan(new TraceSpan(
                SpanId: Guid.NewGuid().ToString("N")[..16],
                TraceId: traceId,
                ParentSpanId: chatActivity?.SpanId.ToString(),
                OperationName: "router.classify",
                StartTime: classifyStart,
                EndTime: classifyEnd,
                DurationMs: (classifyEnd - classifyStart).TotalMilliseconds,
                Tags: new Dictionary<string, string>
                {
                    ["router.intent"] = decision.Intent,
                    ["router.confidence"] = decision.Confidence.ToString("F2")
                }));
        }

        // Agent selection with tracing
        ISpecialistAgent? specialist;
        {
            var selectStart = DateTimeOffset.UtcNow;
            using var selectActivity = AgentTelemetry.StartRouterSelectAgent();

            specialist = specialists.FirstOrDefault(s =>
                string.Equals(s.Key, decision.AgentKey, StringComparison.OrdinalIgnoreCase));

            if (specialist is null)
            {
                logger.LogWarning("No specialist found for key '{AgentKey}' — using General agent", decision.AgentKey);
                specialist = specialists.First(s => s.Key == "general");
            }

            var selectEnd = DateTimeOffset.UtcNow;
            selectActivity?.SetTag("router.selected_agent", specialist.Key);
            selectActivity?.SetTag("router.selected_agent_name", specialist.DisplayName);

            traceCollector.CaptureSpan(new TraceSpan(
                SpanId: Guid.NewGuid().ToString("N")[..16],
                TraceId: traceId,
                ParentSpanId: chatActivity?.SpanId.ToString(),
                OperationName: "router.select_agent",
                StartTime: selectStart,
                EndTime: selectEnd,
                DurationMs: (selectEnd - selectStart).TotalMilliseconds,
                Tags: new Dictionary<string, string>
                {
                    ["router.selected_agent"] = specialist.Key,
                    ["router.selected_agent_name"] = specialist.DisplayName
                }));
        }

        logger.LogInformation(
            "Routing to {AgentKey} ({DisplayName}) — intent: {Intent}, confidence: {Confidence:F2}, traceId: {TraceId}",
            specialist.Key, specialist.DisplayName, decision.Intent, decision.Confidence, traceId);

        // Agent execution with tracing
        ChatResponse response;
        {
            var agentStart = DateTimeOffset.UtcNow;
            using var agentActivity = AgentTelemetry.StartAgentProcess(specialist.Key);

            response = await specialist.HandleAsync(enrichedRequest, ct);

            var agentEnd = DateTimeOffset.UtcNow;
            var toolsCalledCount = response.Spans?.Count(s => s.Type == "tool_call") ?? 0;
            var inputTokens = response.TokenUsage?.InputTokens ?? 0;
            var outputTokens = response.TokenUsage?.OutputTokens ?? 0;

            agentActivity?.SetTag("agent.name", specialist.Key);
            agentActivity?.SetTag("agent.tools_called_count", toolsCalledCount);
            agentActivity?.SetTag("agent.token_input", inputTokens);
            agentActivity?.SetTag("agent.token_output", outputTokens);

            traceCollector.CaptureSpan(new TraceSpan(
                SpanId: Guid.NewGuid().ToString("N")[..16],
                TraceId: traceId,
                ParentSpanId: chatActivity?.SpanId.ToString(),
                OperationName: $"agent.{specialist.Key}.process",
                StartTime: agentStart,
                EndTime: agentEnd,
                DurationMs: (agentEnd - agentStart).TotalMilliseconds,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                Tags: new Dictionary<string, string>
                {
                    ["agent.name"] = specialist.Key,
                    ["agent.tools_called_count"] = toolsCalledCount.ToString(),
                    ["agent.token_input"] = inputTokens.ToString(),
                    ["agent.token_output"] = outputTokens.ToString()
                }));
        }

        // Record individual tool spans from agent response
        if (response.Spans is { Count: > 0 })
        {
            foreach (var span in response.Spans.Where(s => s.Type is "tool_call" or "tool_result"))
            {
                traceCollector.CaptureSpan(new TraceSpan(
                    SpanId: Guid.NewGuid().ToString("N")[..16],
                    TraceId: traceId,
                    ParentSpanId: chatActivity?.SpanId.ToString(),
                    OperationName: $"tool.{span.Name}",
                    StartTime: span.Timestamp,
                    EndTime: span.Timestamp.AddMilliseconds(span.DurationMs),
                    DurationMs: span.DurationMs,
                    Tags: new Dictionary<string, string>
                    {
                        ["tool.name"] = span.Name,
                        ["tool.duration_ms"] = span.DurationMs.ToString("F0"),
                        ["tool.result_size"] = span.Detail?.Length > 0 ? $"{span.Detail.Length} chars" : ""
                    }));
            }
        }

        // Memory store with tracing (fire-and-forget)
        if (decision.Intent != AgentIntent.MemoryManagement)
        {
            var capturedTraceId = traceId;
            var capturedParentSpanId = chatActivity?.SpanId.ToString();
            _ = Task.Run(async () =>
            {
                try
                {
                    var storeStart = DateTimeOffset.UtcNow;
                    using var memoryStoreActivity = AgentTelemetry.StartMemoryStore(userId);
                    await memoryMiddleware.ExtractAndStoreAsync(userId, request.Message, response.Reply, CancellationToken.None);
                    var storeEnd = DateTimeOffset.UtcNow;

                    traceCollector.CaptureSpan(new TraceSpan(
                        SpanId: Guid.NewGuid().ToString("N")[..16],
                        TraceId: capturedTraceId,
                        ParentSpanId: capturedParentSpanId,
                        OperationName: "memory.store",
                        StartTime: storeStart,
                        EndTime: storeEnd,
                        DurationMs: (storeEnd - storeStart).TotalMilliseconds,
                        Tags: new Dictionary<string, string>
                        {
                            ["memory.user_id"] = userId,
                            ["memory.entries_stored"] = "extracted"
                        }));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Background memory extraction failed for user {UserId}", userId);
                }
            }, CancellationToken.None);
        }

        // Notify trace completion via SignalR
        _ = traceCollector.NotifyTraceCompletedAsync(traceId);

        chatActivity?.SetTag("response.length", response.Reply.Length);
        chatActivity?.SetTag("trace.id", traceId);

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

// ── Alert endpoints ──────────────────────────────────────────────────────

app.MapGet("/api/alerts/active", async (IAlertService alertService, CancellationToken ct) =>
{
    var alerts = await alertService.GetActiveAlertsAsync(ct);
    return Results.Ok(alerts);
})
.WithName("GetActiveAlerts");

app.MapGet("/api/alerts/history", async (IAlertService alertService, HttpContext http, CancellationToken ct) =>
{
    var userId = http.Request.Query["userId"].FirstOrDefault() ?? "default";
    var limitStr = http.Request.Query["limit"].FirstOrDefault();
    var limit = int.TryParse(limitStr, out var l) ? l : 50;

    var alerts = await alertService.GetHistoryAsync(userId, limit, ct);
    return Results.Ok(alerts);
})
.WithName("GetAlertHistory");

app.MapPost("/api/alerts/{alertId}/snooze", async (string alertId, AlertSnoozeDto body, IAlertService alertService, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.UserId))
        return Results.BadRequest(new { error = "userId is required." });

    var duration = body.DurationHours switch
    {
        <= 0 => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(body.DurationHours)
    };

    await alertService.SnoozeAsync(body.AlertType ?? alertId, body.UserId, duration, ct);
    return Results.Ok(new { alertId, snoozedFor = duration.ToString(), userId = body.UserId });
})
.WithName("SnoozeAlert");

app.MapPost("/api/alerts/{alertId}/dismiss", async (string alertId, AlertDismissDto body, IAlertService alertService, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.UserId))
        return Results.BadRequest(new { error = "userId is required." });

    await alertService.DismissAsync(alertId, body.UserId, ct);
    return Results.Ok(new { alertId, dismissed = true, userId = body.UserId });
})
.WithName("DismissAlert");

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

// ── Trace endpoints ──────────────────────────────────────────────────────

app.MapGet("/api/traces/recent", (ITraceCollector traceCollector, int? count) =>
{
    var traces = traceCollector.GetRecentTraces(count ?? 20);
    return Results.Ok(traces);
})
.WithName("GetRecentTraces");

app.MapGet("/api/traces/{traceId}/summary", (string traceId, ITraceCollector traceCollector) =>
{
    var summary = traceCollector.GetStructuredSummary(traceId);
    return summary is not null
        ? Results.Ok(summary)
        : Results.NotFound(new { error = $"Trace '{traceId}' not found." });
})
.WithName("GetTraceSummary");

app.MapGet("/api/traces/{traceId}/spans", (string traceId, ITraceCollector traceCollector) =>
{
    var spans = traceCollector.GetSpans(traceId);
    return spans is not null
        ? Results.Ok(spans)
        : Results.NotFound(new { error = $"Trace '{traceId}' not found." });
})
.WithName("GetTraceSpans");

// ── Promo planning endpoints ─────────────────────────────────────────────

app.MapGet("/api/promo/calendar", async (HttpContext http, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var brand = http.Request.Query["brand"].FirstOrDefault();
    var region = http.Request.Query["region"].FirstOrDefault();
    var monthsStr = http.Request.Query["months"].FirstOrDefault();
    var months = int.TryParse(monthsStr, out var m) ? m : 6;

    var client = httpFactory.CreateClient("McpServer");
    var url = $"/api/promo/calendar?months={months}";
    if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
    if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";

    var response = await client.GetAsync(url, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("GetPromoCalendar");

app.MapGet("/api/promo/types", async (IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var client = httpFactory.CreateClient("McpServer");
    var response = await client.GetAsync("/api/promo/types", ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(json, "application/json");
})
.WithName("GetPromoTypes");

app.MapPost("/api/taskmodule/promo", async (PromoEvaluationRequest request, IHttpClientFactory httpFactory, IApprovalGate approvalGate, ILogger<Program> logger, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Brand) || string.IsNullOrWhiteSpace(request.Region) ||
        string.IsNullOrWhiteSpace(request.PromoType) || request.Budget <= 0)
    {
        return Results.BadRequest(new { error = "Fields brand, region, promoType, and budget (> 0) are required." });
    }

    if (!DateOnly.TryParse(request.StartDate, out var startDate) || !DateOnly.TryParse(request.EndDate, out var endDate))
    {
        return Results.BadRequest(new { error = "startDate and endDate must be valid ISO dates (yyyy-MM-dd)." });
    }

    var durationWeeks = Math.Max(1, (endDate.DayNumber - startDate.DayNumber) / 7);
    var client = httpFactory.CreateClient("McpServer");

    // Orchestrate: call all promo tools in parallel
    var historyTask = client.GetStringAsync(
        $"/api/promo/history?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&promoType={Uri.EscapeDataString(request.PromoType)}&months=12", ct);
    var liftTask = client.GetStringAsync(
        $"/api/promo/calculate-lift?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&promoType={Uri.EscapeDataString(request.PromoType)}&spend={request.Budget}", ct);
    var timingTask = client.GetStringAsync(
        $"/api/promo/evaluate-timing?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&startDate={Uri.EscapeDataString(request.StartDate)}&endDate={Uri.EscapeDataString(request.EndDate)}", ct);
    var roiTask = client.GetStringAsync(
        $"/api/promo/estimate-roi?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&promoType={Uri.EscapeDataString(request.PromoType)}&spend={request.Budget}&durationWeeks={durationWeeks}", ct);

    await Task.WhenAll(historyTask, liftTask, timingTask, roiTask);

    var historyJson = await historyTask;
    var liftJson = await liftTask;
    var timingJson = await timingTask;
    var roiJson = await roiTask;

    // Parse ROI for approval gate decision
    using var roiDoc = System.Text.Json.JsonDocument.Parse(roiJson);
    var expectedRoi = roiDoc.RootElement.TryGetProperty("expected_roi", out var roiProp) ? roiProp.GetDouble() : 0;

    // Determine recommendation
    var recommendation = expectedRoi switch
    {
        >= 3.0 => "strongly_recommended",
        >= 2.0 => "recommended",
        >= 1.0 => "proceed_with_caution",
        _ => "not_recommended"
    };

    // Build risk factors
    var riskFactors = new List<string>();
    using var timingDoc = System.Text.Json.JsonDocument.Parse(timingJson);
    if (timingDoc.RootElement.TryGetProperty("conflicts", out var conflicts) && conflicts.GetArrayLength() > 0)
        riskFactors.Add($"{conflicts.GetArrayLength()} overlapping campaign(s) detected");
    if (timingDoc.RootElement.TryGetProperty("risks", out var risks))
    {
        foreach (var risk in risks.EnumerateArray())
        {
            if (risk.TryGetProperty("detail", out var detail))
                riskFactors.Add(detail.GetString() ?? "Unknown risk");
        }
    }
    if (expectedRoi < 1.0)
        riskFactors.Add("Expected ROI below breakeven (1.0x)");
    if (request.Budget > 500000)
        riskFactors.Add("High-budget campaign (>$500K) — requires executive approval");

    // Check approval gate trigger
    string? approvalRequestId = null;
    var requiresApproval = request.Budget > 500000 || (expectedRoi < 2.0 && request.Budget > 100000);
    if (requiresApproval)
    {
        var reason = request.Budget > 500000
            ? $"High-budget promo: ${request.Budget:N0} for {request.Brand} in {request.Region}"
            : $"Low-ROI risk: {expectedRoi:F2}x ROI with ${request.Budget:N0} budget for {request.Brand}";

        var approvalRequest = await approvalGate.RequestApprovalAsync(new RetailPulse.Contracts.Approval.ApprovalContext(
            AgentId: "promo-planning",
            UserId: "taskmodule",
            Action: $"Execute {request.PromoType} promotion for {request.Brand} in {request.Region}",
            Impact: $"Budget: ${request.Budget:N0}, Expected ROI: {expectedRoi:F2}x, Duration: {durationWeeks} weeks",
            Urgency: request.Budget > 500000 ? "high" : "medium",
            Reasoning: reason
        ), ct);

        approvalRequestId = approvalRequest.RequestId;
        logger.LogInformation("Promo task module triggered approval gate: {RequestId} for {Brand}/{Region}", approvalRequestId, request.Brand, request.Region);
    }

    return Results.Ok(new
    {
        recommendation,
        brand = request.Brand,
        region = request.Region,
        promo_type = request.PromoType,
        budget = request.Budget,
        period = new { start = request.StartDate, end = request.EndDate, duration_weeks = durationWeeks },
        target_lift = request.TargetLift,
        roi_estimate = System.Text.Json.JsonSerializer.Deserialize<object>(roiJson),
        timing_assessment = System.Text.Json.JsonSerializer.Deserialize<object>(timingJson),
        lift_analysis = System.Text.Json.JsonSerializer.Deserialize<object>(liftJson),
        historical_context = System.Text.Json.JsonSerializer.Deserialize<object>(historyJson),
        risk_factors = riskFactors,
        approval = requiresApproval ? new
        {
            required = true,
            request_id = approvalRequestId,
            reason = request.Budget > 500000 ? "high_budget" : "low_roi_risk"
        } : new
        {
            required = false,
            request_id = (string?)null,
            reason = (string?)null
        }
    });
})
.WithName("PromoTaskModule");

app.Run();

// ── DTOs ─────────────────────────────────────────────────────────────────

/// <summary>
/// Request body for the POST /api/approvals/{requestId}/respond endpoint.
/// </summary>
record ApprovalResponseDto(string Decision, string? Comment = null);

/// <summary>
/// Request body for the POST /api/alerts/{alertId}/snooze endpoint.
/// </summary>
record AlertSnoozeDto(string UserId, string? AlertType = null, double DurationHours = 1);

/// <summary>
/// Request body for the POST /api/alerts/{alertId}/dismiss endpoint.
/// </summary>
record AlertDismissDto(string UserId);

/// <summary>
/// Request body for the POST /api/taskmodule/promo endpoint.
/// </summary>
record PromoEvaluationRequest(
    string Brand,
    string Region,
    string PromoType,
    double Budget,
    string StartDate,
    string EndDate,
    double? TargetLift = null
);
